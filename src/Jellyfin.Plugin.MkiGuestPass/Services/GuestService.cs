using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;
using AccessSchedule = Jellyfin.Database.Implementations.Entities.AccessSchedule;

namespace Jellyfin.Plugin.MkiGuestPass.Services;

// An error with a message meant for the person who sees it (a guest on the join page or the administrator).
public sealed class GuestError : Exception
{
	public GuestError(int status, string message, string? reason = null)
		: base(message)
	{
		Status = status;
		Reason = reason;
	}

	public int Status { get; }

	// For the join page, which picks its screen by it: "ended", "removed", "full", "paused", "home", "update".
	public string? Reason { get; }
}

public class NewPass
{
	public string? Name { get; set; }

	public List<string>? Libraries { get; set; }

	public DateTime EndUtc { get; set; }

	public int MaxGuests { get; set; }

	public int DevicesPerGuest { get; set; }

	public int StreamsPerGuest { get; set; }

	public int? MaxRating { get; set; }

	public int? MaxSubRating { get; set; }

	public bool RemoteAccess { get; set; }

	public bool WithCode { get; set; }
}

// Where a join request came from. ViaUnknownProxy: a home address passed on a request for someone else, through a
// reverse proxy Jellyfin does not know (Networking -> Known proxies), so the real visitor is unknown. ThrottleAddress
// is then the address that proxy named, so one visitor cannot use up the limits of everyone behind it.
public sealed record JoinOrigin(IPAddress? Address, bool ViaUnknownProxy, IPAddress? ThrottleAddress);

public class JoinRequest
{
	public string? Token { get; set; }

	public string? Code { get; set; }

	public string? RejoinKey { get; set; }

	public string? Name { get; set; }

	public string? DeviceId { get; set; }

	public string? DeviceName { get; set; }
}

// Creates, extends, ends and deletes passes and guest accounts. Every change of a guest account runs under one
// lock, one at a time; reading never waits for it.
public class GuestService
{
	public const string UserPrefix = "Guest ";

	// An account is created under this prefix and a random name, and renamed to "Guest Anna" only once it has the
	// guest settings: nobody can guess its name while it still has Jellyfin's defaults.
	public const string TempPrefix = "mkigp-";

	public const int MaxNameLength = 20;

	public const int MaxOpenPasses = 20;

	public const int MaxGuestAccounts = 50;

	public const int MaxGuestsPerPass = 10;

	public const int MaxDevicesPerGuest = 5;

	public const int MaxStreamsPerGuest = 5;

	// Read by MKI Password Policy while an account is being created and has no guest settings yet: "name|ticks".
	public const string CreatingKey = "MkiGuestPass.Creating";

	public static readonly TimeSpan MinLength = TimeSpan.FromMinutes(10);

	public static readonly TimeSpan MaxLength = TimeSpan.FromDays(30);

	private static readonly Guid PasswordPolicyId = Guid.Parse("ea705ff9-2793-4156-a253-3720f294ec89");

	private static readonly Version PasswordPolicyWithGuests = new(1, 1, 1, 0);

	private readonly SemaphoreSlim _gate = new(1, 1);

	private DateTime _unknownProxyUtc = DateTime.MinValue;

	private readonly PassStore _store;

	private readonly Secrets _secrets;

	private readonly Throttle _throttle;

	private readonly IUserManager _users;

	private readonly ISessionManager _sessions;

	private readonly IDeviceManager _devices;

	private readonly ILibraryManager _library;

	private readonly ILocalizationManager _localization;

	private readonly IServerApplicationHost _host;

	private readonly IServerConfigurationManager _config;

	private readonly INetworkManager _network;

	private readonly IActivityManager _activity;

	private readonly IPlaylistManager _playlists;

	private readonly IPluginManager _plugins;

	private readonly ILogger<GuestService> _logger;

	public GuestService(
		PassStore store,
		Secrets secrets,
		Throttle throttle,
		IUserManager users,
		ISessionManager sessions,
		IDeviceManager devices,
		ILibraryManager library,
		ILocalizationManager localization,
		IServerApplicationHost host,
		IServerConfigurationManager config,
		INetworkManager network,
		IActivityManager activity,
		IPlaylistManager playlists,
		IPluginManager plugins,
		ILogger<GuestService> logger)
	{
		_store = store;
		_secrets = secrets;
		_throttle = throttle;
		_users = users;
		_sessions = sessions;
		_devices = devices;
		_library = library;
		_localization = localization;
		_host = host;
		_config = config;
		_network = network;
		_activity = activity;
		_playlists = playlists;
		_plugins = plugins;
		_logger = logger;
		_throttle.CodesPaused += OnCodesPaused;
	}

	public PassStore Store => _store;

	public static bool UsesGuestSignIn(User user) => string.Equals(user.AuthenticationProviderId, GuestSignIn.ProviderId, StringComparison.OrdinalIgnoreCase);

	// ---------- for the settings page ----------

	// origin: the administrator's own request, to tell them whether a link for the home network only opens where
	// they are right now.
	public object Overview(JoinOrigin origin)
	{
		var now = DateTime.UtcNow;
		bool youAreHome = IsLocal(origin);
		var data = _store.Snapshot();
		var libraries = Libraries();
		var names = libraries.ToDictionary(l => l.Id, l => l.Name);
		var sessions = _sessions.Sessions.ToList();
		int deleteDays = Plugin.Config.DeleteAfterDays;
		return new
		{
			Now = now,
			ServerName = _host.FriendlyName,
			Passes = data.Passes.OrderByDescending(p => p.CreatedUtc).Select(p => new
			{
				Id = p.Id.ToString("N", CultureInfo.InvariantCulture),
				p.Name,
				Libraries = p.Libraries.Select(id => new { Id = id.ToString("N", CultureInfo.InvariantCulture), Name = names.TryGetValue(id, out var n) ? n : "Removed library" }),
				p.CreatedUtc,
				p.EndUtc,
				Open = !p.HasEnded(now),
				EndedEarly = p.EndedEarlyUtc != null,
				p.MaxGuests,
				p.Used,
				p.DevicesPerGuest,
				p.StreamsPerGuest,
				Rating = p.RatingName,
				p.RemoteAccess,
				HasCode = p.CodeHash != null,
				p.CreatedByName,
				Guests = p.Guests.OrderBy(g => g.JoinedUtc).Select(g =>
				{
					var mine = sessions.Where(s => s.UserId == g.UserId).ToList();
					var playing = mine.FirstOrDefault(s => s.NowPlayingItem != null);
					return new
					{
						UserId = g.UserId.ToString("N", CultureInfo.InvariantCulture),
						g.Name,
						g.UserName,
						g.JoinedUtc,
						g.Remote,
						State = g.State.ToString(),
						g.LockedUtc,
						DeleteUtc = g.LockedUtc?.AddDays(deleteDays),
						Devices = DeviceCount(g.UserId),
						DeviceList = DeviceList(g.UserId),
						LastSeenUtc = mine.Count > 0 ? mine.Max(s => s.LastActivityDate) : (DateTime?)null,
						Watching = playing?.NowPlayingItem?.Name
					};
				})
			}),
			Libraries = libraries.Select(l => new { Id = l.Id.ToString("N", CultureInfo.InvariantCulture), l.Name, l.Type }),
			Ratings = Ratings(),
			Limits = new
			{
				MaxOpenPasses,
				MaxGuestAccounts,
				MaxGuestsPerPass,
				MaxDevicesPerGuest,
				MaxStreamsPerGuest,
				MinMinutes = (int)MinLength.TotalMinutes,
				MaxDays = (int)MaxLength.TotalDays
			},
			OpenPasses = data.Passes.Count(p => !p.HasEnded(now)),
			GuestAccounts = data.AllGuests().Count(),
			CodesPausedUntil = _throttle.CodesPausedUntil,
			QuickConnect = _config.Configuration.QuickConnectAvailable,
			PasswordPolicyOutdated = PasswordPolicyOutdated(),
			WebClientProblem = WebPatcher.Problem,
			UnknownProxy = now - _unknownProxyUtc < TimeSpan.FromDays(7),
			YouAreHome = youAreHome,
			DlnaInstalled = DlnaInstalled()
		};
	}

	public async Task<object> CreatePass(NewPass r, User admin)
	{
		var now = DateTime.UtcNow;
		string name = Plugin.Text(r.Name, 60);
		if (name.Length == 0)
		{
			throw new GuestError(400, "Give the pass a name, for example \"Weekend at the lake\".");
		}
		var known = Libraries().Select(l => l.Id).ToHashSet();
		var libraries = new List<Guid>();
		foreach (var raw in r.Libraries ?? new List<string>())
		{
			if (!Guid.TryParse(raw, out var id) || !known.Contains(id))
			{
				throw new GuestError(400, "One of the chosen libraries does not exist any more. Reload the page.");
			}
			if (!libraries.Contains(id))
			{
				libraries.Add(id);
			}
		}
		if (libraries.Count == 0)
		{
			throw new GuestError(400, "Choose at least one library.");
		}
		var end = AsUtc(r.EndUtc);
		CheckEnd(end, now);
		if (r.MaxGuests < 1 || r.MaxGuests > MaxGuestsPerPass)
		{
			throw new GuestError(400, $"A pass is for 1 to {MaxGuestsPerPass} people.");
		}
		if (r.DevicesPerGuest < 1 || r.DevicesPerGuest > MaxDevicesPerGuest)
		{
			throw new GuestError(400, $"Each guest can use 1 to {MaxDevicesPerGuest} devices.");
		}
		if (r.StreamsPerGuest < 1 || r.StreamsPerGuest > Math.Min(MaxStreamsPerGuest, r.DevicesPerGuest))
		{
			throw new GuestError(400, "Each guest can play on 1 device at a time, up to the number of devices.");
		}
		string? ratingName = null;
		if (r.MaxRating != null)
		{
			var rating = Ratings().FirstOrDefault(x => x.Score == r.MaxRating && x.SubScore == r.MaxSubRating)
				?? throw new GuestError(400, "That rating is not known on this server. Reload the page.");
			ratingName = rating.Name;
		}

		string token = Secrets.NewToken();
		string? code = r.WithCode ? Secrets.NewCode() : null;
		await _gate.WaitAsync().ConfigureAwait(false);
		PassRecord pass;
		try
		{
			pass = _store.Change(d =>
			{
				if (d.Passes.Count(p => !p.HasEnded(now)) >= MaxOpenPasses)
				{
					throw new GuestError(409, $"There are already {MaxOpenPasses} open passes. End one first.");
				}
				var p = new PassRecord
				{
					Id = Guid.NewGuid(),
					Name = name,
					Libraries = libraries,
					CreatedUtc = now,
					EndUtc = end,
					MaxGuests = r.MaxGuests,
					DevicesPerGuest = r.DevicesPerGuest,
					StreamsPerGuest = r.StreamsPerGuest,
					MaxRating = r.MaxRating,
					MaxSubRating = r.MaxRating == null ? null : r.MaxSubRating,
					RatingName = ratingName,
					RemoteAccess = r.RemoteAccess,
					CreatedBy = admin.Id,
					CreatedByName = Plugin.Config.InviterName,
					LinkHash = _secrets.Hash("link", token),
					CodeHash = code == null ? null : _secrets.Hash("code", code)
				};
				d.Passes.Add(p);
				return p;
			});
		}
		finally
		{
			_gate.Release();
		}
		_logger.LogInformation("Guest pass {Pass} created by {Admin}, open until {End:u}", name, admin.Username, end);
		await Log("Guest pass \"" + name + "\" created by " + admin.Username, "MkiGuestPassCreated", admin.Id,
			"Open until " + end.ToString("u", CultureInfo.InvariantCulture) + " for up to " + r.MaxGuests + " people.").ConfigureAwait(false);
		return Secret(pass, token, code);
	}

	// A new link (and code) for a pass: the old ones stop working at once. Guests who joined are not affected.
	public async Task<object> NewLink(Guid passId, User admin)
	{
		string token = Secrets.NewToken();
		string? code = null;
		await _gate.WaitAsync().ConfigureAwait(false);
		PassRecord pass;
		try
		{
			pass = _store.Change(d =>
			{
				var p = d.Pass(passId) ?? throw new GuestError(404, "That pass does not exist any more.");
				if (p.HasEnded(DateTime.UtcNow))
				{
					throw new GuestError(409, "This pass has ended. Extend it first.");
				}
				p.LinkHash = _secrets.Hash("link", token);
				if (p.CodeHash != null)
				{
					code = Secrets.NewCode();
					p.CodeHash = _secrets.Hash("code", code);
				}
				return p;
			});
		}
		finally
		{
			_gate.Release();
		}
		_logger.LogInformation("New link for guest pass {Pass} made by {Admin}; the old link no longer works", pass.Name, admin.Username);
		return Secret(pass, token, code);
	}

	// Moves the end of a pass, earlier or later. Guests locked because the pass ended get in again, but only when
	// the administrator asked to open it again: a page that still shows the pass as open must not reopen it.
	public async Task Extend(Guid passId, DateTime endUtc, bool reopen, User admin)
	{
		var now = DateTime.UtcNow;
		var end = AsUtc(endUtc);
		CheckEnd(end, now);
		await _gate.WaitAsync().ConfigureAwait(false);
		try
		{
			var unlock = _store.Change(d =>
			{
				var p = d.Pass(passId) ?? throw new GuestError(404, "That pass does not exist any more.");
				if (p.HasEnded(DateTime.UtcNow) && !reopen)
				{
					throw new GuestError(409, "This pass has ended in the meantime. Nothing was changed; look again before opening it again.");
				}
				p.EndUtc = end;
				p.EndedEarlyUtc = null;
				var back = new List<Guid>();
				foreach (var g in p.Guests)
				{
					if (g.State == GuestState.Locked)
					{
						g.State = GuestState.Active;
						g.LockedUtc = null;
						back.Add(g.UserId);
					}
					g.Warned = false;
				}
				return back;
			});
			foreach (var id in unlock)
			{
				await SetDisabled(id, false).ConfigureAwait(false);
			}
			var name = _store.Read(d => d.Pass(passId)?.Name);
			_logger.LogInformation("Guest pass {Pass} now ends {End:u} (changed by {Admin})", name, end, admin.Username);
			await Log("Guest pass \"" + name + "\" now ends " + end.ToString("u", CultureInfo.InvariantCulture), "MkiGuestPassChanged", admin.Id, null).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	// Where the guests of an open pass may join and watch. Limited to the home network, the guests' devices that are
	// away right now are signed out; Jellyfin refuses their other devices outside the home network by itself.
	public async Task SetWhere(Guid passId, bool remote, bool from, User admin)
	{
		await _gate.WaitAsync().ConfigureAwait(false);
		try
		{
			var (name, guests) = _store.Change(d =>
			{
				var p = d.Pass(passId) ?? throw new GuestError(404, "That pass does not exist any more.");
				if (p.HasEnded(DateTime.UtcNow))
				{
					throw new GuestError(409, "This pass has ended in the meantime. Nothing was changed.");
				}
				if (p.RemoteAccess != from)
				{
					throw new GuestError(409, "Where this pass works was changed in the meantime. Nothing was changed; look again.");
				}
				p.RemoteAccess = remote;
				return (p.Name, p.Guests.Select(g => g.UserId).ToList());
			});
			foreach (var id in guests)
			{
				await SetRemote(id, remote).ConfigureAwait(false);
			}
			if (!remote)
			{
				await SignOutAway(guests).ConfigureAwait(false);
			}
			string where = remote ? "home and away" : "the home network only";
			_logger.LogInformation("Guest pass {Pass} now works for {Where} (changed by {Admin})", name, where, admin.Username);
			await Log("Guest pass \"" + name + "\" now works for " + where, "MkiGuestPassChanged", admin.Id, null).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task EndPass(Guid passId, User admin)
	{
		await _gate.WaitAsync().ConfigureAwait(false);
		try
		{
			var name = _store.Change(d =>
			{
				var p = d.Pass(passId) ?? throw new GuestError(404, "That pass does not exist any more.");
				var now = DateTime.UtcNow;
				if (!p.HasEnded(now))
				{
					p.EndUtc = now;
					p.EndedEarlyUtc = now;
				}
				return p.Name;
			});
			await LockEnded(false).ConfigureAwait(false);
			_logger.LogInformation("Guest pass {Pass} ended by {Admin}", name, admin.Username);
			await Log("Guest pass \"" + name + "\" ended by " + admin.Username, "MkiGuestPassEnded", admin.Id, null).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	// Deletes every guest account of the pass now, then the pass itself. With the number of guests the administrator
	// was shown, nobody who joined since is deleted unseen.
	public async Task DeletePass(Guid passId, int? guestsSeen, User admin)
	{
		await _gate.WaitAsync().ConfigureAwait(false);
		try
		{
			var pass = _store.Read(d => d.Pass(passId)) ?? throw new GuestError(404, "That pass does not exist any more.");
			if (guestsSeen != null && guestsSeen.Value != pass.Guests.Count)
			{
				throw new GuestError(409, "Someone joined this pass in the meantime. Nothing was deleted; look again.");
			}
			foreach (var g in pass.Guests.ToList())
			{
				await DeleteAccount(g.UserId, "the pass was deleted").ConfigureAwait(false);
			}
			_store.Change(d => d.Passes.RemoveAll(p => p.Id == passId));
			_logger.LogInformation("Guest pass {Pass} and its guest accounts deleted by {Admin}", pass.Name, admin.Username);
			await Log("Guest pass \"" + pass.Name + "\" deleted by " + admin.Username, "MkiGuestPassDeleted", admin.Id, null).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task EndGuest(Guid userId, User admin)
	{
		await _gate.WaitAsync().ConfigureAwait(false);
		try
		{
			var name = _store.Change(d =>
			{
				var found = d.Guest(userId) ?? throw new GuestError(404, "That guest does not exist any more.");
				var g = found.Guest;
				if (g.State != GuestState.Ended)
				{
					g.State = GuestState.Ended;
					g.LockedUtc ??= DateTime.UtcNow;
				}
				return g.UserName;
			});
			await Lock(userId, "Your guest access was ended.").ConfigureAwait(false);
			_logger.LogInformation("Guest {Guest} ended by {Admin}", name, admin.Username);
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task DeleteGuest(Guid userId, User admin)
	{
		await _gate.WaitAsync().ConfigureAwait(false);
		try
		{
			var name = _store.Read(d => d.Guest(userId)?.Guest.UserName) ?? throw new GuestError(404, "That guest does not exist any more.");
			await DeleteAccount(userId, "deleted by " + admin.Username).ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	// ---------- the join page (anonymous) ----------

	// What the join page shows before the guest types a name. Only for a valid link or code.
	public object Peek(JoinRequest r, JoinOrigin origin)
	{
		string key = Throttle.Key(origin.ThrottleAddress ?? origin.Address);
		if (!_throttle.Request(key))
		{
			throw TooMany();
		}
		var pass = Resolve(r, key);
		var now = DateTime.UtcNow;
		var names = Libraries().ToDictionary(l => l.Id, l => l.Name);
		bool local = IsLocal(origin);
		return new
		{
			// Lets a browser that joined this pass before offer "continue" instead of using another place.
			Id = pass.Id.ToString("N", CultureInfo.InvariantCulture),
			pass.Name,
			From = pass.CreatedByName,
			ServerName = _host.FriendlyName,
			pass.EndUtc,
			Ended = pass.HasEnded(now),
			PlacesLeft = Math.Max(0, pass.MaxGuests - pass.Used),
			pass.MaxGuests,
			Libraries = pass.Libraries.Where(names.ContainsKey).Select(id => names[id]).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase),
			Rating = pass.RatingName,
			pass.DevicesPerGuest,
			HomeOnly = !pass.RemoteAccess,
			CanJoinHere = pass.RemoteAccess || local,
			NeedsUpdate = PasswordPolicyOutdated()
		};
	}

	public async Task<object> Redeem(JoinRequest r, JoinOrigin origin)
	{
		var ip = origin.Address;
		string key = Throttle.Key(origin.ThrottleAddress ?? ip);
		if (!_throttle.Request(key))
		{
			throw TooMany();
		}
		var found = Resolve(r, key);
		string name = CleanName(r.Name) ?? throw new GuestError(400, "Type your first name: letters, numbers, spaces, - ' and . only, up to " + MaxNameLength + " characters.");
		var (deviceId, deviceName) = Device(r);
		bool local = IsLocal(origin);

		await _gate.WaitAsync().ConfigureAwait(false);
		try
		{
			var now = DateTime.UtcNow;
			// Checked again under the lock: the pass may have changed while this request waited.
			var pass = _store.Read(d => d.Pass(found.Id)) ?? throw Invalid();
			CheckJoinable(pass, local, now);
			if (_store.Read(d => d.AllGuests().Count()) >= MaxGuestAccounts)
			{
				throw new GuestError(409, "This server has as many guest accounts as it allows right now. Ask the person who invited you.");
			}
			string userName = FreeUserName(name);
			string tempName = TempPrefix + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
			_store.Change(d =>
			{
				var p = d.Pass(pass.Id) ?? throw Invalid();
				p.Used++;
				d.Pending.Add(new PendingAccount { UserName = tempName, StartedUtc = now });
			});

			User user;
			try
			{
				user = await CreateAccount(tempName, userName, pass).ConfigureAwait(false);
			}
			catch
			{
				_store.Change(d =>
				{
					var p = d.Pass(pass.Id);
					if (p != null)
					{
						p.Used = Math.Max(0, p.Used - 1);
					}
				});
				throw;
			}

			string rejoin = Secrets.NewToken();
			_store.Change(d =>
			{
				d.Pass(pass.Id)!.Guests.Add(new GuestRecord
				{
					UserId = user.Id,
					Name = name,
					UserName = user.Username,
					JoinedUtc = now,
					Remote = !local,
					State = GuestState.Active,
					RejoinHash = _secrets.Hash("rejoin", rejoin)
				});
				d.Pending.RemoveAll(x => x.UserName == tempName);
			});

			AuthenticationResult auth;
			try
			{
				auth = await SignIn(user, deviceId, deviceName, ip).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Guest account {User} was created but could not be signed in; it is deleted again", user.Username);
				await DeleteAccount(user.Id, "sign-in failed").ConfigureAwait(false);
				_store.Change(d =>
				{
					var p = d.Pass(pass.Id);
					if (p != null)
					{
						p.Used = Math.Max(0, p.Used - 1);
					}
				});
				throw new GuestError(500, "Something went wrong while signing you in. Please try again.");
			}

			_logger.LogInformation("{User} joined with pass {Pass} from {Address}", user.Username, pass.Name, ip);
			await Log(user.Username + " joined with guest pass \"" + pass.Name + "\"", "MkiGuestPassJoined", user.Id,
				(local ? "From the home network" : "From the internet") + ", until " + pass.EndUtc.ToString("u", CultureInfo.InvariantCulture) + ".").ConfigureAwait(false);
			return Joined(auth, pass, name, rejoin);
		}
		finally
		{
			_gate.Release();
		}
	}

	// A guest who joined before, back on a browser that kept its rejoin key: signs in again without using a place.
	public async Task<object> Rejoin(JoinRequest r, JoinOrigin origin)
	{
		var ip = origin.Address;
		string key = Throttle.Key(origin.ThrottleAddress ?? ip);
		if (!_throttle.Request(key))
		{
			throw TooMany();
		}
		if (_throttle.SecretBlocked(key))
		{
			throw TooMany();
		}
		string? rejoin = r.RejoinKey;
		if (!Secrets.LooksLikeToken(rejoin))
		{
			_throttle.SecretFailed(key);
			throw new GuestError(404, "This browser's guest sign-in is not valid any more.");
		}
		var (deviceId, deviceName) = Device(r);
		bool local = IsLocal(origin);

		await _gate.WaitAsync().ConfigureAwait(false);
		try
		{
			var now = DateTime.UtcNow;
			var hit = _store.Read(d => d.AllGuests().Where(x => _secrets.Matches(x.Guest.RejoinHash, "rejoin", rejoin!)).Select(x => ((PassRecord, GuestRecord)?)x).FirstOrDefault());
			if (hit == null)
			{
				_throttle.SecretFailed(key);
				throw new GuestError(404, "This browser's guest sign-in is not valid any more.");
			}
			var (pass, guest) = hit.Value;
			if (guest.State == GuestState.Paused)
			{
				throw new GuestError(410, "Guest access is paused on this server at the moment.", "paused");
			}
			if (guest.State == GuestState.Ended)
			{
				throw new GuestError(410, "The person who invited you ended your guest access.", "removed");
			}
			if (guest.State != GuestState.Active || pass.HasEnded(now))
			{
				throw new GuestError(410, "This guest pass has ended. Thanks for watching!", "ended");
			}
			if (PasswordPolicyOutdated())
			{
				throw NeedsUpdate();
			}
			if (!pass.RemoteAccess && !local)
			{
				throw new GuestError(403, "This guest pass works only on the home network.", "home");
			}
			var user = _users.GetUserById(guest.UserId);
			if (user == null || !UsesGuestSignIn(user) || user.HasPermission(PermissionKind.IsDisabled))
			{
				throw new GuestError(410, "Your guest access has ended.");
			}
			// This browser's old sign-in is replaced, every other device counts.
			foreach (var old in _devices.GetDevices(new DeviceQuery { UserId = user.Id, DeviceId = deviceId }).Items.ToList())
			{
				await SignOutDevice(old).ConfigureAwait(false);
			}
			int devices = DeviceCount(user.Id);
			if (devices >= pass.DevicesPerGuest)
			{
				throw new GuestError(409, "You are already signed in on " + Plural(devices, "device") + ", the most this pass allows. Sign out of Jellyfin on one of them first (in its menu or settings), or ask the person who invited you.");
			}
			var auth = await SignIn(user, deviceId, deviceName, ip).ConfigureAwait(false);
			_logger.LogInformation("{User} signed in again from {Address}", user.Username, ip);
			return Joined(auth, pass, guest.Name, null);
		}
		finally
		{
			_gate.Release();
		}
	}

	// For the badge in the web client. Only ever about the caller's own account.
	public object Me(User user)
	{
		var found = _store.Read(d => d.Guest(user.Id));
		if (found == null || !UsesGuestSignIn(user))
		{
			return new { Guest = false };
		}
		var (pass, guest) = found.Value;
		return new
		{
			Guest = true,
			guest.Name,
			PassId = pass.Id.ToString("N", CultureInfo.InvariantCulture),
			Pass = pass.Name,
			pass.EndUtc,
			Plugin.Config.WarnHours,
			Devices = DeviceCount(user.Id),
			pass.DevicesPerGuest,
			QuickConnect = _config.Configuration.QuickConnectAvailable
		};
	}

	// ---------- checks used by the gate and the event handlers (never wait for the lock) ----------

	public bool AnyGuests() => _store.Read(d => d.Passes.Any(p => p.Guests.Count > 0)) || CreatingNow() != null;

	public (PassRecord Pass, GuestRecord Guest)? Find(Guid userId) => _store.Read(d => d.Guest(userId));

	public static string? CreatingNow()
	{
		if (AppContext.GetData(CreatingKey) is string s)
		{
			var parts = s.Split('|');
			if (parts.Length == 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
				&& DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < TimeSpan.FromSeconds(30))
			{
				return parts[0];
			}
		}
		return null;
	}

	public int DeviceCount(Guid userId) => _devices.GetDevices(new DeviceQuery { UserId = userId }).Items.Count;

	// Signs out one device of a guest. Jellyfin's own Logout(device) also ends the sessions of any other user with the
	// same device id, and a guest chooses their device id: only the guest's own sessions are ended here.
	public async Task SignOutDevice(Jellyfin.Database.Implementations.Entities.Security.Device device)
	{
		await _devices.DeleteDevice(device).ConfigureAwait(false);
		foreach (var s in _sessions.Sessions.Where(s => s.UserId == device.UserId && string.Equals(s.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase)).ToList())
		{
			await _sessions.ReportSessionEnded(s.Id).ConfigureAwait(false);
		}
	}

	public void NoteUnknownProxy() => _unknownProxyUtc = DateTime.UtcNow;

	// What the administrator sees of a guest's devices: names as the apps report them, never their tokens.
	private object[] DeviceList(Guid userId) => _devices.GetDevices(new DeviceQuery { UserId = userId }).Items
		.OrderByDescending(d => d.DateLastActivity)
		.Select(d => (object)new { Name = Plugin.Text(d.DeviceName, 40), App = Plugin.Text(d.AppName, 40), LastSeenUtc = d.DateLastActivity })
		.ToArray();

	// The end of the next pass that is still open, so its guests are locked right at the end.
	public DateTime? NextEnd()
	{
		var now = DateTime.UtcNow;
		var ends = _store.Read(d => d.Passes.Where(p => !p.HasEnded(now) && p.Guests.Any(g => g.State == GuestState.Active)).Select(p => p.EndUtc).ToList());
		return ends.Count == 0 ? null : ends.Min();
	}

	// Jellyfin started again, or the plugin was switched back on in the dashboard before Jellyfin restarted.
	public async Task Resume()
	{
		if (!_store.Read(d => d.AllGuests().Any(x => x.Guest.State == GuestState.Paused)))
		{
			return;
		}
		await _gate.WaitAsync().ConfigureAwait(false);
		try
		{
			await Unpause().ConfigureAwait(false);
		}
		finally
		{
			_gate.Release();
		}
	}

	// ---------- expiry (run by ExpiryService under the lock) ----------

	// Every 30 seconds; "thorough" every few minutes also looks at every locked account and at accounts gone elsewhere.
	public async Task Sweep(bool starting, bool thorough)
	{
		await _gate.WaitAsync().ConfigureAwait(false);
		try
		{
			if (starting || thorough)
			{
				// Nothing is being created while this runs (same lock), so any record left is a failed creation.
				await CleanPending().ConfigureAwait(false);
			}
			if (starting)
			{
				await Unpause().ConfigureAwait(false);
			}
			await Warn().ConfigureAwait(false);
			await LockEnded(thorough).ConfigureAwait(false);
			await DeleteDue().ConfigureAwait(false);
			if (thorough)
			{
				await ForgetGone().ConfigureAwait(false);
				DropOldPasses();
			}
		}
		finally
		{
			_gate.Release();
		}
	}

	// Accounts with the guest sign-in that no pass knows (for example after passes.json was lost): locked, never
	// left usable. To keep one as a regular user, an administrator sets a password on it first, then switches it to
	// another sign-in provider. Accounts this plugin started to create and never finished are deleted.
	public async Task LockStrays()
	{
		await _gate.WaitAsync().ConfigureAwait(false);
		try
		{
			var known = _store.Read(d => d.AllGuests().Select(x => x.Guest.UserId).ToHashSet());
			string? creating = CreatingNow();
			foreach (var u in _users.GetUsers().ToList())
			{
				if (known.Contains(u.Id) || string.Equals(u.Username, creating, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				if (Unfinished(u))
				{
					_logger.LogWarning("Deleting {User}: a guest account that was never finished", u.Username);
					await _sessions.RevokeUserTokens(u.Id, null!).ConfigureAwait(false);
					await _users.DeleteUserAsync(u.Id).ConfigureAwait(false);
					continue;
				}
				if (!UsesGuestSignIn(u) || u.HasPermission(PermissionKind.IsDisabled))
				{
					continue;
				}
				_logger.LogWarning("Guest account {User} belongs to no guest pass; it is locked", u.Username);
				await SetDisabled(u.Id, true).ConfigureAwait(false);
				await _sessions.RevokeUserTokens(u.Id, null!).ConfigureAwait(false);
			}
		}
		finally
		{
			_gate.Release();
		}
	}

	// Jellyfin stops, or the plugin is being disabled: every guest is locked until it runs again, so nobody keeps access
	// without expiry and without the request checks, for example when a Jellyfin update stops loading the plugin.
	// signOut: also stop playback and sign out every device (disabled for good); otherwise the sign-ins are kept, so
	// guests carry on after a restart, but Jellyfin refuses them while the account is locked.
	public async Task PauseAll(bool signOut)
	{
		await _gate.WaitAsync().ConfigureAwait(false);
		try
		{
			var ids = _store.Change(d =>
			{
				var list = new List<Guid>();
				foreach (var (_, g) in d.AllGuests())
				{
					if (g.State == GuestState.Active)
					{
						g.State = GuestState.Paused;
						list.Add(g.UserId);
					}
				}
				return list;
			});
			foreach (var id in ids)
			{
				try
				{
					if (signOut)
					{
						await Lock(id, null).ConfigureAwait(false);
					}
					else
					{
						await SetDisabled(id, true).ConfigureAwait(false);
					}
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Could not lock a guest account while MKI Guest Pass stops");
				}
			}
			if (ids.Count > 0)
			{
				_logger.LogInformation("MKI Guest Pass stops: {Count} guest accounts are locked until it runs again", ids.Count);
			}
		}
		finally
		{
			_gate.Release();
		}
	}

	public async Task DeleteAll()
	{
		await _gate.WaitAsync().ConfigureAwait(false);
		try
		{
			foreach (var id in _store.Read(d => d.AllGuests().Select(x => x.Guest.UserId).ToList()))
			{
				await DeleteAccount(id, "the plugin was uninstalled").ConfigureAwait(false);
			}
			foreach (var u in _users.GetUsers().Where(UsesGuestSignIn).ToList())
			{
				await DeleteAccount(u.Id, "the plugin was uninstalled").ConfigureAwait(false);
			}
			_store.Change(d => d.Passes.Clear());
		}
		finally
		{
			_gate.Release();
		}
	}

	// Plays more streams than the pass allows: the newest one is stopped.
	public async Task CheckStreams(SessionInfo session, Guid userId)
	{
		var found = Find(userId);
		if (found == null)
		{
			return;
		}
		int allowed = found.Value.Pass.StreamsPerGuest;
		int playing = _sessions.Sessions.Count(s => s.UserId == userId && s.NowPlayingItem != null);
		if (playing <= allowed)
		{
			return;
		}
		await _sessions.SendPlaystateCommand(null!, session.Id, new PlaystateRequest { Command = PlaystateCommand.Stop }, CancellationToken.None).ConfigureAwait(false);
		await Message(session.Id, allowed == 1
			? "Your guest pass plays on one device at a time. Stop it on the other device first."
			: "Your guest pass plays on " + allowed + " devices at a time. Stop it on another device first.").ConfigureAwait(false);
		_logger.LogInformation("Stopped a stream of guest {User}: more than {Allowed} at a time", found.Value.Guest.UserName, allowed);
	}

	// ---------- internals ----------

	private async Task Warn()
	{
		int hours = Plugin.Config.WarnHours;
		if (hours <= 0)
		{
			return;
		}
		var now = DateTime.UtcNow;
		bool Due(PassRecord p, GuestRecord g) => g.State == GuestState.Active && !g.Warned && !p.HasEnded(now) && p.EndUtc - now <= TimeSpan.FromHours(hours);
		if (!_store.Read(d => d.AllGuests().Any(x => Due(x.Pass, x.Guest))))
		{
			return;
		}
		var due = _store.Change(d =>
		{
			var list = new List<(Guid, DateTime)>();
			foreach (var (p, g) in d.AllGuests())
			{
				if (Due(p, g))
				{
					g.Warned = true;
					list.Add((g.UserId, p.EndUtc));
				}
			}
			return list;
		});
		foreach (var (id, end) in due)
		{
			var left = end - now;
			string when = left.TotalMinutes < 90 ? Plural(Math.Max(1, (int)Math.Round(left.TotalMinutes)), "minute") : Plural((int)Math.Round(left.TotalHours), "hour");
			foreach (var s in _sessions.Sessions.Where(s => s.UserId == id).ToList())
			{
				await Message(s.Id, "Your guest access ends in " + when + ".").ConfigureAwait(false);
			}
		}
	}

	// Active guests of ended passes are locked. A thorough run also locks again any locked guest account that was
	// switched back on in Jellyfin's user editor or got a sign-in: the pass has to be extended for that.
	private async Task LockEnded(bool thorough)
	{
		var now = DateTime.UtcNow;
		var ids = new List<Guid>();
		if (_store.Read(d => d.AllGuests().Any(x => x.Guest.State == GuestState.Active && x.Pass.HasEnded(now))))
		{
			ids = _store.Change(d =>
			{
				var list = new List<Guid>();
				foreach (var (p, g) in d.AllGuests())
				{
					if (g.State == GuestState.Active && p.HasEnded(now))
					{
						g.State = GuestState.Locked;
						g.LockedUtc = p.EndUtc < now ? p.EndUtc : now;
						list.Add(g.UserId);
					}
				}
				return list;
			});
		}
		foreach (var id in ids)
		{
			var u = _users.GetUserById(id);
			if (u == null || !UsesGuestSignIn(u))
			{
				continue;
			}
			try
			{
				await Lock(id, "Your guest access has ended. Thanks for watching!").ConfigureAwait(false);
				_logger.LogInformation("{User} locked: the pass ended", u.Username);
			}
			catch (Exception ex)
			{
				// Tried again on the next check (seconds away), and the other guests are locked meanwhile.
				_logger.LogError(ex, "Could not lock guest {User}; trying again shortly", u.Username);
				_store.Change(d =>
				{
					var g = d.Guest(id);
					if (g != null && g.Value.Guest.State == GuestState.Locked)
					{
						g.Value.Guest.State = GuestState.Active;
					}
				});
			}
		}
		if (!thorough)
		{
			return;
		}
		var locked = _store.Read(d => d.AllGuests().Where(x => x.Guest.State != GuestState.Active).Select(x => x.Guest.UserId).ToList());
		foreach (var id in locked.Except(ids))
		{
			var u = _users.GetUserById(id);
			if (u != null && UsesGuestSignIn(u) && (!u.HasPermission(PermissionKind.IsDisabled) || DeviceCount(id) > 0))
			{
				_logger.LogInformation("{User} was usable again although its access ended; locked again", u.Username);
				await Lock(id, null).ConfigureAwait(false);
			}
		}
	}

	private async Task DeleteDue()
	{
		var now = DateTime.UtcNow;
		int days = Plugin.Config.DeleteAfterDays;
		var due = _store.Read(d => d.AllGuests()
			.Where(x => (x.Guest.State == GuestState.Locked || x.Guest.State == GuestState.Ended) && x.Guest.LockedUtc != null && x.Guest.LockedUtc.Value.AddDays(days) <= now)
			.Select(x => x.Guest.UserId).ToList());
		foreach (var id in due)
		{
			await DeleteAccount(id, "the pass ended").ConfigureAwait(false);
		}
	}

	// Guests whose account was deleted elsewhere, or moved to another sign-in provider by an administrator. One moved
	// without a password first would let anyone sign in with an empty one under a name easy to guess: it is disabled.
	private async Task ForgetGone()
	{
		var gone = _store.Read(d => d.AllGuests().Select(x => x.Guest).Where(g =>
		{
			var u = _users.GetUserById(g.UserId);
			return u == null || !UsesGuestSignIn(u);
		}).Select(g => (g.UserId, g.UserName)).ToList());
		if (gone.Count == 0)
		{
			return;
		}
		foreach (var (id, name) in gone)
		{
			var u = _users.GetUserById(id);
			if (u != null && string.IsNullOrEmpty(u.Password) && !u.HasPermission(PermissionKind.IsDisabled))
			{
				try
				{
					var policy = _users.GetUserDto(u).Policy;
					policy.IsDisabled = true;
					await _users.UpdatePolicyAsync(id, policy).ConfigureAwait(false);
					await _sessions.RevokeUserTokens(id, null!).ConfigureAwait(false);
					_logger.LogWarning("Former guest {User} got another sign-in but has no password; it is disabled until it has one", name);
					await Log(name + " disabled: no password", "MkiGuestPassNoPassword", id, "This former guest account was moved to another sign-in without a password. Set a password on it, then enable it.").ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Could not disable former guest {User}, which has no password", name);
					continue;
				}
			}
			_logger.LogInformation("{User} is no longer managed by MKI Guest Pass (deleted, or given another sign-in)", name);
		}
		var ids = gone.Select(x => x.UserId).ToHashSet();
		_store.Change(d =>
		{
			foreach (var p in d.Passes)
			{
				p.Guests.RemoveAll(g => ids.Contains(g.UserId));
			}
		});
	}

	// An ended pass without guests stays listed for a day, then goes.
	private void DropOldPasses()
	{
		var now = DateTime.UtcNow;
		if (_store.Read(d => d.Passes.Any(p => Old(p, now))))
		{
			_store.Change(d => d.Passes.RemoveAll(p => Old(p, now)));
		}

		static bool Old(PassRecord p, DateTime now) => p.HasEnded(now) && p.Guests.Count == 0 && p.EndUtc.AddDays(1) < now;
	}

	// Accounts whose creation failed or was cut off by a stop: deleted. A record stays until its account is gone.
	private async Task CleanPending()
	{
		var pending = _store.Read(d => d.Pending.ToList());
		if (pending.Count == 0)
		{
			return;
		}
		var known = _store.Read(d => d.AllGuests().Select(x => x.Guest.UserId).ToHashSet());
		var done = new List<PendingAccount>();
		foreach (var p in pending)
		{
			var u = (p.UserId is Guid id ? _users.GetUserById(id) : null) ?? _users.GetUserByName(p.UserName);
			if (u != null && known.Contains(u.Id))
			{
				done.Add(p);
				continue;
			}
			if (u != null && (UsesGuestSignIn(u) || Unfinished(u) || (p.UserId == u.Id && string.IsNullOrEmpty(u.Password))))
			{
				try
				{
					_logger.LogWarning("Deleting {User}: a guest account that was never finished", u.Username);
					await _sessions.RevokeUserTokens(u.Id, null!).ConfigureAwait(false);
					await _users.DeleteUserAsync(u.Id).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Could not delete the unfinished guest account {User}; trying again in a few minutes", u.Username);
					continue;
				}
			}
			done.Add(p);
		}
		_store.Change(d => d.Pending.RemoveAll(x => done.Any(y => y.UserName == x.UserName)));
	}

	// An account this plugin created under its temporary name and never renamed: never anybody else's.
	private static bool Unfinished(User u) =>
		u.Username.StartsWith(TempPrefix, StringComparison.Ordinal)
		&& u.Username.Length == TempPrefix.Length + 32
		&& u.Username[TempPrefix.Length..].All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
		&& string.IsNullOrEmpty(u.Password);

	private async Task Unpause()
	{
		var now = DateTime.UtcNow;
		var back = _store.Change(d =>
		{
			var list = new List<Guid>();
			foreach (var (p, g) in d.AllGuests())
			{
				if (g.State != GuestState.Paused)
				{
					continue;
				}
				if (p.HasEnded(now))
				{
					g.State = GuestState.Locked;
					g.LockedUtc = p.EndUtc;
				}
				else
				{
					g.State = GuestState.Active;
					list.Add(g.UserId);
				}
			}
			return list;
		});
		foreach (var id in back)
		{
			await SetDisabled(id, false).ConfigureAwait(false);
		}
		if (back.Count > 0)
		{
			_logger.LogInformation("MKI Guest Pass runs again: {Count} paused guest accounts are unlocked", back.Count);
		}
	}

	private async Task<User> CreateAccount(string tempName, string userName, PassRecord pass)
	{
		// Until the guest settings below are saved, the account has Jellyfin's defaults and no password. It has a
		// random name nobody can sign in with until then, the marker tells MKI Password Policy not to act on it, and a
		// sign-in to it in that moment is undone anyway (GuestEvents). Only then it gets the guest's name.
		AppContext.SetData(CreatingKey, tempName + "|" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
		User? user = null;
		try
		{
			user = await _users.CreateUserAsync(tempName).ConfigureAwait(false);
			var id = user.Id;
			_store.Change(d =>
			{
				var p = d.Pending.FirstOrDefault(x => x.UserName == tempName);
				if (p != null)
				{
					p.UserId = id;
				}
			});
			string reset = _users.GetUserDto(user).Policy?.PasswordResetProviderId ?? user.PasswordResetProviderId;
			await _users.UpdatePolicyAsync(user.Id, GuestPolicy(pass, reset)).ConfigureAwait(false);
			// Anything signed in during that moment is signed out.
			await _sessions.RevokeUserTokens(user.Id, null!).ConfigureAwait(false);
			await _users.RenameUser(user.Id, tempName, userName).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Guest account {User} could not be set up; it is deleted again", userName);
			if (user != null)
			{
				await DeleteUnfinished(user.Id, tempName).ConfigureAwait(false);
			}
			else
			{
				_store.Change(d => d.Pending.RemoveAll(x => x.UserName == tempName && x.UserId == null && _users.GetUserByName(tempName) == null));
			}
			throw new GuestError(500, "Something went wrong while creating your account. Please try again.");
		}
		finally
		{
			AppContext.SetData(CreatingKey, null);
		}
		var made = _users.GetUserById(user.Id);
		if (made == null || !UsesGuestSignIn(made) || made.HasPermission(PermissionKind.IsAdministrator) || !string.Equals(made.Username, userName, StringComparison.Ordinal))
		{
			// Never hand out an account that did not get the guest settings.
			await DeleteUnfinished(user.Id, tempName).ConfigureAwait(false);
			throw new GuestError(500, "Something went wrong while creating your account. Please try again.");
		}
		return made;
	}

	// Its pending record stays when this fails, so the next thorough check tries again.
	private async Task DeleteUnfinished(Guid userId, string tempName)
	{
		try
		{
			await _sessions.RevokeUserTokens(userId, null!).ConfigureAwait(false);
			await _users.DeleteUserAsync(userId).ConfigureAwait(false);
			_store.Change(d => d.Pending.RemoveAll(x => x.UserName == tempName));
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Could not delete an unfinished guest account; trying again in a few minutes");
		}
	}

	// Everything a guest does not need is off. Built from scratch, so no default of a newer Jellyfin slips in unseen.
	private static UserPolicy GuestPolicy(PassRecord p, string resetProvider) => new()
	{
		IsAdministrator = false,
		IsHidden = true,
		IsDisabled = false,
		EnableCollectionManagement = false,
		EnableSubtitleManagement = false,
		EnableLyricManagement = false,
		MaxParentalRating = p.MaxRating,
		MaxParentalSubRating = p.MaxRating == null ? null : p.MaxSubRating,
		BlockUnratedItems = p.MaxRating == null ? Array.Empty<UnratedItem>() : Enum.GetValues<UnratedItem>(),
		BlockedTags = Array.Empty<string>(),
		AllowedTags = Array.Empty<string>(),
		EnableUserPreferenceAccess = false,
		AccessSchedules = Array.Empty<AccessSchedule>(),
		EnableRemoteControlOfOtherUsers = false,
		EnableSharedDeviceControl = false,
		EnableRemoteAccess = p.RemoteAccess,
		EnableLiveTvManagement = false,
		EnableLiveTvAccess = false,
		EnableMediaPlayback = true,
		EnableAudioPlaybackTranscoding = true,
		EnableVideoPlaybackTranscoding = true,
		EnablePlaybackRemuxing = true,
		ForceRemoteSourceTranscoding = false,
		EnableContentDeletion = false,
		EnableContentDeletionFromFolders = Array.Empty<string>(),
		EnableContentDownloading = false,
		EnableSyncTranscoding = false,
		EnableMediaConversion = false,
		EnabledDevices = Array.Empty<string>(),
		EnableAllDevices = true,
		EnabledChannels = Array.Empty<Guid>(),
		EnableAllChannels = false,
		EnabledFolders = p.Libraries.ToArray(),
		EnableAllFolders = false,
		InvalidLoginAttemptCount = 0,
		// No lockout: a password sign-in can never succeed, and strangers trying must not lock the guest out.
		LoginAttemptsBeforeLockout = -1,
		MaxActiveSessions = p.DevicesPerGuest,
		EnablePublicSharing = false,
		RemoteClientBitrateLimit = 0,
		AuthenticationProviderId = GuestSignIn.ProviderId,
		PasswordResetProviderId = resetProvider,
		SyncPlayAccess = SyncPlayUserAccessType.None
	};

	private async Task<AuthenticationResult> SignIn(User user, string deviceId, string deviceName, IPAddress? ip)
	{
		return await _sessions.AuthenticateDirect(new AuthenticationRequest
		{
			UserId = user.Id,
			Username = user.Username,
			App = "Jellyfin Web",
			AppVersion = _host.ApplicationVersionString,
			DeviceId = deviceId,
			DeviceName = deviceName,
			RemoteEndPoint = ip?.ToString() ?? string.Empty
		}).ConfigureAwait(false);
	}

	private object Joined(AuthenticationResult auth, PassRecord pass, string name, string? rejoin) => new
	{
		auth.AccessToken,
		UserId = auth.User.Id.ToString("N", CultureInfo.InvariantCulture),
		auth.ServerId,
		ServerName = _host.FriendlyName,
		PassId = pass.Id.ToString("N", CultureInfo.InvariantCulture),
		Pass = pass.Name,
		Name = name,
		pass.EndUtc,
		RejoinKey = rejoin
	};

	// The page sends UTC ("...Z"); a time with an offset arrives as local time and is converted, never relabelled.
	private static DateTime AsUtc(DateTime d) => d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);

	private static object Secret(PassRecord p, string token, string? code) => new
	{
		Id = p.Id.ToString("N", CultureInfo.InvariantCulture),
		p.Name,
		p.EndUtc,
		p.MaxGuests,
		p.Used,
		p.RemoteAccess,
		From = p.CreatedByName,
		Token = token,
		Code = code == null ? null : Secrets.ShowCode(code)
	};

	// The pass behind a link or code. Every miss counts against the address; codes have the tighter limits.
	private PassRecord Resolve(JoinRequest r, string key)
	{
		if (!string.IsNullOrEmpty(r.Token))
		{
			if (_throttle.SecretBlocked(key))
			{
				throw TooMany();
			}
			if (Secrets.LooksLikeToken(r.Token))
			{
				var hit = _store.Read(d => d.Passes.FirstOrDefault(p => _secrets.Matches(p.LinkHash, "link", r.Token!)));
				if (hit != null)
				{
					return hit;
				}
			}
			_throttle.SecretFailed(key);
			throw Invalid();
		}
		if (!string.IsNullOrEmpty(r.Code))
		{
			if (_throttle.CodesPausedUntil != null)
			{
				throw new GuestError(429, "Code entry is paused for a few minutes because of too many wrong codes. Use the link if you have it, or try again later.");
			}
			if (_throttle.CodeBlocked(key))
			{
				throw TooMany();
			}
			string? code = Secrets.NormalizeCode(r.Code);
			if (code != null)
			{
				var hit = _store.Read(d => d.Passes.FirstOrDefault(p => _secrets.Matches(p.CodeHash, "code", code)));
				if (hit != null)
				{
					return hit;
				}
			}
			_throttle.CodeFailed(key);
			throw new GuestError(404, "That code is not valid. Check it and try again.");
		}
		throw Invalid();
	}

	private void CheckJoinable(PassRecord pass, bool local, DateTime now)
	{
		if (pass.HasEnded(now))
		{
			throw new GuestError(410, "This guest pass has ended.", "ended");
		}
		if (pass.Used >= pass.MaxGuests)
		{
			throw new GuestError(409, pass.MaxGuests == 1 ? "This guest pass has already been used." : "All " + pass.MaxGuests + " places on this guest pass are taken.", "full");
		}
		if (PasswordPolicyOutdated())
		{
			throw NeedsUpdate();
		}
		if (!pass.RemoteAccess && !local)
		{
			throw new GuestError(403, "This guest pass works only on the home network.", "home");
		}
	}

	// MKI Password Policy before 1.1.1.0 does not know guest accounts and signs them out right after they join, so
	// nobody joins (and uses a place) until it is updated.
	private static GuestError NeedsUpdate() => new(409, "A plugin on this server needs an update before guests can join.", "update");

	private static void CheckEnd(DateTime end, DateTime now)
	{
		if (end < now + MinLength - TimeSpan.FromMinutes(1))
		{
			throw new GuestError(400, "The end must be at least " + (int)MinLength.TotalMinutes + " minutes from now.");
		}
		if (end > now + MaxLength + TimeSpan.FromMinutes(1))
		{
			throw new GuestError(400, "A pass can last at most " + (int)MaxLength.TotalDays + " days from now.");
		}
	}

	// Letters (any language), digits, spaces, - ' and . Nothing invisible, nothing that could pass for another account.
	public static string? CleanName(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw) || raw.Length > 100)
		{
			return null;
		}
		// Phone keyboards type a curly apostrophe; it is kept as the plain one.
		string s = raw.Normalize(NormalizationForm.FormC).Replace('\u2019', '\'').Trim();
		var sb = new StringBuilder();
		int marks = 0;
		bool space = false;
		foreach (char c in s)
		{
			var cat = char.GetUnicodeCategory(c);
			if (c == ' ')
			{
				if (!space && sb.Length > 0)
				{
					sb.Append(' ');
				}
				space = true;
				marks = 0;
				continue;
			}
			space = false;
			if (cat == UnicodeCategory.NonSpacingMark)
			{
				if (sb.Length == 0 || ++marks > 2)
				{
					return null;
				}
				sb.Append(c);
				continue;
			}
			marks = 0;
			if (char.IsLetter(c) || cat == UnicodeCategory.DecimalDigitNumber)
			{
				sb.Append(c);
			}
			else if (c == '-' || c == '\'' || c == '.')
			{
				if (sb.Length == 0)
				{
					return null;
				}
				sb.Append(c);
			}
			else
			{
				return null;
			}
		}
		string name = sb.ToString().Trim();
		// Letters of one alphabet only: "Аnna" with a Cyrillic А would look like another guest's name in the lists.
		if (name.Where(char.IsLetter).Select(Script).Distinct().Count() > 1)
		{
			return null;
		}
		return name.Length == 0 || name.Length > MaxNameLength ? null : name;

		static int Script(char c) => c <= '\u024F' || (c >= '\u1E00' && c <= '\u1EFF') ? 0
			: (c >= '\u0370' && c <= '\u03FF') || (c >= '\u1F00' && c <= '\u1FFF') ? 1
			: c >= '\u0400' && c <= '\u052F' ? 2
			: 3;
	}

	private string FreeUserName(string name)
	{
		string baseName = UserPrefix + name;
		if (_users.GetUserByName(baseName) == null)
		{
			return baseName;
		}
		for (int i = 2; i < 100; i++)
		{
			string n = baseName + " " + i.ToString(CultureInfo.InvariantCulture);
			if (_users.GetUserByName(n) == null)
			{
				return n;
			}
		}
		throw new GuestError(409, "Please use another name.");
	}

	// The browser's own device id, so the web client and the server agree on which device this is.
	private static (string Id, string Name) Device(JoinRequest r)
	{
		string id = r.DeviceId ?? string.Empty;
		if (id.Length < 8 || id.Length > 256 || id.Any(c => c < '!' || c > '~'))
		{
			throw new GuestError(400, "This browser sent no usable device id. Reload the page and try again.");
		}
		string name = Plugin.Text(r.DeviceName, 64);
		return (id, name.Length == 0 ? "Web browser" : name);
	}

	// A home address that passed on the request for someone else is a reverse proxy Jellyfin does not know
	// (Networking -> Known proxies): Jellyfin then sees the proxy, not the guest, so the guest is treated as away.
	private bool IsLocal(JoinOrigin origin)
	{
		if (origin.Address == null || !_network.IsInLocalNetwork(origin.Address))
		{
			return false;
		}
		if (origin.ViaUnknownProxy)
		{
			NoteUnknownProxy();
			return false;
		}
		return true;
	}

	private async Task Lock(Guid userId, string? message)
	{
		var sessions = _sessions.Sessions.Where(s => s.UserId == userId).ToList();
		foreach (var s in sessions)
		{
			try
			{
				if (s.NowPlayingItem != null)
				{
					await _sessions.SendPlaystateCommand(null!, s.Id, new PlaystateRequest { Command = PlaystateCommand.Stop }, CancellationToken.None).ConfigureAwait(false);
				}
				if (message != null)
				{
					await Message(s.Id, message).ConfigureAwait(false);
				}
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "Could not reach a session of a guest that is being locked");
			}
		}
		await SetDisabled(userId, true).ConfigureAwait(false);
		await _sessions.RevokeUserTokens(userId, null!).ConfigureAwait(false);
	}

	// Only the "disabled" switch changes; anything an administrator changed on the account stays.
	private async Task SetDisabled(Guid userId, bool disabled)
	{
		var user = _users.GetUserById(userId);
		if (user == null || !UsesGuestSignIn(user) || user.HasPermission(PermissionKind.IsDisabled) == disabled)
		{
			return;
		}
		var policy = _users.GetUserDto(user).Policy;
		policy.IsDisabled = disabled;
		await _users.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);
	}

	// Only the remote access switch changes, like SetDisabled.
	private async Task SetRemote(Guid userId, bool remote)
	{
		var user = _users.GetUserById(userId);
		if (user == null || !UsesGuestSignIn(user) || user.HasPermission(PermissionKind.EnableRemoteAccess) == remote)
		{
			return;
		}
		var policy = _users.GetUserDto(user).Policy;
		policy.EnableRemoteAccess = remote;
		await _users.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);
	}

	// Devices of these guests with a session from outside the home network (or an address that cannot be read).
	private async Task SignOutAway(List<Guid> userIds)
	{
		var away = _sessions.Sessions.Where(s => userIds.Contains(s.UserId) && !AtHome(s.RemoteEndPoint)).Select(s => (s.UserId, s.DeviceId)).Distinct().ToList();
		foreach (var (userId, deviceId) in away)
		{
			foreach (var device in _devices.GetDevices(new DeviceQuery { UserId = userId, DeviceId = deviceId }).Items.ToList())
			{
				await SignOutDevice(device).ConfigureAwait(false);
			}
		}
	}

	private bool AtHome(string? endPoint)
	{
		if (string.IsNullOrEmpty(endPoint))
		{
			return false;
		}
		if (!IPAddress.TryParse(endPoint, out var ip))
		{
			if (!IPEndPoint.TryParse(endPoint, out var ep))
			{
				return false;
			}
			ip = ep.Address;
		}
		return _network.IsInLocalNetwork(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
	}

	private async Task DeleteAccount(Guid userId, string why)
	{
		var user = _users.GetUserById(userId);
		if (user != null && UsesGuestSignIn(user))
		{
			try
			{
				await Lock(userId, null).ConfigureAwait(false);
				await _playlists.RemovePlaylistsAsync(userId).ConfigureAwait(false);
				await _users.DeleteUserAsync(userId).ConfigureAwait(false);
				_logger.LogInformation("Guest account {User} deleted: {Why}", user.Username, why);
				await Log("Guest account " + user.Username + " deleted", "MkiGuestPassDeleted", Guid.Empty, char.ToUpperInvariant(why[0]) + why[1..] + ".").ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Could not delete guest account {User}; it stays locked and is tried again", user.Username);
				return;
			}
		}
		_store.Change(d =>
		{
			foreach (var p in d.Passes)
			{
				p.Guests.RemoveAll(g => g.UserId == userId);
			}
		});
	}

	private async Task Message(string sessionId, string text)
	{
		try
		{
			await _sessions.SendMessageCommand(null!, sessionId, new MessageCommand { Header = "Guest pass", Text = text, TimeoutMs = 15000 }, CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Could not send a message to a guest session");
		}
	}

	private async Task Log(string name, string type, Guid userId, string? overview)
	{
		try
		{
			await _activity.CreateAsync(new ActivityLog(name.Length > 500 ? name[..500] : name, type, userId)
			{
				ShortOverview = overview,
				LogSeverity = LogLevel.Information
			}).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Could not write to the activity log");
		}
	}

	private void OnCodesPaused()
	{
		_logger.LogWarning("MKI Guest Pass: {Count} wrong guest codes within {Minutes} minutes; code entry is paused for {Pause} minutes. Links keep working",
			Throttle.CodeFailuresServerWide, Throttle.Window.TotalMinutes, Throttle.CodePause.TotalMinutes);
		_ = Log("Guest code entry paused: too many wrong codes", "MkiGuestPassCodesPaused", Guid.Empty,
			"Code entry works again in " + Throttle.CodePause.TotalMinutes + " minutes. Pass links are not affected.");
	}

	private bool DlnaInstalled()
	{
		try
		{
			return _plugins.Plugins.Any(p => p.IsEnabledAndSupported && p.Name.Contains("DLNA", StringComparison.OrdinalIgnoreCase));
		}
		catch (Exception)
		{
			return false;
		}
	}

	private bool PasswordPolicyOutdated()
	{
		try
		{
			var copies = _plugins.Plugins.Where(p => p.Id == PasswordPolicyId && p.IsEnabledAndSupported).ToList();
			return copies.Count > 0 && copies.Max(p => p.Version) < PasswordPolicyWithGuests;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private List<(Guid Id, string Name, string Type)> Libraries()
	{
		var list = new List<(Guid, string, string)>();
		foreach (var f in _library.GetVirtualFolders())
		{
			if (Guid.TryParse(f.ItemId, out var id))
			{
				list.Add((id, f.Name, f.CollectionType?.ToString() ?? "mixed"));
			}
		}
		return list.OrderBy(x => x.Item2, StringComparer.CurrentCultureIgnoreCase).ToList();
	}

	public sealed record RatingChoice(int Score, int? SubScore, string Name);

	// The server's ratings (its metadata country), one entry per level, names of the same level joined.
	private List<RatingChoice> Ratings()
	{
		var levels = new List<RatingChoice>();
		foreach (var g in _localization.GetParentalRatings().Where(r => r.RatingScore != null)
			.GroupBy(r => (r.RatingScore!.Score, r.RatingScore.SubScore))
			.OrderBy(g => g.Key.Score).ThenBy(g => g.Key.SubScore ?? -1))
		{
			var names = g.Select(r => r.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3);
			levels.Add(new RatingChoice(g.Key.Score, g.Key.SubScore, string.Join(" / ", names)));
		}
		return levels;
	}

	private static GuestError Invalid() => new(404, "This guest link is not valid.");

	private static GuestError TooMany() => new(429, "Too many tries from this network. Please wait 15 minutes and try again.");

	private static string Plural(int n, string one) => n + " " + (n == 1 ? one : one + "s");
}
