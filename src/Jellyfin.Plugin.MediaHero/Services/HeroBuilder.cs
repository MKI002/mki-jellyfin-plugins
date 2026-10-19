using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MediaHero.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaHero.Services;

public class HeroBuilder
{
	private sealed class Sammler
	{
		public Dictionary<Guid, int> Bedarf { get; } = new Dictionary<Guid, int>();

		public Dictionary<Guid, List<(BaseItem Item, string Quelle)>> Koerbe { get; } = new Dictionary<Guid, List<(BaseItem, string)>>();

		public HashSet<Guid> Gesehen { get; } = new HashSet<Guid>();

		public bool Fertig => Bedarf.Values.All((int v) => v <= 0);
	}

	private readonly ILibraryManager _libraryManager;

	private readonly IUserManager _userManager;

	private readonly JellyseerrClient _jellyseerr;

	private readonly ILogger<HeroBuilder> _logger;

	private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

	private readonly Random _random = new Random();

	private IReadOnlyList<HeroItem> _cache = Array.Empty<HeroItem>();

	private DateTime _builtUtc = DateTime.MinValue;

	private string _etag = string.Empty;

	private int _erneuertGerade;

	private Guid _letzterNutzer = Guid.Empty;

	public string ETag => _etag;

	public DateTime BuiltUtc => _builtUtc;

	public HeroBuilder(ILibraryManager libraryManager, IUserManager userManager, JellyseerrClient jellyseerr, ILogger<HeroBuilder> logger)
	{
		_libraryManager = libraryManager;
		_userManager = userManager;
		_jellyseerr = jellyseerr;
		_logger = logger;
	}

	public void Invalidate()
	{
		_builtUtc = DateTime.MinValue;
	}

	public async Task<IReadOnlyList<HeroItem>> GetAsync(Guid userId, CancellationToken ct)
	{
		PluginConfiguration cfg = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration ?? new PluginConfiguration();
		bool flag = DateTime.UtcNow - _builtUtc < TimeSpan.FromMinutes(Math.Max(1, cfg.CacheMinutes));
		if (_cache.Count > 0)
		{
			if (!flag)
			{
				ImHintergrundErneuern(userId);
			}
			return _cache;
		}
		await _gate.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			if (DateTime.UtcNow - _builtUtc < TimeSpan.FromMinutes(Math.Max(1, cfg.CacheMinutes)) && _cache.Count > 0)
			{
				return _cache;
			}
			IReadOnlyList<HeroItem> readOnlyList = await BauenAsync(cfg, userId, ct).ConfigureAwait(continueOnCapturedContext: false);
			Uebernehmen(readOnlyList, userId);
			return readOnlyList;
		}
		finally
		{
			_gate.Release();
		}
	}

	private void Uebernehmen(IReadOnlyList<HeroItem> liste, Guid userId)
	{
		_cache = liste;
		_builtUtc = DateTime.UtcNow;
		_letzterNutzer = userId;
		_etag = "\"" + _builtUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "-" + liste.Count.ToString(CultureInfo.InvariantCulture) + "\"";
	}

	public void ImHintergrundErneuern(Guid userId)
	{
		if (Interlocked.CompareExchange(ref _erneuertGerade, 1, 0) != 0)
		{
			return;
		}
		Guid wer = ((userId == Guid.Empty) ? _letzterNutzer : userId);
		Task.Run(async delegate
		{
			_ = 1;
			try
			{
				PluginConfiguration cfg = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration ?? new PluginConfiguration();
				await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
				try
				{
					IReadOnlyList<HeroItem> readOnlyList = await BauenAsync(cfg, wer, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
					if (readOnlyList.Count > 0)
					{
						Uebernehmen(readOnlyList, wer);
					}
				}
				finally
				{
					_gate.Release();
				}
			}
			catch (Exception ex)
			{
				LoggerExtensions.LogWarning((ILogger)(object)_logger, ex, "Auffrischen der Leiste im Hintergrund fehlgeschlagen", Array.Empty<object>());
			}
			finally
			{
				Interlocked.Exchange(ref _erneuertGerade, 0);
			}
		});
	}

	private async Task<IReadOnlyList<HeroItem>> BauenAsync(PluginConfiguration cfg, Guid userId, CancellationToken ct)
	{
		User user = ((userId == Guid.Empty) ? null : _userManager.GetUserById(userId));
		BaseItemKind[] typen = TypenAus(cfg.IncludeItemTypes);
		List<(Guid Id, int Ziel)> ziele = (from b in BibliothekenVervollstaendigen(cfg)
			where b.Enabled && b.Count > 0
			select (Id: GuidAus(b.Id), Ziel: Math.Clamp(b.Count, 1, 40)) into b
			where b.Id != Guid.Empty
			select b).ToList();
		if (ziele.Count == 0)
		{
			return Array.Empty<HeroItem>();
		}
		Sammler korb = new Sammler();
		foreach (var item in ziele)
		{
			korb.Bedarf[item.Id] = item.Ziel;
			korb.Koerbe[item.Id] = new List<(BaseItem, string)>();
		}
		Guid[] wurzeln = ziele.Select(((Guid Id, int Ziel) z) => z.Id).ToArray();
		foreach (SourceEntry quelle in from q in QuellenVervollstaendigen(cfg)
			where q.Enabled
			select q)
		{
			if (korb.Fertig)
			{
				break;
			}
			ct.ThrowIfCancellationRequested();
			try
			{
				switch (quelle.Source)
				{
				case HeroSource.JellyseerrTrending:
					await AusJellyseerrAsync(korb, cfg, quelle, typen, wurzeln, "/api/v1/discover/trending", user, ct).ConfigureAwait(continueOnCapturedContext: false);
					break;
				case HeroSource.JellyseerrPopularMovies:
					await AusJellyseerrAsync(korb, cfg, quelle, typen, wurzeln, "/api/v1/discover/movies", user, ct).ConfigureAwait(continueOnCapturedContext: false);
					break;
				case HeroSource.JellyseerrPopularSeries:
					await AusJellyseerrAsync(korb, cfg, quelle, typen, wurzeln, "/api/v1/discover/tv", user, ct).ConfigureAwait(continueOnCapturedContext: false);
					break;
				case HeroSource.Whitelist:
				{
					int num = Math.Max(1, quelle.Take);
					foreach (BaseItem item2 in AusWhitelist(cfg, typen))
					{
						if (num > 0 && !korb.Fertig)
						{
							if (Aufnehmen(korb, item2, quelle, cfg, user))
							{
								num--;
							}
							continue;
						}
						break;
					}
					break;
				}
				default:
					AusBibliotheken(korb, cfg, quelle, typen, user);
					break;
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex2)
			{
				LoggerExtensions.LogWarning((ILogger)(object)_logger, ex2, "Quelle {Quelle} uebersprungen", new object[1] { quelle.Source });
			}
		}
		List<(BaseItem, string)> list = new List<(BaseItem, string)>();
		List<List<(BaseItem, string)>> list2 = ziele.Select(((Guid Id, int Ziel) z) => korb.Koerbe[z.Id]).ToList();
		int num2 = 0;
		while (true)
		{
			bool flag = false;
			foreach (List<(BaseItem, string)> item3 in list2)
			{
				if (num2 < item3.Count)
				{
					list.Add(item3[num2]);
					flag = true;
				}
			}
			if (!flag)
			{
				break;
			}
			num2++;
		}
		if (cfg.ShuffleResult)
		{
			for (int num3 = list.Count - 1; num3 > 0; num3--)
			{
				int num4 = _random.Next(num3 + 1);
				List<(BaseItem, string)> list3 = list;
				int index = num3;
				int index2 = num4;
				(BaseItem, string) value = list[num4];
				(BaseItem, string) value2 = list[num3];
				list3[index] = value;
				list[index2] = value2;
			}
		}
		List<KeyValuePair<Guid, int>> list4 = korb.Bedarf.Where((KeyValuePair<Guid, int> b) => b.Value > 0).ToList();
		if (list4.Count > 0)
		{
			LoggerExtensions.LogInformation((ILogger)(object)_logger, "Leiste gebaut: {Anzahl} Eintraege; nicht ganz erreicht: {Offen}", new object[2]
			{
				list.Count,
				string.Join(", ", list4.Select((KeyValuePair<Guid, int> o) => Bibliotheksname(o.Key) + " fehlt " + o.Value))
			});
		}
		else
		{
			LoggerExtensions.LogInformation((ILogger)(object)_logger, "Leiste gebaut: {Anzahl} Eintraege, alle Ziele erreicht", new object[1] { list.Count });
		}
		return list.Select<(BaseItem, string), HeroItem>(((BaseItem Item, string Quelle) e) => Abbilden(e.Item, e.Quelle)).ToList();
	}

	private bool Aufnehmen(Sammler korb, BaseItem item, SourceEntry quelle, PluginConfiguration cfg, User? user)
	{
		if (quelle.MinYear > 0 && (!item.ProductionYear.HasValue || item.ProductionYear < quelle.MinYear))
		{
			return false;
		}
		Guid guid = BibliothekVon(item);
		if (guid == Guid.Empty || !korb.Bedarf.TryGetValue(guid, out var value) || value <= 0)
		{
			return false;
		}
		if (korb.Gesehen.Contains(item.Id) || !Passt(item, cfg, user))
		{
			return false;
		}
		korb.Gesehen.Add(item.Id);
		korb.Koerbe[guid].Add((item, quelle.Source.ToString()));
		korb.Bedarf[guid] = value - 1;
		return true;
	}

	private Guid BibliothekVon(BaseItem item)
	{
		try
		{
			Folder? obj = _libraryManager.GetCollectionFolders(item).FirstOrDefault();
			return (obj != null) ? ((BaseItem)obj).Id : Guid.Empty;
		}
		catch (Exception)
		{
			return Guid.Empty;
		}
	}

	private string Bibliotheksname(Guid id)
	{
		try
		{
			Guid result;
			VirtualFolderInfo? obj = ((IEnumerable<VirtualFolderInfo>)_libraryManager.GetVirtualFolders()).FirstOrDefault((Func<VirtualFolderInfo, bool>)((VirtualFolderInfo v) => Guid.TryParse(v.ItemId, out result) && result == id));
			return ((obj != null) ? obj.Name : null) ?? id.ToString("N");
		}
		catch (Exception)
		{
			return id.ToString("N");
		}
	}

	private IEnumerable<LibraryEntry> BibliothekenVervollstaendigen(PluginConfiguration cfg)
	{
		List<LibraryEntry> list = (cfg.Libraries ?? Array.Empty<LibraryEntry>()).ToList();
		bool enabled = list.Count == 0;
		HashSet<string> hashSet = new HashSet<string>(list.Select((LibraryEntry v) => (v.Id ?? string.Empty).Replace("-", string.Empty)), StringComparer.OrdinalIgnoreCase);
		try
		{
			foreach (VirtualFolderInfo virtualFolder in _libraryManager.GetVirtualFolders())
			{
				string text = (virtualFolder.ItemId ?? string.Empty).Replace("-", string.Empty);
				if (text.Length != 0 && !hashSet.Contains(text))
				{
					list.Add(new LibraryEntry
					{
						Id = text,
						Name = (virtualFolder.Name ?? text),
						Enabled = enabled,
						Count = 10
					});
				}
			}
		}
		catch (Exception)
		{
		}
		return list;
	}

	private static IReadOnlyList<SourceEntry> QuellenVervollstaendigen(PluginConfiguration cfg)
	{
		Dictionary<HeroSource, SourceEntry> dictionary = (from s in cfg.Sources ?? Array.Empty<SourceEntry>()
			group s by s.Source).ToDictionary((IGrouping<HeroSource, SourceEntry> g) => g.Key, (IGrouping<HeroSource, SourceEntry> g) => g.First());
		List<SourceEntry> list = new List<SourceEntry>();
		HeroSource[] values = Enum.GetValues<HeroSource>();
		foreach (HeroSource heroSource in values)
		{
			list.Add(dictionary.TryGetValue(heroSource, out var value) ? value : new SourceEntry
			{
				Source = heroSource,
				Take = 8,
				Enabled = false
			});
		}
		return list;
	}

	private void AusBibliotheken(Sammler korb, PluginConfiguration cfg, SourceEntry quelle, BaseItemKind[] typen, User? user)
	{
		int num = Math.Max(1, quelle.Take);
		foreach (var (guid2, num3) in korb.Bedarf.ToList())
		{
			if (num3 <= 0 || num <= 0)
			{
				continue;
			}
			int limit = Math.Clamp(num3 * 4, 4, 200);
			foreach (BaseItem item in AusBibliothek(cfg, quelle, typen, user, new Guid[1] { guid2 }, limit))
			{
				if (num <= 0 || korb.Bedarf[guid2] <= 0)
				{
					break;
				}
				if (Aufnehmen(korb, item, quelle, cfg, user))
				{
					num--;
				}
			}
		}
	}

	private Guid[] AlleBibliotheken()
	{
		try
		{
			Guid result;
			return (from v in _libraryManager.GetVirtualFolders()
				select (!Guid.TryParse(v.ItemId, out result)) ? Guid.Empty : result into g
				where g != Guid.Empty
				select g).ToArray();
		}
		catch (Exception)
		{
			return Array.Empty<Guid>();
		}
	}

	private IEnumerable<BaseItem> AusBibliothek(PluginConfiguration cfg, SourceEntry quelle, BaseItemKind[] typen, User? user, Guid[] wurzeln, int limit)
	{
		//IL_0002: Unknown result type (might be due to invalid IL or missing references)
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Expected O, but got Unknown
		InternalItemsQuery val = new InternalItemsQuery(user)
		{
			IncludeItemTypes = typen,
			Recursive = true,
			IsVirtualItem = false,
			Limit = Math.Clamp(limit, 1, 200),
			EnableTotalRecordCount = false
		};
		if (wurzeln.Length != 0)
		{
			val.AncestorIds = wurzeln;
		}
		switch (quelle.Source)
		{
		case HeroSource.RecentlyAdded:
			val.OrderBy = new[] { ((ItemSortBy)5, (SortOrder)1) };
			break;
		case HeroSource.RecentlyReleased:
			val.OrderBy = new[] { ((ItemSortBy)8, (SortOrder)1) };
			val.MinPremiereDate = new DateTime(Math.Max(cfg.MinProductionYear, DateTime.UtcNow.Year - 3), 1, 1, 0, 0, 0, DateTimeKind.Utc);
			break;
		case HeroSource.TopRated:
			val.OrderBy = new[] { ((ItemSortBy)14, (SortOrder)1) };
			val.MinCommunityRating = Math.Max(cfg.MinCommunityRating, 6.5);
			break;
		case HeroSource.Random:
			val.OrderBy = new[] { ((ItemSortBy)12, (SortOrder)0) };
			break;
		case HeroSource.Favorites:
			val.IsFavorite = true;
			val.OrderBy = new[] { ((ItemSortBy)12, (SortOrder)0) };
			break;
		case HeroSource.NextUp:
			val.IsPlayed = false;
			val.OrderBy = new[] { ((ItemSortBy)7, (SortOrder)1) };
			break;
		default:
			val.OrderBy = new[] { ((ItemSortBy)5, (SortOrder)1) };
			break;
		}
		return _libraryManager.GetItemList(val);
	}

	private IEnumerable<BaseItem> AusWhitelist(PluginConfiguration cfg, BaseItemKind[] typen)
	{
		string[] titleWhitelist = cfg.TitleWhitelist;
		foreach (string text in titleWhitelist)
		{
			if (Guid.TryParse(text, out var result))
			{
				BaseItem itemById = _libraryManager.GetItemById(result);
				if (itemById != null)
				{
					yield return itemById;
				}
				continue;
			}
			IReadOnlyList<BaseItem> itemList = _libraryManager.GetItemList(new InternalItemsQuery
			{
				IncludeItemTypes = typen,
				Recursive = true,
				IsVirtualItem = false,
				Name = text,
				Limit = 1
			});
			foreach (BaseItem item in itemList)
			{
				yield return item;
			}
		}
	}

	private async Task AusJellyseerrAsync(Sammler korb, PluginConfiguration cfg, SourceEntry quelle, BaseItemKind[] typen, Guid[] wurzeln, string route, User? user, CancellationToken ct)
	{
		if (!cfg.JellyseerrOnlyInLibrary)
		{
			return;
		}
		int verbleibend = Math.Max(1, quelle.Take);
		int geprueft = 0;
		int genommen = 0;
		int seiten = 0;
		for (int seite = 1; seite <= 25; seite++)
		{
			if (verbleibend <= 0)
			{
				break;
			}
			if (korb.Fertig)
			{
				break;
			}
			ct.ThrowIfCancellationRequested();
			IReadOnlyList<TrendEntry> readOnlyList = await _jellyseerr.SeiteAsync(cfg.JellyseerrUrl, cfg.JellyseerrApiKey, route, seite, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (readOnlyList.Count == 0)
			{
				break;
			}
			seiten++;
			foreach (TrendEntry item in readOnlyList)
			{
				if (verbleibend <= 0 || korb.Fertig)
				{
					break;
				}
				if (!(item.Popularity < (double)cfg.JellyseerrMinPopularity))
				{
					geprueft++;
					BaseItem val = TrefferZu(item.TmdbId, typen, wurzeln);
					if (val != null && Aufnehmen(korb, val, quelle, cfg, user))
					{
						verbleibend--;
						genommen++;
					}
				}
			}
		}
		LoggerExtensions.LogInformation((ILogger)(object)_logger, "Seerr {Route}: {Seiten} Seiten, {Geprueft} Titel geprueft, {Genommen} genommen", new object[4] { route, seiten, geprueft, genommen });
	}

	private BaseItem? TrefferZu(int tmdbId, BaseItemKind[] typen, Guid[] wurzeln)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_0061: Expected O, but got Unknown
		InternalItemsQuery val = new InternalItemsQuery
		{
			IncludeItemTypes = typen,
			Recursive = true,
			IsVirtualItem = false,
			Limit = 1,
			EnableTotalRecordCount = false,
			HasAnyProviderId = new Dictionary<string, string> { [((object)(MetadataProvider)3/*cast due to .constrained prefix*/).ToString()] = tmdbId.ToString(CultureInfo.InvariantCulture) }
		};
		if (wurzeln.Length != 0)
		{
			val.AncestorIds = wurzeln;
		}
		return _libraryManager.GetItemList(val).FirstOrDefault();
	}

	private bool Passt(BaseItem item, PluginConfiguration cfg, User? user)
	{
		string text = item.Name ?? string.Empty;
		string text2 = item.Id.ToString("N");
		string[] titleBlacklist = cfg.TitleBlacklist;
		foreach (string text3 in titleBlacklist)
		{
			if (!string.IsNullOrWhiteSpace(text3) && (text.Contains(text3, StringComparison.OrdinalIgnoreCase) || text2.Equals(text3.Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)))
			{
				return false;
			}
		}
		string[] genres = item.Genres ?? Array.Empty<string>();
		if (cfg.GenreBlacklist.Any((string g) => ((ReadOnlySpan<string>)genres).Contains(g, (IEqualityComparer<string>?)StringComparer.OrdinalIgnoreCase)))
		{
			return false;
		}
		if (cfg.GenreWhitelist.Length != 0 && !cfg.GenreWhitelist.Any((string g) => ((ReadOnlySpan<string>)genres).Contains(g, (IEqualityComparer<string>?)StringComparer.OrdinalIgnoreCase)))
		{
			return false;
		}
		string[] tags = item.Tags ?? Array.Empty<string>();
		if (cfg.TagBlacklist.Any((string t) => ((ReadOnlySpan<string>)tags).Contains(t, (IEqualityComparer<string>?)StringComparer.OrdinalIgnoreCase)))
		{
			return false;
		}
		if (cfg.TagWhitelist.Length != 0 && !cfg.TagWhitelist.Any((string t) => ((ReadOnlySpan<string>)tags).Contains(t, (IEqualityComparer<string>?)StringComparer.OrdinalIgnoreCase)))
		{
			return false;
		}
		if (!string.IsNullOrEmpty(item.OfficialRating) && ((ReadOnlySpan<string>)cfg.OfficialRatingBlacklist).Contains(item.OfficialRating, (IEqualityComparer<string>?)StringComparer.OrdinalIgnoreCase))
		{
			return false;
		}
		if (cfg.MinCommunityRating > 0.0 && (!item.CommunityRating.HasValue || (double?)item.CommunityRating < cfg.MinCommunityRating))
		{
			return false;
		}
		if (cfg.MinProductionYear > 0 && (!item.ProductionYear.HasValue || item.ProductionYear < cfg.MinProductionYear))
		{
			return false;
		}
		if (cfg.RequireBackdrop && item.GetImageInfo((ImageType)2, 0) == null)
		{
			return false;
		}
		if (cfg.RequireLogo && item.GetImageInfo((ImageType)4, 0) == null)
		{
			return false;
		}
		if (cfg.MinBackdropWidth > 0)
		{
			ItemImageInfo imageInfo = item.GetImageInfo((ImageType)2, 0);
			if (imageInfo == null || (imageInfo.Width > 0 && imageInfo.Width < cfg.MinBackdropWidth))
			{
				return false;
			}
		}
		return true;
	}

	private static bool Gleich(string a, string b)
	{
		return a.Replace("-", string.Empty, StringComparison.Ordinal).Equals(b.Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
	}

	private HeroItem Abbilden(BaseItem item, string quelle)
	{
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		HeroItem obj = new HeroItem
		{
			Id = item.Id.ToString("N"),
			Name = (item.Name ?? string.Empty),
			Overview = item.Overview,
			Type = ((object)item.GetBaseItemKind()/*cast due to .constrained prefix*/).ToString(),
			Year = item.ProductionYear,
			Rating = item.CommunityRating,
			OfficialRating = item.OfficialRating
		};
		long? runTimeTicks = item.RunTimeTicks;
		obj.RuntimeMinutes = ((runTimeTicks.HasValue && runTimeTicks.GetValueOrDefault() > 0) ? new int?((int)TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalMinutes) : ((int?)null));
		obj.Genres = item.Genres?.ToList() ?? new List<string>();
		obj.BackdropTag = Marke(item, (ImageType)2);
		obj.LogoTag = Marke(item, (ImageType)4);
		obj.PosterTag = Marke(item, (ImageType)0);
		obj.ThumbTag = Marke(item, (ImageType)5);
		obj.Source = quelle;
		obj.PlayId = item.Id.ToString("N");
		return obj;
	}

	private static string? Marke(BaseItem item, ImageType typ)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		ItemImageInfo imageInfo = item.GetImageInfo(typ, 0);
		if (imageInfo != null)
		{
			return imageInfo.DateModified.Ticks.ToString(CultureInfo.InvariantCulture);
		}
		return null;
	}

	private static Guid GuidAus(string? wert)
	{
		if (!Guid.TryParse((wert ?? string.Empty).Trim(), out var result))
		{
			return Guid.Empty;
		}
		return result;
	}

	private static Guid[] GuidsAus(string[] werte)
	{
		List<Guid> list = new List<Guid>();
		for (int i = 0; i < werte.Length; i++)
		{
			if (Guid.TryParse(werte[i], out var result))
			{
				list.Add(result);
			}
		}
		return list.ToArray();
	}

	private static BaseItemKind[] TypenAus(string[] namen)
	{
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		List<BaseItemKind> list = new List<BaseItemKind>();
		for (int i = 0; i < namen.Length; i++)
		{
			if (Enum.TryParse<BaseItemKind>(namen[i], ignoreCase: true, out BaseItemKind result))
			{
				list.Add(result);
			}
		}
		if (list.Count <= 0)
		{
			return (BaseItemKind[])(object)new BaseItemKind[2]
			{
				(BaseItemKind)13,
				(BaseItemKind)28
			};
		}
		return list.ToArray();
	}
}
