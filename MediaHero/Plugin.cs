using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MediaHero.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MediaHero;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
	public static Plugin? Instance { get; private set; }

	public override string Name => "Media Hero";

	public override Guid Id => Guid.Parse("6f3a1d84-2b57-4c19-9a0e-7d51c8e4b2f6");

	public override string Description => "Startseiten-Leiste mit serverseitig vorbereiteter Liste, Whitelists, Blacklists und optionalen Trendtiteln aus Jellyseerr.";

	public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
		: base(applicationPaths, xmlSerializer)
	{
		Instance = this;
	}

	public IEnumerable<PluginPageInfo> GetPages()
	{
		yield return new PluginPageInfo
		{
			Name = ((BasePlugin)this).Name,
			EmbeddedResourcePath = ((object)this).GetType().Namespace + ".Configuration.configPage.html"
		};
	}
}
