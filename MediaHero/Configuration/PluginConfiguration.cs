using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaHero.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
	public LibraryEntry[] Libraries { get; set; } = Array.Empty<LibraryEntry>();

	public SourceEntry[] Sources { get; set; } = new SourceEntry[3]
	{
		new SourceEntry
		{
			Source = HeroSource.RecentlyAdded,
			Take = 12,
			Enabled = true
		},
		new SourceEntry
		{
			Source = HeroSource.TopRated,
			Take = 6,
			Enabled = false
		},
		new SourceEntry
		{
			Source = HeroSource.JellyseerrTrending,
			Take = 8,
			Enabled = false
		}
	};

	public bool ShuffleResult { get; set; } = true;

	public bool ShuffleOnLoad { get; set; }

	public string[] IncludeItemTypes { get; set; } = new string[2] { "Movie", "Series" };

	public string[] TitleWhitelist { get; set; } = Array.Empty<string>();

	public string[] TitleBlacklist { get; set; } = Array.Empty<string>();

	public string[] GenreWhitelist { get; set; } = Array.Empty<string>();

	public string[] GenreBlacklist { get; set; } = Array.Empty<string>();

	public string[] TagWhitelist { get; set; } = Array.Empty<string>();

	public string[] TagBlacklist { get; set; } = Array.Empty<string>();

	public string[] OfficialRatingBlacklist { get; set; } = Array.Empty<string>();

	public double MinCommunityRating { get; set; }

	public int MinProductionYear { get; set; }

	public bool RequireBackdrop { get; set; } = true;

	public bool RequireLogo { get; set; }

	public bool ExcludeWatched { get; set; }

	public int MinBackdropWidth { get; set; }

	public string JellyseerrUrl { get; set; } = string.Empty;

	public string JellyseerrApiKey { get; set; } = string.Empty;

	public bool JellyseerrOnlyInLibrary { get; set; } = true;

	public int JellyseerrMinPopularity { get; set; }

	public HeroLayout Layout { get; set; }

	public HeroTransition Transition { get; set; }

	public HeroIndicator Indicator { get; set; }

	public int HeightVh { get; set; } = 56;

	public int MaxHeightPx { get; set; } = 720;

	public int CornerRadius { get; set; } = 12;

	public int ScrimStrength { get; set; } = 70;

	public bool BlendIntoHeader { get; set; } = true;

	public int BottomFadePercent { get; set; } = 18;

	public int LogoMaxWidthPercent { get; set; } = 34;

	public int LogoMaxHeightPercent { get; set; } = 40;

	public int TextLeftPercent { get; set; } = 4;

	public int TextBottomPercent { get; set; } = 8;

	public int TextWidthPercent { get; set; } = 52;

	public int ElementGap { get; set; } = 7;

	public int TitleScale { get; set; } = 100;

	public int TextScale { get; set; } = 100;

	public int ButtonScale { get; set; } = 100;

	public bool ShowLogo { get; set; } = true;

	public bool ShowTitleWhenNoLogo { get; set; } = true;

	public bool ShowOverview { get; set; } = true;

	public int OverviewLines { get; set; } = 3;

	public bool ShowYear { get; set; } = true;

	public bool ShowRating { get; set; } = true;

	public bool ShowRuntime { get; set; } = true;

	public bool ShowGenres { get; set; } = true;

	public bool ShowOfficialRating { get; set; } = true;

	public bool ShowPlayButton { get; set; } = true;

	public bool ShowDetailsButton { get; set; } = true;

	public bool ShowTrailerButton { get; set; }

	public bool ShowFavoriteButton { get; set; }

	public bool Autoplay { get; set; } = true;

	public int IntervalSeconds { get; set; } = 9;

	public bool PauseOnHover { get; set; } = true;

	public bool EnableKeyboard { get; set; } = true;

	public bool EnableSwipe { get; set; } = true;

	public bool RespectReducedMotion { get; set; } = true;

	public int CacheMinutes { get; set; } = 30;

	public bool HomeOnly { get; set; } = true;

	public bool Enabled { get; set; } = true;

	public string CustomCss { get; set; } = string.Empty;
}
