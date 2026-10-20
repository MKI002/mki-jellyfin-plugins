using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Services;

public class Enforcer
{
	public const string DefaultNotifyText = "Your administrator requires a new password. You will be signed out shortly. Set your new password the next time you sign in from a browser, or here: {url}";

	public const string DefaultNoticeText = "Your administrator requires a new password by {date}. Until then everything works as usual. Set it on your phone or computer here: {url}";

	// Reminders during notice periods: session id -> (user, device name, sent at, web). "Web" sessions run the
	// plugin's own script, which shows its popup itself, so they get no server message on top.
	private readonly ConcurrentDictionary<string, (Guid User, string Device, DateTime Sent, bool Web)> _erinnert = new();

	private readonly PolicyStore _store;

	private readonly IUserManager _users;

	private readonly ISessionManager _sessions;

	private readonly WebPatcher _patcher;

	private readonly IDeviceManager _devices;

	private readonly ILogger<Enforcer> _logger;

	public Enforcer(PolicyStore store, IUserManager users, ISessionManager sessions, WebPatcher patcher, IDeviceManager devices, ILogger<Enforcer> logger)
	{
		_store = store;
		_users = users;
		_sessions = sessions;
		_patcher = patcher;
		_devices = devices;
		_logger = logger;
	}

	public static bool IsAdmin(User user) => user.HasPermission(PermissionKind.IsAdministrator);

	public static bool IsExempt(User user) => Plugin.Config.ExemptAdministrators && IsAdmin(user);

	// Flags the users and tells their open apps. Without a notice period every device is signed out after the
	// grace time; with one the users keep access until the deadline and every app they open is reminded.
	public int Require(IEnumerable<Guid> userIds, string reason, string changeUrl, int noticeDays = 0)
	{
		var ids = userIds.Distinct().Where(id => _users.GetUserById(id) != null).ToList();
		if (ids.Count == 0)
		{
			return 0;
		}
		var c = Plugin.Config;
		bool frist = noticeDays > 0;
		int gnade = c.NotifySessions ? Math.Clamp(c.GraceSeconds, 0, 300) : 0;
		foreach (var id in ids)
		{
			_store.Require(id, reason, frist ? TimeSpan.FromDays(noticeDays) : TimeSpan.FromSeconds(gnade), frist, changeUrl);
		}
		_patcher.Sync();
		_logger.LogInformation("New password required from {Count} user(s), reason {Reason}, notice {Days} day(s)", ids.Count, reason, noticeDays);
		// Accounts without any password get no notice period and no grace time.
		var ohne = ids.Select(id => _users.GetUserById(id)).Where(u => u != null && EnforceNoPassword(u)).Select(u => u!.Id).ToList();
		ids = ids.Except(ohne).ToList();

		if (frist)
		{
			// A short head start lets the plugin's own popup in web-based apps report itself first.
			_ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => Remind(), TaskScheduler.Default);
			return ids.Count + ohne.Count;
		}
		if (c.NotifySessions)
		{
			string text = (string.IsNullOrWhiteSpace(c.NotifyText) ? DefaultNotifyText : c.NotifyText).Replace("{url}", changeUrl, StringComparison.OrdinalIgnoreCase);
			foreach (var sitzung in _sessions.Sessions.Where(s => ids.Contains(s.UserId)).ToList())
			{
				_ = Benachrichtigen(sitzung.Id, text);
			}
		}
		_ = Abmelden(ids, gnade);
		return ids.Count + ohne.Count;
	}

	// An account without any password that has rules or an open request is blocked right away, on every sign-in path,
	// until a password is set. Returns true when this call blocked it.
	public bool EnforceNoPassword(User user)
	{
		if (!Policy.HasNoPassword(user) || _store.BlockedNow(user.Id))
		{
			return false;
		}
		if (!_store.MustChange(user.Id) && Policy.RulesFor(user) == null)
		{
			return false;
		}
		_store.RequireNow(user.Id, "nopassword");
		_patcher.Sync();
		_logger.LogInformation("{User} has no password: blocked until one is set", user.Username);
		_ = SignOutEverywhere(user.Id);
		return true;
	}

	// A password someone else knows (set by an administrator, a temporary password, a reset PIN) has to be replaced
	// by the user at the next sign-in. Their devices are signed out right away.
	public void RequireOwnPassword(User user, string reason)
	{
		_store.RequireNow(user.Id, reason);
		_patcher.Sync();
		_logger.LogInformation("{User} must replace a password set by someone else ({Reason})", user.Username, reason);
		_ = SignOutEverywhere(user.Id);
	}

	// Sets a random temporary password that meets the user's rules and returns it once. Only Jellyfin keeps it, as a hash.
	public async Task<string> SetTemporaryPassword(User user)
	{
		var regeln = Policy.RulesFor(user);
		string passwort;
		int versuch = 0;
		do
		{
			passwort = TemporaryPasswords.Create(regeln?.MinLength ?? 0);
		}
		while (regeln != null && PasswordRules.Check(passwort, user.Username, regeln).Count > 0 && ++versuch < 50);
		if (regeln != null && PasswordRules.Check(passwort, user.Username, regeln).Count > 0)
		{
			throw new InvalidOperationException("Could not create a temporary password that meets the user's rules");
		}
		await _users.ChangePassword(user.Id, passwort).ConfigureAwait(false);
		RequireOwnPassword(user, "temporary");
		return passwort;
	}

	// Applies EnforceNoPassword to everyone. Does nothing while the plugin is switched off.
	public void SweepNoPassword()
	{
		if (!Policy.Configured(Plugin.Config) && !_store.AnyMustChange())
		{
			return;
		}
		foreach (var user in _users.GetUsers().ToList())
		{
			EnforceNoPassword(user);
		}
	}

	// The plugin's popup was shown in this web-based app: no server message for these sessions.
	public void MarkWeb(Guid userId, string? deviceId)
	{
		foreach (var sitzung in _sessions.Sessions.Where(s => s.UserId == userId && (deviceId == null || s.DeviceId == deviceId)).ToList())
		{
			_erinnert[sitzung.Id] = (userId, string.IsNullOrEmpty(sitzung.DeviceName) ? sitzung.Client : sitzung.DeviceName, DateTime.UtcNow, true);
		}
	}

	// Sends the notice to every connected app of a user in a notice period: once right away, then again at most
	// once a day per device and only after the app was used since. Runs on a timer, so an app opened days later still gets it.
	public void Remind()
	{
		foreach (var alt in _erinnert.Where(e => !_store.InNotice(e.Value.User)).ToList())
		{
			_erinnert.TryRemove(alt.Key, out _);
		}
		if (!_store.AnyMustChange())
		{
			return;
		}
		foreach (var sitzung in _sessions.Sessions.ToList())
		{
			if (sitzung.UserId == Guid.Empty || sitzung.SessionControllers.Count == 0 || !_store.InNotice(sitzung.UserId))
			{
				continue;
			}
			if (_erinnert.TryGetValue(sitzung.Id, out var zuletzt)
				&& (zuletzt.Web || DateTime.UtcNow - zuletzt.Sent < TimeSpan.FromHours(20) || sitzung.LastActivityDate <= zuletzt.Sent.AddMinutes(1)))
			{
				continue;
			}
			var s = _store.Get(sitzung.UserId);
			string datum = s.EnforceAfterUtc!.Value.ToLocalTime().ToString("dddd, d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture);
			var c = Plugin.Config;
			string text = (string.IsNullOrWhiteSpace(c.NotifyText) ? DefaultNoticeText : c.NotifyText)
				.Replace("{url}", s.ChangeUrl ?? "/MkiPasswordPolicy/Change", StringComparison.OrdinalIgnoreCase)
				.Replace("{date}", datum, StringComparison.OrdinalIgnoreCase);
			_erinnert[sitzung.Id] = (sitzung.UserId, string.IsNullOrEmpty(sitzung.DeviceName) ? sitzung.Client : sitzung.DeviceName, DateTime.UtcNow, false);
			_ = Benachrichtigen(sitzung.Id, text);
		}
	}

	// Tokens of blocked users are refused on every request anyway; this also deletes them, so no other way in
	// (another plugin, a header the gate does not know) can still use one. Catches notice periods that just ended
	// and grace times cut short by a restart.
	public async Task SignOutBlocked()
	{
		foreach (var id in _store.OpenUsers())
		{
			if (_store.SessionsBlocked(id) && _devices.GetDevices(new DeviceQuery { UserId = id }).TotalRecordCount > 0)
			{
				_logger.LogInformation("Signing out {User}: their deadline for a new password has passed", _users.GetUserById(id)?.Username ?? id.ToString());
				await SignOutEverywhere(id).ConfigureAwait(false);
			}
		}
	}

	// A sign-in of a user who must change their password, on a path the gate does not see (another plugin's single
	// sign-on, a client that signs in some other way): its token is deleted before the client gets to use it.
	// Other devices keep a grace time that is still running.
	public async Task UndoSignIn(Guid userId, string? deviceId)
	{
		var user = _users.GetUserById(userId);
		if (user == null)
		{
			return;
		}
		EnforceNoPassword(user);
		if (!_store.MustChange(userId) || _store.InNotice(userId))
		{
			return;
		}
		if (string.IsNullOrEmpty(deviceId) || _store.SessionsBlocked(userId))
		{
			await SignOutEverywhere(userId).ConfigureAwait(false);
			return;
		}
		foreach (var geraet in _devices.GetDevices(new DeviceQuery { UserId = userId, DeviceId = deviceId }).Items.ToList())
		{
			try
			{
				await _sessions.Logout(geraet).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Signing out a device of {User} failed", user.Username);
			}
		}
	}

	public IReadOnlyList<string> RemindedDevices(Guid userId)
	{
		return _erinnert.Values.Where(e => e.User == userId).Select(e => e.Device).Distinct().OrderBy(d => d).ToList();
	}

	public async Task SignOutEverywhere(Guid userId)
	{
		try
		{
			await _sessions.RevokeUserTokens(userId, null!).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Signing out {User} failed", userId);
		}
	}

	private async Task Benachrichtigen(string sitzung, string text)
	{
		try
		{
			// No timeout: Jellyfin's apps then show a dialog that has to be closed instead of a toast that fades away.
			await _sessions.SendMessageCommand(null!, sitzung, new MessageCommand
			{
				Header = "New password required",
				Text = text
			}, CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Could not deliver message to session {Session}", sitzung);
		}
	}

	private async Task Abmelden(List<Guid> ids, int gnade)
	{
		if (gnade > 0)
		{
			await Task.Delay(TimeSpan.FromSeconds(gnade)).ConfigureAwait(false);
		}
		foreach (var id in ids)
		{
			// The user may already have set a new password during the grace time.
			if (_store.MustChange(id))
			{
				await SignOutEverywhere(id).ConfigureAwait(false);
			}
		}
	}
}
