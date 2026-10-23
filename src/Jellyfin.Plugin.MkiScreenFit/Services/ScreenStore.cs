using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiScreenFit.Services;

// The screen size each web client reported, per user and device. A report is only taken from the device the
// sign-in belongs to, and kept per user, so nobody can change what is used for someone else's playback. Holds ids,
// the size, whether it can be trusted and a date; nothing else. Written to disk a few seconds after a change.
public class ScreenStore : IDisposable
{
	public const int MaxEntries = 2000;

	// Devices one user keeps reports for: more than anyone uses, so one account can never push out everyone else's.
	public const int MaxPerUser = 20;

	// A report older than this no longer counts: the device may have been plugged into another screen since.
	public static readonly TimeSpan MaxAge = TimeSpan.FromDays(60);

	private readonly object _lock = new();

	private readonly SavedFile _file;

	private readonly Dictionary<string, ScreenReport> _reports;

	private DateTime _lastChange = DateTime.MinValue;

	public ScreenStore(IApplicationPaths paths, ILogger<ScreenStore> logger)
	{
		_file = new SavedFile(Path.Combine(paths.PluginConfigurationsPath, "MkiScreenFit", "screens.json"), Snapshot, logger);
		var oldest = DateTime.UtcNow - MaxAge;
		_reports = (_file.Load<List<ScreenReport>>() ?? new List<ScreenReport>())
			.Where(r => r != null && !string.IsNullOrEmpty(r.DeviceId) && ScreenRules.ValidSize(r.Width, r.Height) && r.At > oldest)
			.OrderByDescending(r => r.At).Take(MaxEntries)
			.GroupBy(r => Key(r.UserId, r.DeviceId)).ToDictionary(g => g.Key, g => g.First());
	}

	public void Report(Guid userId, string deviceId, int width, int height, bool reliable)
	{
		var now = DateTime.UtcNow;
		bool save;
		lock (_lock)
		{
			string key = Key(userId, deviceId);
			_reports.TryGetValue(key, out var old);
			// The same size, now hidden (a browser in full screen looks like a privacy setting): the good report stays.
			if (old != null && old.Reliable && !reliable && old.Width == width && old.Height == height)
			{
				reliable = true;
			}
			bool changed = old == null || old.Width != width || old.Height != height || old.Reliable != reliable;
			_reports[key] = new ScreenReport { UserId = userId, DeviceId = deviceId, Width = width, Height = height, Reliable = reliable, At = now };
			var own = _reports.Where(r => r.Value.UserId == userId).ToList();
			if (own.Count > MaxPerUser)
			{
				foreach (var gone in own.OrderBy(r => r.Value.At).Take(own.Count - MaxPerUser).Select(r => r.Key).ToList())
				{
					_reports.Remove(gone);
				}
			}
			if (_reports.Count > MaxEntries)
			{
				foreach (var gone in _reports.OrderBy(r => r.Value.At).Take(_reports.Count - MaxEntries).Select(r => r.Key).ToList())
				{
					_reports.Remove(gone);
				}
			}
			// Only the date changed: the file is refreshed at most once an hour.
			save = changed || now - _lastChange > TimeSpan.FromHours(1);
			if (save)
			{
				_lastChange = now;
			}
		}
		if (save)
		{
			_file.Changed();
		}
	}

	// The screen this user's device reported, when it is recent and the browser did not hide its real size.
	public ScreenReport? Get(Guid userId, string deviceId)
	{
		lock (_lock)
		{
			return _reports.TryGetValue(Key(userId, deviceId), out var r) && r.Reliable && DateTime.UtcNow - r.At < MaxAge ? r.Copy() : null;
		}
	}

	public List<ScreenReport> All()
	{
		lock (_lock)
		{
			return _reports.Values.Select(r => r.Copy()).ToList();
		}
	}

	public void Dispose()
	{
		_file.Dispose();
		GC.SuppressFinalize(this);
	}

	private static string Key(Guid userId, string deviceId) => userId.ToString("N") + "|" + deviceId;

	// What is written to disk: reports that no longer count are dropped first.
	private object Snapshot()
	{
		lock (_lock)
		{
			var oldest = DateTime.UtcNow - MaxAge;
			foreach (var gone in _reports.Where(r => r.Value.At <= oldest).Select(r => r.Key).ToList())
			{
				_reports.Remove(gone);
			}
			return _reports.Values.Select(r => r.Copy()).ToList();
		}
	}
}

public class ScreenReport
{
	public Guid UserId { get; set; }

	public string DeviceId { get; set; } = string.Empty;

	// Physical pixels, the longer side first.
	public int Width { get; set; }

	public int Height { get; set; }

	// False when the browser reported its window instead of the screen (Firefox's fingerprinting protection).
	public bool Reliable { get; set; }

	public DateTime At { get; set; }

	public ScreenReport Copy() => (ScreenReport)MemberwiseClone();
}
