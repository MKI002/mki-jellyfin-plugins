using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MkiPasswordPolicy.Services;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Api;

[ApiController]
[Route("MkiPasswordPolicy")]
public class PasswordPolicyController : ControllerBase
{
	// One answer for every failure, so the anonymous endpoints reveal neither usernames nor who has an open request.
	private const string AllgemeinerFehler = "Wrong username or password, or no password change is pending for this account.";

	private readonly PolicyStore _store;

	private readonly Enforcer _enforcer;

	private readonly Throttle _throttle;

	private readonly WebPatcher _patcher;

	private readonly IUserManager _users;

	private readonly ISessionManager _sessions;

	private readonly IDeviceManager _devices;

	private readonly ILogger<PasswordPolicyController> _logger;

	public PasswordPolicyController(PolicyStore store, Enforcer enforcer, Throttle throttle, WebPatcher patcher, IUserManager users, ISessionManager sessions, IDeviceManager devices, ILogger<PasswordPolicyController> logger)
	{
		_store = store;
		_enforcer = enforcer;
		_throttle = throttle;
		_patcher = patcher;
		_users = users;
		_sessions = sessions;
		_devices = devices;
		_logger = logger;
	}

	// Rules for the change dialog. Needs the current password, so nobody learns anything about an account they cannot open.
	[HttpPost("ChangeRules")]
	[AllowAnonymous]
	public async Task<ActionResult> ChangeRules([FromBody] ChangeRequest anfrage)
	{
		var (benutzer, fehler) = await Pruefen(anfrage).ConfigureAwait(false);
		return benutzer == null ? fehler! : new JsonResult(RegelnDto(Policy.RulesFor(benutzer)));
	}

	// Used by the web dialog and the change page; works without a session because the user cannot sign in yet.
	[HttpPost("Change")]
	[AllowAnonymous]
	public async Task<ActionResult> Change([FromBody] ChangeRequest anfrage)
	{
		var (benutzer, fehler) = await Pruefen(anfrage).ConfigureAwait(false);
		if (benutzer == null)
		{
			return fehler!;
		}
		string neu = anfrage.NewPassword ?? string.Empty;
		if (neu.Length == 0 || neu == (anfrage.CurrentPassword ?? string.Empty))
		{
			return Fehler(StatusCodes.Status400BadRequest, "The new password must not be empty and must differ from the current one.");
		}
		var regeln = Policy.RulesFor(benutzer);
		if (regeln != null)
		{
			var verstoesse = PasswordRules.Check(neu, benutzer.Username, regeln);
			if (verstoesse.Count > 0)
			{
				return Fehler(StatusCodes.Status400BadRequest, PasswordRules.Describe(verstoesse));
			}
		}
		// Jellyfin hashes and stores the password; the plugin keeps only the fact that it passed the check.
		await _users.ChangePassword(benutzer.Id, neu).ConfigureAwait(false);
		_store.Release(benutzer.Id);
		if (regeln != null)
		{
			_store.MarkVerified(benutzer.Id, regeln);
		}
		_patcher.Sync();
		// Like Jellyfin's own password change: every other device is signed out, so whoever knew the old password
		// loses access too. The app the change was made in stays signed in.
		string? behalten = LoginGate.Tokens(Request, LoginGate.MaxTokens).FirstOrDefault(t => _devices.GetDevices(new DeviceQuery { AccessToken = t, Limit = 1 }).Items.FirstOrDefault()?.UserId == benutzer.Id);
		try
		{
			await _sessions.RevokeUserTokens(benutzer.Id, behalten!).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Signing out the other devices of {User} failed", benutzer.Username);
		}
		_logger.LogInformation("{User} set a new password", benutzer.Username);
		return NoContent();
	}

	// Rules for the signed-in user's own password, or for another user's when an admin asks. newUser: the rules a new account gets.
	[HttpGet("MyRules")]
	[Authorize]
	public ActionResult MyRules([FromQuery] Guid? userId, [FromQuery] bool newUser = false)
	{
		var aufrufer = _users.GetUserById(NutzerId());
		if (aufrufer == null)
		{
			return Forbid();
		}
		bool admin = Enforcer.IsAdmin(aufrufer);
		if (newUser)
		{
			return admin ? new JsonResult(RegelnDto(Plugin.Config.ApplyToAdminChanges ? Policy.RulesForNewUser() : null)) : Forbid();
		}
		var ziel = _users.GetUserById(userId ?? aufrufer.Id);
		if (ziel == null || (ziel.Id != aufrufer.Id && !admin))
		{
			return Forbid();
		}
		bool adminFuerAndere = ziel.Id != aufrufer.Id;
		return new JsonResult(RegelnDto(adminFuerAndere && !Plugin.Config.ApplyToAdminChanges ? null : Policy.RulesFor(ziel)));
	}

	// Lets the web client ask before submitting, so the user sees the real reason instead of Jellyfin's generic error.
	[HttpPost("Validate")]
	[Authorize]
	public ActionResult Validate([FromBody] ValidateRequest anfrage)
	{
		Response.Headers.CacheControl = "no-store";
		var aufrufer = _users.GetUserById(NutzerId());
		if (aufrufer == null)
		{
			return Forbid();
		}
		bool admin = Enforcer.IsAdmin(aufrufer);
		RuleSnapshot? regeln;
		string? name;
		if (!string.IsNullOrEmpty(anfrage.NewUsername))
		{
			if (!admin)
			{
				return Forbid();
			}
			regeln = Plugin.Config.ApplyToAdminChanges ? Policy.RulesForNewUser() : null;
			name = anfrage.NewUsername;
		}
		else
		{
			var ziel = _users.GetUserById(anfrage.UserId ?? aufrufer.Id);
			if (ziel == null || (ziel.Id != aufrufer.Id && !admin))
			{
				return Forbid();
			}
			regeln = ziel.Id != aufrufer.Id && !Plugin.Config.ApplyToAdminChanges ? null : Policy.RulesFor(ziel);
			name = ziel.Username;
		}
		var verstoesse = regeln == null ? new List<string>() : PasswordRules.Check(anfrage.Password, name, regeln);
		return new JsonResult(new { Ok = verstoesse.Count == 0, Message = verstoesse.Count == 0 ? null : PasswordRules.Describe(verstoesse) });
	}

	[HttpGet("Change")]
	[AllowAnonymous]
	public ActionResult GetChangePage()
	{
		// The page asks for passwords: no framing, no foreign scripts, no referrer.
		Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
		Response.Headers["X-Frame-Options"] = "DENY";
		Response.Headers["Referrer-Policy"] = "no-referrer";
		Response.Headers["X-Content-Type-Options"] = "nosniff";
		return Datei("change.html", "text/html; charset=utf-8");
	}

	[HttpGet("client.js")]
	[AllowAnonymous]
	public ActionResult GetScript()
	{
		Response.Headers["X-Content-Type-Options"] = "nosniff";
		return Datei("client.js", "application/javascript; charset=utf-8");
	}

	// Called by the plugin's script in web-based apps when it showed its popup, so these sessions get no server message on top.
	[HttpPost("Seen")]
	[Authorize]
	public ActionResult Seen()
	{
		var id = NutzerId();
		if (id != Guid.Empty && _store.InNotice(id))
		{
			_enforcer.MarkWeb(id, User.FindFirstValue("Jellyfin-DeviceId"));
		}
		return NoContent();
	}

	[HttpGet("Users")]
	[Authorize(Policy = "RequiresElevation")]
	public ActionResult GetUsers()
	{
		var c = Plugin.Config;
		var liste = _users.GetUsers().OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase).Select(u =>
		{
			var s = _store.Get(u.Id);
			return new
			{
				u.Id,
				Name = u.Username,
				IsAdmin = Enforcer.IsAdmin(u),
				IsDisabled = u.HasPermission(PermissionKind.IsDisabled),
				IsExempt = Enforcer.IsExempt(u),
				Profile = Policy.AssignedProfile(u.Id, c),
				EffectiveProfile = Enforcer.IsExempt(u) ? "none" : Policy.EffectiveProfile(u.Id, c),
				s.MustChange,
				s.Reason,
				Deadline = s.MustChange && s.Notice ? s.EnforceAfterUtc : null,
				RemindedOn = _enforcer.RemindedDevices(u.Id),
				s.LastChangedUtc,
				PasswordStatus = Status(u, s),
				s.VerifiedRules
			};
		}).ToList();
		return new JsonResult(new
		{
			Users = liste,
			Presets = PasswordRules.Presets,
			Common = PasswordRules.Common,
			ChangeUrl = Links.ChangeUrl(Request, Request.PathBase.Value ?? string.Empty)
		});
	}

	[HttpPost("Require")]
	[Authorize(Policy = "RequiresElevation")]
	public ActionResult Require([FromBody] RequireRequest anfrage)
	{
		var c = Plugin.Config;
		IEnumerable<Guid> ids = anfrage.All || anfrage.OnlyNotMeetingRules
			// Disabled accounts are included: they have to set a new password once they are enabled again.
			? _users.GetUsers()
				.Where(u => anfrage.IncludeAdministrators || !Enforcer.IsAdmin(u))
				// Never the administrator who clicks: a bulk request must not lock them out mid-way.
				.Where(u => u.Id != NutzerId())
				.Where(u => !anfrage.OnlyNotMeetingRules || Status(u, _store.Get(u.Id)) is "unknown" or "older" or "none")
				.Select(u => u.Id)
			: anfrage.UserIds ?? new List<Guid>();
		int tage = Math.Clamp(anfrage.NoticeDays ?? c.NoticeDays, 0, 30);
		int anzahl = _enforcer.Require(ids, "admin", Links.ChangeUrl(Request, Request.PathBase.Value ?? string.Empty), tage);
		return new JsonResult(new { Count = anzahl });
	}

	// Sets a random temporary password and shows it once to the administrator. The user must replace it at the next sign-in.
	[HttpPost("TemporaryPassword")]
	[Authorize(Policy = "RequiresElevation")]
	public async Task<ActionResult> TemporaryPassword([FromBody] RequireRequest anfrage)
	{
		Response.Headers.CacheControl = "no-store";
		var id = anfrage.UserIds?.Count == 1 ? anfrage.UserIds[0] : Guid.Empty;
		var user = _users.GetUserById(id);
		if (user == null || id == NutzerId())
		{
			return BadRequest();
		}
		string passwort = await _enforcer.SetTemporaryPassword(user).ConfigureAwait(false);
		_logger.LogInformation("Temporary password set for {User}", user.Username);
		return new JsonResult(new { Password = passwort, ChangeUrl = Links.ChangeUrl(Request, Request.PathBase.Value ?? string.Empty) });
	}

	[HttpPost("Release")]
	[Authorize(Policy = "RequiresElevation")]
	public ActionResult Release([FromBody] RequireRequest anfrage)
	{
		var ids = anfrage.All ? _users.GetUsers().Select(u => u.Id) : anfrage.UserIds ?? new List<Guid>();
		foreach (var id in ids.Where(_store.MustChange).ToList())
		{
			_store.Release(id);
			// Lifting an expired password would trip again at the next sign-in, so restart its clock.
			_store.RestartClock(id);
			// An account without a password stays blocked while it has rules.
			var user = _users.GetUserById(id);
			if (user != null)
			{
				_enforcer.EnforceNoPassword(user);
			}
		}
		_patcher.Sync();
		return NoContent();
	}

	// Shared first step of ChangeRules and Change: throttle, open request, then the current password.
	private async Task<(User? Benutzer, ActionResult? Fehler)> Pruefen(ChangeRequest anfrage)
	{
		Response.Headers.CacheControl = "no-store";
		string ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
		if (_throttle.Blocked(ip))
		{
			return (null, Fehler(StatusCodes.Status429TooManyRequests, "Too many failed attempts. Please try again in 15 minutes."));
		}
		var benutzer = string.IsNullOrWhiteSpace(anfrage.Username) ? null : _users.GetUserByName(anfrage.Username.Trim());
		bool ok = false;
		// The password is only checked for accounts with an open request, so this is no general password oracle.
		// An account without any password cannot be claimed here: anyone who knows the name could do that.
		// Its owner gets a temporary password from the administrator instead.
		if (benutzer != null && _store.MustChange(benutzer.Id) && !Policy.HasNoPassword(benutzer))
		{
			try
			{
				ok = await _users.AuthenticateUser(benutzer.Username, anfrage.CurrentPassword ?? string.Empty, ip, false).ConfigureAwait(false) != null;
			}
			catch (Exception ex)
			{
				_logger.LogInformation("Password change for {User} rejected: {Reason}", benutzer.Username, ex.Message);
			}
		}
		// Evens out the time between unknown names, accounts without a request, wrong passwords and success.
		await Task.Delay(Random.Shared.Next(400, 900)).ConfigureAwait(false);
		if (!ok)
		{
			_throttle.Fail(ip);
			return (null, Fehler(StatusCodes.Status403Forbidden, AllgemeinerFehler));
		}
		return (benutzer, null);
	}

	private static object RegelnDto(RuleSnapshot? r)
	{
		if (r == null)
		{
			return new { Active = false, Plugin.Config.WebHints };
		}
		return new
		{
			Active = true,
			Plugin.Config.WebHints,
			r.MinLength,
			r.RequireUpper,
			r.RequireLower,
			r.RequireDigit,
			r.RequireSymbol,
			r.ForbidUsername,
			r.BlockCommon,
			r.MaxRepeat,
			Common = r.BlockCommon ? PasswordRules.Common : Array.Empty<string>()
		};
	}

	// none: the plugin saw the password being set to empty. norules: no profile applies to this user.
	// ok: set through a checked path under rules at least as strict as the user's current ones.
	// older: checked, but the rules have become stricter since.
	// unknown: never checked; the plugin cannot judge a stored password and does not try.
	private static string Status(User u, UserState s)
	{
		// Read from Jellyfin itself: no stored hash means no password. The plugin's own record could be out of date.
		if (Policy.HasNoPassword(u))
		{
			return "none";
		}
		var regeln = Policy.RulesFor(u);
		if (regeln == null)
		{
			return "norules";
		}
		if (s.VerifiedRules == null)
		{
			return "unknown";
		}
		return PasswordRules.Covers(s.VerifiedRules, regeln) ? "ok" : "older";
	}

	private Guid NutzerId()
	{
		return Guid.TryParse(User.FindFirstValue("Jellyfin-UserId") ?? User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
	}

	private ObjectResult Fehler(int status, string text) => StatusCode(status, new { Message = text });

	private ActionResult Datei(string name, string typ)
	{
		var assembly = Assembly.GetExecutingAssembly();
		string? ressource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + name, StringComparison.Ordinal));
		Stream? strom = ressource == null ? null : assembly.GetManifestResourceStream(ressource);
		if (strom == null)
		{
			return NotFound();
		}
		Response.Headers.CacheControl = "no-cache";
		return File(strom, typ);
	}
}

public class ChangeRequest
{
	public string? Username { get; set; }

	public string? CurrentPassword { get; set; }

	public string? NewPassword { get; set; }
}

public class RequireRequest
{
	public bool All { get; set; }

	public bool IncludeAdministrators { get; set; }

	// Everyone whose password is not known to meet their rules (unknown, older rules or none).
	public bool OnlyNotMeetingRules { get; set; }

	public List<Guid>? UserIds { get; set; }

	// Days the users keep access, with reminders, before they are signed out. Null = the saved setting.
	public int? NoticeDays { get; set; }
}

public class ValidateRequest
{
	public Guid? UserId { get; set; }

	public string? NewUsername { get; set; }

	public string? Password { get; set; }
}
