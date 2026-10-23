using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Jellyfin.Plugin.MkiGuestPass.Services;

// Limits on the public join endpoints, per client address (IPv6 per /64, so a client cannot hop addresses).
// - Typed codes are short, so they get tight limits: 5 wrong codes per address in 15 minutes, and 100 wrong codes
//   on the whole server in 15 minutes pause code entry for everyone for 15 minutes. Links keep working meanwhile.
//   At that pace even a large botnet needs thousands of years to guess one 8-character code.
// - Links and rejoin keys are 256-bit random values that cannot be guessed; their limit only keeps junk traffic cheap.
// - Every address gets at most 60 join requests in 15 minutes, right or wrong.
// A key can name two addresses (separated by a space) when it is unclear which one is the visitor's: every limit then
// holds for each of them, so changing one escapes nothing.
public class Throttle
{
	public const int CodeFailuresPerAddress = 5;

	public const int CodeFailuresServerWide = 100;

	public const int SecretFailuresPerAddress = 30;

	public const int RequestsPerAddress = 60;

	public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

	public static readonly TimeSpan CodePause = TimeSpan.FromMinutes(15);

	private readonly ConcurrentDictionary<string, Queue<DateTime>> _codeFailures = new();

	private readonly ConcurrentDictionary<string, Queue<DateTime>> _secretFailures = new();

	private readonly ConcurrentDictionary<string, Queue<DateTime>> _requests = new();

	private readonly Queue<DateTime> _allCodeFailures = new();

	private DateTime _codesPausedUntil = DateTime.MinValue;

	private static long _lastCleanTicks;

	// Raised once when code entry pauses, so the plugin can say so in the log and the activity list.
	public event Action? CodesPaused;

	public DateTime? CodesPausedUntil
	{
		get
		{
			lock (_allCodeFailures)
			{
				return _codesPausedUntil > DateTime.UtcNow ? _codesPausedUntil : null;
			}
		}
	}

	public static string Key(IPAddress? ip, IPAddress? also)
	{
		string one = Key(ip);
		string two = also == null ? one : Key(also);
		return two == one ? one : one + " " + two;
	}

	public static string Key(IPAddress? ip)
	{
		if (ip == null)
		{
			return "unknown";
		}
		if (ip.IsIPv4MappedToIPv6)
		{
			ip = ip.MapToIPv4();
		}
		if (ip.AddressFamily == AddressFamily.InterNetworkV6)
		{
			var b = ip.GetAddressBytes();
			for (int i = 8; i < b.Length; i++)
			{
				b[i] = 0;
			}
			return new IPAddress(b) + "/64";
		}
		return ip.ToString();
	}

	// Counts the request; false when the address has used up its requests for now.
	public bool Request(string key)
	{
		bool ok = true;
		foreach (var one in Parts(key))
		{
			ok = Add(_requests, one, RequestsPerAddress) && ok;
		}
		return ok;
	}

	public bool CodeBlocked(string key) => CodesPausedUntil != null || Parts(key).Any(k => Count(_codeFailures, k) >= CodeFailuresPerAddress);

	public bool SecretBlocked(string key) => Parts(key).Any(k => Count(_secretFailures, k) >= SecretFailuresPerAddress);

	public void CodeFailed(string key)
	{
		foreach (var one in Parts(key))
		{
			Add(_codeFailures, one, int.MaxValue);
		}
		bool paused = false;
		lock (_allCodeFailures)
		{
			Prune(_allCodeFailures);
			_allCodeFailures.Enqueue(DateTime.UtcNow);
			if (_allCodeFailures.Count >= CodeFailuresServerWide && _codesPausedUntil <= DateTime.UtcNow)
			{
				_codesPausedUntil = DateTime.UtcNow + CodePause;
				_allCodeFailures.Clear();
				paused = true;
			}
		}
		if (paused)
		{
			CodesPaused?.Invoke();
		}
	}

	public void SecretFailed(string key)
	{
		foreach (var one in Parts(key))
		{
			Add(_secretFailures, one, int.MaxValue);
		}
	}

	private static string[] Parts(string key) => key.Split(' ');

	private static int Count(ConcurrentDictionary<string, Queue<DateTime>> map, string key)
	{
		if (!map.TryGetValue(key, out var q))
		{
			return 0;
		}
		lock (q)
		{
			Prune(q);
			return q.Count;
		}
	}

	private static bool Add(ConcurrentDictionary<string, Queue<DateTime>> map, string key, int limit)
	{
		var q = map.GetOrAdd(key, _ => new Queue<DateTime>());
		bool ok;
		lock (q)
		{
			Prune(q);
			ok = q.Count < limit;
			if (ok)
			{
				q.Enqueue(DateTime.UtcNow);
			}
		}
		// Many addresses: forget the ones whose window is over, so the maps cannot grow without end (once a minute at
		// most, so a flood of addresses does not make every request walk the whole map).
		long last = Interlocked.Read(ref _lastCleanTicks);
		if (map.Count > 10000 && DateTime.UtcNow.Ticks - last > TimeSpan.TicksPerMinute && Interlocked.CompareExchange(ref _lastCleanTicks, DateTime.UtcNow.Ticks, last) == last)
		{
			foreach (var old in map.Where(e => { lock (e.Value) { Prune(e.Value); return e.Value.Count == 0; } }).Select(e => e.Key).ToList())
			{
				map.TryRemove(old, out _);
			}
		}
		return ok;
	}

	private static void Prune(Queue<DateTime> q)
	{
		var from = DateTime.UtcNow - Window;
		while (q.Count > 0 && q.Peek() < from)
		{
			q.Dequeue();
		}
	}
}
