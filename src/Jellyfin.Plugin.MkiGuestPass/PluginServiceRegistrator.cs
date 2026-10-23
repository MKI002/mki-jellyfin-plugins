using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.MkiGuestPass.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Authentication;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MkiGuestPass;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
	public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
	{
		if (DuplicateGuard.IsSuperseded)
		{
			// A newer copy of this plugin is loaded; leave all hooks to it.
			return;
		}

		// Guest accounts are tied to this provider by its type name. Without it (plugin removed) Jellyfin uses its
		// InvalidAuthProvider for them, which refuses every sign-in as well.
		serviceCollection.AddSingleton<IAuthenticationProvider, GuestSignIn>();
		serviceCollection.AddSingleton<PassStore>();
		serviceCollection.AddSingleton<Secrets>();
		serviceCollection.AddSingleton<Throttle>();
		serviceCollection.AddSingleton<GuestService>();
		serviceCollection.AddSingleton<IStartupFilter, GuestGate>();
		serviceCollection.AddScoped<IEventConsumer<AuthenticationResultEventArgs>, GuestEvents>();
		serviceCollection.AddScoped<IEventConsumer<UserDeletedEventArgs>, GuestEvents>();
		serviceCollection.AddSingleton<WebPatcher>();
		serviceCollection.AddHostedService(sp => sp.GetRequiredService<WebPatcher>());
		serviceCollection.AddHostedService<ExpiryService>();
	}
}
