using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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

	// Deadline of a notice period, on every response to a user who is in one.
	public const string NoticeHeader = "X-Mki-Password-Notice";

	// Bodies of password requests are tiny; anything bigger is not a real client.
	private const int MaxRumpf = 64 * 1024;

	// Real clients send one token, two at most. More is refused while anyone is blocked, so a pile of junk cannot hide one.
	internal const int MaxTokens = 8;

	// Every place a token can come from. Jellyfin reads some of them only with legacy sign-in switched on, and another
	// plugin may copy one into another after this check has run (Seerr Bridge does), so all of them count.
	private static readonly string[] SchemaKoepfe = { "Authorization", "X-Emby-Authorization" };

	private static readonly string[] TokenKoepfe = { "X-Emby-Token", "X-MediaBrowser-Token" };

	private static readonly string[] TokenParameter = { "ApiKey", "api_key" };

	// Any path segment: Jellyfin binds route GUIDs in several spellings, e.g. with braces.
	private static readonly Regex Anmeldung = new(@"^(?<base>.*)/Users/(AuthenticateByName|AuthenticateWithQuickConnect|[^/]+/Authenticate)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex Passwort = new(@"/Users/(?:(?<id>[^/]+)/)?Password/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex NeuerBenutzer = new(@"/Users/New/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	// Jellyfin's "forgot password": redeeming the PIN sets the password to the PIN itself.
	private static readonly Regex PinEinloesen = new(@"/Users/ForgotPassword/Pin/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	// What a blocked user may still reach: signing in (handled below), the change page and its script, and signing out.
	private static readonly Regex Frei = new(@"/(Users/(AuthenticateByName|AuthenticateWithQuickConnect|[^/]+/Authenticate)|MkiPasswordPolicy/(Change|ChangeRules|client\.js)|Sessions/Logout|System/Info/Public)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private readonly PolicyStore _store;

	private readonly Enforcer _enforcer;

	private readonly IUserManager _users;

	private readonly ISessionManager _sessions;

	private readonly IDeviceManager _devices;

	private readonly ILogger<LoginGate> _logger;

	public LoginGate(PolicyStore store, Enforcer enforcer, IUserManager users, ISessionManager sessions, IDeviceManager devices, ILogger<LoginGate> logger)
	{
		_store = store;
		_enforcer = enforcer;
		_users = users;
		_sessions = sessions;
		_devices = devices;
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

				if (offen && !Frei.IsMatch(pfad))
				{
					var ids = Benutzer(kontext.Request);
					if (ids == null || ids.Any(_store.SessionsBlocked))
					{
						kontext.Response.StatusCode = StatusCodes.Status401Unauthorized;
						if (ids != null)
						{
							kontext.Response.Headers[MarkerHeader] = "required";
						}
						return;
					}
					var hinweis = ids.FirstOrDefault(_store.InNotice);
					if (hinweis != Guid.Empty)
					{
						// Lets the plugin's script in web-based apps show its popup on any response.
						kontext.Response.Headers[NoticeHeader] = _store.Get(hinweis).EnforceAfterUtc!.Value.ToString("o", CultureInfo.InvariantCulture);
						kontext.Response.Headers.Append("Access-Control-Expose-Headers", NoticeHeader);
					}
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
					// Also when only profiles are set: an account without a password is blocked at its first sign-in.
					if (offen || Plugin.Config.ExpiryDays > 0 || Policy.Configured(Plugin.Config))
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
				else if (PinEinloesen.IsMatch(pfad))
				{
					await PinEingeloest(kontext, weiter).ConfigureAwait(false);
				}
				else
				{
					await weiter().ConfigureAwait(false);
				}
			});
			next(app);
		};
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
					_logger.LogError(ex, "Could not check sign-in - refused because password changes are pending");
					grund = "admin";
				}
				else
				{
					_logger.LogError(ex, "Could not check sign-in - allowed, no password changes pending");
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
			"New password required. Please set it in a browser: " + Links.ChangeUrl(kontext.Request, basePath)).ConfigureAwait(false);
	}

	// Returns the reason the sign-in is refused, or null to let it through.
	private async Task<string?> Pruefen(byte[] antwort)
	{
		using var doc = JsonDocument.Parse(antwort);
		var wurzel = doc.RootElement;
		string? token = Text(wurzel, "AccessToken");
		if (!TryProp(wurzel, "User", out var benutzerDto) || !Guid.TryParse(Text(benutzerDto, "Id"), out var id))
		{
			throw new InvalidDataException("Sign-in response without user");
		}
		var benutzer = _users.GetUserById(id) ?? throw new InvalidDataException("Unknown user in sign-in response");
		if (Policy.IsGuest(benutzer))
		{
			// Guest accounts of MKI Guest Pass have no password; their access is that plugin's business.
			_enforcer.ForgetGuest(id);
			return null;
		}

		string? grund = null;
		_enforcer.EnforceNoPassword(benutzer);
		var stand = _store.Get(id);
		// During a notice period sign-ins still work; the new session gets a reminder instead.
		if (stand.MustChange && !_store.InNotice(id))
		{
			// Without a password the user cannot set one themselves; the dialog says to ask for a temporary password.
			grund = Policy.HasNoPassword(benutzer) ? "nopassword" : stand.Reason == "nopassword" ? "temporary" : stand.Reason ?? "admin";
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
			_logger.LogInformation("Sign-in of {User} refused: new password required ({Reason})", benutzer.Username, grund);
		}
		return grund;
	}

	private async Task PasswortAendern(HttpContext kontext, Func<Task> weiter, string routenId)
	{
		// Tokens of different users in one request: nobody counts as the caller, so the strictest rules apply.
		var ids = Benutzer(kontext.Request);
		User? aufrufer = ids is { Count: 1 } ? _users.GetUserById(ids[0]) : null;
		bool mehrdeutig = ids == null || ids.Count > 1;
		Guid zielId = Guid.TryParse(routenId, out var r) ? r
			: Guid.TryParse(kontext.Request.Query["userId"].ToString(), out var q) ? q
			: aufrufer?.Id ?? Guid.Empty;
		var ziel = zielId == Guid.Empty ? null : _users.GetUserById(zielId);
		if (ziel == null)
		{
			if (mehrdeutig && (Policy.Configured(Plugin.Config) || _store.AnyMustChange()))
			{
				// Jellyfin would pick one of the users itself, and that password would go in unchecked.
				kontext.Response.StatusCode = StatusCodes.Status400BadRequest;
				return;
			}
			await weiter().ConfigureAwait(false);
			return;
		}

		bool selbst = aufrufer?.Id == ziel.Id;
		bool adminFuerAndere = !selbst && aufrufer != null && Enforcer.IsAdmin(aufrufer);
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
		// A password an administrator set for someone with rules is known to that administrator: the user replaces it
		// at the next sign-in. An open request stays open in any case.
		else if (!selbst && !string.IsNullOrEmpty(Feld(rumpf, "NewPw")) && (Policy.RulesFor(ziel) != null || _store.MustChange(ziel.Id)))
		{
			_enforcer.RequireOwnPassword(ziel, "temporary");
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
				_enforcer.EnforceNoPassword(neu);
			}
			else
			{
				if (pruefen)
				{
					_store.MarkVerified(neu.Id, regeln!);
				}
				// The administrator chose this password, so a user with rules sets their own at the first sign-in.
				if (Policy.RulesFor(neu) != null || _store.MustChange(neu.Id))
				{
					_enforcer.RequireOwnPassword(neu, "new");
				}
			}
		}
	}

	// After a PIN reset the password is the PIN, which was readable in a file on the server: users with rules replace it at the next sign-in.
	private async Task PinEingeloest(HttpContext kontext, Func<Task> weiter)
	{
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
		try
		{
			if (kontext.Response.StatusCode == StatusCodes.Status200OK && puffer.Length > 0)
			{
				using var doc = JsonDocument.Parse(puffer.ToArray());
				if (TryProp(doc.RootElement, "UsersReset", out var namen) && namen.ValueKind == JsonValueKind.Array)
				{
					foreach (var n in namen.EnumerateArray())
					{
						var user = n.ValueKind == JsonValueKind.String ? _users.GetUserByName(n.GetString()!) : null;
						if (user != null && (Policy.RulesFor(user) != null || _store.MustChange(user.Id)))
						{
							_enforcer.RequireOwnPassword(user, "temporary");
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not read the result of a password reset PIN");
		}
		puffer.Position = 0;
		await puffer.CopyToAsync(original).ConfigureAwait(false);
	}

	private static async Task Ablehnen(HttpContext kontext, IReadOnlyList<string> fehler)
	{
		kontext.Response.StatusCode = StatusCodes.Status400BadRequest;
		kontext.Response.ContentType = "text/plain; charset=utf-8";
		await kontext.Response.WriteAsync(PasswordRules.Describe(fehler)).ConfigureAwait(false);
	}

	// The users behind every token in the request, in Jellyfin's order of preference. API keys belong to no user.
	// Null when the request carries more tokens than any real client sends.
	// Null also when the lookup fails: while anyone is blocked, an unknown caller is refused rather than let through.
	private List<Guid>? Benutzer(HttpRequest request)
	{
		try
		{
			var tokens = Tokens(request, MaxTokens);
			if (tokens.Count > MaxTokens)
			{
				return null;
			}
			var ids = new List<Guid>();
			foreach (var token in tokens)
			{
				var geraet = _devices.GetDevices(new DeviceQuery { AccessToken = token, Limit = 1 }).Items.FirstOrDefault();
				if (geraet != null && geraet.UserId != Guid.Empty && !ids.Contains(geraet.UserId))
				{
					ids.Add(geraet.UserId);
				}
			}
			return ids;
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Caller could not be determined");
			return null;
		}
	}

	// Stops once more than "grenze" tokens are found: the caller refuses such a request anyway, and a header packed
	// with thousands of tokens must not cost more than reading it once.
	internal static List<string> Tokens(HttpRequest request, int grenze)
	{
		var tokens = new List<string>();
		var gesehen = new HashSet<string>(StringComparer.Ordinal);
		void Dazu(string? token)
		{
			if (tokens.Count > grenze)
			{
				return;
			}
			if (!string.IsNullOrWhiteSpace(token) && gesehen.Add(token))
			{
				tokens.Add(token);
			}
			string kurz = token?.Trim().Trim('"') ?? string.Empty;
			if (kurz.Length > 0 && gesehen.Add(kurz))
			{
				tokens.Add(kurz);
			}
		}
		foreach (var name in SchemaKoepfe)
		{
			var werte = request.Headers[name];
			foreach (var wert in werte.Append(werte.ToString()))
			{
				foreach (var token in TokensAusSchema(wert))
				{
					Dazu(token);
				}
			}
		}
		foreach (var name in TokenKoepfe)
		{
			var werte = request.Headers[name];
			foreach (var wert in werte.Append(werte.ToString()))
			{
				Dazu(wert);
			}
		}
		foreach (var name in TokenParameter)
		{
			var werte = request.Query[name];
			foreach (var wert in werte.Append(werte.ToString()))
			{
				Dazu(wert);
			}
		}
		return tokens;
	}

	// Every Token value of a "MediaBrowser Client=..., Token=..." header, split like Jellyfin splits it, whatever the scheme.
	private static IEnumerable<string> TokensAusSchema(string? kopf)
	{
		int leer = kopf?.IndexOf(' ') ?? -1;
		if (kopf == null || leer < 0)
		{
			return Array.Empty<string>();
		}
		var tokens = new List<string>();
		string rest = kopf[(leer + 1)..];
		bool inAnfuehrung = false;
		int start = 0;
		string schluessel = string.Empty;
		int i;
		void Teil(int ende)
		{
			if (string.Equals(schluessel.Trim(), "Token", StringComparison.OrdinalIgnoreCase))
			{
				tokens.Add(WebUtility.UrlDecode(rest[start..ende].Trim('"')));
			}
			schluessel = string.Empty;
		}
		for (i = 0; i < rest.Length; i++)
		{
			char c = rest[i];
			if (c == '"')
			{
				inAnfuehrung = !inAnfuehrung;
			}
			else if (c == ',' && !inAnfuehrung)
			{
				if (start < i)
				{
					Teil(i);
				}
				start = i + 1;
			}
			else if (c == '=' && !inAnfuehrung)
			{
				schluessel = rest[start..i];
				start = i + 1;
			}
		}
		if (start < i)
		{
			Teil(i);
		}
		return tokens;
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
