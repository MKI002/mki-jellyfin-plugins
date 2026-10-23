using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MkiScreenFit.Services;

// What the plugin decided recently, for the settings page (administrators only). Kept in memory: it starts empty
// when Jellyfin starts. Clients ask for playback information several times per start, so the same decision for
// the same device and item is written once per few minutes.
public class ActivityLog
{
	public const int MaxEntries = 100;

	private static readonly TimeSpan Repeat = TimeSpan.FromMinutes(5);

	private readonly object _lock = new();

	private readonly LinkedList<Decision> _entries = new();

	private readonly DateTime _since = DateTime.UtcNow;

	private int _versions;

	private int _limits;

	private int _kept;

	public DateTime Since => _since;

	// False when the same decision was just written (the caller then does not log it again).
	public bool Add(Decision d)
	{
		lock (_lock)
		{
			var now = DateTime.UtcNow;
			if (_entries.Any(e => e.Kind == d.Kind && e.DeviceId == d.DeviceId && e.ItemId == d.ItemId && now - e.At < Repeat))
			{
				return false;
			}
			d.At = now;
			_entries.AddFirst(d);
			while (_entries.Count > MaxEntries)
			{
				_entries.RemoveLast();
			}
			switch (d.Kind)
			{
				case "version": _versions++; break;
				case "limit": _limits++; break;
				default: _kept++; break;
			}
			return true;
		}
	}

	public (List<Decision> Entries, int Versions, int Limits, int Kept) Snapshot()
	{
		lock (_lock)
		{
			return (_entries.Select(e => e.Copy()).ToList(), _versions, _limits, _kept);
		}
	}
}

public class Decision
{
	public DateTime At { get; set; }

	// "version" (played the Full HD version), "limit" (transcode limited to the screen) or "kept" (default version stays).
	public string Kind { get; set; } = string.Empty;

	public string DeviceId { get; set; } = string.Empty;

	public string DeviceName { get; set; } = string.Empty;

	public string Client { get; set; } = string.Empty;

	public string UserName { get; set; } = string.Empty;

	public Guid ItemId { get; set; }

	public string ItemName { get; set; } = string.Empty;

	public string From { get; set; } = string.Empty;

	public string To { get; set; } = string.Empty;

	public string Reason { get; set; } = string.Empty;

	// Where the screen size came from: "reported" by the web client, or the administrator's "rule".
	public string Screen { get; set; } = string.Empty;

	public Decision Copy() => (Decision)MemberwiseClone();
}
