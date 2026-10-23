using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.MkiScreenFit.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiScreenFit.Services;

// Runs around Jellyfin's "POST Items/{itemId}/PlaybackInfo", the request every client sends before it plays a
// video. Before Jellyfin answers it may ask for the Full HD version instead of the default 4K one; afterwards it
// may lower the size of a transcode to the screen. Every other request passes untouched, and any error here lets
// Jellyfin answer exactly as it would without the plugin.
public class PlaybackInfoFilter : IAsyncActionFilter
{
	private const string ControllerType = "Jellyfin.Api.Controllers.MediaInfoController";

	private const string ActionName = "GetPostedPlaybackInfo";

	private readonly ScreenStore _screens;

	private readonly ActivityLog _activity;

	private readonly IUserManager _users;

	private readonly ILibraryManager _library;

	private readonly IMediaSourceManager _mediaSources;

	private readonly ILogger<PlaybackInfoFilter> _logger;

	public PlaybackInfoFilter(ScreenStore screens, ActivityLog activity, IUserManager users, ILibraryManager library, IMediaSourceManager mediaSources, ILogger<PlaybackInfoFilter> logger)
	{
		_screens = screens;
		_activity = activity;
		_users = users;
		_library = library;
		_mediaSources = mediaSources;
		_logger = logger;
	}

	public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
	{
		if (!IsPlaybackInfo(context) || !Plugin.AnyOn)
		{
			await next().ConfigureAwait(false);
			return;
		}
		Plan? plan = null;
		try
		{
			plan = Before(context);
		}
		catch (Exception ex)
		{
			plan = null;
			_logger.LogError(ex, "MKI Screen Fit: could not check the version to play; Jellyfin's default is used");
		}
		var executed = await next().ConfigureAwait(false);
		if (plan == null || executed.Exception != null)
		{
			return;
		}
		try
		{
			After(executed, plan);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "MKI Screen Fit: could not limit the transcode size; Jellyfin's answer is used as it is");
		}
	}

	private static bool IsPlaybackInfo(ActionExecutingContext context) =>
		context.ActionDescriptor is ControllerActionDescriptor action
		&& string.Equals(action.ActionName, ActionName, StringComparison.Ordinal)
		&& string.Equals(action.ControllerTypeInfo.FullName, ControllerType, StringComparison.Ordinal);

	private Plan? Before(ActionExecutingContext context)
	{
		var principal = context.HttpContext.User;
		string? deviceId = principal.FindFirst("Jellyfin-DeviceId")?.Value;
		if (!Guid.TryParse(principal.FindFirst("Jellyfin-UserId")?.Value, out var userId) || userId == Guid.Empty || string.IsNullOrEmpty(deviceId)
			|| !context.ActionArguments.TryGetValue("itemId", out var idArg) || idArg is not Guid itemId)
		{
			return null;
		}
		var mode = Plugin.ModeOf(deviceId);
		int width, height;
		string source;
		if (mode == DeviceMode.Off)
		{
			return null;
		}
		if (mode == DeviceMode.FullHd)
		{
			(width, height, source) = (ScreenRules.FullHdWidth, ScreenRules.FullHdHeight, "rule");
		}
		else
		{
			var report = _screens.Get(userId, deviceId);
			if (report == null)
			{
				// Screen unknown: nothing is changed.
				return null;
			}
			(width, height, source) = (report.Width, report.Height, "reported");
		}

		var plan = new Plan
		{
			UserId = userId,
			DeviceId = deviceId,
			DeviceName = principal.FindFirst("Jellyfin-Device")?.Value ?? string.Empty,
			Client = principal.FindFirst("Jellyfin-Client")?.Value ?? string.Empty,
			ItemId = itemId,
			Width = width,
			Height = height,
			Source = source
		};
		if (Plugin.Config.EnableFullHdVersion && ScreenRules.IsFullHdScreen(width, height))
		{
			SwapVersion(context, plan);
		}
		return plan;
	}

	private void SwapVersion(ActionExecutingContext context, Plan plan)
	{
		var args = context.ActionArguments;
		args.TryGetValue("playbackInfoDto", out var dto);
		// The query value wins over the body, as in Jellyfin's own code.
		var askedUser = Arg<Guid?>(args, "userId") ?? Prop<Guid?>(dto, "UserId");
		string? mediaSourceId = Arg<string>(args, "mediaSourceId") ?? Prop<string>(dto, "MediaSourceId");
		string? liveStreamId = Arg<string>(args, "liveStreamId") ?? Prop<string>(dto, "LiveStreamId");
		int? audio = Arg<int?>(args, "audioStreamIndex") ?? Prop<int?>(dto, "AudioStreamIndex");
		int? subtitle = Arg<int?>(args, "subtitleStreamIndex") ?? Prop<int?>(dto, "SubtitleStreamIndex");

		// Asked on behalf of another user (an administrator's tool): not this device's viewer, so leave it.
		if ((askedUser.HasValue && askedUser.Value != Guid.Empty && askedUser.Value != plan.UserId) || !string.IsNullOrEmpty(liveStreamId))
		{
			return;
		}
		var user = _users.GetUserById(plan.UserId);
		var item = user == null ? null : _library.GetItemById<BaseItem>(plan.ItemId, user);
		if (user == null || item is not Video)
		{
			return;
		}
		plan.ItemName = NameOf(item);
		plan.UserName = user.Username;
		var sources = _mediaSources.GetStaticMediaSources(item, false, user);
		if (sources.Count < 2)
		{
			return;
		}
		// A version other than the default was picked by hand: that choice stays.
		if (!string.IsNullOrEmpty(mediaSourceId) && !string.Equals(mediaSourceId, sources[0].Id, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		var choice = ScreenRules.Choose(sources, audio, subtitle);
		if (choice.To != null)
		{
			args["mediaSourceId"] = choice.To.Id;
			if (audio.HasValue)
			{
				args["audioStreamIndex"] = choice.Audio;
			}
			if (subtitle.HasValue)
			{
				args["subtitleStreamIndex"] = choice.Subtitle;
			}
			bool fresh = Record(plan, "version", ScreenRules.SizeOf(choice.From?.VideoStream), ScreenRules.SizeOf(choice.To.VideoStream), string.Empty);
			_logger.Log(
				fresh ? LogLevel.Information : LogLevel.Debug,
				"MKI Screen Fit: {Device} has a {Width}x{Height} screen, playing the {To} version of {Item} instead of {From}",
				plan.DeviceName,
				plan.Width,
				plan.Height,
				ScreenRules.SizeOf(choice.To.VideoStream),
				plan.ItemName,
				ScreenRules.SizeOf(choice.From?.VideoStream));
		}
		else if (choice.Kept != null)
		{
			Record(plan, "kept", ScreenRules.SizeOf(sources[0].VideoStream), string.Empty, choice.Kept);
		}
	}

	private void After(ActionExecutedContext executed, Plan plan)
	{
		if (!Plugin.Config.EnableTranscodeLimit || executed.Result is not ObjectResult { Value: PlaybackInfoResponse info } || info.MediaSources == null)
		{
			return;
		}
		int maxWidth = Math.Max(ScreenRules.MinLimitWidth, plan.Width);
		int maxHeight = Math.Max(ScreenRules.MinLimitHeight, plan.Height);
		foreach (var source in info.MediaSources)
		{
			var video = source?.VideoStream;
			if (source == null || video == null || source.IsInfiniteStream || string.IsNullOrEmpty(source.TranscodingUrl)
				|| ((video.Width ?? 0) <= maxWidth && (video.Height ?? 0) <= maxHeight)
				|| !ScreenRules.ReencodesVideo(source))
			{
				continue;
			}
			string? url = ScreenRules.LimitUrl(source.TranscodingUrl, maxWidth, maxHeight);
			if (url == null)
			{
				continue;
			}
			source.TranscodingUrl = url;
			if (plan.ItemName.Length == 0)
			{
				var item = _library.GetItemById(plan.ItemId);
				plan.ItemName = item == null ? string.Empty : NameOf(item);
				plan.UserName = _users.GetUserById(plan.UserId)?.Username ?? string.Empty;
			}
			Record(plan, "limit", ScreenRules.SizeOf(video), "at most " + maxWidth + "x" + maxHeight, Reasons(source));
		}
	}

	private static string Reasons(MediaSourceInfo source)
	{
		var flags = Enum.GetValues<MediaBrowser.Model.Session.TranscodeReason>().Where(r => source.TranscodeReasons.HasFlag(r)).Select(r => r.ToString());
		return string.Join(", ", flags);
	}

	private bool Record(Plan plan, string kind, string from, string to, string reason)
	{
		return _activity.Add(new Decision
		{
			Kind = kind,
			DeviceId = plan.DeviceId,
			DeviceName = plan.DeviceName,
			Client = plan.Client,
			UserName = plan.UserName,
			ItemId = plan.ItemId,
			ItemName = plan.ItemName,
			From = from,
			To = to,
			Reason = reason,
			Screen = plan.Source == "rule" ? "set to Full HD" : plan.Width + "x" + plan.Height
		});
	}

	private static string NameOf(BaseItem item) => item is Episode e && !string.IsNullOrEmpty(e.SeriesName) ? e.SeriesName + " · " + e.Name : item.Name ?? string.Empty;

	private static T? Arg<T>(System.Collections.Generic.IDictionary<string, object?> args, string name) =>
		args.TryGetValue(name, out var v) && v is T t ? t : default;

	// PlaybackInfoDto lives in Jellyfin's API assembly, which plugins do not reference: read it by name.
	private static T? Prop<T>(object? dto, string name)
	{
		var p = dto?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
		return p != null && p.GetValue(dto) is T t ? t : default;
	}

	private sealed class Plan
	{
		public Guid UserId { get; init; }

		public string DeviceId { get; init; } = string.Empty;

		public string DeviceName { get; init; } = string.Empty;

		public string Client { get; init; } = string.Empty;

		public Guid ItemId { get; init; }

		public int Width { get; init; }

		public int Height { get; init; }

		public string Source { get; init; } = string.Empty;

		public string ItemName { get; set; } = string.Empty;

		public string UserName { get; set; } = string.Empty;
	}
}
