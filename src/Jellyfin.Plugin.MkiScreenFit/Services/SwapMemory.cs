using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiScreenFit.Services;

// Which Full HD versions the plugin played for a user instead of the default version. Jellyfin puts the version a
// user watched last at the front, so after a swap on a laptop the Full HD version would also be the default on the
// 4K TV. When the plugin caused that, a device that is not a Full HD screen gets the default version back, exactly as
// it would without the plugin. Holds ids and dates only.
public class SwapMemory : IDisposable
{
	public const int MaxEntries = 5000;

	// Per user, so one account that plays many movies can never push out everyone else's.
	public const int MaxPerUser = 1000;

	// A movie is rarely resumed later than this.
	public static readonly TimeSpan MaxAge = TimeSpan.FromDays(180);

	private readonly object _lock = new();

	private readonly Dictionary<string, SwapEntry> _swaps;

	private readonly SavedFile _file;

	public SwapMemory(IApplicationPaths paths, ILogger<SwapMemory> logger)
	{
		_file = new SavedFile(Path.Combine(paths.PluginConfigurationsPath, "MkiScreenFit", "swaps.json"), Snapshot, logger);
		var oldest = DateTime.UtcNow - MaxAge;
		var loaded = _file.Load<List<SwapEntry>>() ?? new List<SwapEntry>();
		// The same limits as for new entries, so an older file cannot bring back more than they allow.
		_swaps = loaded
			.Where(s => s != null && !string.IsNullOrEmpty(s.SourceId) && s.At > oldest)
			.GroupBy(s => Key(s.UserId, s.SourceId)).Select(g => g.OrderByDescending(s => s.At).First())
			.GroupBy(s => s.UserId).SelectMany(g => g.OrderByDescending(s => s.At).Take(MaxPerUser))
			.OrderByDescending(s => s.At).Take(MaxEntries)
			.ToDictionary(s => Key(s.UserId, s.SourceId));
		if (_swaps.Count != loaded.Count)
		{
			_file.Changed();
		}
	}

	public void Remember(Guid userId, string sourceId)
	{
		lock (_lock)
		{
			_swaps[Key(userId, sourceId)] = new SwapEntry { UserId = userId, SourceId = sourceId, At = DateTime.UtcNow };
			var own = _swaps.Where(s => s.Value.UserId == userId).ToList();
			if (own.Count > MaxPerUser)
			{
				foreach (var gone in own.OrderBy(s => s.Value.At).Take(own.Count - MaxPerUser).Select(s => s.Key).ToList())
				{
					_swaps.Remove(gone);
				}
			}
			if (_swaps.Count > MaxEntries)
			{
				foreach (var gone in _swaps.OrderBy(s => s.Value.At).Take(_swaps.Count - MaxEntries).Select(s => s.Key).ToList())
				{
					_swaps.Remove(gone);
				}
			}
		}
		_file.Changed();
	}

	// The user picked this version by hand on a screen that is not Full HD: from now on it is their own choice.
	public void Forget(Guid userId, string sourceId)
	{
		bool removed;
		lock (_lock)
		{
			removed = _swaps.Remove(Key(userId, sourceId));
		}
		if (removed)
		{
			_file.Changed();
		}
	}

	// True when the plugin played this version for this user instead of the default.
	public bool Played(Guid userId, string sourceId)
	{
		lock (_lock)
		{
			return _swaps.TryGetValue(Key(userId, sourceId), out var s) && DateTime.UtcNow - s.At < MaxAge;
		}
	}

	public void Dispose()
	{
		_file.Dispose();
		GC.SuppressFinalize(this);
	}

	private static string Key(Guid userId, string sourceId) => userId.ToString("N") + "|" + sourceId.ToLowerInvariant();

	private object Snapshot()
	{
		lock (_lock)
		{
			var oldest = DateTime.UtcNow - MaxAge;
			return _swaps.Values.Where(s => s.At > oldest).Select(s => new SwapEntry { UserId = s.UserId, SourceId = s.SourceId, At = s.At }).ToList();
		}
	}
}

public class SwapEntry
{
	public Guid UserId { get; set; }

	public string SourceId { get; set; } = string.Empty;

	public DateTime At { get; set; }
}
