using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiMergeVersions;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
	public static Plugin? Instance { get; private set; }

	public override string Name => "MKI Merge Versions";

	public override string Description => "Merges movies that exist in several resolutions into a single entry. The highest resolution becomes the primary entry.";

	public override Guid Id => Guid.Parse("b8e2f3a1-6c47-4d59-9f0e-7a1c2d3e4b5f");

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
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_0035: Expected O, but got Unknown
		return (IEnumerable<PluginPageInfo>)(object)new PluginPageInfo[1]
		{
			new PluginPageInfo
			{
				Name = ((BasePlugin)this).Name,
				EmbeddedResourcePath = ((object)this).GetType().Namespace + ".Configuration.configPage.html"
			}
		};
	}
}
