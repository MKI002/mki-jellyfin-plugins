using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Services;

// Sits in front of Jellyfin's own endpoints. With nothing configured and no open change request it only passes requests on.
// - Sign-ins of users who must change their password are undone and answered with 401 plus a marker header.
// - Any other request from such a user is refused once the grace time is over, so no token stays usable.
// - New passwords that break the user's rules are refused before Jellyfin stores them.
// Passwords are never stored, logged or kept beyond the request; only the rule check sees them.
public class LoginGate : IStartupFilter
{
	public const string MarkerHeader = "X-Mki-Password-Change";

	public const string ReasonHeader = "X-Mki-Password-Reason";

	// Bodies of password requests are tiny; anything bigger is not a real client.
	private const int MaxRumpf = 64 * 1024;

	// Any path segment: Jellyfin binds route GUIDs in several spellings, e.g. with braces.
	private static readonly Regex Anmeldung = new(@"^(?<base>.*)/Users/(AuthenticateByName|AuthenticateWithQuickConnect|[^/]+/Authenticate)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex Passwort = new(@"/Users/(?:(?<id>[^/]+)/)?Password/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex NeuerBenutzer = new(@"/Users/New/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	// What a blocked user may still reach: signing in (handled below), the change page and its script, and signing out.
	private static readonly Regex Frei = new(@"/(Users/(AuthenticateByName|AuthenticateWithQuickConnect|[^/]+/Authenticate)|MkiPasswordPolicy/(Change|ChangeRules|client\.js)|Sessions/Logout|System/Info/Public)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private readonly PolicyStore _store;

	private readonly Enforcer _enforcer;

	private readonly IUserManager _users;

	private readonly ISessionManager _sessions;

	private readonly ILogger<LoginGate> _logger;

	public LoginGate(PolicyStore store, Enforcer enforcer, IUserManager users, ISessionManager sessions, ILogger<LoginGate> logger)
	{
		_store = store;
		_enforcer = enforcer;
		_users = users;
		_sessions = sessions;
		_logger = logger;
	}

	public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
	{
		return app =>
		{
			app.Use(async (kontext, weiter) =>
			{
				string pfad = kontext.Request.Path.Value ?? string.Empty;
				bool offen = _store.AnyMustChange();

				if (offen && !Frei.IsMatch(pfad) && await GesperrteSitzung(kontext).ConfigureAwait(false))
				{
					kontext.Response.StatusCode = StatusCodes.Status401Unauthorized;
					kontext.Response.Headers[MarkerHeader] = "required";
					return;
				}

				// Fresh install or everything switched off: nothing to do, not even a look at the request.
				if (!offen && Plugin.Config.ExpiryDays <= 0 && !Policy.Configured(Plugin.Config))
				{
					await weiter().ConfigureAwait(false);
					return;
				}

				if (!HttpMethods.IsPost(kontext.Request.Method) || !pfad.Contains("/Users/", StringComparison.OrdinalIgnoreCase))
				{
					await weiter().ConfigureAwait(false);
					return;
				}
				Match m;
				if ((m = Anmeldung.Match(pfad)).Success)
				{
					if (offen || Plugin.Config.ExpiryDays > 0)
					{
						await Anmelden(kontext, weiter, m.Groups["base"].Value).ConfigureAwait(false);
					}
					else
					{
						await weiter().ConfigureAwait(false);
					}
				}
				else if ((m = Passwort.Match(pfad)).Success)
				{
					await PasswortAendern(kontext, weiter, m.Groups["id"].Value).ConfigureAwait(false);
				}
				else if (NeuerBenutzer.IsMatch(pfad))
				{
					await BenutzerAnlegen(kontext, weiter).ConfigureAwait(false);
				}
				else
				{
					await weiter().ConfigureAwait(false);
				}
			});
			next(app);
		};
	}

	// Backstop for every route, including ones this gate does not know: a token of a blocked user is worthless.
	private async Task<bool> GesperrteSitzung(HttpContext kontext)
	{
		var info = await Aufrufer(kontext).ConfigureAwait(false);
		return info != null && !info.IsApiKey && info.UserId != Guid.Empty && _store.SessionsBlocked(info.UserId);
	}

	private async Task Anmelden(HttpContext kontext, Func<Task> weiter, string basePath)
	{
		// The password in this request is never read here. Only Jellyfin's answer is, to learn who signed in.
		// That answer has to be readable, so response compression must not kick in.
		kontext.Request.Headers.Remove("Accept-Encoding");

		var original = kontext.Response.Body;
		using var puffer = new MemoryStream();
		kontext.Response.Body = puffer;
		try
		{
			await weiter().ConfigureAwait(false);
		}
		finally
		{
			kontext.Response.Body = original;
		}

		string? grund = null;
		if (kontext.Response.StatusCode == StatusCodes.Status200OK)
		{
			try
			{
				grund = await Pruefen(puffer.ToArray()).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				// Fail closed while anyone is waiting for a new password.
				if (_store.AnyMustChange())
				{
					_logger.LogError(ex, "Anmeldung nicht pruefbar - abgewiesen, weil Passwortwechsel offen sind");
					grund = "admin";
				}
				else
				{
					_logger.LogError(ex, "Anmeldung nicht pruefbar - durchgelassen, kein Passwortwechsel offen");
				}
			}
		}

		if (grund == null)
		{
			puffer.Position = 0;
			await puffer.CopyToAsync(original).ConfigureAwait(false);
			return;
		}

		kontext.Response.Clear();
		kontext.Response.StatusCode = StatusCodes.Status401Unauthorized;
		kontext.Response.Headers[MarkerHeader] = "required";
		kontext.Response.Headers[ReasonHeader] = grund;
		kontext.Response.Headers["Access-Control-Expose-Headers"] = MarkerHeader + ", " + ReasonHeader;
		kontext.Response.Headers.CacheControl = "no-store";
		kontext.Response.ContentType = "text/plain; charset=utf-8";
		await kontext.Response.WriteAsync(
			"Neues Passwort erforderlich. Bitte im Browser festlegen: " + Links.ChangeUrl(kontext.Request, basePath)).ConfigureAwait(false);
	}

	// Returns the reason the sign-in is refused, or null to let it through.
	private async Task<string?> Pruefen(byte[] antwort)
	{
		using var doc = JsonDocument.Parse(antwort);
		var wurzel = doc.RootElement;
		string? token = Text(wurzel, "AccessToken");
		if (!TryProp(wurzel, "User", out var benutzerDto) || !Guid.TryParse(Text(benutzerDto, "Id"), out var id))
		{
			throw new InvalidDataException("Anmeldeantwort ohne Benutzer");
		}
		var benutzer = _users.GetUserById(id) ?? throw new InvalidDataException("Unbekannter Benutzer in der Anmeldeantwort");

		string? grund = null;
		var stand = _store.Get(id);
		if (stand.MustChange)
		{
			grund = stand.Reason ?? "admin";
		}
		else if (Plugin.Config.ExpiryDays > 0 && Policy.RulesFor(benutzer) != null
			&& _store.LastChangedOrBaseline(id).AddDays(Plugin.Config.ExpiryDays) < DateTime.UtcNow)
		{
			grund = "expired";
			_store.Require(id, grund, TimeSpan.Zero);
			await _enforcer.SignOutEverywhere(id).ConfigureAwait(false);
		}

		if (grund != null)
		{
			if (!string.IsNullOrEmpty(token))
			{
				await _sessions.Logout(token).ConfigureAwait(false);
			}
			_logger.LogInformation("Anmeldung von {Benutzer} abgewiesen: neues Passwort erforderlich ({Grund})", benutzer.Username, grund);
		}
		return grund;
	}

	private async Task PasswortAendern(HttpContext kontext, Func<Task> weiter, string routenId)
	{
		var info = await Aufrufer(kontext).ConfigureAwait(false);
		Guid zielId = Guid.TryParse(routenId, out var r) ? r
			: Guid.TryParse(kontext.Request.Query["userId"].ToString(), out var q) ? q
			: info?.UserId ?? Guid.Empty;
		var ziel = zielId == Guid.Empty ? null : _users.GetUserById(zielId);
		if (ziel == null)
		{
			await weiter().ConfigureAwait(false);
			return;
		}

		bool selbst = info?.UserId == ziel.Id;
		bool adminFuerAndere = !selbst && info?.User != null && Enforcer.IsAdmin(info.User);
		var regeln = adminFuerAndere && !Plugin.Config.ApplyToAdminChanges ? null : Policy.RulesFor(ziel);
		bool erfassen = regeln != null || Policy.Configured(Plugin.Config) || _store.MustChange(ziel.Id);
		if (!erfassen)
		{
			await weiter().ConfigureAwait(false);
			return;
		}

		var (gelesen, rumpf) = await RumpfLesen(kontext.Request).ConfigureAwait(false);
		if (!gelesen)
		{
			// Unreadable for us means unchecked: refuse rather than let Jellyfin store an unchecked password.
			kontext.Response.StatusCode = StatusCodes.Status400BadRequest;
			return;
		}
		if (rumpf != null && Wahr(rumpf.Value, "ResetPassword"))
		{
			await weiter().ConfigureAwait(false);
			if (kontext.Response.StatusCode == StatusCodes.Status204NoContent)
			{
				_store.MarkEmpty(ziel.Id);
			}
			return;
		}

		if (regeln != null)
		{
			var fehler = PasswordRules.Check(Feld(rumpf, "NewPw"), ziel.Username, regeln);
			if (fehler.Count > 0)
			{
				await Ablehnen(kontext, fehler).ConfigureAwait(false);
				return;
			}
		}
		await weiter().ConfigureAwait(false);
		if (kontext.Response.StatusCode != StatusCodes.Status204NoContent)
		{
			return;
		}
		if (regeln != null)
		{
			_store.MarkVerified(ziel.Id, regeln);
		}
		// A user who sets a different password of their own during the grace time has done what was asked.
		if (selbst && _store.MustChange(ziel.Id) && Feld(rumpf, "NewPw") != Feld(rumpf, "CurrentPw"))
		{
			_store.Release(ziel.Id);
		}
	}

	private async Task BenutzerAnlegen(HttpContext kontext, Func<Task> weiter)
	{
		var regeln = Plugin.Config.ApplyToAdminChanges ? Policy.RulesForNewUser() : null;
		if (regeln == null && !Policy.Configured(Plugin.Config))
		{
			await weiter().ConfigureAwait(false);
			return;
		}
		var (gelesen, rumpf) = await RumpfLesen(kontext.Request).ConfigureAwait(false);
		if (!gelesen)
		{
			kontext.Response.StatusCode = StatusCodes.Status400BadRequest;
			return;
		}
		string? passwort = Feld(rumpf, "Password");
		string? name = Feld(rumpf, "Name");
		// An empty password is the admin's choice; "new users must change" or the user review catch it later.
		bool pruefen = regeln != null && !string.IsNullOrEmpty(passwort);
		if (pruefen)
		{
			var fehler = PasswordRules.Check(passwort, name, regeln!);
			if (fehler.Count > 0)
			{
				await Ablehnen(kontext, fehler).ConfigureAwait(false);
				return;
			}
		}
		await weiter().ConfigureAwait(false);
		if (kontext.Response.StatusCode == StatusCodes.Status200OK && name != null && _users.GetUserByName(name) is { } neu)
		{
			if (string.IsNullOrEmpty(passwort))
			{
				_store.MarkEmpty(neu.Id);
			}
			else if (pruefen)
			{
				_store.MarkVerified(neu.Id, regeln!);
			}
		}
	}

	private static async Task Ablehnen(HttpContext kontext, IReadOnlyList<string> fehler)
	{
		kontext.Response.StatusCode = StatusCodes.Status400BadRequest;
		kontext.Response.ContentType = "text/plain; charset=utf-8";
		await kontext.Response.WriteAsync(PasswordRules.Describe(fehler)).ConfigureAwait(false);
	}

	private async Task<AuthorizationInfo?> Aufrufer(HttpContext kontext)
	{
		try
		{
			var auth = kontext.RequestServices.GetService<IAuthorizationContext>();
			return auth == null ? null : await auth.GetAuthorizationInfo(kontext).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "Aufrufer nicht ermittelbar");
			return null;
		}
	}

	// (false, _) = body present but not readable as JSON; (true, null) = empty body.
	private static async Task<(bool Gelesen, JsonElement? Rumpf)> RumpfLesen(HttpRequest request)
	{
		if (request.ContentLength > MaxRumpf)
		{
			return (false, null);
		}
		request.EnableBuffering();
		try
		{
			var puffer = new char[MaxRumpf + 1];
			using var leser = new StreamReader(request.Body, Encoding.UTF8, true, 4096, leaveOpen: true);
			int laenge = await leser.ReadBlockAsync(puffer, 0, puffer.Length).ConfigureAwait(false);
			if (laenge > MaxRumpf)
			{
				return (false, null);
			}
			string text = new string(puffer, 0, laenge);
			if (string.IsNullOrWhiteSpace(text))
			{
				return (true, null);
			}
			using var doc = JsonDocument.Parse(text);
			return doc.RootElement.ValueKind == JsonValueKind.Object ? (true, doc.RootElement.Clone()) : (false, null);
		}
		catch (JsonException)
		{
			return (false, null);
		}
		finally
		{
			request.Body.Position = 0;
		}
	}

	// Case-insensitive and last one wins, like Jellyfin's own JSON reader, so a duplicated key cannot slip past the check.
	private static bool TryProp(JsonElement e, string name, out JsonElement wert)
	{
		bool gefunden = false;
		wert = default;
		if (e.ValueKind == JsonValueKind.Object)
		{
			foreach (var p in e.EnumerateObject())
			{
				if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
				{
					wert = p.Value;
					gefunden = true;
				}
			}
		}
		return gefunden;
	}

	// Jellyfin also accepts numbers and booleans where it expects text.
	private static string? Text(JsonElement e, string name)
	{
		if (!TryProp(e, name, out var w))
		{
			return null;
		}
		return w.ValueKind switch
		{
			JsonValueKind.String => w.GetString(),
			JsonValueKind.Number => w.GetRawText(),
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			_ => null
		};
	}

	private static string? Feld(JsonElement? e, string name) => e == null ? null : Text(e.Value, name);

	private static bool Wahr(JsonElement e, string name) => TryProp(e, name, out var w) && w.ValueKind == JsonValueKind.True;
}
