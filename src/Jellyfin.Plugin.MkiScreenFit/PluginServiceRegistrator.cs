using Jellyfin.Plugin.MkiScreenFit.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MkiScreenFit;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
	public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
	{
		if (DuplicateGuard.IsSuperseded)
		{
			// A newer copy of this plugin is loaded; leave all hooks to it.
			return;
		}

		serviceCollection.AddSingleton<ScreenStore>();
		serviceCollection.AddSingleton<ActivityLog>();
		serviceCollection.AddSingleton<PlaybackInfoFilter>();
		// An MVC filter, not request middleware: it only sees the PlaybackInfo action, after Jellyfin has
		// authenticated and authorized the request, and works on the action's own arguments and result.
		serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<PlaybackInfoFilter>());
		serviceCollection.AddSingleton<WebPatcher>();
		serviceCollection.AddHostedService(sp => sp.GetRequiredService<WebPatcher>());
	}
}
