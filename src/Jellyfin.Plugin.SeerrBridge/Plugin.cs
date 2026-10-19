using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SeerrBridge.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SeerrBridge;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
	public static Plugin? Instance { get; private set; }

	public override string Name => "MKI Seerr Bridge";

	public override Guid Id => Guid.Parse("b7d41e02-9c58-4f6a-8d13-5a2c7e9f4610");

	public override string Description => "Bindet Seerr in die Jellyfin-Oberflaeche ein: Schaltflaeche in Kopfleiste und Seitenmenue, eingebettete Ansicht statt Weiterleitung und Suchvorschlaege mit Anfrage direkt aus der Trefferliste.";

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
