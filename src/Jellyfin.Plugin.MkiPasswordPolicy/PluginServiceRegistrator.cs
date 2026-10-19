using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.MkiPasswordPolicy.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Authentication;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MkiPasswordPolicy;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
	public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
	{
		serviceCollection.AddSingleton<PolicyStore>();
		serviceCollection.AddSingleton<Enforcer>();
		serviceCollection.AddSingleton<Throttle>();
		serviceCollection.AddSingleton<IStartupFilter, LoginGate>();
		serviceCollection.AddScoped<IEventConsumer<UserPasswordChangedEventArgs>, UserEvents>();
		serviceCollection.AddScoped<IEventConsumer<UserCreatedEventArgs>, UserEvents>();
		serviceCollection.AddScoped<IEventConsumer<UserDeletedEventArgs>, UserEvents>();
		serviceCollection.AddScoped<IEventConsumer<AuthenticationResultEventArgs>, UserEvents>();
		serviceCollection.AddSingleton<WebPatcher>();
		serviceCollection.AddHostedService(sp => sp.GetRequiredService<WebPatcher>());
		serviceCollection.AddHostedService<Reminder>();
	}
}
