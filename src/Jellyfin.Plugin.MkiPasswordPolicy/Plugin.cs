using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MkiPasswordPolicy.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.MkiPasswordPolicy;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
	public static Plugin? Instance { get; private set; }

	public override string Name => "MKI Password Policy";

	public override Guid Id => Guid.Parse("ea705ff9-2793-4156-a253-3720f294ec89");

	public override string Description => "Password rules on every client and forced password changes for single users or everyone.";

	public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
		: base(applicationPaths, xmlSerializer)
	{
		Instance = this;
	}

	public static PluginConfiguration Config => Instance?.Configuration ?? new PluginConfiguration();

	public static event Action? ConfigSaved;

	public override void UpdateConfiguration(BasePluginConfiguration configuration)
	{
		base.UpdateConfiguration(configuration);
		ConfigSaved?.Invoke();
	}

	public IEnumerable<PluginPageInfo> GetPages()
	{
		yield return new PluginPageInfo
		{
			Name = Name,
			EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
		};
	}
}
