using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MkiGuestPass.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiGuestPass;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
	public const string PluginId = "3ae42f4f-a017-405f-ab3d-02c3d876c5a2";

	private readonly ILogger<Plugin> _logger;

	public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogger<Plugin> logger)
		: base(applicationPaths, xmlSerializer)
	{
		Instance = this;
		_logger = logger;
		if (!DuplicateGuard.IsSuperseded)
		{
			DuplicateGuard.RetireOlderCopies(Id, logger);
		}
	}

	public static Plugin? Instance { get; private set; }

	public override string Name => "MKI Guest Pass";

	public override Guid Id => Guid.Parse(PluginId);

	public override string Description => "Temporary guest accounts from a link, QR code or code: only the libraries you pick, locked and deleted when the pass ends.";

	public static PluginConfiguration Config => Instance?.Configuration ?? new PluginConfiguration();

	public static event Action? ConfigSaved;

	// Runs while Jellyfin uninstalls the plugin: every guest account is deleted, so none outlives the plugin.
	public static event Action? Uninstalling;

	public override void UpdateConfiguration(BasePluginConfiguration configuration)
	{
		if (configuration is PluginConfiguration c)
		{
			Clean(c);
		}
		base.UpdateConfiguration(configuration);
		ConfigSaved?.Invoke();
	}

	public override void OnUninstalling()
	{
		try
		{
			Uninstalling?.Invoke();
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "MKI Guest Pass could not delete every guest account while uninstalling");
		}
		try
		{
			Services.WebPatcher.TurnOff(ApplicationPaths, true);
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

	// The page checks the same, but only what is saved here counts: names are shown to people who are not signed in.
	private static void Clean(PluginConfiguration c)
	{
		c.DeleteAfterDays = Math.Clamp(c.DeleteAfterDays, 0, 30);
		c.WarnHours = Math.Clamp(c.WarnHours, 0, 24);
		c.InviterName = Text(c.InviterName, 40);
		string url = (c.PublicUrl ?? string.Empty).Trim().TrimEnd('/');
		c.PublicUrl = Uri.TryCreate(url, UriKind.Absolute, out var u)
			&& (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
			&& string.IsNullOrEmpty(u.UserInfo) && string.IsNullOrEmpty(u.Query) && string.IsNullOrEmpty(u.Fragment)
			&& url.Length <= 200
			? url
			: string.Empty;
	}

	internal static string Text(string? s, int max)
	{
		var clean = new string((s ?? string.Empty).Where(ch => !char.IsControl(ch) && !IsInvisible(ch)).ToArray()).Trim();
		return clean.Length > max ? clean[..max].Trim() : clean;
	}

	// Direction overrides and other invisible format characters could make a name look like something else.
	internal static bool IsInvisible(char ch) => char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format;
}
