using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Services;

// Limits failed attempts on the anonymous change endpoints per client address, on top of Jellyfin's own lockout.
public class Throttle
{
	public const int MaxFailures = 10;

	public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

	private readonly ConcurrentDictionary<string, Queue<DateTime>> _fehlschlaege = new();

	public bool Blocked(string ip)
	{
		if (!_fehlschlaege.TryGetValue(ip, out var liste))
		{
			return false;
		}
		lock (liste)
		{
			Aufraeumen(liste);
			return liste.Count >= MaxFailures;
		}
	}

	public void Fail(string ip)
	{
		var liste = _fehlschlaege.GetOrAdd(ip, _ => new Queue<DateTime>());
		lock (liste)
		{
			Aufraeumen(liste);
			liste.Enqueue(DateTime.UtcNow);
		}
		if (_fehlschlaege.Count > 10000)
		{
			foreach (var alt in _fehlschlaege.Where(e => e.Value.Count == 0 || e.Value.Peek() < DateTime.UtcNow - Window).Select(e => e.Key).ToList())
			{
				_fehlschlaege.TryRemove(alt, out _);
			}
		}
	}

	private static void Aufraeumen(Queue<DateTime> liste)
	{
		while (liste.Count > 0 && liste.Peek() < DateTime.UtcNow - Window)
		{
			liste.Dequeue();
		}
	}
}
