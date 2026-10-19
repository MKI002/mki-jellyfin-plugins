using System;
using System.Collections.Generic;
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

	// Flags the users, tells their open apps and signs every device out after the grace time.
	public int Require(IEnumerable<Guid> userIds, string reason, string changeUrl)
	{
		var ids = userIds.Distinct().Where(id => _users.GetUserById(id) != null).ToList();
		if (ids.Count == 0)
		{
			return 0;
		}
		var c = Plugin.Config;
		int gnade = c.NotifySessions ? Math.Clamp(c.GraceSeconds, 0, 300) : 0;
		foreach (var id in ids)
		{
			_store.Require(id, reason, TimeSpan.FromSeconds(gnade));
		}
		_patcher.Sync();
		_logger.LogInformation("New password required from {Count} user(s), reason {Reason}", ids.Count, reason);

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
