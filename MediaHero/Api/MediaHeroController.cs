using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaHero.Configuration;
using Jellyfin.Plugin.MediaHero.Services;
using MediaBrowser.Common.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.MediaHero.Api;

[ApiController]
[Route("MediaHero")]
public class MediaHeroController : ControllerBase
{
	private readonly HeroBuilder _builder;

	public MediaHeroController(HeroBuilder builder)
	{
		_builder = builder;
	}

	[HttpGet("items")]
	[Authorize]
	public unsafe async Task<ActionResult> GetItems(CancellationToken ct)
	{
		PluginConfiguration cfg = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration ?? new PluginConfiguration();
		IReadOnlyList<HeroItem> items = await _builder.GetAsync(NutzerId(), ct).ConfigureAwait(continueOnCapturedContext: false);
		string eTag = _builder.ETag;
		if (!string.IsNullOrEmpty(eTag) && ((IDictionary<string, StringValues>)((ControllerBase)this).Request.Headers).TryGetValue("If-None-Match", out StringValues value) && ((object)(*(StringValues*)(&value))/*cast due to .constrained prefix*/).ToString() == eTag)
		{
			return (ActionResult)(object)((ControllerBase)this).StatusCode(304);
		}
		if (!string.IsNullOrEmpty(eTag))
		{
			((ControllerBase)this).Response.Headers["ETag"] = StringValues.op_Implicit(eTag);
		}
		((ControllerBase)this).Response.Headers.CacheControl = StringValues.op_Implicit("private, max-age=60");
		return (ActionResult)new JsonResult((object)new HeroPayload
		{
			Items = items,
			BuiltAtUnix = new DateTimeOffset(_builder.BuiltUtc, TimeSpan.Zero).ToUnixTimeSeconds(),
			Display = Anzeige(cfg)
		});
	}

	[HttpGet("display")]
	[Authorize]
	public ActionResult GetDisplay()
	{
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Expected O, but got Unknown
		return (ActionResult)new JsonResult(Anzeige(((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration ?? new PluginConfiguration()));
	}

	[HttpPost("refresh")]
	[Authorize(Policy = "RequiresElevation")]
	public ActionResult Refresh()
	{
		_builder.Invalidate();
		return (ActionResult)(object)((ControllerBase)this).NoContent();
	}

	[HttpGet("client.js")]
	[AllowAnonymous]
	public ActionResult GetScript()
	{
		return Datei("mediahero.js", "application/javascript; charset=utf-8");
	}

	[HttpGet("client.css")]
	[AllowAnonymous]
	public ActionResult GetStyle()
	{
		return Datei("mediahero.css", "text/css; charset=utf-8");
	}

	private ActionResult Datei(string name, string typ)
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
		((ControllerBase)this).Response.Headers.CacheControl = StringValues.op_Implicit("public, max-age=300");
		return (ActionResult)(object)((ControllerBase)this).File(manifestResourceStream, typ);
	}

	private Guid NutzerId()
	{
		if (!Guid.TryParse(PrincipalExtensions.FindFirstValue(((ControllerBase)this).User, "Jellyfin-UserId") ?? PrincipalExtensions.FindFirstValue(((ControllerBase)this).User, "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier"), out var result))
		{
			return Guid.Empty;
		}
		return result;
	}

	private static object Anzeige(PluginConfiguration c)
	{
		return new
		{
			enabled = c.Enabled,
			layout = c.Layout.ToString(),
			transition = c.Transition.ToString(),
			indicator = c.Indicator.ToString(),
			heightVh = c.HeightVh,
			maxHeightPx = c.MaxHeightPx,
			cornerRadius = c.CornerRadius,
			scrimStrength = c.ScrimStrength,
			blendIntoHeader = c.BlendIntoHeader,
			bottomFadePercent = c.BottomFadePercent,
			logoMaxWidthPercent = c.LogoMaxWidthPercent,
			logoMaxHeightPercent = c.LogoMaxHeightPercent,
			textLeftPercent = c.TextLeftPercent,
			textBottomPercent = c.TextBottomPercent,
			textWidthPercent = c.TextWidthPercent,
			elementGap = c.ElementGap,
			titleScale = c.TitleScale,
			textScale = c.TextScale,
			buttonScale = c.ButtonScale,
			showLogo = c.ShowLogo,
			showTitleWhenNoLogo = c.ShowTitleWhenNoLogo,
			showOverview = c.ShowOverview,
			overviewLines = c.OverviewLines,
			showYear = c.ShowYear,
			showRating = c.ShowRating,
			showRuntime = c.ShowRuntime,
			showGenres = c.ShowGenres,
			showOfficialRating = c.ShowOfficialRating,
			showPlayButton = c.ShowPlayButton,
			showDetailsButton = c.ShowDetailsButton,
			showTrailerButton = c.ShowTrailerButton,
			showFavoriteButton = c.ShowFavoriteButton,
			shuffleOnLoad = c.ShuffleOnLoad,
			autoplay = c.Autoplay,
			intervalSeconds = c.IntervalSeconds,
			pauseOnHover = c.PauseOnHover,
			enableKeyboard = c.EnableKeyboard,
			enableSwipe = c.EnableSwipe,
			respectReducedMotion = c.RespectReducedMotion,
			homeOnly = c.HomeOnly,
			customCss = c.CustomCss
		};
	}
}
