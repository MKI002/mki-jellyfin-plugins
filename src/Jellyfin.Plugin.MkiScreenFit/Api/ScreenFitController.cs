using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
				// The screen as the device's latest user reported it: what their playback uses.
				reports.TryGetValue(g.Key, out var list);
				var screen = list?.Where(r => r.UserId == latest.LastUserId).OrderByDescending(r => r.At).FirstOrDefault()
					?? list?.OrderByDescending(r => r.At).FirstOrDefault();
				return new
				{
					Id = g.Key,
					Name = string.IsNullOrWhiteSpace(latest.CustomName) ? latest.Name ?? string.Empty : latest.CustomName,
					App = latest.AppName ?? string.Empty,
					AppVersion = latest.AppVersion ?? string.Empty,
					Users = g.Select(d => d.LastUserName).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).ToList(),
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
			Activity = log.Entries,
			log.Versions,
			log.Limits,
			log.Kept,
			Since = _activity.Since,
			FullHdMaxShortSide = ScreenRules.FullHdMaxShortSide,
			MinLimit = new { Width = ScreenRules.MinLimitWidth, Height = ScreenRules.MinLimitHeight }
		});
	}

	private void Private()
	{
		Response.Headers.CacheControl = "private, no-store";
		Response.Headers.Vary = "Authorization";
	}

	private ObjectResult Error(int status, string text) => StatusCode(status, new { Message = text });
}

public class ScreenRequest
{
	public int Width { get; set; }

	public int Height { get; set; }

	public bool Reliable { get; set; }
}
