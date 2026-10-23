using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MkiLoginShowcase.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiLoginShowcase.Services;

// Picks what the sign-in page shows. People who are not signed in see it, so only movies and shows from the libraries
// an administrator chose, within the age rating limit and with artwork stored on the server, are ever picked. The
// sign-in page gets pictures by their position in the pick; item ids, paths and library names never leave the server.
public sealed class ShowcaseService
{
	public const int MaxPosters = 48;

	public const int MaxBackdrops = 12;

	// More candidates than needed, because some have no poster or no backdrop.
	private const int Candidates = 240;

	// A new pick every hour, and at once after the settings change.
	private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

	// How often the pick is checked again: a title re-rated, deleted or moved out of the ticked libraries, or a library
	// removed in the dashboard, leaves the sign-in page within this time (the pick is made again).
	private static readonly TimeSpan Recheck = TimeSpan.FromSeconds(30);

	// Library names that suggest content not meant for everyone. Nothing is chosen by default anyway; the settings page
	// flags these so they are not ticked by mistake.
	private static readonly Regex KidsName = new(@"\b(kids?|child|children|family|cartoons?|junior|kinder|enfants?|bambini)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	private static readonly Regex AdultName = new(@"\b(adults?|xxx|nsfw|porn\w*|erotic\w*|erotik\w*|hentai|x-?rated|18\s*\+)|18\+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	private readonly ILibraryManager _library;

	private readonly ILogger<ShowcaseService> _logger;

	private readonly object _lock = new();

	private ShowcaseSet? _current;

	private ShowcaseSet? _previous;

	private int _generation;

	private DateTime _checkedUtc;

	public ShowcaseService(ILibraryManager library, ILogger<ShowcaseService> logger)
	{
		_library = library;
		_logger = logger;
		Plugin.ConfigSaved += Reset;
	}

	// The pick the sign-in page shows now, made fresh when it is older than an hour or the settings changed.
	public ShowcaseSet Current()
	{
		lock (_lock)
		{
			CheckAgain(Plugin.Config);
			if (_current != null && _current.Generation == _generation && DateTime.UtcNow - _current.BuiltUtc < Lifetime)
			{
				return _current;
			}
			var next = Build(Plugin.Config, _generation);
			// The page that loaded the last pick may still ask for its pictures for a while.
			_previous = _current;
			_current = next;
			_checkedUtc = DateTime.UtcNow;
			return next;
		}
	}

	// The pick with this id, while it is the current or the one before it.
	public ShowcaseSet? Find(string id)
	{
		lock (_lock)
		{
			CheckAgain(Plugin.Config);
			if (_current != null && _current.Generation == _generation && string.Equals(_current.Id, id, StringComparison.Ordinal))
			{
				return _current;
			}
			if (_previous != null && _previous.Generation == _generation && string.Equals(_previous.Id, id, StringComparison.Ordinal))
			{
				return _previous;
			}
			return null;
		}
	}

	// Settings changed or an administrator asked for new titles: the next request makes a new pick, and pictures of
	// earlier picks are no longer served (a library that was just removed must disappear at once).
	public void Reset()
	{
		lock (_lock)
		{
			_generation++;
			_current = null;
			_previous = null;
		}
	}

	// Every 30 seconds the current pick (and the one before it) is checked against the library as it is now. A pick
	// with a title that may no longer be shown is dropped as a whole, so its pictures stop at once and the next request
	// makes a new pick. Called under the lock.
	private void CheckAgain(PluginConfiguration config)
	{
		if (_current == null || DateTime.UtcNow - _checkedUtc < Recheck)
		{
			return;
		}
		_checkedUtc = DateTime.UtcNow;
		try
		{
			var libs = ChosenLibraries(config);
			if (_previous != null && !StillShown(_previous, config, libs))
			{
				_previous = null;
			}
			if (!StillShown(_current, config, libs))
			{
				_logger.LogDebug("MKI Login Showcase makes a new pick: a picked title may no longer be shown");
				_generation++;
				_current = null;
				_previous = null;
			}
		}
		catch (Exception ex)
		{
			// When the check cannot run, the pick is not trusted either.
			_logger.LogWarning(ex, "MKI Login Showcase could not check the current pick again");
			_generation++;
			_current = null;
			_previous = null;
		}
	}

	private bool StillShown(ShowcaseSet set, PluginConfiguration config, IReadOnlyDictionary<Guid, bool> libs)
	{
		foreach (var entry in set.Items)
		{
			var item = _library.GetItemById(entry.ItemId);
			if (item == null || !Shown(item, config, libs)
				|| (entry.Poster && !LocalImage(item, ImageType.Primary))
				|| (entry.Backdrop && !LocalImage(item, ImageType.Backdrop)))
			{
				return false;
			}
		}
		return true;
	}

	// The libraries the settings page offers: movies, shows and mixed libraries only (never home videos, photos,
	// music or books), with what they hold.
	public IReadOnlyList<LibraryInfo> Libraries(PluginConfiguration config)
	{
		var chosen = ChosenIds(config);
		var list = new List<LibraryInfo>();
		foreach (var folder in _library.GetVirtualFolders())
		{
			if (!Eligible(folder) || !Guid.TryParse(folder.ItemId, out var id))
			{
				continue;
			}
			var one = new[] { id };
			bool mixed = IsMixed(folder);
			list.Add(new LibraryInfo
			{
				Id = id.ToString("N"),
				Name = folder.Name ?? string.Empty,
				Type = mixed ? "mixed" : folder.CollectionType!.Value.ToString(),
				Selected = chosen.Contains(id),
				Looks = AdultName.IsMatch(folder.Name ?? string.Empty) ? "adult" : KidsName.IsMatch(folder.Name ?? string.Empty) ? "kids" : null,
				Titles = Count(one, null, false, false, 0),
				Shown = Count(one, null, true, config.IncludeUnrated && !mixed, config.MaxRating)
			});
		}
		return list.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private ShowcaseSet Build(PluginConfiguration config, int generation)
	{
		var set = new ShowcaseSet
		{
			Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant(),
			BuiltUtc = DateTime.UtcNow,
			Generation = generation,
			Pick = config.Pick
		};
		try
		{
			var libs = ChosenLibraries(config);
			var roots = libs.Keys.ToArray();
			if (roots.Length == 0)
			{
				return set;
			}
			var query = Query(roots, null, true, config.IncludeUnrated, config.MaxRating);
			query.Limit = Candidates;
			query.OrderBy = config.Pick switch
			{
				ShowcasePick.Recent => new[] { (ItemSortBy.DateCreated, SortOrder.Descending), (ItemSortBy.SortName, SortOrder.Ascending) },
				ShowcasePick.TopRated => new[] { (ItemSortBy.CommunityRating, SortOrder.Descending), (ItemSortBy.SortName, SortOrder.Ascending) },
				_ => new[] { (ItemSortBy.Random, SortOrder.Ascending) }
			};
			int posters = 0, backdrops = 0;
			foreach (var item in _library.GetItemList(query))
			{
				if (posters >= MaxPosters && backdrops >= MaxBackdrops)
				{
					break;
				}
				// The database filter above is checked again here: what is shown before sign-in must never depend on
				// one filter alone.
				if (!Shown(item, config, libs))
				{
					continue;
				}
				bool poster = posters < MaxPosters && LocalImage(item, ImageType.Primary);
				bool backdrop = backdrops < MaxBackdrops && LocalImage(item, ImageType.Backdrop);
				if (!poster && !backdrop)
				{
					continue;
				}
				posters += poster ? 1 : 0;
				backdrops += backdrop ? 1 : 0;
				set.Items.Add(new ShowcaseEntry
				{
					ItemId = item.Id,
					Name = item.Name ?? string.Empty,
					Year = item.ProductionYear,
					Poster = poster,
					Backdrop = backdrop
				});
			}
			KeepWhatIsDrawn(set, config.Style);
			if (config.ShowCounts)
			{
				// Only titles the sign-in page may show are counted: the number must not reveal how much in the ticked
				// libraries is above the age rating limit or unrated.
				set.Movies = CountShown(libs, BaseItemKind.Movie, config);
				set.Series = CountShown(libs, BaseItemKind.Series, config);
			}
		}
		catch (Exception ex)
		{
			// The sign-in page then simply stays as Jellyfin ships it.
			_logger.LogError(ex, "MKI Login Showcase could not pick titles");
			set.Items.Clear();
		}
		return set;
	}

	// Only what the chosen style draws leaves the server: backdrops for Backdrop Spotlight, posters for the other
	// three. When there is none of that kind, the sign-in page falls back to a style that draws the other kind.
	private static void KeepWhatIsDrawn(ShowcaseSet set, ShowcaseStyle style)
	{
		bool backdrops = style == ShowcaseStyle.Spotlight ? set.Items.Any(e => e.Backdrop) : !set.Items.Any(e => e.Poster);
		set.Items.RemoveAll(e => backdrops ? !e.Backdrop : !e.Poster);
		foreach (var e in set.Items)
		{
			e.Poster = !backdrops;
			e.Backdrop = backdrops;
		}
		set.Backdrops = backdrops;
	}

	// Whether an item may appear before sign-in: a movie or show, real (not a missing episode placeholder), and within
	// the age rating limit. Without a rating only when the administrator allows unrated titles.
	public static bool Allowed(BaseItem item, PluginConfiguration config)
	{
		if (item is not Movie && item is not Series)
		{
			return false;
		}
		if (item.IsVirtualItem)
		{
			return false;
		}
		var score = item.InheritedParentalRatingValue;
		if (score == null)
		{
			return config.IncludeUnrated;
		}
		return score.Value < config.MaxRating || (score.Value == config.MaxRating && (item.InheritedParentalRatingSubValue ?? 0) <= 0);
	}

	// Allowed, and in a ticked library as the library is now. A title without a rating never comes from a mixed library:
	// Jellyfin files a home video there as a movie, with a frame of the video as its poster.
	private bool Shown(BaseItem item, PluginConfiguration config, IReadOnlyDictionary<Guid, bool> libs)
	{
		if (!Allowed(item, config))
		{
			return false;
		}
		bool inChosen = false, inMixed = false;
		foreach (var folder in _library.GetCollectionFolders(item))
		{
			if (libs.TryGetValue(folder.Id, out var mixed))
			{
				inChosen = true;
				inMixed |= mixed;
			}
		}
		return inChosen && !(inMixed && item.InheritedParentalRatingValue == null);
	}

	// Only artwork stored on the server: serving a picture never makes the server fetch anything from elsewhere. GIFs
	// are left out because Jellyfin never redraws them, and only redrawn pictures are served.
	public static bool LocalImage(BaseItem item, ImageType type)
	{
		var info = item.GetImageInfo(type, 0);
		return info != null && info.IsLocalFile && !string.IsNullOrEmpty(info.Path)
			&& !info.Path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
	}

	// The ticked libraries that still exist and still hold movies or shows, and whether each is a mixed library.
	private Dictionary<Guid, bool> ChosenLibraries(PluginConfiguration config)
	{
		var libs = new Dictionary<Guid, bool>();
		var chosen = ChosenIds(config);
		if (chosen.Count == 0)
		{
			return libs;
		}
		foreach (var folder in _library.GetVirtualFolders())
		{
			if (Eligible(folder) && Guid.TryParse(folder.ItemId, out var id) && chosen.Contains(id))
			{
				libs[id] = IsMixed(folder);
			}
		}
		return libs;
	}

	private static HashSet<Guid> ChosenIds(PluginConfiguration config)
	{
		var ids = new HashSet<Guid>();
		foreach (var s in config.LibraryIds ?? Array.Empty<string>())
		{
			if (Guid.TryParse(s, out var id) && id != Guid.Empty)
			{
				ids.Add(id);
			}
		}
		return ids;
	}

	private static bool Eligible(VirtualFolderInfo folder)
	{
		return folder.CollectionType is null or CollectionTypeOptions.movies or CollectionTypeOptions.tvshows or CollectionTypeOptions.mixed;
	}

	private static bool IsMixed(VirtualFolderInfo folder) => folder.CollectionType is null or CollectionTypeOptions.mixed;

	private static InternalItemsQuery Query(Guid[] roots, BaseItemKind? kind, bool rated, bool includeUnrated, int maxRating)
	{
		var query = new InternalItemsQuery
		{
			IncludeItemTypes = kind.HasValue ? new[] { kind.Value } : new[] { BaseItemKind.Movie, BaseItemKind.Series },
			AncestorIds = roots,
			Recursive = true,
			IsVirtualItem = false,
			EnableTotalRecordCount = false,
			DtoOptions = new DtoOptions(false)
		};
		if (rated)
		{
			query.ImageTypes = new[] { ImageType.Primary, ImageType.Backdrop };
			query.MaxParentalRating = new ParentalRatingScore(maxRating, 0);
			if (!includeUnrated)
			{
				query.BlockUnratedItems = new[] { UnratedItem.Movie, UnratedItem.Series };
			}
		}
		return query;
	}

	// Titles the sign-in page may show, of one kind: titles without a rating only from libraries that are not mixed.
	private int CountShown(IReadOnlyDictionary<Guid, bool> libs, BaseItemKind kind, PluginConfiguration config)
	{
		var plain = libs.Where(l => !l.Value).Select(l => l.Key).ToArray();
		var mixed = libs.Where(l => l.Value).Select(l => l.Key).ToArray();
		return (plain.Length > 0 ? Count(plain, kind, true, config.IncludeUnrated, config.MaxRating) : 0)
			+ (mixed.Length > 0 ? Count(mixed, kind, true, false, config.MaxRating) : 0);
	}

	private int Count(Guid[] roots, BaseItemKind? kind, bool rated, bool includeUnrated, int maxRating)
	{
		try
		{
			return _library.GetCount(Query(roots, kind, rated, includeUnrated, maxRating));
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "MKI Login Showcase could not count titles");
			return 0;
		}
	}
}

public sealed class ShowcaseSet
{
	public string Id { get; set; } = string.Empty;

	public DateTime BuiltUtc { get; set; }

	public int Generation { get; set; }

	public ShowcasePick Pick { get; set; }

	// True when the pick holds backdrops (Backdrop Spotlight), false when it holds posters.
	public bool Backdrops { get; set; }

	public List<ShowcaseEntry> Items { get; } = new();

	public int? Movies { get; set; }

	public int? Series { get; set; }
}

public sealed class ShowcaseEntry
{
	public Guid ItemId { get; set; }

	public string Name { get; set; } = string.Empty;

	public int? Year { get; set; }

	public bool Poster { get; set; }

	public bool Backdrop { get; set; }
}

public sealed class LibraryInfo
{
	public string Id { get; set; } = string.Empty;

	public string Name { get; set; } = string.Empty;

	public string Type { get; set; } = string.Empty;

	public bool Selected { get; set; }

	// "kids" or "adult" when the name suggests it; such libraries are flagged on the settings page.
	public string? Looks { get; set; }

	// Movies and shows in the library.
	public int Titles { get; set; }

	// Of those, how many pass the age rating limit and have artwork.
	public int Shown { get; set; }
}
