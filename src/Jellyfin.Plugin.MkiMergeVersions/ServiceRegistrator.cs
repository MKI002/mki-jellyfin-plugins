using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MkiMergeVersions;

public class ServiceRegistrator : IPluginServiceRegistrator
{
	public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
	{
		if (DuplicateGuard.IsSuperseded)
		{
			// A newer copy of this plugin is loaded; leave all hooks to it.
			return;
		}

		ServiceCollectionHostedServiceExtensions.AddHostedService<RuheWache>(serviceCollection);
	}
}
