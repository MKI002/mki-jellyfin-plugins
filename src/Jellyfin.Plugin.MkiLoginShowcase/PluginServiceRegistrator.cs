using Jellyfin.Plugin.MkiLoginShowcase.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MkiLoginShowcase;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
	public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
	{
		if (DuplicateGuard.IsSuperseded)
		{
			// A newer copy of this plugin is loaded; leave all hooks to it.
			return;
		}

		serviceCollection.AddSingleton<ShowcaseService>();
		serviceCollection.AddSingleton<WebPatcher>();
		serviceCollection.AddHostedService(sp => sp.GetRequiredService<WebPatcher>());
	}
}
