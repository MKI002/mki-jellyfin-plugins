using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MkiContinueWatching.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MkiContinueWatching.Api;

// Users act only on their own Continue Watching: the user always comes from the session, never from the request.
// Everything that looks at or changes other users' data needs an administrator.
[ApiController]
[Route("MkiContinueWatching")]
public class ContinueWatchingController : ControllerBase
{
	private readonly ResumeService _resume;

	private readonly IUserManager _users;

	public ContinueWatchingController(ResumeService resume, IUserManager users)
	{
		_resume = resume;
		_users = users;
	}

	// The web client script. Static, holds no data; the script asks Status (signed in) before it shows anything.
	[HttpGet("client.js")]
	[AllowAnonymous]
	public ActionResult GetScript()
	{
		var assembly = Assembly.GetExecutingAssembly();
		string? name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".client.js", StringComparison.Ordinal));
		Stream? stream = name == null ? null : assembly.GetManifestResourceStream(name);
		if (stream == null)
		{
			return NotFound();
		}
		Response.Headers.CacheControl = "no-cache";
		return File(stream, "application/javascript; charset=utf-8");
	}

	[HttpGet("Status")]
	[Authorize]
	public ActionResult Status()
	{
		Private();
		return new JsonResult(new { RemoveButton = Plugin.Config.EnableRemoveButton });
	}

	[HttpPost("Remove")]
	[Authorize]
	public ActionResult Remove([FromBody] ItemRequest request)
	{
		Private();
		var user = Caller();
		if (user == null)
		{
			return Unauthorized();
		}
		if (!Plugin.Config.EnableRemoveButton)
		{
			return Error(StatusCodes.Status403Forbidden, "Removing from Continue Watching is turned off on this server.");
		}
		if (!Guid.TryParse(request?.ItemId, out var id) || id == Guid.Empty)
		{
			return Error(StatusCodes.Status400BadRequest, "No item given.");
		}
		int? removed = _resume.Remove(user, id);
		return removed == null ? Error(StatusCodes.Status404NotFound, "Item not found.") : new JsonResult(new { Removed = removed });
	}

	// Undo for the user's own last removal of this item.
	[HttpPost("Restore")]
	[Authorize]
	public ActionResult Restore([FromBody] ItemRequest request)
	{
		Private();
		var user = Caller();
		if (user == null)
		{
			return Unauthorized();
		}
		if (!Guid.TryParse(request?.ItemId, out var id) || id == Guid.Empty)
		{
			return Error(StatusCodes.Status400BadRequest, "No item given.");
		}
		return new JsonResult(new { Restored = _resume.Restore(user, id) });
	}

	// For the settings page: what a cleanup with these days would clear now, and the recent runs.
	[HttpGet("Overview")]
	[Authorize(Policy = "RequiresElevation")]
	public ActionResult Overview([FromQuery] int? days)
	{
		Private();
		int d = Math.Clamp(days ?? Plugin.Days, Plugin.MinDays, Plugin.MaxDays);
		var runs = _resume.Store.Runs();
		return new JsonResult(new
		{
			Days = d,
			MinDays = Plugin.MinDays,
			MaxDays = Plugin.MaxDays,
			KeepDays = ClearedStore.KeepDays,
			Users = _resume.Preview(d),
			Runs = runs.Take(10),
			RemovedLast7Days = _resume.Store.ManualSince(DateTime.UtcNow.AddDays(-7))
		});
	}

	[HttpPost("Run")]
	[Authorize(Policy = "RequiresElevation")]
	public ActionResult Run()
	{
		Private();
		if (!Plugin.Config.EnableAutoCleanup)
		{
			return Error(StatusCodes.Status409Conflict, "Auto cleanup is off. Turn it on first.");
		}
		var run = _resume.RunCleanup("manual", CancellationToken.None);
		return run == null ? Error(StatusCodes.Status409Conflict, "Auto cleanup is off. Turn it on first.") : new JsonResult(run);
	}

	[HttpPost("UndoRun")]
	[Authorize(Policy = "RequiresElevation")]
	public ActionResult UndoRun([FromBody] RunRequest request)
	{
		Private();
		if (!Guid.TryParse(request?.RunId, out var id) || _resume.Store.Runs().All(r => r.Id != id))
		{
			return Error(StatusCodes.Status404NotFound, "That cleanup run is no longer in the undo history.");
		}
		return new JsonResult(new { Restored = _resume.UndoRun(id) });
	}

	private User? Caller()
	{
		var claim = User.FindFirstValue("Jellyfin-UserId") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
		return Guid.TryParse(claim, out var id) && id != Guid.Empty ? _users.GetUserById(id) : null;
	}

	// Answers differ per user: never cached or shared between accounts.
	private void Private()
	{
		Response.Headers.CacheControl = "private, no-store";
		Response.Headers.Vary = "Authorization";
	}

	private ObjectResult Error(int status, string text) => StatusCode(status, new { Message = text });
}

public class ItemRequest
{
	public string? ItemId { get; set; }
}

public class RunRequest
{
	public string? RunId { get; set; }
}
