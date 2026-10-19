using System;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Services;

public class UserEvents : IEventConsumer<UserPasswordChangedEventArgs>, IEventConsumer<UserCreatedEventArgs>, IEventConsumer<UserDeletedEventArgs>
{
	private readonly PolicyStore _store;

	public UserEvents(PolicyStore store)
	{
		_store = store;
	}

	// Any password change restarts the expiry clock. It does not lift a required change:
	// only the user setting their own password through the change dialog does that.
	public Task OnEvent(UserPasswordChangedEventArgs eventArgs)
	{
		// Nothing is recorded until the admin has switched something on.
		if (Policy.Configured(Plugin.Config))
		{
			_store.MarkChanged(eventArgs.Argument.Id);
		}
		return Task.CompletedTask;
	}

	public Task OnEvent(UserCreatedEventArgs eventArgs)
	{
		if (Policy.Configured(Plugin.Config))
		{
			_store.MarkChanged(eventArgs.Argument.Id);
		}
		if (Plugin.Config.RequireChangeForNewUsers)
		{
			_store.Require(eventArgs.Argument.Id, "new", TimeSpan.Zero);
		}
		return Task.CompletedTask;
	}

	public Task OnEvent(UserDeletedEventArgs eventArgs)
	{
		_store.Remove(eventArgs.Argument.Id);
		return Task.CompletedTask;
	}
}
