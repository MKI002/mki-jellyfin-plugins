using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiMergeVersions;

public class MergeService
{
	public sealed record Ergebnis(int Gruppen, int Zusammengefuehrt, int Umgebaut, int Fehler);

	// More versions than this under one id is a metadata mistake (or someone planting ids), not a real movie.
	private const int MaxVersionen = 8;

	// The scheduled task, its triggers and the quiet watcher all write the same links: one run at a time.
	private static readonly SemaphoreSlim Lauf = new SemaphoreSlim(1, 1);

	private readonly ILibraryManager _library;

	private readonly IUserManager _users;

	private readonly IUserDataManager _userData;

	private readonly ILogger _log;

	public MergeService(ILibraryManager library, IUserManager users, IUserDataManager userData, ILogger log)
	{
		_library = library;
		_users = users;
		_userData = userData;
		_log = log;
	}

	private List<Video> AlleFilme()
	{
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Expected O, but got Unknown
		ILibraryManager library = _library;
		InternalItemsQuery val = new InternalItemsQuery();
		val.IncludeItemTypes = (BaseItemKind[])(object)new BaseItemKind[1] { (BaseItemKind)13 };
		val.IsVirtualItem = false;
		val.Recursive = true;
		// Without this Jellyfin leaves out every version that points to a primary entry in the same library, and a
		// stale link there could never be found. Local versions stay out: they have an owner.
		val.IncludeAlternateVersions = true;
		return library.GetItemList(val).OfType<Video>().ToList();
	}

	private static string? Schluessel(BaseItem item)
	{
		string providerId = ProviderIdsExtensions.GetProviderId((IHasProviderIds)(object)item, (MetadataProvider)3);
		if (!string.IsNullOrWhiteSpace(providerId))
		{
			return "tmdb:" + providerId;
		}
		string providerId2 = ProviderIdsExtensions.GetProviderId((IHasProviderIds)(object)item, (MetadataProvider)2);
		if (!string.IsNullOrWhiteSpace(providerId2))
		{
			return "imdb:" + providerId2;
		}
		return null;
	}

	private static string Jahr(Video v)
	{
		return ((BaseItem)v).ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? "?";
	}

	private static int Breite(BaseItem v)
	{
		return v.Width;
	}

	private const int FullHdBreite = 1920;

	// The versions in the order they should lead, the chosen one first. A file Jellyfin has not measured yet (width 0)
	// never leads while a measured one exists. The sort is stable: on a tie the current primary entry (passed first)
	// stays, so two runs never swap equal versions back and forth.
	private static List<Video> NachWahl(IEnumerable<Video> versionen, PrimaryChoice wahl)
	{
		IOrderedEnumerable<Video> sortiert = versionen.OrderBy((Video v) => Breite((BaseItem)(object)v) > 0 ? 0 : 1);
		if (wahl == PrimaryChoice.FullHd)
		{
			// Closest to 1920 px across, measured as a ratio: 720p (1.5x smaller) beats 4K (2x larger), but 4K beats a
			// DVD (2.7x smaller). At the same distance the higher resolution wins.
			sortiert = sortiert.ThenBy((Video v) => Breite((BaseItem)(object)v) > 0 ? Math.Abs(Math.Log((double)Breite((BaseItem)(object)v) / FullHdBreite)) : double.MaxValue);
		}
		return sortiert.ThenByDescending((Video v) => Breite((BaseItem)(object)v)).ToList();
	}

	private static string WahlText(PrimaryChoice wahl)
	{
		return wahl == PrimaryChoice.FullHd ? "closest to 1080p" : "highest resolution";
	}

	// Who may open this entry: one mark per user, from Jellyfin's own check (libraries, parental rating, tags).
	// Versions are only ever linked when this is the same for all of them. A linked version shows up in the
	// other's played state and media sources, so a user could otherwise reach a file they are not allowed to see,
	// e.g. a 4K copy in a library only adults can open.
	private string Sichtbarkeit(BaseItem item, IReadOnlyList<User> nutzer)
	{
		StringBuilder sb = new StringBuilder(nutzer.Count);
		foreach (User user in nutzer)
		{
			bool sichtbar;
			try
			{
				sichtbar = item.IsVisibleStandalone(user);
			}
			catch (Exception)
			{
				// Unknown counts as different from everything, so nothing is linked on a guess.
				return "?" + item.Id.ToString("N");
			}
			sb.Append(sichtbar ? '1' : '0');
		}
		return sb.ToString();
	}

	public async Task<Ergebnis?> RunAsync(bool dryRun, bool fixPrimaryOrder, PrimaryChoice wahl, IProgress<double>? progress, bool warten, CancellationToken ct)
	{
		if (warten)
		{
			await Lauf.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		else if (!(await Lauf.WaitAsync(0, ct).ConfigureAwait(continueOnCapturedContext: false)))
		{
			return null;
		}
		try
		{
			return await LaufenAsync(dryRun, fixPrimaryOrder, wahl, progress, ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			Lauf.Release();
		}
	}

	private async Task<Ergebnis> LaufenAsync(bool dryRun, bool fixPrimaryOrder, PrimaryChoice wahl, IProgress<double>? progress, CancellationToken ct)
	{
		List<Video> filme = AlleFilme();
		List<User> nutzer = _users.GetUsers().OrderBy((User u) => u.Id).ToList();
		LoggerExtensions.LogInformation(_log, "MKI Merge: read {Count} movie entries, primary version: {Choice}", new object[2] { filme.Count, WahlText(wahl) });
		int zusammen = 0;
		int umgebaut = 0;
		int fehler = 0;

		// Existing links first: versions that no longer share the same audience are taken apart again.
		int getrennt = 0;
		foreach (Video haupt in filme.Where((Video x) => !x.PrimaryVersionId.HasValue && x.LinkedAlternateVersions.Length != 0).ToList())
		{
			ct.ThrowIfCancellationRequested();
			try
			{
				if (await TrennenAsync(haupt, nutzer, dryRun, ct).ConfigureAwait(continueOnCapturedContext: false))
				{
					getrennt++;
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				fehler++;
				LoggerExtensions.LogError(_log, ex, "MKI Merge: checking the versions of {Title} failed", new object[1] { ((BaseItem)haupt).Name });
			}
		}
		fehler += await WaisenAsync(filme, dryRun, ct).ConfigureAwait(continueOnCapturedContext: false);
		if (getrennt > 0 && !dryRun)
		{
			filme = AlleFilme();
		}

		Dictionary<string, List<Video>> dictionary = new Dictionary<string, List<Video>>(StringComparer.Ordinal);
		foreach (Video item in filme.Where((Video x) => !x.PrimaryVersionId.HasValue))
		{
			string text = Schluessel((BaseItem)(object)item);
			if (text != null)
			{
				if (!dictionary.TryGetValue(text, out var value))
				{
					value = (dictionary[text] = new List<Video>());
				}
				value.Add(item);
			}
		}
		List<List<Video>> arbeit = new List<List<Video>>();
		foreach (KeyValuePair<string, List<Video>> gruppe in dictionary.Where((KeyValuePair<string, List<Video>> g) => g.Value.Count > 1))
		{
			if (gruppe.Value.Count > MaxVersionen)
			{
				LoggerExtensions.LogWarning(_log, "MKI Merge: {Count} entries share {Key} ({Title}) - skipped, check their metadata", new object[3]
				{
					gruppe.Value.Count,
					gruppe.Key,
					((BaseItem)gruppe.Value[0]).Name
				});
				continue;
			}
			// Same audience and the same production year; otherwise the entries stay separate. An entry without a year
			// only joins others without one: a wrong id is likelier than a missing year on a matched movie.
			Dictionary<Guid, string> publikum = gruppe.Value.ToDictionary((Video v) => ((BaseItem)v).Id, (Video v) => Sichtbarkeit(v, nutzer));
			foreach (IGrouping<string, Video> teil in gruppe.Value.GroupBy((Video v) => publikum[((BaseItem)v).Id] + "|" + Jahr(v)))
			{
				if (teil.Count() > 1)
				{
					arbeit.Add(teil.ToList());
				}
			}
			if (publikum.Values.Distinct().Count() > 1)
			{
				LoggerExtensions.LogInformation(_log, "MKI Merge: {Title} - not every user may see every version, those versions stay separate", new object[1] { ((BaseItem)gruppe.Value[0]).Name });
			}
			if (gruppe.Value.Select(Jahr).Distinct().Count() > 1)
			{
				LoggerExtensions.LogInformation(_log, "MKI Merge: {Title} - the versions have different production years (or none), those versions stay separate", new object[1] { ((BaseItem)gruppe.Value[0]).Name });
			}
		}
		LoggerExtensions.LogInformation(_log, "MKI Merge: {Count} groups to merge", new object[1] { arbeit.Count });
		for (int i = 0; i < arbeit.Count; i++)
		{
			ct.ThrowIfCancellationRequested();
			progress?.Report(50.0 * (double)i / (double)Math.Max(arbeit.Count, 1));
			try
			{
				List<Video> list2 = arbeit[i];
				if (await UmbauenAsync(list2, NachWahl(list2, wahl)[0], dryRun, ct).ConfigureAwait(continueOnCapturedContext: false))
				{
					zusammen++;
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				fehler++;
				LoggerExtensions.LogError(_log, ex, "MKI Merge: {Title} failed", new object[1] { ((BaseItem)arbeit[i][0]).Name });
			}
		}
		if (fixPrimaryOrder)
		{
			if (zusammen > 0 && !dryRun)
			{
				filme = AlleFilme();
			}
			int i = 0;
			foreach (Video haupt in filme.Where((Video x) => !x.PrimaryVersionId.HasValue))
			{
				ct.ThrowIfCancellationRequested();
				progress?.Report(50.0 + 50.0 * (double)i++ / (double)Math.Max(filme.Count, 1));
				List<Video> list3 = Verknuepfte(haupt);
				if (list3.Count == 0)
				{
					continue;
				}
				List<Video> list4 = new List<Video> { haupt };
				list4.AddRange(list3);
				Video val = NachWahl(list4, wahl)[0];
				if (((BaseItem)val).Id.Equals(((BaseItem)haupt).Id))
				{
					continue;
				}
				// A rebuild hands every version to the new primary entry, so the group must still pass the same checks as a
				// new merge: same audience (an unknown answer counts as different) and the same production year.
				if (list4.Select((Video v) => Sichtbarkeit((BaseItem)(object)v, nutzer)).Distinct().Count() > 1 || list4.Select(Jahr).Distinct().Count() > 1)
				{
					LoggerExtensions.LogInformation(_log, "MKI Merge: {Title} - not every user may see every version, or the years differ; primary entry left as it is", new object[1] { ((BaseItem)haupt).Name });
					continue;
				}
				LoggerExtensions.LogInformation(_log, "MKI Merge: {Title} - primary entry is {Old}px, the {Choice} is {New}px. Rebuilding.", new object[4]
				{
					((BaseItem)haupt).Name,
					Breite((BaseItem)(object)haupt),
					WahlText(wahl),
					Breite((BaseItem)(object)val)
				});
				try
				{
					if (await UmbauenAsync(list4, val, dryRun, ct).ConfigureAwait(continueOnCapturedContext: false))
					{
						umgebaut++;
					}
				}
				catch (Exception ex2) when (ex2 is not OperationCanceledException)
				{
					fehler++;
					LoggerExtensions.LogError(_log, ex2, "MKI Merge: rebuilding {Title} failed", new object[1] { ((BaseItem)haupt).Name });
				}
			}
		}
		progress?.Report(100.0);
		LoggerExtensions.LogInformation(_log, "MKI Merge done: {Merged} merged, {Rebuilt} rebuilt, {Split} split, {Errors} errors{DryRun}", new object[5]
		{
			zusammen,
			umgebaut,
			getrennt,
			fehler,
			dryRun ? " (dry run, nothing written)" : string.Empty
		});
		return new Ergebnis(arbeit.Count, zusammen, umgebaut, fehler);
	}

	private List<Video> Verknuepfte(Video haupt)
	{
		return (from v in haupt.LinkedAlternateVersions.Where((LinkedChild l) => l.ItemId.HasValue && !l.ItemId.Value.Equals(((BaseItem)haupt).Id)).Select(delegate(LinkedChild l)
			{
				BaseItem itemById = _library.GetItemById(l.ItemId.Value);
				return (Video)(object)((itemById is Video) ? itemById : null);
			})
			where v != null
			select (v)).ToList();
	}

	// Removes linked versions whose audience differs from the primary entry's. Local versions (several files in
	// one folder, which Jellyfin links itself) are not touched.
	private async Task<bool> TrennenAsync(Video haupt, IReadOnlyList<User> nutzer, bool dryRun, CancellationToken ct)
	{
		string soll = Sichtbarkeit((BaseItem)(object)haupt, nutzer);
		List<Video> weg = Verknuepfte(haupt).Where((Video v) => Sichtbarkeit((BaseItem)(object)v, nutzer) != soll).ToList();
		if (weg.Count == 0)
		{
			return false;
		}
		LoggerExtensions.LogInformation(_log, "MKI Merge: {Title} - {Count} linked version(s) are not visible to the same users, unlinking them", new object[2]
		{
			((BaseItem)haupt).Name,
			weg.Count
		});
		if (dryRun)
		{
			return true;
		}
		HashSet<Guid> wegIds = weg.Select((Video v) => ((BaseItem)v).Id).ToHashSet();
		haupt = Frisch(haupt);
		haupt.LinkedAlternateVersions = haupt.LinkedAlternateVersions.Where((LinkedChild l) => !l.ItemId.HasValue || !wegIds.Contains(l.ItemId.Value)).ToArray();
		await ((BaseItem)haupt).UpdateToRepositoryAsync((ItemUpdateType)16, ct).ConfigureAwait(continueOnCapturedContext: false);
		foreach (Video v in weg)
		{
			Video item = Frisch(v);
			if (item.PrimaryVersionId.HasValue && item.PrimaryVersionId.Value.Equals(((BaseItem)haupt).Id))
			{
				item.SetPrimaryVersionId((Guid?)null);
				await ((BaseItem)item).UpdateToRepositoryAsync((ItemUpdateType)16, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		return true;
	}

	// An entry that points to a primary entry which does not list it (or no longer exists) is hidden from the
	// library or never merged again. That happens when a run is interrupted halfway, or after Jellyfin replaced a
	// primary file. Such entries are released again; the merge below links them anew where that is allowed.
	private async Task<int> WaisenAsync(List<Video> filme, bool dryRun, CancellationToken ct)
	{
		int fehler = 0;
		foreach (Video v in filme.Where((Video x) => x.PrimaryVersionId.HasValue).ToList())
		{
			ct.ThrowIfCancellationRequested();
			try
			{
				// The list was read before the links were checked; work with the current state only.
				Video item = Frisch(v);
				if (!item.PrimaryVersionId.HasValue)
				{
					continue;
				}
				BaseItem itemById = _library.GetItemById(item.PrimaryVersionId.Value);
				Video? haupt = (Video?)(object)((itemById is Video) ? itemById : null);
				Guid id = ((BaseItem)item).Id;
				if (haupt != null && (haupt.LinkedAlternateVersions.Any((LinkedChild l) => l.ItemId.HasValue && l.ItemId.Value.Equals(id)) || _library.GetLocalAlternateVersionIds(haupt).Contains(id)))
				{
					continue;
				}
				LoggerExtensions.LogInformation(_log, "MKI Merge: {Title} pointed to a primary entry that does not list it, showing it again", new object[1] { ((BaseItem)item).Name });
				if (!dryRun)
				{
					item.SetPrimaryVersionId((Guid?)null);
					await ((BaseItem)item).UpdateToRepositoryAsync((ItemUpdateType)16, ct).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				fehler++;
				LoggerExtensions.LogError(_log, ex, "MKI Merge: checking {Title} failed", new object[1] { ((BaseItem)v).Name });
			}
		}
		return fehler;
	}

	private Video Frisch(Video v)
	{
		BaseItem itemById = _library.GetItemById(((BaseItem)v).Id);
		return (Video)(((object)((itemById is Video) ? itemById : null)) ?? ((object)v));
	}

	// Jellyfin keeps favourite, rating and like per entry and shows the primary entry's (played state and the resume
	// point are already read across all versions). So a new primary entry takes these over from the entries it
	// replaces; otherwise a switch from 4K to 1080p would seem to drop every favourite. Only ever adds: a favourite
	// stays a favourite, and a rating or like the new primary entry already has is kept. Nothing is removed from the
	// former entries, so switching back finds them as they were.
	private void MitnehmenNutzerdaten(Video haupt, List<Video> andere)
	{
		foreach (User user in _users.GetUsers())
		{
			try
			{
				UserItemData? ziel = _userData.GetUserData(user, (BaseItem)(object)haupt);
				if (ziel == null)
				{
					continue;
				}
				bool geaendert = false;
				foreach (Video v in andere)
				{
					UserItemData? quelle = _userData.GetUserData(user, (BaseItem)(object)v);
					if (quelle == null)
					{
						continue;
					}
					if (quelle.IsFavorite && !ziel.IsFavorite)
					{
						ziel.IsFavorite = true;
						geaendert = true;
					}
					if (quelle.Rating.HasValue && !ziel.Rating.HasValue)
					{
						ziel.Rating = quelle.Rating;
						geaendert = true;
					}
					if (quelle.Likes.HasValue && !ziel.Likes.HasValue)
					{
						ziel.Likes = quelle.Likes;
						geaendert = true;
					}
				}
				if (geaendert)
				{
					_userData.SaveUserData(user, (BaseItem)(object)haupt, ziel, UserDataSaveReason.UpdateUserRating, CancellationToken.None);
				}
			}
			catch (Exception ex)
			{
				LoggerExtensions.LogWarning(_log, ex, "MKI Merge: {Title} - could not carry over favourites and ratings for one user", new object[1] { ((BaseItem)haupt).Name });
			}
		}
	}

	private async Task<bool> UmbauenAsync(List<Video> alle, Video haupt, bool dryRun, CancellationToken ct)
	{
		List<Video> andere = alle.Where((Video x) => !((BaseItem)x).Id.Equals(((BaseItem)haupt).Id)).ToList();
		if (andere.Count == 0)
		{
			return false;
		}
		string text = string.Join(" + ", from x in alle.OrderByDescending(Breite)
			select Breite((BaseItem)(object)x).ToString(CultureInfo.InvariantCulture) + "px");
		LoggerExtensions.LogInformation(_log, "MKI Merge: {Title} [{Resolutions}] -> primary entry {Width}px", new object[3]
		{
			((BaseItem)haupt).Name,
			text,
			Breite((BaseItem)(object)haupt)
		});
		if (dryRun)
		{
			return true;
		}
		haupt = Frisch(haupt);
		Guid hauptId = ((BaseItem)haupt).Id;
		// The full list of versions is worked out before anything is written. A version never lists itself.
		List<LinkedChild> nebenversionen = haupt.LinkedAlternateVersions.Where((LinkedChild l) => !l.ItemId.HasValue || !l.ItemId.Value.Equals(hauptId)).ToList();
		List<Video> frische = andere.Select(Frisch).ToList();
		foreach (Video item in frische)
		{
			Dazu(((BaseItem)item).Id);
			foreach (LinkedChild val in item.LinkedAlternateVersions)
			{
				if (val.ItemId.HasValue)
				{
					Dazu(val.ItemId.Value);
				}
			}
		}
		// The primary entry is written first: saving it already points every listed version to it in the database.
		// The loop below brings the cached entries in line and clears lists the former primary entries kept.
		if (haupt.PrimaryVersionId.HasValue)
		{
			haupt.SetPrimaryVersionId((Guid?)null);
		}
		haupt.LinkedAlternateVersions = nebenversionen.ToArray();
		await ((BaseItem)haupt).UpdateToRepositoryAsync((ItemUpdateType)16, ct).ConfigureAwait(continueOnCapturedContext: false);
		// Every listed version points to the new primary entry, including versions taken over from a former one.
		foreach (LinkedChild l in nebenversionen.Where((LinkedChild x) => x.ItemId.HasValue).ToList())
		{
			BaseItem itemById = _library.GetItemById(l.ItemId!.Value);
			Video? item = (Video?)(object)((itemById is Video) ? itemById : null);
			if (item == null || (item.PrimaryVersionId.HasValue && item.PrimaryVersionId.Value.Equals(hauptId) && item.LinkedAlternateVersions.Length == 0))
			{
				continue;
			}
			item.SetPrimaryVersionId((Guid?)hauptId);
			item.LinkedAlternateVersions = Array.Empty<LinkedChild>();
			await ((BaseItem)item).UpdateToRepositoryAsync((ItemUpdateType)16, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
		}
		MitnehmenNutzerdaten(haupt, frische);
		return true;
		void Dazu(Guid id)
		{
			if (!id.Equals(hauptId) && !nebenversionen.Any((LinkedChild l) => l.ItemId.HasValue && l.ItemId.Value.Equals(id)))
			{
				nebenversionen.Add(new LinkedChild
				{
					ItemId = id
				});
			}
		}
	}
}
