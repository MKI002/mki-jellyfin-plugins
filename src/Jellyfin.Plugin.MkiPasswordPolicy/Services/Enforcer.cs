using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Services;

public class Enforcer
{
	public const string DefaultNotifyText = "Your administrator requires a new password. You will be signed out shortly. Set your new password the next time you sign in from a browser, or here: {url}";

	public const string DefaultNoticeText = "Your administrator requires a new password by {date}. Until then everything works as usual. Set it on your phone or computer here: {url}";

	// Reminders sent during notice periods: session id -> (user, device name, sent at).
	private readonly ConcurrentDictionary<string, (Guid User, string Device, DateTime Sent)> _erinnert = new();

	private readonly PolicyStore _store;

	private readonly IUserManager _users;

	private readonly ISessionManager _sessions;

	private readonly WebPatcher _patcher;

	private readonly ILogger<Enforcer> _logger;

	public Enforcer(PolicyStore store, IUserManager users, ISessionManager sessions, WebPatcher patcher, ILogger<Enforcer> logger)
	{
		_store = store;
		_users = users;
		_sessions = sessions;
		_patcher = patcher;
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

		if (frist)
		{
			Remind();
			return ids.Count;
		}
		if (c.NotifySessions)
		{
			string text = (string.IsNullOrWhiteSpace(c.NotifyText) ? DefaultNotifyText : c.NotifyText).Replace("{url}", changeUrl, StringComparison.OrdinalIgnoreCase);
			foreach (var sitzung in _sessions.Sessions.Where(s => ids.Contains(s.UserId)).ToList())
			{
				_ = Benachrichtigen(sitzung.Id, text, gnade);
			}
		}
		_ = Abmelden(ids, gnade);
		return ids.Count;
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
				&& (DateTime.UtcNow - zuletzt.Sent < TimeSpan.FromHours(20) || sitzung.LastActivityDate <= zuletzt.Sent.AddMinutes(1)))
			{
				continue;
			}
			var s = _store.Get(sitzung.UserId);
			string datum = s.EnforceAfterUtc!.Value.ToLocalTime().ToString("dddd, d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture);
			var c = Plugin.Config;
			string text = (string.IsNullOrWhiteSpace(c.NotifyText) ? DefaultNoticeText : c.NotifyText)
				.Replace("{url}", s.ChangeUrl ?? "/MkiPasswordPolicy/Change", StringComparison.OrdinalIgnoreCase)
				.Replace("{date}", datum, StringComparison.OrdinalIgnoreCase);
			_erinnert[sitzung.Id] = (sitzung.UserId, string.IsNullOrEmpty(sitzung.DeviceName) ? sitzung.Client : sitzung.DeviceName, DateTime.UtcNow);
			_ = Benachrichtigen(sitzung.Id, text, 60);
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

	private async Task Benachrichtigen(string sitzung, string text, int gnade)
	{
		try
		{
			await _sessions.SendMessageCommand(null!, sitzung, new MessageCommand
			{
				Header = "New password required",
				Text = text,
				TimeoutMs = Math.Max(gnade, 10) * 1000
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
