using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
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

	public const int MaxDeviceId = 256;

	private static readonly object RulesLock = new();

	public static Plugin? Instance { get; private set; }

	// Set while the plugin is being uninstalled, so nothing writes its files again.
	public static bool Uninstalled { get; private set; }

	public override string Name => "MKI Screen Fit";

	public override Guid Id => Guid.Parse("10b53a4f-258a-459a-962b-2e29418e0775");

	public override string Description => "Plays the Full HD version of a movie on screens that cannot show 4K, and makes transcodes only as large as the screen.";

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

	// A device id that can be kept in the settings file. Jellyfin URL-decodes the sign-in header, so an id can hold
	// control characters, and a settings file with those cannot be read back: Jellyfin would reset the plugin to off.
	public static bool UsableDeviceId(string? id)
	{
		if (string.IsNullOrEmpty(id) || id.Length > MaxDeviceId || id.Any(char.IsControl))
		{
			return false;
		}
		try
		{
			XmlConvert.VerifyXmlChars(id);
			return true;
		}
		catch (XmlException)
		{
			return false;
		}
	}

	public static event Action? ConfigSaved;

	// The settings page saves the two switches here. Device settings are changed one device at a time through
	// SetRule, so a page that was opened before another administrator's change cannot write an old list back.
	public override void UpdateConfiguration(BasePluginConfiguration configuration)
	{
		lock (RulesLock)
		{
			if (configuration is PluginConfiguration incoming)
			{
				incoming.DeviceRules = Copy(Configuration.DeviceRules);
			}
			base.UpdateConfiguration(configuration);
		}
		ConfigSaved?.Invoke();
	}

	// Sets one device's mode (Auto removes its rule) and returns all rules as they are now. Null when there are
	// already too many; stale when expected is given and the device no longer has that mode (an undo after another
	// administrator's newer change, which then stays).
	public static List<DeviceRule>? SetRule(string deviceId, DeviceMode mode, DeviceMode? expected, out bool stale)
	{
		stale = false;
		var plugin = Instance;
		if (plugin == null)
		{
			return null;
		}
		lock (RulesLock)
		{
			if (expected.HasValue && ModeOf(deviceId) != expected.Value)
			{
				stale = true;
				return Copy(plugin.Configuration.DeviceRules);
			}
			var list = Copy(plugin.Configuration.DeviceRules).Where(r => !string.Equals(r.DeviceId, deviceId, StringComparison.Ordinal)).ToList();
			if (mode != DeviceMode.Auto)
			{
				if (list.Count >= MaxRules)
				{
					return null;
				}
				list.Add(new DeviceRule { DeviceId = deviceId, Mode = mode });
			}
			// A new list, never a changed one: playback reads the rules without a lock.
			plugin.Configuration.DeviceRules = list;
			plugin.SaveConfiguration();
			return Copy(list);
		}
	}

	public static List<DeviceRule> Rules() => Copy(Config.DeviceRules);

	private static List<DeviceRule> Copy(List<DeviceRule>? rules) =>
		(rules ?? new List<DeviceRule>()).Where(r => r != null && UsableDeviceId(r.DeviceId) && Enum.IsDefined(r.Mode))
			.Take(MaxRules).Select(r => new DeviceRule { DeviceId = r.DeviceId, Mode = r.Mode }).ToList();

	public override void OnUninstalling()
	{
		Uninstalled = true;
		try
		{
			Services.WebPatcher.RemoveScript(ApplicationPaths);
		}
		catch (Exception)
		{
			// Uninstalling must not fail because index.html is read-only.
		}
		try
		{
			// The screen sizes and swap hints go with the plugin.
			string folder = Path.Combine(ApplicationPaths.PluginConfigurationsPath, "MkiScreenFit");
			if (Directory.Exists(folder))
			{
				Directory.Delete(folder, true);
			}
		}
		catch (Exception)
		{
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
