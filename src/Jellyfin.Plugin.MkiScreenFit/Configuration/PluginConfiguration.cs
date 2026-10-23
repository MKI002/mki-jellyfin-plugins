using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MkiScreenFit.Configuration;

// Everything is off after installing: the plugin changes nothing until an administrator turns a part on.
public class PluginConfiguration : BasePluginConfiguration
{
	// Plays the Full HD version of a movie with several versions when the screen cannot show more than Full HD.
	public bool EnableFullHdVersion { get; set; }

	// Keeps a transcode no larger than the screen when the video has to be re-encoded anyway.
	public bool EnableTranscodeLimit { get; set; }

	// Per device: Auto (use the screen the web client reports), FullHd (treat as a Full HD screen) or Off.
	public List<DeviceRule> DeviceRules { get; set; } = new();
}

public class DeviceRule
{
	public string DeviceId { get; set; } = string.Empty;

	public DeviceMode Mode { get; set; }
}

public enum DeviceMode
{
	Auto = 0,
	FullHd = 1,
	Off = 2
}
