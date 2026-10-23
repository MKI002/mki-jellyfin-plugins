using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MkiLoginShowcase.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiLoginShowcase;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
	public static Plugin? Instance { get; private set; }

	public override string Name => "MKI Login Showcase";

	public override Guid Id => Guid.Parse("f61ac908-2f39-419b-b7d3-302f7930455b");

	public override string Description => "An animated showcase of your library's artwork behind the Jellyfin sign-in page.";

	public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
		: base(applicationPaths, xmlSerializer)
	{
		Instance = this;
		if (!DuplicateGuard.IsSuperseded)
		{
			DuplicateGuard.RetireOlderCopies(Id, logger);
		}
	}

	public static PluginConfiguration Config => Instance?.Configuration ?? new PluginConfiguration();

	public static event Action? ConfigSaved;

	public override void UpdateConfiguration(BasePluginConfiguration configuration)
	{
		base.UpdateConfiguration(configuration);
		ConfigSaved?.Invoke();
	}

	public override void OnUninstalling()
	{
		try
		{
			// An older copy that a newer one replaced leaves the script tag to the newer copy.
			if (!DuplicateGuard.IsSuperseded)
			{
				Services.WebPatcher.RemoveScript(ApplicationPaths);
			}
		}
		catch (Exception)
		{
			// Uninstalling must not fail because index.html is read-only.
		}
		base.OnUninstalling();
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
