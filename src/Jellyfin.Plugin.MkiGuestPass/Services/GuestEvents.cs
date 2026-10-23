using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Data.Queries;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Events.Authentication;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiGuestPass.Services;

// Every successful sign-in on any path (a pass, Quick Connect, another plugin). Jellyfin raises it before the client
// gets its token, so a sign-in a guest must not have is undone before it can be used. A guest account is signed in
// only by this plugin's join calls and by Quick Connect (which the request gate checks); any other way is undone.
public class GuestEvents : IEventConsumer<AuthenticationResultEventArgs>, IEventConsumer<UserDeletedEventArgs>
{
	private static readonly Regex AllowedSignIn = new(@"/(MkiGuestPass/(Redeem|Rejoin)|QuickConnect/Authorize)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private readonly GuestService _guests;

	private readonly IUserManager _users;

	private readonly IHttpContextAccessor _http;

	private readonly ISessionManager _sessions;

	private readonly IDeviceManager _devices;

	private readonly ILogger<GuestEvents> _logger;

	public GuestEvents(GuestService guests, IUserManager users, IHttpContextAccessor http, ISessionManager sessions, IDeviceManager devices, ILogger<GuestEvents> logger)
	{
		_guests = guests;
		_users = users;
		_http = http;
		_sessions = sessions;
		_devices = devices;
		_logger = logger;
	}

	public async Task OnEvent(AuthenticationResultEventArgs e)
	{
		var user = e.User;
		if (user == null || user.Id == Guid.Empty)
		{
			return;
		}
		// The account as it is now: the copy in the event was read when the sign-in started, maybe before the guest
		// settings were saved.
		var now = _users.GetUserById(user.Id);
		string? creating = GuestService.CreatingNow();
		bool guestProvider = string.Equals(user.Policy?.AuthenticationProviderId, GuestSignIn.ProviderId, StringComparison.OrdinalIgnoreCase)
			|| (now != null && GuestService.UsesGuestSignIn(now));
		bool inCreation = (creating != null && (string.Equals(creating, user.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(creating, now?.Username, StringComparison.OrdinalIgnoreCase)))
			|| (user.Name ?? string.Empty).StartsWith(GuestService.TempPrefix, StringComparison.Ordinal);
		if (!guestProvider && !inCreation && _guests.Find(user.Id) == null)
		{
			return;
		}

		string? why = null;
		bool tooMany = false;
		var found = _guests.Find(user.Id);
		string path = _http.HttpContext?.Request.Path.Value ?? string.Empty;
		if (inCreation)
		{
			why = "the account was still being set up";
		}
		else if (!AllowedSignIn.IsMatch(path))
		{
			why = "it did not come from a guest link or Quick Connect";
		}
		else if (found == null)
		{
			why = "the account belongs to no guest pass";
		}
		else if (found.Value.Guest.State != GuestState.Active || found.Value.Pass.HasEnded(DateTime.UtcNow))
		{
			why = "the guest's access has ended";
		}
		else if (_guests.DeviceCount(user.Id) > found.Value.Pass.DevicesPerGuest)
		{
			why = "the guest is already signed in on as many devices as the pass allows";
			tooMany = true;
		}
		if (why == null)
		{
			return;
		}

		_logger.LogInformation("Sign-in of {User} undone: {Why}", user.Name, why);
		try
		{
			if (!tooMany)
			{
				// No access at all: every device of the account is signed out.
				await _sessions.RevokeUserTokens(user.Id, null!).ConfigureAwait(false);
				return;
			}
			// One device too many: only the new one goes. Without its id the others stay as they are.
			string? deviceId = e.SessionInfo?.DeviceId;
			if (string.IsNullOrEmpty(deviceId))
			{
				return;
			}
			foreach (var d in _devices.GetDevices(new DeviceQuery { UserId = user.Id, DeviceId = deviceId }).Items.ToList())
			{
				await _guests.SignOutDevice(d).ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Could not undo the sign-in of {User}", user.Name);
		}
	}

	public Task OnEvent(UserDeletedEventArgs e)
	{
		var id = e.Argument.Id;
		if (_guests.Find(id) != null)
		{
			_guests.Store.Change(d =>
			{
				foreach (var p in d.Passes)
				{
					p.Guests.RemoveAll(g => g.UserId == id);
				}
			});
		}
		return Task.CompletedTask;
	}
}
