using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MkiMergeVersions;

public class PluginConfiguration : BasePluginConfiguration
{
	public bool DryRun { get; set; }

	public bool FixPrimaryOrder { get; set; } = true;

	public PrimaryChoice PrimaryResolution { get; set; } = PrimaryChoice.Highest;
}
