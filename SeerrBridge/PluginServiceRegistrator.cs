using Jellyfin.Plugin.SeerrBridge.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SeerrBridge;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
	public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
	{
		ServiceCollectionServiceExtensions.AddSingleton<SeerrClient>(serviceCollection);
		ServiceCollectionServiceExtensions.AddSingleton<MkiRepository>(serviceCollection);
		ServiceCollectionServiceExtensions.AddSingleton<IStartupFilter, LegacyAuthHeader>(serviceCollection);
		ServiceCollectionServiceExtensions.AddSingleton<IStartupFilter, SsoBruecke>(serviceCollection);
		ServiceCollectionHostedServiceExtensions.AddHostedService<WebPatcher>(serviceCollection);
	}
}
