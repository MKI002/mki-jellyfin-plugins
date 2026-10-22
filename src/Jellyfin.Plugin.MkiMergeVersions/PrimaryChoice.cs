namespace Jellyfin.Plugin.MkiMergeVersions;

// Which version of a merged movie becomes the primary entry. The names are saved in the plugin's XML: never rename.
public enum PrimaryChoice
{
	// The widest picture (4K over 1080p), as before this setting existed.
	Highest = 0,

	// The version closest to 1080p (1920 px across). Without a 1080p file the next closest one leads.
	FullHd = 1
}
