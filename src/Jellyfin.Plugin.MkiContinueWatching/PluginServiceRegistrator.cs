using Jellyfin.Plugin.MkiContinueWatching.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MkiContinueWatching;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
	public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
	{
		if (DuplicateGuard.IsSuperseded)
		{
			// A newer copy of this plugin is loaded; leave all hooks to it.
			return;
		}

		serviceCollection.AddSingleton<ClearedStore>();
		serviceCollection.AddSingleton<ResumeService>();
		serviceCollection.AddSingleton<WebPatcher>();
		serviceCollection.AddHostedService(sp => sp.GetRequiredService<WebPatcher>());
	}
}
