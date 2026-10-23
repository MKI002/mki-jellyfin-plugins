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

	// Device, client and user names come from the player's own request headers: kept short.
	private const int MaxName = 100;

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
			if (_entries.Any(e => e.Kind == d.Kind && e.UserId == d.UserId && e.DeviceId == d.DeviceId && e.ItemId == d.ItemId && now - e.At < Repeat))
			{
				return false;
			}
			d.At = now;
			d.DeviceId = Cut(d.DeviceId);
			d.DeviceName = Cut(d.DeviceName);
			d.Client = Cut(d.Client);
			d.UserName = Cut(d.UserName);
			d.ItemName = Cut(d.ItemName);
			_entries.AddFirst(d);
			while (_entries.Count > MaxEntries)
			{
				_entries.RemoveLast();
			}
			switch (d.Kind)
			{
				case "version": _versions++; break;
				case "limit": _limits++; break;
				case "kept": _kept++; break;
			}
			return true;
		}
	}

	private static string Cut(string text) => text.Length > MaxName ? text[..(MaxName - 1)] + "…" : text;

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

	// "version" (played the Full HD version), "restore" (the default version again after a swap elsewhere), "limit"
	// (transcode limited to the screen) or "kept" (default version stays).
	public string Kind { get; set; } = string.Empty;

	public string DeviceId { get; set; } = string.Empty;

	public string DeviceName { get; set; } = string.Empty;

	public string Client { get; set; } = string.Empty;

	[System.Text.Json.Serialization.JsonIgnore]
	public Guid UserId { get; set; }

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
