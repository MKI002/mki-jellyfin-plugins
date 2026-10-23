using System;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Authentication;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Services;

public class UserEvents : IEventConsumer<UserPasswordChangedEventArgs>, IEventConsumer<UserCreatedEventArgs>, IEventConsumer<UserDeletedEventArgs>, IEventConsumer<AuthenticationResultEventArgs>
{
	private readonly PolicyStore _store;

	private readonly Enforcer _enforcer;

	private readonly IUserManager _users;

	public UserEvents(PolicyStore store, Enforcer enforcer, IUserManager users)
	{
		_store = store;
		_enforcer = enforcer;
		_users = users;
	}

	// Any password change restarts the expiry clock. It does not lift a required change:
	// only the user setting their own password through the change dialog does that.
	public Task OnEvent(UserPasswordChangedEventArgs eventArgs)
	{
		var user = _users.GetUserById(eventArgs.Argument.Id);
		if (user != null && Policy.IsGuest(user))
		{
			return Task.CompletedTask;
		}
		// Nothing is recorded until the admin has switched something on.
		if (Policy.Configured(Plugin.Config))
		{
			_store.MarkChanged(eventArgs.Argument.Id);
		}
		// A reset to no password blocks a user with rules or an open request right away.
		if (user != null)
		{
			if (!Policy.HasNoPassword(user))
			{
				_store.PasswordGivenToLocked(user.Id);
			}
			_enforcer.EnforceNoPassword(user);
		}
		return Task.CompletedTask;
	}

	public Task OnEvent(UserCreatedEventArgs eventArgs)
	{
		// A guest account of MKI Guest Pass: it never has a password, so "new users must change" cannot apply.
		var neu = _users.GetUserById(eventArgs.Argument.Id);
		if (Policy.IsGuest(neu ?? eventArgs.Argument))
		{
			return Task.CompletedTask;
		}
		if (Policy.Configured(Plugin.Config))
		{
			_store.MarkChanged(eventArgs.Argument.Id);
		}
		if (Plugin.Config.RequireChangeForNewUsers)
		{
			_store.Require(eventArgs.Argument.Id, "new", TimeSpan.Zero);
		}
		var user = _users.GetUserById(eventArgs.Argument.Id);
		if (user != null)
		{
			_enforcer.EnforceNoPassword(user);
		}
		return Task.CompletedTask;
	}

	public Task OnEvent(UserDeletedEventArgs eventArgs)
	{
		_store.Remove(eventArgs.Argument.Id);
		return Task.CompletedTask;
	}

	// Every successful sign-in, on any path. Jellyfin raises it before the client receives its token.
	public async Task OnEvent(AuthenticationResultEventArgs eventArgs)
	{
		var id = eventArgs.User?.Id ?? Guid.Empty;
		if (id != Guid.Empty && _users.GetUserById(id) is { } user && Policy.IsGuest(user))
		{
			// Guests are none of this plugin's business; a state left from before they became guests goes.
			_enforcer.ForgetGuest(id);
			return;
		}
		if (id != Guid.Empty && (_store.AnyMustChange() || Policy.Configured(Plugin.Config)))
		{
			await _enforcer.UndoSignIn(id, eventArgs.SessionInfo?.DeviceId).ConfigureAwait(false);
		}
	}
}
