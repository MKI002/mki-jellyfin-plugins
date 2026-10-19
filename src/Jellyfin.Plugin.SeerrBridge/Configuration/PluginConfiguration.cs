using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SeerrBridge.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
	public bool Enabled { get; set; } = true;

	public string PublicUrl { get; set; } = string.Empty;

	public string ServerUrl { get; set; } = string.Empty;

	public string ApiKey { get; set; } = string.Empty;

	public string Label { get; set; } = string.Empty;

	public bool ShowInHeader { get; set; } = true;

	public bool ShowInDrawer { get; set; } = true;

	public bool EnableSearch { get; set; } = true;

	public int SearchLimit { get; set; } = 8;

	public bool HideAvailable { get; set; } = true;

	public bool AllowRequests { get; set; } = true;

	public bool MapUsers { get; set; } = true;

	public string Language { get; set; } = "en";

	public bool LegacyAuthHeader { get; set; } = true;

	public bool EnableSso { get; set; } = true;

	public string CookieDomain { get; set; } = string.Empty;
}
