using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiScreenFit.Services;

// The screen size each web client reported, per user and device. Kept per user so that someone who signs in with
// another person's device id can only ever change what is used for their own playback. Holds ids, two numbers and
// a date; nothing else. Written to disk a few seconds after a change, so a page reload never costs a disk write.
public class ScreenStore : IDisposable
{
	public const int MaxEntries = 2000;

	// A report older than this no longer counts: the device may have been plugged into another screen since.
	public static readonly TimeSpan MaxAge = TimeSpan.FromDays(60);

	private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(5);

	private readonly object _lock = new();

	private readonly object _saveLock = new();

	private readonly string _file;

	private readonly ILogger<ScreenStore> _logger;

	private readonly Timer _timer;

	private Dictionary<string, ScreenReport> _reports;

	private DateTime _lastSave = DateTime.MinValue;

	public ScreenStore(IApplicationPaths paths, ILogger<ScreenStore> logger)
	{
		_logger = logger;
		_file = Path.Combine(paths.PluginConfigurationsPath, "MkiScreenFit", "screens.json");
		_reports = Load();
		_timer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
	}

	public void Report(Guid userId, string deviceId, int width, int height, bool reliable)
	{
		var now = DateTime.UtcNow;
		lock (_lock)
		{
			string key = Key(userId, deviceId);
			bool changed = !_reports.TryGetValue(key, out var old) || old.Width != width || old.Height != height || old.Reliable != reliable;
			_reports[key] = new ScreenReport { UserId = userId, DeviceId = deviceId, Width = width, Height = height, Reliable = reliable, At = now };
			if (_reports.Count > MaxEntries)
			{
				foreach (var gone in _reports.OrderBy(r => r.Value.At).Take(_reports.Count - MaxEntries).Select(r => r.Key).ToList())
				{
					_reports.Remove(gone);
				}
			}
			// Only the date changed: refresh the file at most once an hour.
			if (changed || now - _lastSave > TimeSpan.FromHours(1))
			{
				_timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
			}
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

	// Every report for a device (any user), newest first, for the settings page.
	public List<ScreenReport> ForDevice(string deviceId)
	{
		lock (_lock)
		{
			return _reports.Values.Where(r => string.Equals(r.DeviceId, deviceId, StringComparison.Ordinal))
				.OrderByDescending(r => r.At).Select(r => r.Copy()).ToList();
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
		_timer.Dispose();
		Save();
		GC.SuppressFinalize(this);
	}

	private static string Key(Guid userId, string deviceId) => userId.ToString("N") + "|" + deviceId;

	private Dictionary<string, ScreenReport> Load()
	{
		try
		{
			if (File.Exists(_file))
			{
				var list = JsonSerializer.Deserialize<List<ScreenReport>>(File.ReadAllText(_file));
				if (list != null)
				{
					return list.Where(r => r != null && !string.IsNullOrEmpty(r.DeviceId) && ScreenRules.ValidSize(r.Width, r.Height))
						.OrderByDescending(r => r.At).Take(MaxEntries)
						.GroupBy(r => Key(r.UserId, r.DeviceId)).ToDictionary(g => g.Key, g => g.First());
				}
			}
		}
		catch (Exception ex)
		{
			// Only measurements: the next page load of each web client reports them again.
			_logger.LogWarning(ex, "MKI Screen Fit could not read the saved screen sizes; browsers report them again on their next visit");
		}
		return new Dictionary<string, ScreenReport>();
	}

	private void Save()
	{
		List<ScreenReport> copy;
		lock (_lock)
		{
			copy = _reports.Values.Select(r => r.Copy()).ToList();
			_lastSave = DateTime.UtcNow;
		}
		lock (_saveLock)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
				string tmp = _file + ".tmp";
				File.WriteAllText(tmp, JsonSerializer.Serialize(copy));
				File.Move(tmp, _file, overwrite: true);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "MKI Screen Fit could not save the screen sizes");
			}
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
