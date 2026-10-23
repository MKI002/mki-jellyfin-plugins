using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Security;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.MkiGuestPass.Services;

// Sits in front of a few of Jellyfin's own endpoints that every signed-in user may call, and narrows them for guests:
// - the user list shows a guest only their own account, and other accounts cannot be looked up one by one;
// - library scans, metadata and image searches on the internet, client log uploads, profile pictures, playlists and
//   file downloads, which any user may use, are refused;
// - MKI Seerr Bridge is not available (a Seerr account would outlive the guest);
// - Quick Connect signs in a TV only while the guest has a device left on their pass;
// - a video, song or subtitle is played to a guest only from the libraries and up to the rating of their pass, and
//   only while it lasts (Jellyfin checks neither on its playback routes);
// - with "Streams need a sign-in" on (the default), the stream and subtitle routes Jellyfin serves without any sign-in
//   need a valid one, so a guest whose access ended cannot keep playing titles they saw;
// - the server information and settings any user may read are cut down to what apps use (no folder paths, addresses
//   or passwords of the server);
// - behind a reverse proxy Jellyfin does not know, every visitor looks like one at home, so a guest of a pass for the
//   home network only is refused there.
// Without guests it only compares the path of a request and passes it on (streams aside). When the check itself
// fails while guests exist, a request it narrows is refused rather than let through unchecked; any other request
// is never touched. The rights a guest has come from the account's own settings.
public class GuestGate : IStartupFilter
{
	internal const int MaxTokens = 8;

	// What the gate saw of a join request's connection, before Jellyfin applied a known proxy's headers.
	public const string OriginKey = "MkiGuestPass.Origin";

	// Any of these says a proxy passed the request on: home devices send none of them.
	private static readonly string[] ForwardingHeaders = { "X-Forwarded-For", "Forwarded", "X-Real-IP", "X-Forwarded-Proto", "X-Forwarded-Host" };

	private static readonly Regex Logout = new(@"/Sessions/Logout/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex UserList = new(@"/Users/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex UserById = new(@"/Users/(?<id>[^/]+)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex LibraryScan = new(@"/Library/(Media/Updated|Movies/(Added|Updated)|Series/(Added|Updated))/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex Seerr = new(@"/SeerrBridge(/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex QuickConnect = new(@"/QuickConnect/Authorize/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex SystemInfo = new(@"/System/Info/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex Settings = new(@"/System/Configuration(/(?<key>[^/]+))?/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex ItemFile = new(@"/Items/[^/]+/(File|RemoteImages(/Providers)?)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	// Jellyfin's playback routes without [Authorize]: the file stream and the subtitle file.
	private static readonly Regex Stream = new(@"/(Videos|Audio)/(?<id>[^/]+)/(stream(\.[^/]*)?|[^/]+/Subtitles/[^/]+(/[^/]+)?/Stream\.[^/]+)/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	// Every playback route keyed by an item: streams, HLS playlists and segments, subtitles, attachments, trickplay.
	private static readonly Regex Media = new(@"/(Videos|Audio)/(?<id>[^/]+)(/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex SyncPlayList = new(@"/SyncPlay/List/?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly Regex Changes = new(@"/(Items/RemoteSearch|ClientLog|UserImage|Playlists)(/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static readonly string[] SchemeHeaders = { "Authorization", "X-Emby-Authorization" };

	private static readonly string[] TokenHeaders = { "X-Emby-Token", "X-MediaBrowser-Token" };

	private static readonly string[] TokenParameters = { "ApiKey", "api_key" };

	private readonly GuestService _guests;

	private readonly IUserManager _users;

	private readonly IDeviceManager _devices;

	private readonly IServerConfigurationManager _config;

	private readonly ISystemManager _system;

	private readonly ILibraryManager _library;

	private readonly IAuthenticationManager _apiKeys;

	private readonly INetworkManager _network;

	private readonly ForwardedHeadersOptions _forwarding;

	// Jellyfin's own X-Forwarded-For handling, run on a copy of the request to learn the address Jellyfin will use.
	private readonly ForwardedHeadersMiddleware _forwarder;

	private readonly ILogger<GuestGate> _logger;

	public GuestGate(GuestService guests, IUserManager users, IDeviceManager devices, IServerConfigurationManager config, ISystemManager system, ILibraryManager library, IAuthenticationManager apiKeys, INetworkManager network, IOptions<ForwardedHeadersOptions> forwarding, ILogger<GuestGate> logger)
	{
		_network = network;
		_forwarding = forwarding.Value;
		_forwarder = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, forwarding);
		_guests = guests;
		_users = users;
		_devices = devices;
		_config = config;
		_system = system;
		_library = library;
		_apiKeys = apiKeys;
		_logger = logger;
	}

	// ViaUnknownProxy: a home address passed the request on for someone else, and Jellyfin will not take the visitor's
	// address from it, or a known proxy passed it on without saying for whom or with headers that disagree. Visitor:
	// the address that proxy named, if any. Address: the address Jellyfin will take the request for (null: none).
	// Quiet: not worth the warning on the settings page by itself (a known proxy's own requests, like health checks).
	public sealed record Seen(bool ViaUnknownProxy, IPAddress? Visitor, IPAddress? Address = null, bool Quiet = false, IPAddress? AlsoVisitor = null);

	private enum Kind
	{
		None,
		UserList,
		UserById,
		Refused,
		QuickConnect,
		SystemInfo,
		Settings,
		EmptyList,
		Media
	}

	public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
	{
		return app =>
		{
			app.Use(async (context, go) =>
			{
				bool done;
				try
				{
					var seen = Look(context);
					bool ours = (context.Request.Path.Value ?? string.Empty).Contains("/MkiGuestPass/", StringComparison.OrdinalIgnoreCase);
					if (seen.ViaUnknownProxy && (ours || !seen.Quiet))
					{
						_guests.NoteUnknownProxy();
					}
					if (ours)
					{
						context.Items[OriginKey] = seen;
					}
					bool away = Away(seen);
					done = (away && await HomeOnlyGuestAway(context).ConfigureAwait(false))
						|| await Handle(context, seen).ConfigureAwait(false);
				}
				catch (Exception ex) when (!context.Response.HasStarted)
				{
					// A request the gate narrows is not let through unchecked while it may be a guest's or need a sign-in.
					done = RefuseUnchecked(context);
					_logger.LogWarning(ex, done ? "MKI Guest Pass could not check a request; it is refused" : "MKI Guest Pass request check skipped");
					if (done)
					{
						context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
					}
				}
				if (!done)
				{
					await go().ConfigureAwait(false);
				}
			});
			next(app);
		};
	}

	private async Task<bool> Handle(HttpContext context, Seen seen)
	{
		var request = context.Request;
		string path = request.Path.Value ?? string.Empty;
		Match? byId = null;
		Match? settings = null;
		Match? stream = null;
		Match? media = null;
		Guid item = Guid.Empty;
		Kind kind = Kind.None;
		bool read = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
		if (read && (stream = Stream.Match(path)).Success && Guid.TryParse(stream.Groups["id"].Value, out item))
		{
			return await Streaming(context, item, seen).ConfigureAwait(false);
		}
		if ((media = Media.Match(path)).Success && !string.Equals(media.Groups["id"].Value, "ActiveEncodings", StringComparison.OrdinalIgnoreCase))
		{
			// An id Jellyfin might read some other way than a Guid is nothing a guest may play.
			kind = Kind.Media;
			item = Guid.TryParse(media.Groups["id"].Value, out var parsed) ? parsed : Guid.Empty;
		}
		else if (read)
		{
			if (UserList.IsMatch(path))
			{
				kind = Kind.UserList;
			}
			else if ((byId = UserById.Match(path)).Success && Guid.TryParse(byId.Groups["id"].Value, out _))
			{
				kind = Kind.UserById;
			}
			else if (SystemInfo.IsMatch(path))
			{
				kind = Kind.SystemInfo;
			}
			else if ((settings = Settings.Match(path)).Success)
			{
				kind = Kind.Settings;
			}
			else if (ItemFile.IsMatch(path))
			{
				kind = Kind.Refused;
			}
			else if (SyncPlayList.IsMatch(path))
			{
				kind = Kind.EmptyList;
			}
		}
		else if (Changes.IsMatch(path))
		{
			kind = Kind.Refused;
		}
		if (kind == Kind.None && Seerr.IsMatch(path))
		{
			kind = Kind.Refused;
		}
		if (kind == Kind.None && HttpMethods.IsPost(request.Method))
		{
			if (LibraryScan.IsMatch(path))
			{
				kind = Kind.Refused;
			}
			else if (QuickConnect.IsMatch(path))
			{
				kind = Kind.QuickConnect;
			}
		}
		if (kind == Kind.None || !_guests.AnyGuests())
		{
			return false;
		}

		var tokens = Tokens(request, MaxTokens);
		if (tokens.Count > MaxTokens)
		{
			// No real client sends this many; a pile of junk must not hide a guest's token.
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			return true;
		}
		var guest = GuestCaller(tokens);
		if (guest == null)
		{
			return false;
		}
		var id = guest.Value;
		context.Response.Headers.CacheControl = "private, no-store";
		switch (kind)
		{
			case Kind.UserList:
			{
				var user = _users.GetUserById(id);
				if (user == null)
				{
					context.Response.StatusCode = StatusCodes.Status401Unauthorized;
					return true;
				}
				var dto = _users.GetUserDto(user, context.GetNormalizedRemoteIP().ToString());
				await Json(context, new[] { dto }).ConfigureAwait(false);
				return true;
			}

			case Kind.UserById:
				if (Guid.Parse(byId!.Groups["id"].Value) == id)
				{
					return false;
				}
				context.Response.StatusCode = StatusCodes.Status404NotFound;
				return true;

			case Kind.Refused:
				await Refuse(context, "Guest accounts cannot do this.").ConfigureAwait(false);
				return true;

			case Kind.EmptyList:
				// SyncPlay is off for guests; the web client asks anyway and would log a refusal on every page.
				await Json(context, Array.Empty<object>()).ConfigureAwait(false);
				return true;

			case Kind.Media:
				return await GuestMedia(context, id, item, seen).ConfigureAwait(false);

			case Kind.QuickConnect:
			{
				var found = _guests.Find(id);
				if (found == null || found.Value.Guest.State != GuestState.Active || found.Value.Pass.HasEnded(DateTime.UtcNow))
				{
					await Refuse(context, "Your guest access has ended.").ConfigureAwait(false);
					return true;
				}
				int max = found.Value.Pass.DevicesPerGuest;
				if (_guests.DeviceCount(id) >= max)
				{
					await Refuse(context, max == 1
						? "Your guest pass allows one device, and you are using it. Sign out of Jellyfin here first, or ask the person who invited you."
						: "Your guest pass allows " + max + " devices, and they are all signed in. Sign out of Jellyfin on one of them first (in its menu or settings), or ask the person who invited you.").ConfigureAwait(false);
					return true;
				}
				return false;
			}

			case Kind.SystemInfo:
			{
				// The full information lists the server's folders (logs, cache, data), which can hold the name of the
				// account it runs under. A guest gets what the public variant shows, plus what apps use for users.
				var real = _system.GetSystemInfo(request);
				await Json(context, new SystemInfo
				{
					LocalAddress = real.LocalAddress,
					ServerName = real.ServerName,
					Version = real.Version,
					ProductName = real.ProductName,
					Id = real.Id,
					StartupWizardCompleted = real.StartupWizardCompleted,
					HasPendingRestart = real.HasPendingRestart,
					IsShuttingDown = real.IsShuttingDown,
					WebSocketPortNumber = real.WebSocketPortNumber,
					CastReceiverApplications = real.CastReceiverApplications
				}).ConfigureAwait(false);
				return true;
			}

			case Kind.Settings:
			{
				// Any user may read every server setting, and some hold folder paths, network addresses or the
				// passwords of TV guide services. A guest gets Jellyfin's defaults with the few values apps use for
				// users, and of the named settings only whether the video player may use the fallback font.
				var key = settings!.Groups["key"];
				object answer;
				if (!key.Success)
				{
					answer = GuestSettings();
				}
				else if (string.Equals(key.Value, "encoding", StringComparison.OrdinalIgnoreCase))
				{
					answer = new Dictionary<string, object>
					{
						["EnableFallbackFont"] = (_config.GetConfiguration("encoding") as EncodingOptions)?.EnableFallbackFont ?? false
					};
				}
				else
				{
					await Refuse(context, "Guest accounts cannot do this.").ConfigureAwait(false);
					return true;
				}
				await Json(context, answer).ConfigureAwait(false);
				return true;
			}

			default:
				return false;
		}
	}

	public static bool HasForwarding(HttpRequest request) => ForwardingHeaders.Any(h => request.Headers.ContainsKey(h));

	// Jellyfin applies a proxy's X-Forwarded-For only for a proxy listed under Networking -> Known proxies (these are
	// the options it uses). A home address that names someone else in any other way is a proxy Jellyfin does not know.
	private Seen Look(HttpContext context)
	{
		var request = context.Request;
		bool forwarding = HasForwarding(request);
		var raw = context.Connection.RemoteIpAddress;
		if (raw == null)
		{
			// No address (a Unix socket): Jellyfin takes it for one at home, so a forwarded one cannot be told apart.
			return new Seen(forwarding, forwarding ? Named(request) : null);
		}
		if (raw.IsIPv4MappedToIPv6)
		{
			raw = raw.MapToIPv4();
		}
		bool on = _forwarding.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor);
		bool proxy = on && _forwarding.KnownProxies.Any(p => (p.IsIPv4MappedToIPv6 ? p.MapToIPv4() : p).Equals(raw));
		bool known = proxy || (on && _forwarding.KnownIPNetworks.Any(n => n.Contains(raw)));
		// A known network (Known proxies as a range) often holds ordinary home devices too: without forwarding headers
		// those are what they seem. A proxy listed by its own address passes on visitors, so it must say whom.
		if (known && (forwarding || proxy))
		{
			// Trusted only when Jellyfin really takes a visitor's address from X-Forwarded-For, and an X-Real-IP the proxy
			// sets says the same. Otherwise the proxy passed on what the visitor sent (or nothing), and the visitor could
			// pass for someone at home.
			var applied = Applied(context, raw);
			var said = request.Headers["X-Real-IP"];
			var real = RealIp(request);
			if (applied != null && (said.Count == 0 || (said.Count == 1 && real != null && real.Equals(applied))))
			{
				return new Seen(false, null, applied);
			}
			// The join limits hold for both addresses: one of them may be the visitor's own choice.
			return new Seen(true, real ?? applied ?? Named(request), raw, Quiet: !forwarding, AlsoVisitor: real != null ? applied : null);
		}
		if (!forwarding || !_network.IsInLocalNetwork(raw))
		{
			return new Seen(false, null, raw);
		}
		return new Seen(true, Named(request), raw);
	}

	// Away from the home network as Jellyfin will see it, or through a proxy that cannot be trusted. No address at all
	// (a Unix socket) means some relay passes visitors on, so that counts as away too.
	private bool Away(Seen seen) => seen.ViaUnknownProxy || seen.Address == null || !_network.IsInLocalNetwork(seen.Address);

	// The visitor's address Jellyfin takes from the X-Forwarded-For of a known proxy, or null when it takes none. A
	// changed scheme or host alone also leaves X-Original-For, and a proxy that names itself, loopback or another
	// proxy names no visitor: null as well.
	private IPAddress? Applied(HttpContext context, IPAddress raw)
	{
		var copy = new DefaultHttpContext();
		copy.Connection.RemoteIpAddress = raw;
		copy.Connection.RemotePort = context.Connection.RemotePort;
		copy.Request.Scheme = context.Request.Scheme;
		copy.Request.Host = context.Request.Host;
		foreach (var h in context.Request.Headers)
		{
			if (h.Key.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase))
			{
				copy.Request.Headers[h.Key] = h.Value;
			}
		}
		_forwarder.ApplyForwarders(copy);
		var ip = copy.Connection.RemoteIpAddress;
		// ASP.NET cuts the entries it used off X-Forwarded-For and writes the rest back one value each (unquoted); a
		// header it left alone stays as it was.
		string forFrom = _forwarding.ForwardedForHeaderName;
		var before = context.Request.Headers[forFrom];
		var after = copy.Request.Headers[forFrom];
		bool used = !StringValues.Equals(before, after) && context.Request.Headers.GetCommaSeparatedValues(forFrom).Length > after.Count;
		if (ip == null || !used || !copy.Request.Headers.ContainsKey(_forwarding.OriginalForHeaderName))
		{
			return null;
		}
		ip = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
		bool proxyItself = ip.Equals(raw) || IPAddress.IsLoopback(ip) || _forwarding.KnownProxies.Any(p => (p.IsIPv4MappedToIPv6 ? p.MapToIPv4() : p).Equals(ip));
		return proxyItself ? null : ip;
	}

	// X-Real-IP as a single address; null when missing, unreadable or given more than once.
	private static IPAddress? RealIp(HttpRequest request)
	{
		var said = request.Headers["X-Real-IP"];
		string value = said.Count == 1 ? (said[0] ?? string.Empty).Trim() : string.Empty;
		if (value.Length == 0 || !(IPAddress.TryParse(value, out var ip) || (IPEndPoint.TryParse(value, out var ep) && (ip = ep.Address) != null)))
		{
			return null;
		}
		return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
	}

	// The visitor's address as the proxy named it: the last X-Forwarded-For entry (the one the proxy added), else
	// X-Real-IP. Used only to keep visitors apart for the join limits, never to decide who is at home.
	private static IPAddress? Named(HttpRequest request)
	{
		string? value = request.Headers["X-Forwarded-For"].ToString().Split(',').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0);
		if (string.IsNullOrEmpty(value))
		{
			value = request.Headers["X-Real-IP"].ToString().Trim();
		}
		if (IPAddress.TryParse(value, out var ip) || (IPEndPoint.TryParse(value, out var ep) && (ip = ep.Address) != null))
		{
			return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
		}
		return null;
	}

	// A guest of a pass for the home network only, away from it: refused on every request. Jellyfin checks this only
	// where it sees the visitor (not behind a proxy it does not know) and only where a sign-in is needed (not on
	// stream addresses). Signing out is the exception: it works from anywhere, so no sign-in is left behind.
	private async Task<bool> HomeOnlyGuestAway(HttpContext context)
	{
		if (!_guests.AnyHomeOnlyGuests())
		{
			return false;
		}
		var tokens = Tokens(context.Request, MaxTokens);
		if (tokens.Count > MaxTokens)
		{
			// As in Handle: a pile of junk must not hide a guest's token.
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			return true;
		}
		var guest = GuestCaller(tokens);
		var found = guest == null ? null : _guests.Find(guest.Value);
		if (found == null || found.Value.Pass.RemoteAccess)
		{
			return false;
		}
		context.Response.Headers.CacheControl = "private, no-store";
		if (HttpMethods.IsPost(context.Request.Method) && Logout.IsMatch(context.Request.Path.Value ?? string.Empty))
		{
			foreach (var token in tokens)
			{
				var device = _devices.GetDevices(new DeviceQuery { AccessToken = token, Limit = 1 }).Items.FirstOrDefault();
				if (device != null && device.UserId == guest!.Value)
				{
					await _guests.SignOutDevice(device).ConfigureAwait(false);
				}
			}
			context.Response.StatusCode = StatusCodes.Status204NoContent;
			return true;
		}
		await Refuse(context, "This guest pass works only on the home network.").ConfigureAwait(false);
		return true;
	}

	// Jellyfin streams files and subtitles without a sign-in (no [Authorize] on those routes). A guest gets only what
	// their pass shows; with StreamsNeedSignIn nobody gets anything without a valid sign-in or API key.
	private async Task<bool> Streaming(HttpContext context, Guid itemId, Seen seen)
	{
		bool needSignIn = Plugin.Config.StreamsNeedSignIn;
		if (!needSignIn && !_guests.AnyGuests())
		{
			return false;
		}
		var tokens = Tokens(context.Request, MaxTokens);
		if (tokens.Count > MaxTokens)
		{
			context.Response.StatusCode = StatusCodes.Status400BadRequest;
			return true;
		}
		var guest = GuestCaller(tokens);
		if (guest != null)
		{
			return await GuestMedia(context, guest.Value, itemId, seen).ConfigureAwait(false);
		}
		if (!needSignIn || await SignedIn(tokens).ConfigureAwait(false))
		{
			return false;
		}
		context.Response.StatusCode = StatusCodes.Status401Unauthorized;
		context.Response.Headers.CacheControl = "no-store";
		return true;
	}

	// A guest plays only while their pass lasts, only what their account may see, and with a pass for the home network
	// only, only at home: Jellyfin does not check where a stream comes from, as its stream addresses need no sign-in.
	private async Task<bool> GuestMedia(HttpContext context, Guid guestId, Guid itemId, Seen seen)
	{
		context.Response.Headers.CacheControl = "private, no-store";
		var found = _guests.Find(guestId);
		var user = _users.GetUserById(guestId);
		if (found == null || user == null || found.Value.Guest.State != GuestState.Active || found.Value.Pass.HasEnded(DateTime.UtcNow)
			|| user.HasPermission(PermissionKind.IsDisabled))
		{
			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			return true;
		}
		if (!found.Value.Pass.RemoteAccess && Away(seen))
		{
			await Refuse(context, "This guest pass works only on the home network.").ConfigureAwait(false);
			return true;
		}
		var item = itemId == Guid.Empty ? null : _library.GetItemById(itemId);
		if (item == null || !item.IsVisibleStandalone(user))
		{
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			return true;
		}
		return false;
	}

	// Whether any token of the request is a sign-in of an enabled account or an API key.
	private async Task<bool> SignedIn(List<string> tokens)
	{
		if (tokens.Count == 0)
		{
			return false;
		}
		foreach (var token in tokens)
		{
			var device = _devices.GetDevices(new DeviceQuery { AccessToken = token, Limit = 1 }).Items.FirstOrDefault();
			var user = device == null || device.UserId == Guid.Empty ? null : _users.GetUserById(device.UserId);
			if (user != null && !user.HasPermission(PermissionKind.IsDisabled))
			{
				return true;
			}
		}
		var keys = await _apiKeys.GetApiKeys().ConfigureAwait(false);
		return keys.Any(k => !string.IsNullOrEmpty(k.AccessToken) && tokens.Contains(k.AccessToken, StringComparer.Ordinal));
	}

	// Whether a request whose check failed must be refused: one that needs a sign-in, or one the gate narrows while
	// guests exist. Any other request is passed on, so a fault here never breaks Jellyfin for everyone.
	private bool RefuseUnchecked(HttpContext context)
	{
		var request = context.Request;
		string path = request.Path.Value ?? string.Empty;
		bool read = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
		bool viaUnknownProxy;
		try
		{
			viaUnknownProxy = Look(context).ViaUnknownProxy;
		}
		catch (Exception)
		{
			viaUnknownProxy = HasForwarding(request);
		}
		bool narrowed = viaUnknownProxy || Seerr.IsMatch(path) || Media.IsMatch(path)
			|| (read && (UserList.IsMatch(path) || UserById.IsMatch(path) || SystemInfo.IsMatch(path) || Settings.IsMatch(path) || ItemFile.IsMatch(path) || SyncPlayList.IsMatch(path)))
			|| (!read && Changes.IsMatch(path))
			|| (HttpMethods.IsPost(request.Method) && (LibraryScan.IsMatch(path) || QuickConnect.IsMatch(path)));
		if (!narrowed)
		{
			return false;
		}
		try
		{
			return (read && Stream.IsMatch(path) && Plugin.Config.StreamsNeedSignIn) || _guests.AnyGuests();
		}
		catch (Exception)
		{
			return true;
		}
	}

	private ServerConfiguration GuestSettings()
	{
		var real = _config.Configuration;
		return new ServerConfiguration
		{
			UICulture = real.UICulture,
			PreferredMetadataLanguage = real.PreferredMetadataLanguage,
			MetadataCountryCode = real.MetadataCountryCode,
			MinResumePct = real.MinResumePct,
			MaxResumePct = real.MaxResumePct,
			MinResumeDurationSeconds = real.MinResumeDurationSeconds,
			MinAudiobookResume = real.MinAudiobookResume,
			MaxAudiobookResume = real.MaxAudiobookResume,
			EnableFolderView = real.EnableFolderView,
			EnableGroupingMoviesIntoCollections = real.EnableGroupingMoviesIntoCollections,
			EnableGroupingShowsIntoCollections = real.EnableGroupingShowsIntoCollections,
			DisplaySpecialsWithinSeasons = real.DisplaySpecialsWithinSeasons,
			QuickConnectAvailable = real.QuickConnectAvailable,
			IsStartupWizardCompleted = real.IsStartupWizardCompleted,
			CastReceiverApplications = real.CastReceiverApplications
		};
	}

	private static async Task Json(HttpContext context, object value)
	{
		context.Response.StatusCode = StatusCodes.Status200OK;
		context.Response.ContentType = "application/json; charset=utf-8";
		await context.Response.WriteAsync(JsonSerializer.Serialize(value, value.GetType(), JsonDefaults.Options)).ConfigureAwait(false);
	}

	private static async Task Refuse(HttpContext context, string text)
	{
		context.Response.StatusCode = StatusCodes.Status403Forbidden;
		context.Response.ContentType = "text/plain; charset=utf-8";
		await context.Response.WriteAsync(text).ConfigureAwait(false);
	}

	// The guest behind any token of the request. Tokens of several users: the guest restrictions win.
	private Guid? GuestCaller(List<string> tokens)
	{
		string? creating = GuestService.CreatingNow();
		foreach (var token in tokens)
		{
			var device = _devices.GetDevices(new DeviceQuery { AccessToken = token, Limit = 1 }).Items.FirstOrDefault();
			if (device == null || device.UserId == Guid.Empty)
			{
				continue;
			}
			if (_guests.Find(device.UserId) != null)
			{
				return device.UserId;
			}
			var user = _users.GetUserById(device.UserId);
			if (user != null && (GuestService.UsesGuestSignIn(user) || string.Equals(user.Username, creating, StringComparison.OrdinalIgnoreCase)))
			{
				return device.UserId;
			}
		}
		return null;
	}

	// Every place a token can come from, the way MKI Password Policy reads them: Jellyfin reads some only with legacy
	// sign-in on, and another plugin may copy one into another after this check has run.
	internal static List<string> Tokens(HttpRequest request, int limit)
	{
		var tokens = new List<string>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		void Add(string? token)
		{
			if (tokens.Count > limit)
			{
				return;
			}
			if (!string.IsNullOrWhiteSpace(token) && seen.Add(token))
			{
				tokens.Add(token);
			}
			string t = token?.Trim().Trim('"') ?? string.Empty;
			if (t.Length > 0 && seen.Add(t))
			{
				tokens.Add(t);
			}
		}
		// Each value on its own and, as Jellyfin may read them, all values joined.
		foreach (var name in SchemeHeaders)
		{
			var values = request.Headers[name];
			foreach (var value in values.Append(values.ToString()))
			{
				foreach (var token in FromScheme(value))
				{
					Add(token);
				}
			}
		}
		foreach (var name in TokenHeaders)
		{
			var values = request.Headers[name];
			foreach (var value in values.Append(values.ToString()))
			{
				Add(value);
			}
		}
		foreach (var name in TokenParameters)
		{
			var values = request.Query[name];
			foreach (var value in values.Append(values.ToString()))
			{
				Add(value);
			}
		}
		return tokens;
	}

	// Every Token value of a "MediaBrowser Client=..., Token=..." header, split like Jellyfin splits it.
	private static IEnumerable<string> FromScheme(string? header)
	{
		int space = header?.IndexOf(' ') ?? -1;
		if (header == null || space < 0)
		{
			return Array.Empty<string>();
		}
		var tokens = new List<string>();
		string rest = header[(space + 1)..];
		bool quoted = false;
		int start = 0;
		string key = string.Empty;
		int i;
		void Part(int end)
		{
			if (string.Equals(key.Trim(), "Token", StringComparison.OrdinalIgnoreCase))
			{
				tokens.Add(WebUtility.UrlDecode(rest[start..end].Trim('"')));
			}
			key = string.Empty;
		}
		for (i = 0; i < rest.Length; i++)
		{
			char c = rest[i];
			if (c == '"')
			{
				quoted = !quoted;
			}
			else if (c == ',' && !quoted)
			{
				if (start < i)
				{
					Part(i);
				}
				start = i + 1;
			}
			else if (c == '=' && !quoted)
			{
				key = rest[start..i];
				start = i + 1;
			}
		}
		if (start < i)
		{
			Part(i);
		}
		return tokens;
	}
}
