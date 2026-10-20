using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiContinueWatching.Services;

// What the plugin cleared, so a removal or a whole cleanup run can be undone. Holds item and user ids, the old
// resume position and dates; nothing else. Entries are kept for KeepDays and capped, per user for removals, so
// one user's clicks can never push the administrator's undo data for a cleanup run out of the file.
public class ClearedStore
{
	public const int KeepDays = 30;

	private const int MaxManualPerUser = 200;

	public const int MaxAutoEntries = 50000;

	private const int MaxRuns = 100;

	private readonly object _lock = new();

	private readonly string _file;

	private readonly ILogger<ClearedStore> _logger;

	private Data _data;

	public ClearedStore(IApplicationPaths paths, ILogger<ClearedStore> logger)
	{
		_logger = logger;
		string folder = Path.Combine(paths.PluginConfigurationsPath, "MkiContinueWatching");
		_file = Path.Combine(folder, "cleared.json");
		_data = Load();
	}

	public void Add(IEnumerable<ClearedEntry> entries, CleanupRun? run = null)
	{
		lock (_lock)
		{
			_data.Entries.AddRange(entries);
			if (run != null)
			{
				_data.Runs.Insert(0, run);
			}
			Prune();
			Save();
		}
	}

	// The newest removal of this item by this user that has not been put back yet.
	public List<ClearedEntry> LatestManual(Guid userId, Guid itemId)
	{
		lock (_lock)
		{
			var latest = _data.Entries.Where(e => e.Manual && !e.Restored && e.UserId == userId && e.ClickedItemId == itemId)
				.OrderByDescending(e => e.ClearedAt).FirstOrDefault();
			return latest == null
				? new List<ClearedEntry>()
				: _data.Entries.Where(e => e.Manual && !e.Restored && e.UserId == userId && e.ClickedItemId == itemId && e.ClearedAt == latest.ClearedAt).ToList();
		}
	}

	public List<ClearedEntry> OfRun(Guid runId)
	{
		lock (_lock)
		{
			return _data.Entries.Where(e => !e.Manual && !e.Restored && e.RunId == runId).ToList();
		}
	}

	public void MarkRestored(IEnumerable<ClearedEntry> entries, Guid? runId = null, int restoredCount = 0)
	{
		var list = entries.ToList();
		if (list.Count == 0 && runId == null)
		{
			return;
		}
		lock (_lock)
		{
			foreach (var e in list)
			{
				e.Restored = true;
			}
			var run = runId == null ? null : _data.Runs.FirstOrDefault(r => r.Id == runId);
			if (run != null)
			{
				run.UndoneAt = DateTime.UtcNow;
				run.Restored = restoredCount;
			}
			Save();
		}
	}

	public List<CleanupRun> Runs()
	{
		lock (_lock)
		{
			return _data.Runs.ToList();
		}
	}

	public int ManualSince(DateTime seit)
	{
		lock (_lock)
		{
			return _data.Entries.Count(e => e.Manual && e.ClearedAt >= seit);
		}
	}

	private void Prune()
	{
		var cutoff = DateTime.UtcNow.AddDays(-KeepDays);
		_data.Entries.RemoveAll(e => e.ClearedAt < cutoff);
		foreach (var group in _data.Entries.Where(e => e.Manual).GroupBy(e => e.UserId).ToList())
		{
			foreach (var old in group.OrderByDescending(e => e.ClearedAt).Skip(MaxManualPerUser).ToList())
			{
				_data.Entries.Remove(old);
			}
		}
		var auto = _data.Entries.Where(e => !e.Manual).OrderByDescending(e => e.ClearedAt).Skip(MaxAutoEntries).ToList();
		foreach (var old in auto)
		{
			_data.Entries.Remove(old);
			// The run stays listed, marked so the page can say its undo is no longer complete.
			var run = _data.Runs.FirstOrDefault(r => r.Id == old.RunId);
			if (run != null)
			{
				run.UndoIncomplete = true;
			}
		}
		if (_data.Runs.Count > MaxRuns)
		{
			_data.Runs.RemoveRange(MaxRuns, _data.Runs.Count - MaxRuns);
		}
	}

	private Data Load()
	{
		try
		{
			if (File.Exists(_file))
			{
				var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(_file));
				if (d != null)
				{
					d.Entries ??= new List<ClearedEntry>();
					d.Runs ??= new List<CleanupRun>();
					return d;
				}
			}
		}
		catch (Exception ex)
		{
			// Kept aside instead of being overwritten by the next save, so the undo data can still be recovered.
			string aside = _file + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
			try
			{
				File.Move(_file, aside);
			}
			catch (Exception)
			{
			}
			_logger.LogWarning(ex, "MKI Continue Watching Cleanup could not read its undo data; it was moved to {File} and the plugin starts with none", aside);
		}
		return new Data();
	}

	private void Save()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
			string tmp = _file + ".tmp";
			File.WriteAllText(tmp, JsonSerializer.Serialize(_data));
			File.Move(tmp, _file, overwrite: true);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "MKI Continue Watching Cleanup could not save its undo data; Undo may not work for recent changes");
		}
	}

	private sealed class Data
	{
		public List<ClearedEntry> Entries { get; set; } = new();

		public List<CleanupRun> Runs { get; set; } = new();
	}
}

public class ClearedEntry
{
	public Guid UserId { get; set; }

	// The item whose resume position was cleared (for a movie with several versions, the version that held it).
	public Guid ItemId { get; set; }

	// The card the user clicked, so Undo finds every version cleared by one click.
	public Guid ClickedItemId { get; set; }

	public long PositionTicks { get; set; }

	public DateTime? LastPlayedDate { get; set; }

	public DateTime ClearedAt { get; set; }

	public bool Manual { get; set; }

	public Guid? RunId { get; set; }

	public bool Restored { get; set; }
}

public class CleanupRun
{
	public Guid Id { get; set; }

	public DateTime At { get; set; }

	public int Days { get; set; }

	public int Cleared { get; set; }

	public int Users { get; set; }

	// "schedule" or "manual" (Run now on the settings page).
	public string Trigger { get; set; } = "schedule";

	public DateTime? UndoneAt { get; set; }

	public int Restored { get; set; }

	public bool Finished { get; set; }

	// Stopped part-way (server shut down, error): what it cleared until then is in the undo data.
	public bool Partial { get; set; }

	// More entries than the undo data holds: Undo puts back only part of the run.
	public bool UndoIncomplete { get; set; }
}
