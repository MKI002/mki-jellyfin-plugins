using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MkiGuestPass.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiGuestPass.Api;

// Three groups of endpoints:
// - public: the join page, its script and the three join calls. They only answer for a valid link, code or rejoin
//   key, are rate-limited per address, take small JSON bodies only and are never cached;
// - signed in: Me, about the caller's own guest account (the badge in the web client);
// - administrators: everything that creates, changes or deletes passes and guests.
[ApiController]
[Route("MkiGuestPass")]
[Authorize]
public class GuestPassController : ControllerBase
{
	private const int MaxBody = 4096;

	private readonly GuestService _guests;

	private readonly IUserManager _users;

	private readonly ILogger<GuestPassController> _logger;

	public GuestPassController(GuestService guests, IUserManager users, ILogger<GuestPassController> logger)
	{
		_guests = guests;
		_users = users;
		_logger = logger;
	}

	// ---------- public ----------

	[HttpGet("Join")]
	[AllowAnonymous]
	public ActionResult JoinPage()
	{
		// ".../Join/" would serve the page under another folder, where its script and calls are not found.
		string path = Request.Path.Value ?? string.Empty;
		if (path.EndsWith('/'))
		{
			Response.Headers.CacheControl = "no-store";
			return Redirect(Request.PathBase + path.TrimEnd('/') + Request.QueryString);
		}
		// The secret lives in the part after #, which browsers never send: not to this server, not as a referrer.
		// The page still allows no foreign code, no framing and no referrer, and nothing of it is stored.
		Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
		Response.Headers["X-Frame-Options"] = "DENY";
		Response.Headers["Referrer-Policy"] = "no-referrer";
		Response.Headers["X-Content-Type-Options"] = "nosniff";
		Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
		Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
		Response.Headers.CacheControl = "no-store";
		return Embedded("join.html", "text/html; charset=utf-8");
	}

	[HttpGet("join.js")]
	[AllowAnonymous]
	public ActionResult JoinScript()
	{
		Response.Headers["X-Content-Type-Options"] = "nosniff";
		Response.Headers.CacheControl = "no-cache";
		return Embedded("join.js", "application/javascript; charset=utf-8");
	}

	// The web client script: static, holds no data. It asks Me (signed in) before it shows a badge.
	[HttpGet("client.js")]
	[AllowAnonymous]
	public ActionResult ClientScript()
	{
		Response.Headers["X-Content-Type-Options"] = "nosniff";
		Response.Headers.CacheControl = "no-cache";
		return Embedded("client.js", "application/javascript; charset=utf-8");
	}

	[HttpPost("Peek")]
	[Consumes("application/json")]
	[RequestSizeLimit(MaxBody)]
	[AllowAnonymous]
	public Task<ActionResult> Peek([FromBody] JoinRequest request) => Run(() => Task.FromResult(_guests.Peek(request, Origin())));

	[HttpPost("Redeem")]
	[Consumes("application/json")]
	[RequestSizeLimit(MaxBody)]
	[AllowAnonymous]
	public Task<ActionResult> Redeem([FromBody] JoinRequest request) => Run(() => _guests.Redeem(request, Origin()));

	[HttpPost("Rejoin")]
	[Consumes("application/json")]
	[RequestSizeLimit(MaxBody)]
	[AllowAnonymous]
	public Task<ActionResult> Rejoin([FromBody] JoinRequest request) => Run(() => _guests.Rejoin(request, Origin()));

	// ---------- signed in ----------

	[HttpGet("Me")]
	[Authorize]
	public ActionResult Me()
	{
		NoStore();
		var user = Caller();
		return user == null ? Unauthorized() : new JsonResult(_guests.Me(user));
	}

	// ---------- administrators ----------

	[HttpGet("Overview")]
	[Authorize(Policy = "RequiresElevation")]
	public ActionResult Overview()
	{
		NoStore();
		return new JsonResult(_guests.Overview(Origin()));
	}

	[HttpPost("Passes")]
	[Authorize(Policy = "RequiresElevation")]
	[Consumes("application/json")]
	[RequestSizeLimit(16 * 1024)]
	public Task<ActionResult> CreatePass([FromBody] NewPass request) => Admin(admin => _guests.CreatePass(request, admin));

	[HttpPost("Passes/{id}/NewLink")]
	[Authorize(Policy = "RequiresElevation")]
	public Task<ActionResult> NewLink([FromRoute] Guid id) => Admin(admin => _guests.NewLink(id, admin));

	[HttpPost("Passes/{id}/Extend")]
	[Authorize(Policy = "RequiresElevation")]
	[Consumes("application/json")]
	[RequestSizeLimit(MaxBody)]
	public Task<ActionResult> Extend([FromRoute] Guid id, [FromBody] EndRequest request) => Admin(async admin =>
	{
		await _guests.Extend(id, request.EndUtc, request.Reopen, admin).ConfigureAwait(false);
		return new { Done = true };
	});

	[HttpPost("Passes/{id}/Where")]
	[Authorize(Policy = "RequiresElevation")]
	[Consumes("application/json")]
	[RequestSizeLimit(MaxBody)]
	public Task<ActionResult> Where([FromRoute] Guid id, [FromBody] WhereRequest request) => Admin(async admin =>
	{
		await _guests.SetWhere(id, request.RemoteAccess, request.From, admin).ConfigureAwait(false);
		return new { Done = true };
	});

	[HttpPost("Passes/{id}/End")]
	[Authorize(Policy = "RequiresElevation")]
	public Task<ActionResult> EndPass([FromRoute] Guid id) => Admin(async admin =>
	{
		await _guests.EndPass(id, admin).ConfigureAwait(false);
		return new { Done = true };
	});

	[HttpDelete("Passes/{id}")]
	[Authorize(Policy = "RequiresElevation")]
	public Task<ActionResult> DeletePass([FromRoute] Guid id, [FromQuery] int? guests) => Admin(async admin =>
	{
		await _guests.DeletePass(id, guests, admin).ConfigureAwait(false);
		return new { Done = true };
	});

	[HttpPost("Guests/{id}/End")]
	[Authorize(Policy = "RequiresElevation")]
	public Task<ActionResult> EndGuest([FromRoute] Guid id) => Admin(async admin =>
	{
		await _guests.EndGuest(id, admin).ConfigureAwait(false);
		return new { Done = true };
	});

	[HttpDelete("Guests/{id}")]
	[Authorize(Policy = "RequiresElevation")]
	public Task<ActionResult> DeleteGuest([FromRoute] Guid id) => Admin(async admin =>
	{
		await _guests.DeleteGuest(id, admin).ConfigureAwait(false);
		return new { Done = true };
	});

	// From: where the pass worked when the page last looked, so a change made meanwhile is not overwritten.
	public class WhereRequest
	{
		public bool RemoteAccess { get; set; }

		public bool From { get; set; }
	}

	public class EndRequest
	{
		public DateTime EndUtc { get; set; }

		public bool Reopen { get; set; }
	}

	// ---------- helpers ----------

	// Behind a reverse proxy this is the client's address only when Jellyfin knows the proxy (Networking → Known proxies).
	private IPAddress? Address()
	{
		var ip = HttpContext.Connection.RemoteIpAddress;
		return ip != null && ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
	}

	// The request gate saw the connection before Jellyfin applied a known proxy's headers. Without its note (it always
	// runs first) any forwarding header counts as an unknown proxy.
	private JoinOrigin Origin()
	{
		var ip = Address();
		if (HttpContext.Items[GuestGate.OriginKey] is GuestGate.Seen seen)
		{
			return new JoinOrigin(ip, seen.ViaUnknownProxy, seen.ViaUnknownProxy ? seen.Visitor : null);
		}
		return new JoinOrigin(ip, GuestGate.HasForwarding(Request), null);
	}

	private User? Caller()
	{
		var claim = User.FindFirstValue("Jellyfin-UserId") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
		return Guid.TryParse(claim, out var id) ? _users.GetUserById(id) : null;
	}

	private void NoStore()
	{
		Response.Headers.CacheControl = "private, no-store";
		Response.Headers.Vary = "Authorization";
	}

	private async Task<ActionResult> Run(Func<Task<object>> action)
	{
		NoStore();
		try
		{
			return new JsonResult(await action().ConfigureAwait(false));
		}
		catch (GuestError e)
		{
			return StatusCode(e.Status, new { e.Message, e.Reason });
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "MKI Guest Pass request failed");
			return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "Something went wrong. Please try again." });
		}
	}

	private Task<ActionResult> Admin(Func<User, Task<object>> action)
	{
		var admin = Caller();
		if (admin == null)
		{
			return Task.FromResult<ActionResult>(Unauthorized());
		}
		return Run(() => action(admin));
	}

	private ActionResult Embedded(string name, string type)
	{
		var assembly = Assembly.GetExecutingAssembly();
		string? resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Web." + name, StringComparison.Ordinal));
		Stream? stream = resource == null ? null : assembly.GetManifestResourceStream(resource);
		return stream == null ? NotFound() : File(stream, type);
	}
}
