using System;
using System.Collections.Generic;
using Jellyfin.Plugin.MkiContinueWatching.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiContinueWatching;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
	// Auto cleanup never goes below a week, so a typo cannot clear everything someone watched yesterday.
	public const int MinDays = 7;

	public const int MaxDays = 3650;

	public static Plugin? Instance { get; private set; }

	public override string Name => "MKI Continue Watching Cleanup";

	public override Guid Id => Guid.Parse("bfc7b26a-b0fb-4040-944c-c9417ac73c54");

	public override string Description => "A Remove button on Continue Watching cards, and an optional cleanup of items nobody has touched for a while.";

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

	public static int Days => Math.Clamp(Config.AutoCleanupDays, MinDays, MaxDays);

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
			Services.WebPatcher.RemoveScript(ApplicationPaths);
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
