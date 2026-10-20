using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiContinueWatching.Services;

// Continue Watching in Jellyfin is every video with a saved resume position that is not marked as played. The
// plugin takes an item off that list the only way Jellyfin offers: it sets the resume position back to zero.
// Played state, play count, last played date, favourites and ratings stay exactly as they were, and no file or
// library entry is ever touched. Each change is recorded in ClearedStore so it can be undone.
public class ResumeService
{
	// A movie with several versions keeps its resume position on the version that was played. Clearing goes
	// through at most this many versions of one item.
	private const int MaxVersions = 16;

	private readonly object _runLock = new();

	private readonly ILibraryManager _library;

	private readonly IUserManager _users;

	private readonly IUserDataManager _userData;

	private readonly ClearedStore _store;

	private readonly ILogger<ResumeService> _logger;

	public ResumeService(ILibraryManager library, IUserManager users, IUserDataManager userData, ClearedStore store, ILogger<ResumeService> logger)
	{
		_library = library;
		_users = users;
		_userData = userData;
		_store = store;
		_logger = logger;
	}

	public ClearedStore Store => _store;

	// The user's own Continue Watching videos: what the web client shows in that row. A movie with several
	// versions keeps its resume position on the version that was played, and Jellyfin's queries leave such
	// versions out unless asked for owned items, so they are fetched separately (extras are not included).
	// Each item is loaded again by id: Jellyfin caches user data per item it hands out by id, and a change saved
	// through a query result is not seen by the rest of the server until it restarts.
	public List<BaseItem> Resumable(User user)
	{
		var top = _library.GetItemList(new InternalItemsQuery(user)
		{
			IsResumable = true,
			Recursive = true,
			MediaTypes = new[] { MediaType.Video },
			IsVirtualItem = false
		});
		var versions = _library.GetItemList(new InternalItemsQuery(user)
		{
			IsResumable = true,
			Recursive = true,
			MediaTypes = new[] { MediaType.Video },
			IsVirtualItem = false,
			HasOwnerId = true,
			IncludeAlternateVersions = true
		}).Where(i => i is Video v && v.ExtraType == null && v.PrimaryVersionId.HasValue);
		return top.Concat(versions)
			.Select(i => i.Id)
			.Distinct()
			.Select(id => _library.GetItemById(id))
			.Where(i => i != null && i.IsVisibleStandalone(user))
			.Select(i => i!)
			.ToList();
	}

	// Removes one item from the user's own Continue Watching. Returns null when the user cannot see the item.
	public int? Remove(User user, Guid itemId)
	{
		var item = _library.GetItemById(itemId);
		if (item == null || !item.IsVisibleStandalone(user))
		{
			return null;
		}
		var now = DateTime.UtcNow;
		var cleared = Clear(user, item, itemId, now, manual: true, runId: null);
		if (cleared.Count > 0)
		{
			_store.Add(cleared);
			_logger.LogInformation("MKI Continue Watching: {User} removed {Item} from Continue Watching", user.Username, item.Name);
		}
		return cleared.Count;
	}

	// Puts back what the user's last removal of this item cleared, unless the item was played again since.
	public int Restore(User user, Guid itemId)
	{
		var entries = _store.LatestManual(user.Id, itemId);
		int restored = RestoreEntries(entries, u => u == user.Id ? user : null);
		_store.MarkRestored(entries);
		return restored;
	}

	// Counts per user what a cleanup with the given days would clear right now. Changes nothing.
	public List<UserPreview> Preview(int days)
	{
		var cutoff = DateTime.UtcNow.AddDays(-days);
		var result = new List<UserPreview>();
		foreach (var user in _users.GetUsers().OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase))
		{
			int total = 0, stale = 0;
			foreach (var item in Resumable(user))
			{
				var data = _userData.GetUserData(user, item);
				if (data == null || data.PlaybackPositionTicks <= 0 || data.Played)
				{
					continue;
				}
				total++;
				if (IsStale(data, cutoff))
				{
					stale++;
				}
			}
			result.Add(new UserPreview { UserId = user.Id, Name = user.Username, Total = total, Stale = stale });
		}
		return result;
	}

	// The cleanup: for every user, clears the resume position of videos they have not played for the set number of
	// days. Does nothing unless an administrator turned auto cleanup on.
	public CleanupRun? RunCleanup(string trigger, CancellationToken cancellationToken, IProgress<double>? progress = null)
	{
		if (!Plugin.Config.EnableAutoCleanup)
		{
			_logger.LogInformation("MKI Continue Watching: auto cleanup is off, nothing was changed");
			return null;
		}
		lock (_runLock)
		{
			int days = Plugin.Days;
			var cutoff = DateTime.UtcNow.AddDays(-days);
			var run = new CleanupRun { Id = Guid.NewGuid(), At = DateTime.UtcNow, Days = days, Trigger = trigger };
			var all = new List<ClearedEntry>();
			var users = _users.GetUsers().ToList();
			for (int i = 0; i < users.Count; i++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var user = users[i];
				int before = all.Count;
				foreach (var item in Resumable(user))
				{
					var data = _userData.GetUserData(user, item);
					if (data == null || data.PlaybackPositionTicks <= 0 || data.Played || !IsStale(data, cutoff))
					{
						continue;
					}
					all.AddRange(Clear(user, item, item.Id, run.At, manual: false, runId: run.Id, onlyOlderThan: cutoff));
				}
				if (all.Count > before)
				{
					run.Users++;
				}
				progress?.Report(100.0 * (i + 1) / Math.Max(1, users.Count));
			}
			run.Cleared = all.Count;
			_store.Add(all, run);
			_logger.LogInformation("MKI Continue Watching: cleanup cleared {Count} items for {Users} users (not played for {Days} days)", run.Cleared, run.Users, days);
			return run;
		}
	}

	// Puts back everything a cleanup run cleared that nobody played since.
	public int UndoRun(Guid runId)
	{
		lock (_runLock)
		{
			var entries = _store.OfRun(runId);
			int restored = RestoreEntries(entries, id => _users.GetUserById(id));
			_store.MarkRestored(entries, runId, restored);
			_logger.LogInformation("MKI Continue Watching: undo of a cleanup run put back {Count} items", restored);
			return restored;
		}
	}

	// Never played (no date): unknown age, so it stays. That is the safe side.
	private static bool IsStale(UserItemData data, DateTime cutoff) => data.LastPlayedDate.HasValue && data.LastPlayedDate.Value < cutoff;

	private List<ClearedEntry> Clear(User user, BaseItem item, Guid clickedId, DateTime at, bool manual, Guid? runId, DateTime? onlyOlderThan = null)
	{
		var cleared = new List<ClearedEntry>();
		var seen = new HashSet<Guid>();
		var target = item;
		for (int i = 0; i < MaxVersions && target != null && seen.Add(target.Id); i++)
		{
			var data = _userData.GetUserData(user, target);
			if (data != null && data.PlaybackPositionTicks > 0 && !data.Played
				&& (onlyOlderThan == null || IsStale(data, onlyOlderThan.Value)))
			{
				cleared.Add(new ClearedEntry
				{
					UserId = user.Id,
					ItemId = target.Id,
					ClickedItemId = clickedId,
					PositionTicks = data.PlaybackPositionTicks,
					LastPlayedDate = data.LastPlayedDate,
					ClearedAt = at,
					Manual = manual,
					RunId = runId
				});
				// Only the resume position changes; every other field of the user data is left as it is.
				_userData.SaveUserData(user, target, new UpdateUserItemDataDto { PlaybackPositionTicks = 0 }, UserDataSaveReason.UpdateUserData);
			}
			// The next version that still has a resume position for this user, if any.
			var resume = _userData.GetResumeUserData(user, item);
			target = resume != null && resume.UserData.PlaybackPositionTicks > 0 && !seen.Contains(resume.VersionId)
				? _library.GetItemById(resume.VersionId)
				: null;
		}
		return cleared;
	}

	private int RestoreEntries(List<ClearedEntry> entries, Func<Guid, User?> userOf)
	{
		int restored = 0;
		foreach (var e in entries)
		{
			var user = userOf(e.UserId);
			var item = user == null ? null : _library.GetItemById(e.ItemId);
			if (user == null || item == null || !item.IsVisibleStandalone(user))
			{
				continue;
			}
			var data = _userData.GetUserData(user, item);
			// Played again, marked as played or resumed elsewhere since: the newer state wins.
			if (data == null || data.PlaybackPositionTicks != 0 || data.Played || data.LastPlayedDate != e.LastPlayedDate)
			{
				continue;
			}
			_userData.SaveUserData(user, item, new UpdateUserItemDataDto { PlaybackPositionTicks = e.PositionTicks }, UserDataSaveReason.UpdateUserData);
			restored++;
		}
		return restored;
	}
}

public class UserPreview
{
	public Guid UserId { get; set; }

	public string Name { get; set; } = string.Empty;

	public int Total { get; set; }

	public int Stale { get; set; }
}
