using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MkiLoginShowcase.Configuration;

// Off after installing, and no library is picked: the sign-in page stays as Jellyfin ships it until an administrator
// turns the showcase on and chooses which libraries it may show.
public class PluginConfiguration : BasePluginConfiguration
{
	// Shows the showcase behind the sign-in page.
	public bool Enabled { get; set; }

	public ShowcaseStyle Style { get; set; } = ShowcaseStyle.PosterWall;

	// The only libraries whose artwork may appear (library ids). Movies, shows and mixed libraries only; a mixed library
	// never shows titles without a rating.
	public string[] LibraryIds { get; set; } = Array.Empty<string>();

	// Highest age rating that may appear (Jellyfin's rating score, roughly the age).
	public int MaxRating { get; set; } = 12;

	// Titles without an age rating are left out unless this is on.
	public bool IncludeUnrated { get; set; }

	public ShowcasePick Pick { get; set; } = ShowcasePick.Random;

	// Names the title under the artwork (Backdrop Spotlight with the neon card). Posters show their own artwork either way.
	public bool ShowTitles { get; set; }

	// Shows how many movies and shows of the chosen libraries the sign-in page may show (Flip Mosaic).
	public bool ShowCounts { get; set; }

	public ShowcaseAccent Accent { get; set; } = ShowcaseAccent.Mki;

	public ShowcaseMotion Motion { get; set; } = ShowcaseMotion.Normal;

	// Draws Jellyfin's sign-in form as a neon glass card that fits the chosen style.
	public bool NeonCard { get; set; } = true;
}

public enum ShowcaseStyle
{
	PosterWall,
	Spotlight,
	Horizon,
	Mosaic
}

public enum ShowcasePick
{
	Random,
	Recent,
	TopRated
}

public enum ShowcaseAccent
{
	Mki,
	Jellyfin,
	Sunset,
	Lime
}

public enum ShowcaseMotion
{
	Normal,
	Calm,
	Still
}
