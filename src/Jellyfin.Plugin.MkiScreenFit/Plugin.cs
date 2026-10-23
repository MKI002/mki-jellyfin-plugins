using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MkiScreenFit.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiScreenFit;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
	// Most device rules a configuration may hold; more are ignored (Jellyfin rarely knows more than a few hundred devices).
	public const int MaxRules = 2000;

	public static Plugin? Instance { get; private set; }

	public override string Name => "MKI Screen Fit";

	public override Guid Id => Guid.Parse("10b53a4f-258a-459a-962b-2e29418e0775");

	public override string Description => "Plays the Full HD version of a movie on screens that cannot show 4K, and keeps transcodes no larger than the screen.";

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

	public static bool AnyOn => Config.EnableFullHdVersion || Config.EnableTranscodeLimit;

	// The administrator's choice for a device; Auto when there is none.
	public static DeviceMode ModeOf(string? deviceId)
	{
		if (string.IsNullOrEmpty(deviceId))
		{
			return DeviceMode.Auto;
		}
		var rule = (Config.DeviceRules ?? new List<DeviceRule>()).Take(MaxRules)
			.LastOrDefault(r => r != null && string.Equals(r.DeviceId, deviceId, StringComparison.Ordinal));
		return rule == null || !Enum.IsDefined(rule.Mode) ? DeviceMode.Auto : rule.Mode;
	}

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
