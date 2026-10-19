using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MediaHero.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaHero;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
	public static Plugin? Instance { get; private set; }

	public override string Name => "MKI Media Hero";

	public override Guid Id => Guid.Parse("6f3a1d84-2b57-4c19-9a0e-7d51c8e4b2f6");

	public override string Description => "Home screen banner with a server-side prepared list, whitelists, blacklists and optional trending titles from Seerr.";

	public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
		: base(applicationPaths, xmlSerializer)
	{
		Instance = this;
		if (!DuplicateGuard.IsSuperseded)
		{
			DuplicateGuard.RetireOlderCopies(Id, logger);
		}
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
