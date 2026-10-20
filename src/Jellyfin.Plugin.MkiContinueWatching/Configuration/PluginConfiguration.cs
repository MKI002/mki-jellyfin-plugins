using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MkiContinueWatching.Configuration;

// Everything is off after installing: the plugin changes nothing until an administrator turns a part on.
public class PluginConfiguration : BasePluginConfiguration
{
	// Shows a Remove button on the Continue Watching cards of the web client.
	public bool EnableRemoveButton { get; set; }

	// Lets the daily task clear items nobody has played for AutoCleanupDays days.
	public bool EnableAutoCleanup { get; set; }

	public int AutoCleanupDays { get; set; } = 30;
}
