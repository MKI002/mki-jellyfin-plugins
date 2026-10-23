using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.MkiLoginShowcase.Configuration;
using Jellyfin.Plugin.MkiLoginShowcase.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.MkiLoginShowcase.Api;

// The sign-in page is shown before anyone signs in, so three things here answer without a sign-in: the static script,
// the showcase (style plus which pictures there are) and the pictures themselves, by their position in the current pick.
// They never name an item id, a path or a library. Titles and counts are included only when an administrator turned
// them on. Everything else needs an administrator.
[ApiController]
[Route("MkiLoginShowcase")]
public class LoginShowcaseController : ControllerBase
{
	private const int PosterWidth = 480;

	private const int BackdropWidth = 1920;

	private static readonly Regex SetId = new("^[0-9a-f]{10}$", RegexOptions.CultureInvariant);

	private readonly ShowcaseService _showcase;

	private readonly ILibraryManager _library;

	private readonly IImageProcessor _images;

	private readonly IApplicationPaths _paths;

	private readonly ILogger<LoginShowcaseController> _logger;

	public LoginShowcaseController(ShowcaseService showcase, ILibraryManager library, IImageProcessor images, IApplicationPaths paths, ILogger<LoginShowcaseController> logger)
	{
		_showcase = showcase;
		_library = library;
		_images = images;
		_paths = paths;
		_logger = logger;
	}

	// The sign-in page script. Static, holds no data; it asks Showcase before it shows anything.
	[HttpGet("login.js")]
	[AllowAnonymous]
	public ActionResult GetScript()
	{
		var assembly = Assembly.GetExecutingAssembly();
		string? name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".login.js", StringComparison.Ordinal));
		Stream? stream = name == null ? null : assembly.GetManifestResourceStream(name);
		if (stream == null)
		{
			return NotFound();
		}
		Response.Headers.CacheControl = "no-cache";
		Response.Headers.XContentTypeOptions = "nosniff";
		return File(stream, "application/javascript; charset=utf-8");
	}

	// What the sign-in page shows: the look, and for each picked title whether it has a poster and a backdrop.
	[HttpGet("Showcase")]
	[AllowAnonymous]
	public ActionResult GetShowcase()
	{
		Response.Headers.CacheControl = "no-store";
		Response.Headers.XContentTypeOptions = "nosniff";
		var config = Plugin.Config;
		if (!config.Enabled)
		{
			return new JsonResult(new { On = false });
		}
		var set = _showcase.Current();
		if (set.Items.Count == 0)
		{
			return new JsonResult(new { On = false });
		}
		// Titles and counts only when turned on, and only for the style that shows them. The pick already holds only the
		// pictures that style draws, so no title is named that is not on screen.
		bool titles = config.ShowTitles && config.Style == ShowcaseStyle.Spotlight && set.Backdrops;
		bool counts = config.ShowCounts && config.Style == ShowcaseStyle.Mosaic && !set.Backdrops;
		return new JsonResult(new
		{
			On = true,
			Style = config.Style.ToString(),
			Accent = config.Accent.ToString(),
			Motion = config.Motion.ToString(),
			Card = config.NeonCard,
			Set = set.Id,
			Items = set.Items.Select(e => new { P = e.Poster, B = e.Backdrop, T = titles ? e.Name : null }),
			Caption = titles ? Caption(set.Pick) : null,
			Movies = counts ? set.Movies : null,
			Series = counts ? set.Series : null
		});
	}

	// One picture of a current pick, by position: kind "p" is the poster, "b" the backdrop.
	[HttpGet("Image/{set}/{index}/{kind}")]
	[AllowAnonymous]
	public async Task<ActionResult> GetImage([FromRoute] string set, [FromRoute] int index, [FromRoute] string kind)
	{
		var config = Plugin.Config;
		if (!config.Enabled || set == null || !SetId.IsMatch(set))
		{
			return NotFound();
		}
		ImageType type;
		if (string.Equals(kind, "p", StringComparison.Ordinal))
		{
			type = ImageType.Primary;
		}
		else if (string.Equals(kind, "b", StringComparison.Ordinal))
		{
			type = ImageType.Backdrop;
		}
		else
		{
			return NotFound();
		}
		var pick = _showcase.Find(set);
		if (pick == null || index < 0 || index >= pick.Items.Count)
		{
			return NotFound();
		}
		var entry = pick.Items[index];
		if (type == ImageType.Primary ? !entry.Poster : !entry.Backdrop)
		{
			return NotFound();
		}
		try
		{
			// Checked again: the title may have been re-rated, moved or deleted since it was picked.
			var item = _library.GetItemById(entry.ItemId);
			if (item == null || !ShowcaseService.Allowed(item, config) || !ShowcaseService.LocalImage(item, type))
			{
				return NotFound();
			}
			var info = item.GetImageInfo(type, 0);
			var (path, mime, _) = await _images.ProcessImage(new ImageProcessingOptions
			{
				Item = item,
				ItemId = item.Id,
				Image = info,
				ImageIndex = 0,
				MaxWidth = type == ImageType.Primary ? PosterWidth : BackdropWidth,
				Quality = 85,
				SupportedOutputFormats = Formats()
			}).ConfigureAwait(false);
			// Only a picture Jellyfin has just drawn again, at our size, into its own cache. When Jellyfin hands back the
			// original file instead (a GIF or SVG, a picture it cannot read, no image encoder), nothing is sent: an
			// original can carry camera and location data, and an SVG can carry script.
			if (!Redrawn(path, info.Path) || !(mime is "image/webp" or "image/jpeg"))
			{
				_logger.LogDebug("MKI Login Showcase skipped a picture Jellyfin could not redraw");
				return NotFound();
			}
			// A pick's pictures never change (a new pick has a new id), but browsers only, not shared caches: a library
			// that was just taken out must not stay reachable through a proxy. Sent without Last-Modified or ETag, which
			// would tell when someone last had the sign-in page open.
			Response.Headers.CacheControl = "private, max-age=600";
			Response.Headers.Vary = HeaderNames.Accept;
			Response.Headers.XContentTypeOptions = "nosniff";
			return File(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous), mime);
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "MKI Login Showcase could not serve a picture");
			return NotFound();
		}
	}

	// For the settings page: the libraries it can offer, the current pick (with item ids, for the preview) and whether
	// the sign-in page script could be added to the web client.
	[HttpGet("Overview")]
	[Authorize(Policy = "RequiresElevation")]
	public ActionResult Overview()
	{
		Private();
		return new JsonResult(Describe(Plugin.Config, _showcase.Current()));
	}

	// New titles now, instead of at the next hourly pick.
	[HttpPost("Shuffle")]
	[Authorize(Policy = "RequiresElevation")]
	public ActionResult Shuffle()
	{
		Private();
		_showcase.Reset();
		return new JsonResult(Describe(Plugin.Config, _showcase.Current()));
	}

	private object Describe(PluginConfiguration config, ShowcaseSet set)
	{
		return new
		{
			Libraries = _showcase.Libraries(config),
			Set = set.Id,
			Built = set.BuiltUtc,
			NextPick = set.BuiltUtc.AddHours(1),
			MaxPosters = ShowcaseService.MaxPosters,
			MaxBackdrops = ShowcaseService.MaxBackdrops,
			Items = set.Items.Select(e => new
			{
				Id = e.ItemId.ToString("N"),
				e.Name,
				e.Year,
				PosterTag = e.Poster ? Tag(e.ItemId, ImageType.Primary) : null,
				BackdropTag = e.Backdrop ? Tag(e.ItemId, ImageType.Backdrop) : null
			}).ToList(),
			Movies = set.Movies,
			Series = set.Series,
			WebClient = WebPatcher.Status
		};
	}

	private string? Tag(Guid id, ImageType type)
	{
		try
		{
			var item = _library.GetItemById(id);
			var info = item?.GetImageInfo(type, 0);
			return item == null || info == null ? null : _images.GetImageCacheTag(item, info);
		}
		catch (Exception)
		{
			return null;
		}
	}

	private bool Redrawn(string? path, string original)
	{
		if (string.IsNullOrEmpty(path) || string.Equals(path, original, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(path))
		{
			return false;
		}
		string cache = Path.GetFullPath(Path.Combine(_paths.ImageCachePath, "resized-images")) + Path.DirectorySeparatorChar;
		return Path.GetFullPath(path).StartsWith(cache, StringComparison.Ordinal);
	}

	private static string Caption(ShowcasePick pick) => pick switch
	{
		ShowcasePick.Recent => "Recently added",
		ShowcasePick.TopRated => "Top rated",
		_ => "From the library"
	};

	// WebP for browsers that ask for it, JPEG otherwise, like Jellyfin's own image answers.
	private ImageFormat[] Formats()
	{
		var accept = Request.Headers[HeaderNames.Accept].ToString();
		return accept.Contains("image/webp", StringComparison.OrdinalIgnoreCase)
			? new[] { ImageFormat.Webp, ImageFormat.Jpg }
			: new[] { ImageFormat.Jpg };
	}

	// Answers for administrators: never cached or shared.
	private void Private()
	{
		Response.Headers.CacheControl = "private, no-store";
		Response.Headers.Vary = "Authorization";
	}
}
