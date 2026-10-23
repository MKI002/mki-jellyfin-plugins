using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MkiScreenFit.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

	private readonly SwapMemory _swaps;

	private readonly ActivityLog _activity;

	private readonly IUserManager _users;

	private readonly ILibraryManager _library;

	private readonly IMediaSourceManager _mediaSources;

	private readonly IMediaEncoder _encoder;

	private readonly IDeviceManager _devices;

	private readonly INetworkManager _network;

	private readonly IServerConfigurationManager _server;

	private readonly ILogger<PlaybackInfoFilter> _logger;

	public PlaybackInfoFilter(ScreenStore screens, SwapMemory swaps, ActivityLog activity, IUserManager users, ILibraryManager library, IMediaSourceManager mediaSources, IMediaEncoder encoder, IDeviceManager devices, INetworkManager network, IServerConfigurationManager server, ILogger<PlaybackInfoFilter> logger)
	{
		_screens = screens;
		_swaps = swaps;
		_activity = activity;
		_users = users;
		_library = library;
		_mediaSources = mediaSources;
		_encoder = encoder;
		_devices = devices;
		_network = network;
		_server = server;
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

	// The plan for the transcode limit, or null when the screen is unknown or the request is left alone.
	private Plan? Before(ActionExecutingContext context)
	{
		var principal = context.HttpContext.User;
		string? deviceId = principal.FindFirst("Jellyfin-DeviceId")?.Value;
		if (!Guid.TryParse(principal.FindFirst("Jellyfin-UserId")?.Value, out var userId) || userId == Guid.Empty || string.IsNullOrEmpty(deviceId)
			|| !context.ActionArguments.TryGetValue("itemId", out var idArg) || idArg is not Guid itemId)
		{
			return null;
		}
		var request = new Request(context.ActionArguments);
		// Asked on behalf of another user (an administrator's tool), or for a live stream: not this device's
		// viewer, so nothing changes.
		if ((request.UserId.HasValue && request.UserId.Value != Guid.Empty && request.UserId.Value != userId) || !string.IsNullOrEmpty(request.LiveStreamId))
		{
			return null;
		}
		var mode = Plugin.ModeOf(deviceId);
		if (mode == DeviceMode.Off)
		{
			return null;
		}
		int width = 0, height = 0;
		string source = string.Empty;
		if (mode == DeviceMode.FullHd)
		{
			(width, height, source) = (ScreenRules.FullHdWidth, ScreenRules.FullHdHeight, "rule");
		}
		else if (_screens.Get(userId, deviceId) is { } report)
		{
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
		if (Plugin.Config.EnableFullHdVersion)
		{
			if (plan.Known && ScreenRules.IsFullHdScreen(width, height))
			{
				SwapVersion(context, plan, request);
			}
			else
			{
				RestoreVersion(context, plan, request);
			}
		}
		// Screen unknown: no transcode limit either.
		return plan.Known ? plan : null;
	}

	// The item's versions, or null when there is only one. Picked is a version other than the default that was
	// picked by hand: that choice stays.
	private Found? Versions(Plan plan, Request request)
	{
		var user = _users.GetUserById(plan.UserId);
		var item = user == null ? null : _library.GetItemById<BaseItem>(plan.ItemId, user);
		if (user == null || item is not Video)
		{
			return null;
		}
		plan.ItemName = NameOf(item);
		plan.UserName = user.Username;
		// Only versions this user may see (library access, parental rating), as Jellyfin's own answer.
		var sources = _mediaSources.GetStaticMediaSources(item, false, user);
		if (sources.Count < 2)
		{
			return null;
		}
		string? picked = !string.IsNullOrEmpty(request.MediaSourceId) && !string.Equals(request.MediaSourceId, sources[0].Id, StringComparison.OrdinalIgnoreCase) ? request.MediaSourceId : null;
		return new Found(user, item, sources, picked);
	}

	private void SwapVersion(ActionExecutingContext context, Plan plan, Request request)
	{
		if (Versions(plan, request) is not { Picked: null } found)
		{
			return;
		}
		var sources = found.Sources;
		var choice = ScreenRules.Choose(sources, request.Audio, request.Subtitle, WorkFor(context.HttpContext, plan, request, found.User));
		if (choice.To != null)
		{
			Apply(context.ActionArguments, choice, request);
			_swaps.Remember(plan.UserId, choice.To.Id);
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

	// Jellyfin puts the version watched last first. When that is a Full HD version only because the plugin played
	// it on a Full HD screen, any other screen gets the item's own version back, as without the plugin.
	private void RestoreVersion(ActionExecutingContext context, Plan plan, Request request)
	{
		if (Versions(plan, request) is not { } found)
		{
			return;
		}
		if (found.Picked != null)
		{
			// Picked by hand on this screen: from now on that version is the user's own choice, not the plugin's.
			_swaps.Forget(plan.UserId, found.Picked);
			return;
		}
		// The default version is the primary one, also when Continue Watching opens the Full HD version directly.
		var primary = found.Item is Video { PrimaryVersionId: { } id } ? id : found.Item.Id;
		var choice = ScreenRules.Restore(found.Sources, primary.ToString("N"), request.Audio, request.Subtitle, sourceId => _swaps.Played(plan.UserId, sourceId), WorkFor(context.HttpContext, plan, request, found.User));
		if (choice.To == null)
		{
			return;
		}
		Apply(context.ActionArguments, choice, request);
		bool fresh = Record(plan, "restore", ScreenRules.SizeOf(choice.From?.VideoStream), ScreenRules.SizeOf(choice.To.VideoStream), string.Empty);
		_logger.Log(
			fresh ? LogLevel.Information : LogLevel.Debug,
			"MKI Screen Fit: {Device} is not a known Full HD screen, resuming {Item} in the {To} version again",
			plan.DeviceName,
			plan.ItemName,
			ScreenRules.SizeOf(choice.To.VideoStream));
	}

	// The query values win over the body, as in Jellyfin's own code.
	private static void Apply(IDictionary<string, object?> args, VersionChoice choice, Request request)
	{
		args["mediaSourceId"] = choice.To!.Id;
		if (request.Audio.HasValue)
		{
			args["audioStreamIndex"] = choice.Audio;
		}
		if (request.Subtitle.HasValue)
		{
			args["subtitleStreamIndex"] = choice.Subtitle;
		}
	}

	// How much work a version means for this player, worked out the way Jellyfin plans playback for it, or null
	// when the player sent no device profile.
	private Func<MediaSourceInfo, int?, int?, int>? WorkFor(HttpContext http, Plan plan, Request request, User user)
	{
		var profile = request.Profile ?? _devices.GetCapabilities(plan.DeviceId)?.DeviceProfile;
		if (profile == null)
		{
			return null;
		}
		int? maxBitrate = MaxBitrate(http, request.MaxBitrate ?? profile.MaxStreamingBitrate, user);
		var builder = new StreamBuilder(_encoder, NullLogger.Instance);
		return (source, audio, subtitle) => ScreenRules.Work(builder.GetOptimalVideoStream(new MediaOptions
		{
			Profile = profile,
			MediaSources = new[] { source },
			MediaSourceId = source.Id,
			Context = EncodingContext.Streaming,
			DeviceId = plan.DeviceId,
			ItemId = plan.ItemId,
			AudioStreamIndex = audio,
			SubtitleStreamIndex = subtitle,
			MaxAudioChannels = request.MaxAudioChannels,
			MaxBitrate = maxBitrate,
			EnableDirectPlay = request.EnableDirectPlay,
			// As in Jellyfin: direct streaming over plain HTTP is not offered.
			EnableDirectStream = false,
			AllowAudioStreamCopy = request.AllowAudioCopy,
			AllowVideoStreamCopy = request.AllowVideoCopy,
			AlwaysBurnInSubtitleWhenTranscoding = request.AlwaysBurnIn
		}));
	}

	// The bitrate limit Jellyfin plans with (MediaInfoHelper.GetMaxBitrate): the player's own, lowered for players
	// outside the local network to the user's or the server's limit for remote clients. Only read, never changed.
	private int? MaxBitrate(HttpContext http, int? requested, User user)
	{
		int remote = user.RemoteClientBitrateLimit ?? 0;
		if (remote <= 0)
		{
			remote = _server.Configuration.RemoteClientBitrateLimit;
		}
		if (remote > 0 && http.GetNormalizedRemoteIP() is { } ip && !_network.IsInLocalNetwork(ip))
		{
			return Math.Min(requested ?? remote, remote);
		}
		return requested;
	}

	private void After(ActionExecutedContext executed, Plan plan)
	{
		if (!Plugin.Config.EnableTranscodeLimit || executed.Result is not ObjectResult { Value: PlaybackInfoResponse info } || info.MediaSources == null)
		{
			return;
		}
		foreach (var source in info.MediaSources)
		{
			var video = source?.VideoStream;
			if (source == null || video == null || source.IsInfiniteStream || string.IsNullOrEmpty(source.TranscodingUrl))
			{
				continue;
			}
			int maxWidth = Math.Max(ScreenRules.MinLimitWidth, plan.Width);
			int maxHeight = Math.Max(ScreenRules.MinLimitHeight, plan.Height);
			// The picture as shown: Jellyfin turns a video stored on its side before it scales it.
			int width = video.Width ?? 0, height = video.Height ?? 0;
			if (Math.Abs(video.Rotation ?? 0) % 180 == 90)
			{
				(width, height) = (height, width);
			}
			// The screen turns with the picture: a portrait video is held against the screen's long side.
			if (height > width)
			{
				(maxWidth, maxHeight) = (maxHeight, maxWidth);
			}
			if ((width <= maxWidth && height <= maxHeight) || !ScreenRules.ReencodesVideo(source))
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
			UserId = plan.UserId,
			UserName = plan.UserName,
			ItemId = plan.ItemId,
			ItemName = plan.ItemName,
			From = from,
			To = to,
			Reason = reason,
			Screen = plan.Source == "rule" ? "set to Full HD" : plan.Known ? plan.Width + "x" + plan.Height : "unknown screen"
		});
	}

	private static string NameOf(BaseItem item) => item is Episode e && !string.IsNullOrEmpty(e.SeriesName) ? e.SeriesName + " · " + e.Name : item.Name ?? string.Empty;

	private sealed record Found(User User, BaseItem Item, IReadOnlyList<MediaSourceInfo> Sources, string? Picked);

	private sealed class Plan
	{
		public Guid UserId { get; init; }

		public string DeviceId { get; init; } = string.Empty;

		public string DeviceName { get; init; } = string.Empty;

		public string Client { get; init; } = string.Empty;

		public Guid ItemId { get; init; }

		// The screen, 0 x 0 when it is unknown.
		public int Width { get; init; }

		public int Height { get; init; }

		public bool Known => Width > 0 && Height > 0;

		public string Source { get; init; } = string.Empty;

		public string ItemName { get; set; } = string.Empty;

		public string UserName { get; set; } = string.Empty;
	}

	// The request's playback options, read as Jellyfin reads them: a query value wins over the body.
	private sealed class Request
	{
		public Request(IDictionary<string, object?> args)
		{
			args.TryGetValue("playbackInfoDto", out var dto);
			UserId = Arg<Guid?>(args, "userId") ?? Prop<Guid?>(dto, "UserId");
			MediaSourceId = Arg<string>(args, "mediaSourceId") ?? Prop<string>(dto, "MediaSourceId");
			LiveStreamId = Arg<string>(args, "liveStreamId") ?? Prop<string>(dto, "LiveStreamId");
			Audio = Arg<int?>(args, "audioStreamIndex") ?? Prop<int?>(dto, "AudioStreamIndex");
			Subtitle = Arg<int?>(args, "subtitleStreamIndex") ?? Prop<int?>(dto, "SubtitleStreamIndex");
			MaxBitrate = Arg<int?>(args, "maxStreamingBitrate") ?? Prop<int?>(dto, "MaxStreamingBitrate");
			MaxAudioChannels = Arg<int?>(args, "maxAudioChannels") ?? Prop<int?>(dto, "MaxAudioChannels");
			EnableDirectPlay = Arg<bool?>(args, "enableDirectPlay") ?? Prop<bool?>(dto, "EnableDirectPlay") ?? true;
			AllowVideoCopy = Arg<bool?>(args, "allowVideoStreamCopy") ?? Prop<bool?>(dto, "AllowVideoStreamCopy") ?? true;
			AllowAudioCopy = Arg<bool?>(args, "allowAudioStreamCopy") ?? Prop<bool?>(dto, "AllowAudioStreamCopy") ?? true;
			AlwaysBurnIn = Prop<bool?>(dto, "AlwaysBurnInSubtitleWhenTranscoding") ?? false;
			Profile = Prop<DeviceProfile>(dto, "DeviceProfile");
		}

		public Guid? UserId { get; }

		public string? MediaSourceId { get; }

		public string? LiveStreamId { get; }

		public int? Audio { get; }

		public int? Subtitle { get; }

		public int? MaxBitrate { get; }

		public int? MaxAudioChannels { get; }

		public bool EnableDirectPlay { get; }

		public bool AllowVideoCopy { get; }

		public bool AllowAudioCopy { get; }

		public bool AlwaysBurnIn { get; }

		public DeviceProfile? Profile { get; }

		private static T? Arg<T>(IDictionary<string, object?> args, string name) =>
			args.TryGetValue(name, out var v) && v is T t ? t : default;

		// PlaybackInfoDto lives in Jellyfin's API assembly, which plugins do not reference: read it by name.
		private static T? Prop<T>(object? dto, string name)
		{
			var p = dto?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
			return p != null && p.GetValue(dto) is T t ? t : default;
		}
	}
}
