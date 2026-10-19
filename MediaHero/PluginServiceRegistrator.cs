using Jellyfin.Plugin.MediaHero.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MediaHero;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
	public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
	{
		ServiceCollectionServiceExtensions.AddSingleton<JellyseerrClient>(serviceCollection);
		ServiceCollectionServiceExtensions.AddSingleton<HeroBuilder>(serviceCollection);
		ServiceCollectionHostedServiceExtensions.AddHostedService<WebPatcher>(serviceCollection);
		ServiceCollectionHostedServiceExtensions.AddHostedService<Vorwaermer>(serviceCollection);
	}
}
