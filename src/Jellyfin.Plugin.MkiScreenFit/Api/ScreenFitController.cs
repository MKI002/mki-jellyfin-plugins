using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Jellyfin.Data.Queries;
using Jellyfin.Plugin.MkiScreenFit.Configuration;
using Jellyfin.Plugin.MkiScreenFit.Services;
using MediaBrowser.Controller.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MkiScreenFit.Api;

// A signed-in client reports its own screen; user and device always come from the session, never from the request.
// The device list and the recent decisions are for administrators only.
[ApiController]
[Route("MkiScreenFit")]
public class ScreenFitController : ControllerBase
{
	// Devices shown on the settings page: a server rarely knows more, and the page lists them all at once.
	private const int MaxDevices = 500;

	// Device and app names come from the players' own request headers: kept short.
	private const int MaxName = 100;

	private readonly ScreenStore _screens;

	private readonly ActivityLog _activity;

	private readonly IDeviceManager _devices;

	public ScreenFitController(ScreenStore screens, ActivityLog activity, IDeviceManager devices)
	{
		_screens = screens;
		_activity = activity;
		_devices = devices;
	}

	// The web client script. Static, holds no data.
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
		Response.Headers.XContentTypeOptions = "nosniff";
		return File(stream, "application/javascript; charset=utf-8");
	}

	// The calling device's screen, in physical pixels. Stored for this user and this device only.
	[HttpPost("Screen")]
	[Authorize]
	public ActionResult Screen([FromBody] ScreenRequest request)
	{
		Private();
		string? deviceId = User.FindFirst("Jellyfin-DeviceId")?.Value;
		if (!Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var userId) || userId == Guid.Empty || string.IsNullOrEmpty(deviceId) || deviceId.Length > 256)
		{
			return Error(StatusCodes.Status400BadRequest, "No device in this session.");
		}
		if (request == null || !ScreenRules.ValidSize(request.Width, request.Height))
		{
			return Error(StatusCodes.Status400BadRequest, "Not a screen size.");
		}
		if (!Plugin.AnyOn)
		{
			// Nothing uses it while both parts are off, so nothing is kept.
			return NoContent();
		}
		// Jellyfin takes the device id from the request, so a signed-in user could name any device. Only the device
		// this sign-in belongs to may report its screen.
		string? token = User.FindFirst("Jellyfin-Token")?.Value;
		bool apiKey = bool.TryParse(User.FindFirst("Jellyfin-IsApiKey")?.Value, out bool isKey) && isKey;
		var own = apiKey || string.IsNullOrEmpty(token) ? null : _devices.GetDevices(new DeviceQuery { AccessToken = token, Limit = 1 }).Items.FirstOrDefault();
		if (own == null || own.UserId != userId || !string.Equals(own.DeviceId, deviceId, StringComparison.Ordinal))
		{
			return Error(StatusCodes.Status400BadRequest, "This sign-in does not belong to this device.");
		}
		_screens.Report(userId, deviceId, Math.Max(request.Width, request.Height), Math.Min(request.Width, request.Height), request.Reliable);
		return NoContent();
	}

	// Every device Jellyfin knows, with the screen it reported and what the plugin does for it, and the recent decisions.
	[HttpGet("Overview")]
	[Authorize(Policy = "RequiresElevation")]
	public ActionResult Overview()
	{
		Private();
		var now = DateTime.UtcNow;
		var reports = _screens.All().GroupBy(r => r.DeviceId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
		// Only names, apps and dates leave the server: never the access tokens Jellyfin keeps with each device.
		var devices = (_devices.GetDevicesForUser(null).Items ?? Array.Empty<MediaBrowser.Model.Dto.DeviceInfoDto>())
			.Where(d => !string.IsNullOrEmpty(d.Id))
			.GroupBy(d => d.Id!, StringComparer.Ordinal)
			.Select(g =>
			{
				var latest = g.OrderByDescending(d => d.DateLastActivity ?? DateTime.MinValue).First();
				// The screen as the users of this device reported it, the latest one first: what their playback uses.
				var users = g.Select(d => d.LastUserId).Where(u => u.HasValue).Select(u => u!.Value).ToHashSet();
				reports.TryGetValue(g.Key, out var list);
				var screen = list?.Where(r => users.Contains(r.UserId))
					.OrderByDescending(r => r.UserId == latest.LastUserId).ThenByDescending(r => r.At).FirstOrDefault();
				return new
				{
					Id = g.Key,
					Name = Cut(string.IsNullOrWhiteSpace(latest.CustomName) ? latest.Name ?? string.Empty : latest.CustomName),
					App = Cut(latest.AppName ?? string.Empty),
					AppVersion = Cut(latest.AppVersion ?? string.Empty),
					Users = g.Select(d => d.LastUserName).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).Select(n => Cut(n!)).ToList(),
					LastActive = latest.DateLastActivity,
					Mode = Plugin.ModeOf(g.Key).ToString(),
					Screen = screen == null ? null : new
					{
						screen.Width,
						screen.Height,
						screen.Reliable,
						Current = now - screen.At < ScreenStore.MaxAge,
						screen.At,
						FullHd = ScreenRules.IsFullHdScreen(screen.Width, screen.Height)
					}
				};
			})
			.OrderByDescending(d => d.LastActive ?? DateTime.MinValue)
			.Take(MaxDevices)
			.ToList();
		var log = _activity.Snapshot();
		return new JsonResult(new
		{
			Devices = devices,
			DeviceRules = Plugin.Rules(),
			WebClientProblem = WebPatcher.Problem,
			Activity = log.Entries,
			log.Versions,
			log.Limits,
			log.Kept,
			Since = _activity.Since,
			FullHdMaxShortSide = ScreenRules.FullHdMaxShortSide,
			MinLimit = new { Width = ScreenRules.MinLimitWidth, Height = ScreenRules.MinLimitHeight }
		});
	}

	// One device's setting, changed on its own so that two administrators never overwrite each other's changes.
	[HttpPost("DeviceRule")]
	[Authorize(Policy = "RequiresElevation")]
	public ActionResult SetDeviceRule([FromBody] DeviceRuleRequest request)
	{
		Private();
		if (request == null || request.Mode is not { } mode || !Enum.IsDefined(mode) || (request.From is { } from && !Enum.IsDefined(from)))
		{
			return Error(StatusCodes.Status400BadRequest, "Not a device setting.");
		}
		if (!Plugin.UsableDeviceId(request.DeviceId))
		{
			return Error(StatusCodes.Status400BadRequest, "This device's id cannot be kept in a setting.");
		}
		var rules = Plugin.SetRule(request.DeviceId, mode, request.From, out bool stale);
		if (stale)
		{
			return StatusCode(StatusCodes.Status412PreconditionFailed, new { Message = "This device's setting was changed elsewhere since.", DeviceRules = rules });
		}
		if (rules == null)
		{
			return Error(StatusCodes.Status409Conflict, "Too many device settings.");
		}
		return new JsonResult(new { DeviceRules = rules });
	}

	private static string Cut(string text) => text.Length > MaxName ? text[..(MaxName - 1)] + "…" : text;

	private void Private()
	{
		Response.Headers.CacheControl = "private, no-store";
		Response.Headers.Vary = "Authorization";
	}

	private ObjectResult Error(int status, string text) => StatusCode(status, new { Message = text });
}

public class DeviceRuleRequest
{
	public string DeviceId { get; set; } = string.Empty;

	// Required: a request without it must not remove a setting.
	public DeviceMode? Mode { get; set; }

	// For an undo: the mode the device must still have, so a newer change made elsewhere is not overwritten.
	public DeviceMode? From { get; set; }
}

public class ScreenRequest
{
	public int Width { get; set; }

	public int Height { get; set; }

	public bool Reliable { get; set; }
}
