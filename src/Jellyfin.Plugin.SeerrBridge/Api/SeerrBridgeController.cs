using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SeerrBridge.Configuration;
using Jellyfin.Plugin.SeerrBridge.Services;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.SeerrBridge.Api;

[ApiController]
[Route("SeerrBridge")]
public class SeerrBridgeController : ControllerBase
{
	private readonly SeerrClient _seerr;

	private readonly IUserManager _userManager;

	private readonly ILogger<SeerrBridgeController> _logger;

	private readonly MkiRepository _repo;

	private readonly INetworkManager _network;

	// TMDB poster paths look like "/abc123.jpg"; anything else is not put into a page.
	private static readonly Regex PosterPfad = new Regex("^/[A-Za-z0-9._-]{1,200}$", RegexOptions.Compiled);

	// Per user: the last single sign-on, and the requests of the last hour.
	private static readonly ConcurrentDictionary<Guid, DateTime> LetzteAnmeldung = new ConcurrentDictionary<Guid, DateTime>();

	private static readonly ConcurrentDictionary<Guid, Queue<DateTime>> LetzteAnfragen = new ConcurrentDictionary<Guid, Queue<DateTime>>();

	private const int AnfragenProStunde = 30;

	public SeerrBridgeController(SeerrClient seerr, IUserManager userManager, ILogger<SeerrBridgeController> logger, MkiRepository repo, INetworkManager network)
	{
		_seerr = seerr;
		_userManager = userManager;
		_logger = logger;
		_repo = repo;
		_network = network;
	}

	// Signed-in users only: before sign-in nobody learns where Seerr lives or what is switched on.
	[HttpGet("config")]
	[Authorize]
	public ActionResult GetConfig()
	{
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c9: Expected O, but got Unknown
		PluginConfiguration pluginConfiguration = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration ?? new PluginConfiguration();
		((ControllerBase)this).Response.Headers.CacheControl = ("private, max-age=30");
		((ControllerBase)this).Response.Headers.Vary = ("Authorization");
		string url = SeerrClient.WebAdresse(pluginConfiguration.PublicUrl);
		return (ActionResult)new JsonResult((object)new
		{
			enabled = (pluginConfiguration.Enabled && !string.IsNullOrEmpty(url)),
			url = url,
			label = (pluginConfiguration.Label ?? string.Empty).Trim(),
			showInHeader = pluginConfiguration.ShowInHeader,
			showInDrawer = pluginConfiguration.ShowInDrawer,
			search = (pluginConfiguration.EnableSearch && SeerrClient.Eingerichtet),
			allowRequests = (pluginConfiguration.AllowRequests && SeerrClient.Eingerichtet),
			hideAvailable = pluginConfiguration.HideAvailable,
			sso = pluginConfiguration.EnableSso,
			limit = Math.Clamp(pluginConfiguration.SearchLimit, 1, 40)
		});
	}

	[HttpGet("search")]
	[Authorize]
	public async Task<ActionResult> Search([FromQuery] string? q, CancellationToken ct)
	{
		PluginConfiguration c = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration ?? new PluginConfiguration();
		if (!c.Enabled || !c.EnableSearch || string.IsNullOrWhiteSpace(q))
		{
			return (ActionResult)new JsonResult((object)new
			{
				items = Array.Empty<object>()
			});
		}
		IEnumerable<SeerrTreffer> source = await _seerr.SuchenAsync(q, ct).ConfigureAwait(continueOnCapturedContext: false);
		if (c.HideAvailable)
		{
			source = source.Where((SeerrTreffer t) => t.Status != "vorhanden");
		}
		return (ActionResult)new JsonResult((object)new
		{
			items = (from t in source.Take(Math.Clamp(c.SearchLimit, 1, 40))
				select new
				{
					tmdbId = t.TmdbId,
					mediaType = t.MediaType,
					title = t.Title,
					year = t.Year,
					poster = ((t.PosterPath == null || !PosterPfad.IsMatch(t.PosterPath)) ? null : ("https://image.tmdb.org/t/p/w300" + t.PosterPath)),
					overview = t.Overview,
					rating = Math.Round(t.Rating, 1),
					status = t.Status,
					requestable = (c.AllowRequests && t.Status == "neu")
				}).ToList()
		});
	}

	[HttpPost("request")]
	[Authorize]
	public async Task<ActionResult> Anfragen([FromBody] AnfrageRumpf rumpf, CancellationToken ct)
	{
		PluginConfiguration pluginConfiguration = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration ?? new PluginConfiguration();
		if (!pluginConfiguration.Enabled || !pluginConfiguration.AllowRequests)
		{
			return (ActionResult)new JsonResult((object)new
			{
				ok = false,
				text = "Requests are disabled"
			});
		}
		Guid nutzer = NutzerId();
		if (nutzer == Guid.Empty || rumpf == null)
		{
			return (ActionResult)new JsonResult((object)new
			{
				ok = false,
				text = "no signed-in account"
			});
		}
		if (!AnfrageErlaubt(nutzer))
		{
			return (ActionResult)new JsonResult((object)new
			{
				ok = false,
				text = "Too many requests in the last hour. Please try again later."
			});
		}
		var (ok, text) = await _seerr.AnfragenAsync(rumpf.MediaType, rumpf.TmdbId, nutzer, ct).ConfigureAwait(continueOnCapturedContext: false);
		return (ActionResult)new JsonResult((object)new { ok, text });
	}

	private static bool AnfrageErlaubt(Guid nutzer)
	{
		Queue<DateTime> liste = LetzteAnfragen.GetOrAdd(nutzer, (Guid _) => new Queue<DateTime>());
		lock (liste)
		{
			while (liste.Count > 0 && DateTime.UtcNow - liste.Peek() > TimeSpan.FromHours(1))
			{
				liste.Dequeue();
			}
			if (liste.Count >= AnfragenProStunde)
			{
				return false;
			}
			liste.Enqueue(DateTime.UtcNow);
			return true;
		}
	}

	// POST, and only with the token in a header: a link or a form on another site must not be able to sign this
	// browser in to Seerr with somebody else's account.
	[HttpPost("sso")]
	[Authorize]
	public async Task<ActionResult> Sso(CancellationToken ct)
	{
		PluginConfiguration pluginConfiguration = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration ?? new PluginConfiguration();
		((ControllerBase)this).Response.Headers.CacheControl = ("no-store");
		HttpRequest anfrage = ((ControllerBase)this).Request;
		if (anfrage.Query.ContainsKey("ApiKey") || anfrage.Query.ContainsKey("api_key")
			|| string.Equals(anfrage.Headers["Sec-Fetch-Site"].ToString(), "cross-site", StringComparison.OrdinalIgnoreCase))
		{
			return (ActionResult)(object)((ControllerBase)this).StatusCode(403);
		}
		if (!pluginConfiguration.Enabled || !pluginConfiguration.EnableSso)
		{
			return (ActionResult)new JsonResult((object)new
			{
				ok = false,
				text = "disabled"
			});
		}
		string text = Benutzername();
		Guid guid = NutzerId();
		if (string.IsNullOrEmpty(text) || guid == Guid.Empty)
		{
			return (ActionResult)new JsonResult((object)new
			{
				ok = false,
				text = "no signed-in account"
			});
		}
		// Each sign-on issues a new Jellyfin token for Seerr, so one every few seconds per user is plenty.
		DateTime jetzt = DateTime.UtcNow;
		if (LetzteAnmeldung.TryGetValue(guid, out DateTime zuletzt) && jetzt - zuletzt < TimeSpan.FromSeconds(10))
		{
			return (ActionResult)new JsonResult((object)new
			{
				ok = false,
				text = "too soon"
			});
		}
		LetzteAnmeldung[guid] = jetzt;
		string bereich = KeksBereich(pluginConfiguration);
		if (string.IsNullOrEmpty(bereich))
		{
			return (ActionResult)new JsonResult((object)new
			{
				ok = false,
				text = "Jellyfin and Seerr do not share a common domain"
			});
		}
		string ausweis = SsoBruecke.Ausstellen(guid, text);
		var (flag, text2, text3) = await _seerr.AnmeldenAsync(text, ausweis, ct).ConfigureAwait(continueOnCapturedContext: false);
		if (!flag || text2 == null)
		{
			return (ActionResult)new JsonResult((object)new
			{
				ok = false,
				text = text3
			});
		}
		// Rebuilt from Seerr's cookie, keeping only its value, lifetime and path. It is always HttpOnly, Secure whenever
		// both Seerr and this Jellyfin page use https (a browser drops a Secure cookie sent to an http page), and
		// SameSite=Lax unless Seerr asked for something stricter.
		string[] array = text2.Split(';');
		List<string> list = new List<string> { array[0].Trim() };
		string sameSite = "Lax";
		for (int i = 1; i < array.Length; i++)
		{
			string text4 = array[i].Trim();
			string name = text4.Split('=')[0].Trim();
			if (name.Equals("Expires", StringComparison.OrdinalIgnoreCase) || name.Equals("Max-Age", StringComparison.OrdinalIgnoreCase) || name.Equals("Path", StringComparison.OrdinalIgnoreCase))
			{
				list.Add(text4);
			}
			else if (name.Equals("SameSite", StringComparison.OrdinalIgnoreCase) && text4.EndsWith("Strict", StringComparison.OrdinalIgnoreCase))
			{
				sameSite = "Strict";
			}
		}
		list.Add("Domain=" + bereich);
		list.Add("HttpOnly");
		list.Add("SameSite=" + sameSite);
		if (SeerrClient.WebAdresse(pluginConfiguration.PublicUrl).StartsWith("https://", StringComparison.OrdinalIgnoreCase) && SeiteSicher())
		{
			list.Add("Secure");
		}
		HeaderDictionaryExtensions.Append(((ControllerBase)this).Response.Headers, "Set-Cookie", (string.Join("; ", list)));
		return (ActionResult)new JsonResult((object)new
		{
			ok = true,
			text = "signed in",
			domain = bereich
		});
	}

	private string? KeksBereich(PluginConfiguration c)
	{
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		if (!string.IsNullOrWhiteSpace(c.CookieDomain))
		{
			return c.CookieDomain.Trim();
		}
		HostString host = ((ControllerBase)this).Request.Host;
		string host2 = host.Host;
		string host3;
		try
		{
			host3 = new Uri(SeerrClient.WebAdresse(c.PublicUrl)).Host;
		}
		catch (UriFormatException)
		{
			return null;
		}
		if (string.IsNullOrEmpty(host2) || string.IsNullOrEmpty(host3))
		{
			return null;
		}
		if (IPAddress.TryParse(host2, out IPAddress address) || IPAddress.TryParse(host3, out address))
		{
			return null;
		}
		string[] array = host2.Split('.');
		string[] array2 = host3.Split('.');
		List<string> list = new List<string>();
		for (int i = 1; i <= Math.Min(array.Length, array2.Length) && string.Equals(array[^i], array2[^i], StringComparison.OrdinalIgnoreCase); i++)
		{
			list.Insert(0, array[^i]);
		}
		if (list.Count < 2)
		{
			return null;
		}
		return "." + string.Join('.', list);
	}

	private Guid NutzerId()
	{
		if (!Guid.TryParse(PrincipalExtensions.FindFirstValue(((ControllerBase)this).User, "Jellyfin-UserId") ?? PrincipalExtensions.FindFirstValue(((ControllerBase)this).User, "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"), out var result))
		{
			return Guid.Empty;
		}
		return result;
	}

	[HttpGet("check")]
	[Authorize(Policy = "RequiresElevation")]
	public async Task<ActionResult> Check(CancellationToken ct)
	{
		var (ok, text) = await _seerr.PruefenAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		return (ActionResult)new JsonResult((object)new { ok, text });
	}

	// Whether the browser page that asked uses https. Behind a TLS proxy Jellyfin itself may only see http, the
	// browser's Origin header still tells.
	private bool SeiteSicher()
	{
		HttpRequest request = ((ControllerBase)this).Request;
		return request.IsHttps || request.Headers.Origin.ToString().StartsWith("https://", StringComparison.OrdinalIgnoreCase);
	}

	// The plugin repository answers only inside the home network: from the internet nobody can list or download
	// what is installed here.
	private bool ImHeimnetz()
	{
		try
		{
			IPAddress ip = ((ControllerBase)this).HttpContext.GetNormalizedRemoteIP();
			if (!Lokal(ip))
			{
				return false;
			}
			// Behind a reverse proxy Jellyfin does not know (no known proxies set), the proxy's own address looks
			// local although the caller is not. Every address the proxy passed on must be local too.
			foreach (string adresse in Weitergeleitet(((ControllerBase)this).Request))
			{
				if (!IPAddress.TryParse(adresse, out IPAddress? weiter) || !Lokal(weiter))
				{
					return false;
				}
			}
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private bool Lokal(IPAddress ip)
	{
		return IPAddress.IsLoopback(ip) || _network.IsInLocalNetwork(ip);
	}

	private static IEnumerable<string> Weitergeleitet(HttpRequest request)
	{
		foreach (string kopf in new[] { "X-Forwarded-For", "X-Real-IP" })
		{
			foreach (string? wert in request.Headers[kopf])
			{
				foreach (string teil in (wert ?? string.Empty).Split(','))
				{
					if (teil.Trim().Length > 0)
					{
						yield return OhnePort(teil.Trim());
					}
				}
			}
		}
		foreach (string? wert in request.Headers["Forwarded"])
		{
			foreach (string teil in (wert ?? string.Empty).Split(',', ';'))
			{
				string t = teil.Trim();
				if (t.StartsWith("for=", StringComparison.OrdinalIgnoreCase))
				{
					yield return OhnePort(t.Substring(4).Trim('"'));
				}
			}
		}
	}

	// "1.2.3.4:5678" and "[2001:db8::1]:443" become the bare address; a bare IPv6 address stays as it is.
	private static string OhnePort(string adresse)
	{
		if (adresse.StartsWith('['))
		{
			int zu = adresse.IndexOf(']');
			return zu > 0 ? adresse.Substring(1, zu - 1) : adresse;
		}
		int doppelpunkt = adresse.IndexOf(':');
		return doppelpunkt > 0 && doppelpunkt == adresse.LastIndexOf(':') ? adresse.Substring(0, doppelpunkt) : adresse;
	}

	[HttpGet("repository.json")]
	[AllowAnonymous]
	public ActionResult Repository()
	{
		if (!ImHeimnetz())
		{
			return (ActionResult)(object)((ControllerBase)this).NotFound();
		}
		//IL_001d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0074: Unknown result type (might be due to invalid IL or missing references)
		//IL_007a: Expected O, but got Unknown
		string scheme = ((ControllerBase)this).Request.Scheme;
		HostString host = ((ControllerBase)this).Request.Host;
		string basis = scheme + "://" + host.Value + "/SeerrBridge/repository/";
		var list = (from p in _repo.Pakete()
			select new
			{
				guid = p.Guid,
				name = p.Name,
				description = p.Description,
				overview = p.Overview,
				owner = p.Owner,
				category = p.Category,
				imageUrl = (string)null,
				versions = new[]
				{
					new
					{
						version = p.Version,
						changelog = p.Changelog,
						targetAbi = p.TargetAbi,
						sourceUrl = basis + p.Guid + "/" + p.Version + ".zip",
						checksum = p.Pruefsumme,
						timestamp = p.Stand.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
					}
				}
			}).ToList();
		((ControllerBase)this).Response.Headers.CacheControl = ("no-cache");
		return (ActionResult)new JsonResult((object)list);
	}

	[HttpGet("repository/{guid}/logo.png")]
	[AllowAnonymous]
	public ActionResult RepositoryBild(string guid)
	{
		if (!ImHeimnetz())
		{
			return (ActionResult)(object)((ControllerBase)this).NotFound();
		}
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		MkiPaket mkiPaket = _repo.Finden(guid);
		if (mkiPaket?.Bild == null || !System.IO.File.Exists(mkiPaket.Bild))
		{
			return (ActionResult)(object)((ControllerBase)this).NotFound();
		}
		((ControllerBase)this).Response.Headers.CacheControl = ("public, max-age=3600");
		return (ActionResult)(object)((ControllerBase)this).PhysicalFile(mkiPaket.Bild, "image/png");
	}

	[HttpGet("repository/{guid}/{version}.zip")]
	[AllowAnonymous]
	public ActionResult RepositoryPaket(string guid, string version)
	{
		if (!ImHeimnetz())
		{
			return (ActionResult)(object)((ControllerBase)this).NotFound();
		}
		MkiPaket mkiPaket = _repo.Finden(guid);
		if (mkiPaket == null || !string.Equals(mkiPaket.Version, version, StringComparison.OrdinalIgnoreCase))
		{
			return (ActionResult)(object)((ControllerBase)this).NotFound();
		}
		return (ActionResult)(object)((ControllerBase)this).File(mkiPaket.Zip, "application/zip", mkiPaket.Name.Replace(' ', '_') + "_" + mkiPaket.Version + ".zip");
	}

	[HttpGet("client.js")]
	[AllowAnonymous]
	public ActionResult GetScript()
	{
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		return Ressource("seerr.js", "application/javascript");
	}

	[HttpGet("client.css")]
	[AllowAnonymous]
	public ActionResult GetStyle()
	{
		return Ressource("seerr.css", "text/css");
	}

	private ActionResult Ressource(string name, string typ)
	{
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		Assembly executingAssembly = Assembly.GetExecutingAssembly();
		string text = executingAssembly.GetManifestResourceNames().FirstOrDefault((string n) => n.EndsWith("." + name, StringComparison.Ordinal));
		if (text == null)
		{
			return (ActionResult)(object)((ControllerBase)this).NotFound();
		}
		Stream manifestResourceStream = executingAssembly.GetManifestResourceStream(text);
		if (manifestResourceStream == null)
		{
			return (ActionResult)(object)((ControllerBase)this).NotFound();
		}
		((ControllerBase)this).Response.Headers.CacheControl = ("public, max-age=300");
		return (ActionResult)(object)((ControllerBase)this).File(manifestResourceStream, typ);
	}

	private string? Benutzername()
	{
		if (!Guid.TryParse(PrincipalExtensions.FindFirstValue(((ControllerBase)this).User, "Jellyfin-UserId") ?? PrincipalExtensions.FindFirstValue(((ControllerBase)this).User, "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"), out var result) || result == Guid.Empty)
		{
			return null;
		}
		try
		{
			User userById = _userManager.GetUserById(result);
			return (userById != null) ? userById.Username : null;
		}
		catch (Exception)
		{
			return null;
		}
	}
}
