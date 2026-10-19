using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SeerrBridge.Configuration;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrBridge.Services;

public class SeerrClient
{
	// Seerr's answers are small; anything far bigger is not Seerr.
	private const int MaxAntwort = 4 * 1024 * 1024;

	// Its own client. No cookie jar: one user's Seerr session cookie must never travel along with the next user's
	// sign-in or with an API-key call. No redirects: the API key would follow them to wherever they point.
	private static readonly HttpClient Netz = new HttpClient(new SocketsHttpHandler
	{
		AllowAutoRedirect = false,
		UseCookies = false,
		PooledConnectionLifetime = TimeSpan.FromMinutes(5)
	})
	{
		Timeout = TimeSpan.FromSeconds(20L),
		MaxResponseContentBufferSize = MaxAntwort
	};

	private readonly ILogger<SeerrClient> _logger;

	// Jellyfin user id (32 hex digits) -> Seerr user id.
	private readonly Dictionary<string, int> _kontoZuordnung = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

	private DateTime _zuordnungStand = DateTime.MinValue;

	private static PluginConfiguration Cfg => ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration ?? new PluginConfiguration();

	public static bool Eingerichtet
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(Basis()))
			{
				return !string.IsNullOrWhiteSpace(Cfg.ApiKey);
			}
			return false;
		}
	}

	public SeerrClient(ILogger<SeerrClient> logger)
	{
		_logger = logger;
	}

	// Only plain http(s) addresses count; anything else is treated as not set.
	public static string WebAdresse(string? wert)
	{
		string text = (wert ?? string.Empty).Trim();
		if (!Uri.TryCreate(text, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo))
		{
			return string.Empty;
		}
		return text.TrimEnd('/');
	}

	private static string Basis()
	{
		PluginConfiguration cfg = Cfg;
		return WebAdresse(string.IsNullOrWhiteSpace(cfg.ServerUrl) ? cfg.PublicUrl : cfg.ServerUrl);
	}

	private static HttpClient Client()
	{
		return Netz;
	}

	private HttpRequestMessage Anfrage(HttpMethod methode, string pfad)
	{
		return new HttpRequestMessage(methode, Basis() + pfad)
		{
			Headers = 
			{
				{ "X-Api-Key", Cfg.ApiKey },
				{ "Accept", "application/json" }
			}
		};
	}

	public async Task<(bool Ok, string Text)> PruefenAsync(CancellationToken ct)
	{
		if (!Eingerichtet)
		{
			return (Ok: false, Text: "Address or API key missing");
		}
		try
		{
			HttpClient client = Client();
			using HttpRequestMessage m = Anfrage(HttpMethod.Get, "/api/v1/settings/about");
			using HttpResponseMessage antwort = await client.SendAsync(m, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!antwort.IsSuccessStatusCode)
			{
				return (Ok: false, Text: "Seerr responded with " + (int)antwort.StatusCode + ((antwort.StatusCode == HttpStatusCode.Forbidden) ? " (wrong API key?)" : string.Empty));
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(await antwort.Content.ReadAsStringAsync(ct).ConfigureAwait(continueOnCapturedContext: false));
			JsonElement value;
			string text = (jsonDocument.RootElement.TryGetProperty("version", out value) ? value.GetString() : "?");
			return (Ok: true, Text: "Connected, Seerr " + text);
		}
		catch (Exception ex)
		{
			return (Ok: false, Text: "Unreachable: " + ex.Message);
		}
	}

	public async Task<IReadOnlyList<SeerrTreffer>> SuchenAsync(string begriff, CancellationToken ct)
	{
		if (!Eingerichtet || string.IsNullOrWhiteSpace(begriff))
		{
			return Array.Empty<SeerrTreffer>();
		}
		PluginConfiguration c = Cfg;
		string pfad = "/api/v1/search?query=" + Uri.EscapeDataString(begriff) + "&page=1&language=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(c.Language) ? "en" : c.Language);
		try
		{
			HttpClient client = Client();
			
			{
				using HttpRequestMessage m = Anfrage(HttpMethod.Get, pfad);
				using HttpResponseMessage antwort = await client.SendAsync(m, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (!antwort.IsSuccessStatusCode)
				{
					LoggerExtensions.LogWarning((ILogger)(object)_logger, "Seerr search responded with {Code}", new object[1] { (int)antwort.StatusCode });
					return Array.Empty<SeerrTreffer>();
				}
				IReadOnlyList<SeerrTreffer> result;
				await using (Stream strom = await antwort.Content.ReadAsStreamAsync(ct).ConfigureAwait(continueOnCapturedContext: false))
				{
					using JsonDocument jsonDocument = await JsonDocument.ParseAsync(strom, default(JsonDocumentOptions), ct).ConfigureAwait(continueOnCapturedContext: false);
					if (!jsonDocument.RootElement.TryGetProperty("results", out var value) || value.ValueKind != JsonValueKind.Array)
					{
						result = Array.Empty<SeerrTreffer>();
					}
					else
					{
						List<SeerrTreffer> list = new List<SeerrTreffer>();
						foreach (JsonElement item in value.EnumerateArray())
						{
							string text = Text(item, "mediaType");
							if ((text != "movie" && text != "tv") || !item.TryGetProperty("id", out var value2) || value2.ValueKind != JsonValueKind.Number)
							{
								continue;
							}
							string text2 = Text(item, "title") ?? Text(item, "name") ?? string.Empty;
							if (!string.IsNullOrWhiteSpace(text2))
							{
								string text3 = Text(item, "releaseDate") ?? Text(item, "firstAirDate");
								string year = ((text3 != null && text3.Length >= 4) ? text3.Substring(0, 4) : null);
								double rating = 0.0;
								if (item.TryGetProperty("voteAverage", out var value3) && value3.ValueKind == JsonValueKind.Number)
								{
									rating = value3.GetDouble();
								}
								list.Add(new SeerrTreffer(value2.GetInt32(), text, text2, year, Text(item, "posterPath"), Text(item, "overview"), rating, Zustand(item)));
								if (list.Count >= Math.Clamp(c.SearchLimit, 1, 40) * 3)
								{
									break;
								}
							}
						}
						result = list;
					}
				}
				return result;
			}
			IL_0596:;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex2)
		{
			LoggerExtensions.LogWarning((ILogger)(object)_logger, ex2, "Seerr search failed", Array.Empty<object>());
			return Array.Empty<SeerrTreffer>();
		}
		throw null;
	}

	private static string Zustand(JsonElement e)
	{
		if (!e.TryGetProperty("mediaInfo", out var value) || value.ValueKind != JsonValueKind.Object)
		{
			return "neu";
		}
		JsonElement value2;
		return ((!value.TryGetProperty("status", out value2) || value2.ValueKind != JsonValueKind.Number) ? 1 : value2.GetInt32()) switch
		{
			5 => "vorhanden", 
			4 => "teilweise", 
			3 => "in Arbeit", 
			2 => "angefragt", 
			_ => "neu", 
		};
	}

	public async Task<(bool Ok, string Text)> AnfragenAsync(string mediaType, int tmdbId, Guid jellyfinId, CancellationToken ct)
	{
		if (!Eingerichtet)
		{
			return (Ok: false, Text: "Seerr is not configured");
		}
		if (mediaType != "movie" && mediaType != "tv")
		{
			return (Ok: false, Text: "Unknown media type");
		}
		StringBuilder rumpf = new StringBuilder();
		rumpf.Append("{\"mediaType\":\"").Append(mediaType).Append("\",\"mediaId\":")
			.Append(tmdbId.ToString(CultureInfo.InvariantCulture));
		if (mediaType == "tv")
		{
			rumpf.Append(",\"seasons\":\"all\"");
		}
		int? konto = null;
		if (Cfg.MapUsers)
		{
			// The request is made in the user's own Seerr account, with that account's permissions, quotas and approval
			// rules. Without a linked account it is refused: sent with the API key alone it would count as the
			// administrator's request and skip all of that.
			konto = jellyfinId == Guid.Empty ? null : await KontoAsync(jellyfinId, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!konto.HasValue)
			{
				return (Ok: false, Text: "Your account is not linked to Seerr yet. Sign in to Seerr once, or ask your administrator to import your account.");
			}
			rumpf.Append(",\"userId\":").Append(konto.Value.ToString(CultureInfo.InvariantCulture));
		}
		rumpf.Append('}');
		try
		{
			HttpClient client = Client();
			using HttpRequestMessage m = Anfrage(HttpMethod.Post, "/api/v1/request");
			if (konto.HasValue)
			{
				// Seerr then checks the request as that user, not as the owner of the API key.
				m.Headers.Add("X-API-User", konto.Value.ToString(CultureInfo.InvariantCulture));
			}
			m.Content = new StringContent(rumpf.ToString(), Encoding.UTF8, "application/json");
			using HttpResponseMessage antwort = await client.SendAsync(m, ct).ConfigureAwait(continueOnCapturedContext: false);
			string json = await antwort.Content.ReadAsStringAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			if (antwort.IsSuccessStatusCode)
			{
				return (Ok: true, Text: "Requested");
			}
			string text = string.Empty;
			try
			{
				using JsonDocument jsonDocument = JsonDocument.Parse(json);
				text = (jsonDocument.RootElement.TryGetProperty("message", out var value) ? (value.GetString() ?? string.Empty) : string.Empty);
			}
			catch (JsonException)
			{
			}
			return (Ok: false, Text: string.IsNullOrEmpty(text) ? ("Seerr responded with " + (int)antwort.StatusCode) : text);
		}
		catch (Exception ex2)
		{
			LoggerExtensions.LogWarning((ILogger)(object)_logger, ex2, "Request to Seerr failed", Array.Empty<object>());
			return (Ok: false, Text: "Unreachable");
		}
	}

	public async Task<(bool Ok, string? Keks, string Text)> AnmeldenAsync(string benutzer, string ausweis, CancellationToken ct)
	{
		if (!Eingerichtet)
		{
			return (Ok: false, Keks: null, Text: "Seerr is not configured");
		}
		try
		{
			HttpClient client = Client();
			using HttpRequestMessage m = new HttpRequestMessage(HttpMethod.Post, Basis() + "/api/v1/auth/jellyfin");
			m.Content = new StringContent("{\"username\":" + JsonSerializer.Serialize(benutzer) + ",\"password\":" + JsonSerializer.Serialize(ausweis) + "}", Encoding.UTF8, "application/json");
			using HttpResponseMessage antwort = await client.SendAsync(m, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!antwort.IsSuccessStatusCode)
			{
				string text = await antwort.Content.ReadAsStringAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				LoggerExtensions.LogWarning((ILogger)(object)_logger, "Sign-in to Seerr failed ({Code}): {Text}", new object[2]
				{
					(int)antwort.StatusCode,
					text
				});
				return (Ok: false, Keks: null, Text: "Seerr responded with " + (int)antwort.StatusCode);
			}
			if (!antwort.Headers.TryGetValues("Set-Cookie", out IEnumerable<string> values))
			{
				return (Ok: false, Keks: null, Text: "Seerr did not send a session cookie");
			}
			foreach (string item in values)
			{
				if (item.StartsWith("connect.sid=", StringComparison.OrdinalIgnoreCase))
				{
					return (Ok: true, Keks: item, Text: "signed in");
				}
			}
			return (Ok: false, Keks: null, Text: "No session cookie in the response");
		}
		catch (Exception ex)
		{
			LoggerExtensions.LogWarning((ILogger)(object)_logger, ex, "Sign-in to Seerr failed", Array.Empty<object>());
			return (Ok: false, Keks: null, Text: "Seerr unreachable");
		}
	}

	private async Task<int?> KontoAsync(Guid jellyfinId, CancellationToken ct)
	{
		string schluessel = jellyfinId.ToString("N");
		// Reloaded every 30 minutes, and at most once a minute when someone is missing (e.g. just imported).
		bool fehlt;
		lock (_kontoZuordnung)
		{
			fehlt = !_kontoZuordnung.ContainsKey(schluessel);
		}
		TimeSpan alter = DateTime.UtcNow - _zuordnungStand;
		if (alter > TimeSpan.FromMinutes(30L) || (fehlt && alter > TimeSpan.FromMinutes(1L)))
		{
			await ZuordnungLadenAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		lock (_kontoZuordnung)
		{
			int value;
			return _kontoZuordnung.TryGetValue(schluessel, out value) ? new int?(value) : ((int?)null);
		}
	}

	// Links Seerr accounts to Jellyfin users by the Jellyfin user id Seerr stored when it imported the account.
	// Names are never used: Seerr users can change their display name and Jellyfin users may rename themselves.
	private async Task ZuordnungLadenAsync(CancellationToken ct)
	{
		try
		{
			Dictionary<string, int> neu = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			HashSet<string> doppelt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			HttpClient client = Client();
			for (int skip = 0; skip < 10000; skip += 100)
			{
				using HttpRequestMessage m = Anfrage(HttpMethod.Get, "/api/v1/user?take=100&skip=" + skip.ToString(CultureInfo.InvariantCulture));
				using HttpResponseMessage antwort = await client.SendAsync(m, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (!antwort.IsSuccessStatusCode)
				{
					LoggerExtensions.LogWarning((ILogger)(object)_logger, "Seerr user list responded with {Code}", new object[1] { (int)antwort.StatusCode });
					return;
				}
				using JsonDocument jsonDocument = JsonDocument.Parse(await antwort.Content.ReadAsStringAsync(ct).ConfigureAwait(continueOnCapturedContext: false));
				if (!jsonDocument.RootElement.TryGetProperty("results", out var value) || value.ValueKind != JsonValueKind.Array)
				{
					return;
				}
				int anzahl = 0;
				foreach (JsonElement item in value.EnumerateArray())
				{
					anzahl++;
					string jellyfin = Text(item, "jellyfinUserId")?.Replace("-", string.Empty, StringComparison.Ordinal);
					if (!item.TryGetProperty("id", out var value2) || value2.ValueKind != JsonValueKind.Number || !Guid.TryParse(jellyfin, out var gid))
					{
						continue;
					}
					string schluessel = gid.ToString("N");
					// Two Seerr accounts claiming the same Jellyfin user: neither is used.
					if (!neu.TryAdd(schluessel, value2.GetInt32()))
					{
						doppelt.Add(schluessel);
					}
				}
				if (anzahl < 100)
				{
					break;
				}
			}
			foreach (string d in doppelt)
			{
				neu.Remove(d);
				LoggerExtensions.LogWarning((ILogger)(object)_logger, "Two Seerr accounts are linked to Jellyfin user {Id}; requests from this user are refused until one is removed", new object[1] { d });
			}
			if (neu.Count == 0)
			{
				LoggerExtensions.LogWarning((ILogger)(object)_logger, "No Seerr account is linked to a Jellyfin user, so requests from Jellyfin are refused. Import the Jellyfin users in Seerr, or turn off user mapping to request everything as the API key's owner.", Array.Empty<object>());
			}
			lock (_kontoZuordnung)
			{
				_kontoZuordnung.Clear();
				foreach (KeyValuePair<string, int> e in neu)
				{
					_kontoZuordnung[e.Key] = e.Value;
				}
			}
			_zuordnungStand = DateTime.UtcNow;
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			LoggerExtensions.LogWarning((ILogger)(object)_logger, ex, "Could not load user mapping from Seerr", Array.Empty<object>());
		}
	}

	private static string? Text(JsonElement e, string name)
	{
		if (!e.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
		{
			return null;
		}
		return value.GetString();
	}
}
