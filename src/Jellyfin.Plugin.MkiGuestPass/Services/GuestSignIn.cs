using System;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Authentication;

namespace Jellyfin.Plugin.MkiGuestPass.Services;

// The sign-in provider of every guest account: it never accepts a password and never stores one, so a guest can
// only get in through a pass (or Quick Connect from a device the guest is already signed in on).
// Jellyfin stores the full type name of this class with each guest account. It must never change: an account whose
// provider cannot be found falls back to Jellyfin's InvalidAuthProvider, which refuses every sign-in as well.
public class GuestSignIn : IAuthenticationProvider
{
	public const string ProviderId = "Jellyfin.Plugin.MkiGuestPass.Services.GuestSignIn";

	// Shown in Jellyfin's user editor next to "Default". Has to stay enabled: a disabled provider is left out of that
	// list, and saving a guest there would then move it to the default provider, which has no password.
	public string Name => "MKI Guest Pass (guest accounts only, no password sign-in)";

	public bool IsEnabled => true;

	// Jellyfin asks every provider when nobody has the typed name, so this runs for any failed sign-in on the server.
	// Only AuthenticationException counts as "no" there; anything else would break normal sign-ins.
	public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
	{
		throw new AuthenticationException("Guest accounts cannot sign in with a password.");
	}

	// Jellyfin answers an ArgumentException with 400 and its message; an AuthenticationException would be a 401,
	// which the web client takes as "signed out" - for the administrator who tried to set a guest's password.
	public Task ChangePassword(User user, string newPassword)
	{
		throw new ArgumentException("Guest accounts have no password. Extend or end the guest pass instead.");
	}
}
