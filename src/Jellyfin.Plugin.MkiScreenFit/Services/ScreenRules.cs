using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Session;

namespace Jellyfin.Plugin.MkiScreenFit.Services;

// The decisions, without any Jellyfin service: which version to play, and how large a transcode may be.
// Whenever a rule is unsure it changes nothing, so playback is never worse than without the plugin.
public static class ScreenRules
{
	// A screen whose shorter side has at most this many pixels shows no more than Full HD: 1920x1080, 1920x1200,
	// a 1366x768 laptop, a phone at 2400x1080. A 2560x1440 monitor, an iPad or a 4K TV is larger.
	public const int FullHdMaxShortSide = 1200;

	// A transcode is never limited below this, so small screens still get a sharp picture.
	public const int MinLimitWidth = 1280;

	public const int MinLimitHeight = 720;

	// The screen assumed for a device the administrator set to "Full HD".
	public const int FullHdWidth = 1920;

	public const int FullHdHeight = 1080;

	// Versions whose lengths differ by more than this are treated as different cuts (theatrical, extended, ...)
	// and are never swapped for each other.
	public static readonly long MaxLengthDifferenceTicks = TimeSpan.FromSeconds(60).Ticks;

	// Reasons for which Jellyfin re-encodes the video. Only then is a smaller size free: limiting the size of a
	// transcode that would otherwise copy the video (only the container or the audio change) would force a
	// re-encode, which costs far more than it saves.
	private const TranscodeReason VideoReasons =
		TranscodeReason.VideoCodecNotSupported
		| TranscodeReason.VideoProfileNotSupported
		| TranscodeReason.VideoRangeTypeNotSupported
		| TranscodeReason.VideoCodecTagNotSupported
		| TranscodeReason.VideoLevelNotSupported
		| TranscodeReason.VideoResolutionNotSupported
		| TranscodeReason.VideoBitDepthNotSupported
		| TranscodeReason.VideoFramerateNotSupported
		| TranscodeReason.VideoRotationNotSupported
		| TranscodeReason.RefFramesNotSupported
		| TranscodeReason.AnamorphicVideoNotSupported
		| TranscodeReason.InterlacedVideoNotSupported
		| TranscodeReason.VideoBitrateNotSupported
		| TranscodeReason.ContainerBitrateExceedsLimit;

	// ISO 639-2 bibliographic codes and common two-letter codes, mapped to one spelling so "ger" matches "deu".
	private static readonly Dictionary<string, string> LanguageAliases = new(StringComparer.OrdinalIgnoreCase)
	{
		["ger"] = "deu", ["de"] = "deu", ["fre"] = "fra", ["fr"] = "fra", ["en"] = "eng", ["es"] = "spa", ["it"] = "ita",
		["ja"] = "jpn", ["ko"] = "kor", ["chi"] = "zho", ["zh"] = "zho", ["cze"] = "ces", ["cs"] = "ces", ["dut"] = "nld",
		["nl"] = "nld", ["gre"] = "ell", ["el"] = "ell", ["per"] = "fas", ["fa"] = "fas", ["rum"] = "ron", ["ro"] = "ron",
		["slo"] = "slk", ["sk"] = "slk", ["alb"] = "sqi", ["arm"] = "hye", ["baq"] = "eus", ["bur"] = "mya", ["geo"] = "kat",
		["ice"] = "isl", ["mac"] = "mkd", ["mao"] = "mri", ["may"] = "msa", ["tib"] = "bod", ["wel"] = "cym", ["pt"] = "por",
		["ru"] = "rus", ["pl"] = "pol", ["tr"] = "tur", ["sv"] = "swe", ["da"] = "dan", ["no"] = "nor", ["fi"] = "fin",
		["hu"] = "hun", ["vi"] = "vie", ["th"] = "tha", ["ar"] = "ara", ["he"] = "heb", ["hi"] = "hin", ["uk"] = "ukr"
	};

	public static bool ValidSize(int width, int height) => width >= 160 && height >= 120 && width <= 16384 && height <= 16384;

	public static bool IsFullHdScreen(int width, int height) => Math.Min(width, height) <= FullHdMaxShortSide;

	// 4K and other sizes clearly above Full HD (1440p is not counted: its versions are rare and close to 1080p).
	public static bool IsAboveFullHd(MediaStream? video) => video != null && ((video.Width ?? 0) > 2560 || (video.Height ?? 0) > 1600);

	// 1080p, including scope (1920x800), 4:3 (1440x1080) and 2K DCI (2048x858). Never 720p or smaller.
	public static bool IsFullHd(MediaStream? video)
	{
		int w = video?.Width ?? 0, h = video?.Height ?? 0;
		return w > 0 && h > 0 && w <= 2048 && h <= 1200 && (w >= 1600 || h >= 1000);
	}

	public static string SizeOf(MediaStream? video) => video?.Width > 0 && video.Height > 0
		? video.Width.Value.ToString(CultureInfo.InvariantCulture) + "x" + video.Height.Value.ToString(CultureInfo.InvariantCulture)
		: "unknown size";

	// Picks the Full HD version to play instead of the default one, or says why it keeps the default.
	// sources[0] is the version Jellyfin plays by default (the item itself); audio and subtitle are the stream
	// indexes the client asked for in that version.
	public static VersionChoice Choose(IReadOnlyList<MediaSourceInfo> sources, int? audio, int? subtitle)
	{
		var from = sources[0];
		if (!IsAboveFullHd(from.VideoStream))
		{
			return VersionChoice.Nothing;
		}
		var candidates = sources.Skip(1)
			.Where(s => s.Protocol == MediaProtocol.File && !s.IsInfiniteStream && IsFullHd(s.VideoStream))
			.OrderByDescending(s => (long)(s.VideoStream.Width ?? 0) * (s.VideoStream.Height ?? 0))
			.ToList();
		if (candidates.Count == 0)
		{
			return VersionChoice.Keep("no Full HD version");
		}
		string reason = "no Full HD version";
		foreach (var to in candidates)
		{
			if (!SameLength(from, to))
			{
				reason = "the Full HD version has a different length (another cut?)";
				continue;
			}
			if (!MapAudio(from, to, audio, out int? newAudio))
			{
				reason = "the Full HD version lacks the chosen audio track";
				continue;
			}
			if (!MapSubtitle(from, to, subtitle, out int? newSubtitle))
			{
				reason = "the Full HD version lacks the chosen subtitles";
				continue;
			}
			return new VersionChoice { From = from, To = to, Audio = newAudio, Subtitle = newSubtitle };
		}
		return VersionChoice.Keep(reason);
	}

	public static bool SameLength(MediaSourceInfo a, MediaSourceInfo b)
	{
		long x = a.RunTimeTicks ?? 0, y = b.RunTimeTicks ?? 0;
		return x > 0 && y > 0 && Math.Abs(x - y) <= MaxLengthDifferenceTicks;
	}

	// The same audio track in the other version: same language, preferring the same codec and channel count.
	public static bool MapAudio(MediaSourceInfo from, MediaSourceInfo to, int? index, out int? mapped)
	{
		mapped = null;
		if (index == null)
		{
			// Nothing asked for: Jellyfin picks the user's preferred track in the new version.
			return true;
		}
		var wanted = Streams(from, MediaStreamType.Audio).FirstOrDefault(s => s.Index == index.Value);
		var options = Streams(to, MediaStreamType.Audio).ToList();
		if (wanted == null || options.Count == 0)
		{
			return false;
		}
		var match = SameLanguage(wanted, from, MediaStreamType.Audio, to, options)
			.OrderByDescending(s => string.Equals(s.Codec, wanted.Codec, StringComparison.OrdinalIgnoreCase))
			.ThenByDescending(s => s.Channels == wanted.Channels)
			.ThenByDescending(s => s.Channels ?? 0)
			.FirstOrDefault();
		mapped = match?.Index;
		return match != null;
	}

	// The same subtitles in the other version: same language, forced and SDH flags, preferring the same format.
	public static bool MapSubtitle(MediaSourceInfo from, MediaSourceInfo to, int? index, out int? mapped)
	{
		mapped = index;
		if (index == null || index.Value < 0)
		{
			// Nothing asked for, or subtitles off: stays as it is.
			return true;
		}
		var wanted = Streams(from, MediaStreamType.Subtitle).FirstOrDefault(s => s.Index == index.Value);
		if (wanted == null)
		{
			return false;
		}
		var options = Streams(to, MediaStreamType.Subtitle)
			.Where(s => s.IsForced == wanted.IsForced && s.IsHearingImpaired == wanted.IsHearingImpaired).ToList();
		var match = SameLanguage(wanted, from, MediaStreamType.Subtitle, to, options)
			.OrderByDescending(s => string.Equals(s.Codec, wanted.Codec, StringComparison.OrdinalIgnoreCase))
			.ThenByDescending(s => s.IsExternal == wanted.IsExternal)
			.FirstOrDefault();
		mapped = match?.Index;
		return match != null;
	}

	// Streams with the wanted stream's language. Without a language tag the stream is matched by its position,
	// and only when both versions have the same number of such streams.
	private static IEnumerable<MediaStream> SameLanguage(MediaStream wanted, MediaSourceInfo from, MediaStreamType type, MediaSourceInfo to, List<MediaStream> options)
	{
		string? lang = Language(wanted.Language);
		if (lang != null)
		{
			return options.Where(s => Language(s.Language) == lang);
		}
		var mine = Streams(from, type).ToList();
		var theirs = Streams(to, type).ToList();
		int pos = mine.FindIndex(s => s.Index == wanted.Index);
		if (pos < 0 || mine.Count != theirs.Count || Language(theirs[pos].Language) != null)
		{
			return Enumerable.Empty<MediaStream>();
		}
		return options.Where(s => s.Index == theirs[pos].Index);
	}

	private static IEnumerable<MediaStream> Streams(MediaSourceInfo source, MediaStreamType type) =>
		(source.MediaStreams ?? Array.Empty<MediaStream>()).Where(s => s != null && s.Type == type);

	private static string? Language(string? code)
	{
		if (string.IsNullOrWhiteSpace(code) || string.Equals(code, "und", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		string c = code.Trim().ToLowerInvariant();
		return LanguageAliases.TryGetValue(c, out var alias) ? alias : c;
	}

	// True when Jellyfin will re-encode the video of this source if the client transcodes it.
	public static bool ReencodesVideo(MediaSourceInfo source)
	{
		if ((source.TranscodeReasons & VideoReasons) != 0)
		{
			return true;
		}
		// Burned-in subtitles are drawn into the picture, so the video is re-encoded.
		return source.TranscodingUrl != null
			&& source.TranscodingUrl.Contains("SubtitleMethod=Encode", StringComparison.OrdinalIgnoreCase);
	}

	// The transcode URL with MaxWidth and MaxHeight no larger than the screen. Only lowers values, never raises
	// them, and leaves every other part of the URL (bitrate, codecs, throttling) exactly as Jellyfin wrote it.
	// Returns null when nothing changes.
	public static string? LimitUrl(string url, int maxWidth, int maxHeight)
	{
		int q = url.IndexOf('?', StringComparison.Ordinal);
		if (q < 0 || url.Contains('#', StringComparison.Ordinal))
		{
			return null;
		}
		var parts = url.Substring(q + 1).Split('&').ToList();
		bool changed = false;
		changed |= Cap(parts, "MaxWidth", maxWidth);
		changed |= Cap(parts, "MaxHeight", maxHeight);
		return changed ? url.Substring(0, q + 1) + string.Join('&', parts) : null;
	}

	private static bool Cap(List<string> parts, string name, int limit)
	{
		bool found = false, changed = false;
		for (int i = 0; i < parts.Count; i++)
		{
			int eq = parts[i].IndexOf('=', StringComparison.Ordinal);
			string key = eq < 0 ? parts[i] : parts[i].Substring(0, eq);
			if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			found = true;
			string value = eq < 0 ? string.Empty : parts[i].Substring(eq + 1);
			if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int current) && current > 0 && current <= limit)
			{
				continue;
			}
			parts[i] = name + "=" + limit.ToString(CultureInfo.InvariantCulture);
			changed = true;
		}
		if (!found)
		{
			parts.Add(name + "=" + limit.ToString(CultureInfo.InvariantCulture));
			changed = true;
		}
		return changed;
	}
}

public sealed class VersionChoice
{
	public static readonly VersionChoice Nothing = new();

	public MediaSourceInfo? From { get; init; }

	public MediaSourceInfo? To { get; init; }

	public int? Audio { get; init; }

	public int? Subtitle { get; init; }

	// Why the default version stays, when a swap was looked for but not made.
	public string? Kept { get; init; }

	public static VersionChoice Keep(string reason) => new() { Kept = reason };
}
