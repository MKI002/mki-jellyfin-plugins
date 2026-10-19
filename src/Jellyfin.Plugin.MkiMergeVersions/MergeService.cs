using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiMergeVersions;

public class MergeService
{
	public sealed record Ergebnis(int Gruppen, int Zusammengefuehrt, int Umgebaut, int Fehler);

	private readonly ILibraryManager _library;

	private readonly ILogger _log;

	public MergeService(ILibraryManager library, ILogger log)
	{
		_library = library;
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

	private static int Breite(BaseItem v)
	{
		return v.Width;
	}

	public async Task<Ergebnis> RunAsync(bool dryRun, bool fixPrimaryOrder, IProgress<double>? progress, CancellationToken ct)
	{
		List<Video> filme = AlleFilme();
		LoggerExtensions.LogInformation(_log, "MKI Merge: read {Count} movie entries", new object[1] { filme.Count });
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
		int zusammen = 0;
		int umgebaut = 0;
		int fehler = 0;
		List<List<Video>> arbeit = dictionary.Values.Where((List<Video> g) => g.Count > 1).ToList();
		LoggerExtensions.LogInformation(_log, "MKI Merge: {Count} groups to merge", new object[1] { arbeit.Count });
		for (int i = 0; i < arbeit.Count; i++)
		{
			ct.ThrowIfCancellationRequested();
			progress?.Report(50.0 * (double)i / (double)Math.Max(arbeit.Count, 1));
			try
			{
				List<Video> list2 = arbeit[i];
				if (await UmbauenAsync(list2, list2.OrderByDescending(Breite).First(), dryRun, ct).ConfigureAwait(continueOnCapturedContext: false))
				{
					zusammen++;
				}
			}
			catch (Exception ex)
			{
				fehler++;
				LoggerExtensions.LogError(_log, ex, "MKI Merge: {Title} failed", new object[1] { ((BaseItem)arbeit[i][0]).Name });
			}
		}
		if (fixPrimaryOrder)
		{
			int i = 0;
			foreach (Video haupt in filme)
			{
				ct.ThrowIfCancellationRequested();
				progress?.Report(50.0 + 50.0 * (double)i++ / (double)Math.Max(filme.Count, 1));
				List<Video> list3 = (from v in haupt.LinkedAlternateVersions.Where((LinkedChild l) => l.ItemId.HasValue).Select(delegate(LinkedChild l)
					{
						BaseItem itemById = _library.GetItemById(l.ItemId.Value);
						return (Video)(object)((itemById is Video) ? itemById : null);
					})
					where v != null
					select (v)).ToList();
				if (list3.Count == 0)
				{
					continue;
				}
				List<Video> list4 = new List<Video> { haupt };
				list4.AddRange(list3);
				Video val = list4.OrderByDescending(Breite).First();
				if (((BaseItem)val).Id.Equals(((BaseItem)haupt).Id))
				{
					continue;
				}
				LoggerExtensions.LogInformation(_log, "MKI Merge: {Title} - primary entry is {Old}px, highest resolution is {New}px. Rebuilding.", new object[3]
				{
					((BaseItem)haupt).Name,
					Breite((BaseItem)(object)haupt),
					Breite((BaseItem)(object)val)
				});
				try
				{
					if (await UmbauenAsync(list4, val, dryRun, ct).ConfigureAwait(continueOnCapturedContext: false))
					{
						umgebaut++;
					}
				}
				catch (Exception ex2)
				{
					fehler++;
					LoggerExtensions.LogError(_log, ex2, "MKI Merge: rebuilding {Title} failed", new object[1] { ((BaseItem)haupt).Name });
				}
			}
		}
		progress?.Report(100.0);
		LoggerExtensions.LogInformation(_log, "MKI Merge done: {Merged} merged, {Rebuilt} rebuilt, {Errors} errors{DryRun}", new object[4]
		{
			zusammen,
			umgebaut,
			fehler,
			dryRun ? " (dry run, nothing written)" : string.Empty
		});
		return new Ergebnis(arbeit.Count, zusammen, umgebaut, fehler);
	}

	private Video Frisch(Video v)
	{
		BaseItem itemById = _library.GetItemById(((BaseItem)v).Id);
		return (Video)(((object)((itemById is Video) ? itemById : null)) ?? ((object)v));
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
		if (haupt.PrimaryVersionId.HasValue)
		{
			haupt.SetPrimaryVersionId((Guid?)null);
			await ((BaseItem)haupt).UpdateToRepositoryAsync((ItemUpdateType)16, ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		List<LinkedChild> nebenversionen = haupt.LinkedAlternateVersions.ToList();
		foreach (Video item2 in andere)
		{
			ct.ThrowIfCancellationRequested();
			Video item = Frisch(item2);
			item.SetPrimaryVersionId((Guid?)((BaseItem)haupt).Id);
			await ((BaseItem)item).UpdateToRepositoryAsync((ItemUpdateType)16, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!SchonDrin(((BaseItem)item).Id))
			{
				nebenversionen.Add(new LinkedChild
				{
					ItemId = ((BaseItem)item).Id
				});
			}
			LinkedChild[] linkedAlternateVersions = item.LinkedAlternateVersions;
			foreach (LinkedChild val in linkedAlternateVersions)
			{
				if (val.ItemId.HasValue && !SchonDrin(val.ItemId.Value))
				{
					nebenversionen.Add(val);
				}
			}
			if (item.LinkedAlternateVersions.Length != 0)
			{
				item.LinkedAlternateVersions = Array.Empty<LinkedChild>();
				await ((BaseItem)item).UpdateToRepositoryAsync((ItemUpdateType)16, ct).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		haupt.LinkedAlternateVersions = nebenversionen.ToArray();
		await ((BaseItem)haupt).UpdateToRepositoryAsync((ItemUpdateType)16, ct).ConfigureAwait(continueOnCapturedContext: false);
		return true;
		bool SchonDrin(Guid id)
		{
			return nebenversionen.Any((LinkedChild l) => l.ItemId.HasValue && l.ItemId.Value.Equals(id));
		}
	}
}
