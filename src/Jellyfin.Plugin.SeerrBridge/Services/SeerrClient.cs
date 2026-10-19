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
	private readonly IHttpClientFactory _httpClientFactory;

	private readonly ILogger<SeerrClient> _logger;

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

	public SeerrClient(IHttpClientFactory httpClientFactory, ILogger<SeerrClient> logger)
	{
		_httpClientFactory = httpClientFactory;
		_logger = logger;
	}

	private static string Basis()
	{
		PluginConfiguration cfg = Cfg;
		return ((string.IsNullOrWhiteSpace(cfg.ServerUrl) ? cfg.PublicUrl : cfg.ServerUrl) ?? string.Empty).TrimEnd('/');
	}

	private HttpClient Client()
	{
		HttpClient httpClient = HttpClientFactoryExtensions.CreateClient(_httpClientFactory);
		httpClient.Timeout = TimeSpan.FromSeconds(20L);
		return httpClient;
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
			return (Ok: false, Text: "Adresse oder API-Schluessel fehlt");
		}
		try
		{
			using HttpClient client = Client();
			using HttpRequestMessage m = Anfrage(HttpMethod.Get, "/api/v1/settings/about");
			using HttpResponseMessage antwort = await client.SendAsync(m, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!antwort.IsSuccessStatusCode)
			{
				return (Ok: false, Text: "Seerr antwortete mit " + (int)antwort.StatusCode + ((antwort.StatusCode == HttpStatusCode.Forbidden) ? " (API-Schluessel falsch?)" : string.Empty));
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(await antwort.Content.ReadAsStringAsync(ct).ConfigureAwait(continueOnCapturedContext: false));
			JsonElement value;
			string text = (jsonDocument.RootElement.TryGetProperty("version", out value) ? value.GetString() : "?");
			return (Ok: true, Text: "Verbunden, Seerr " + text);
		}
		catch (Exception ex)
		{
			return (Ok: false, Text: "Nicht erreichbar: " + ex.Message);
		}
	}

	public async Task<IReadOnlyList<SeerrTreffer>> SuchenAsync(string begriff, CancellationToken ct)
	{
		if (!Eingerichtet || string.IsNullOrWhiteSpace(begriff))
		{
			return Array.Empty<SeerrTreffer>();
		}
		PluginConfiguration c = Cfg;
		string pfad = "/api/v1/search?query=" + Uri.EscapeDataString(begriff) + "&page=1&language=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(c.Language) ? "de" : c.Language);
		try
		{
			using (HttpClient client = Client())
			{
				using HttpRequestMessage m = Anfrage(HttpMethod.Get, pfad);
				using HttpResponseMessage antwort = await client.SendAsync(m, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (!antwort.IsSuccessStatusCode)
				{
					LoggerExtensions.LogWarning((ILogger)(object)_logger, "Seerr-Suche antwortete mit {Code}", new object[1] { (int)antwort.StatusCode });
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
			LoggerExtensions.LogWarning((ILogger)(object)_logger, ex2, "Seerr-Suche fehlgeschlagen", Array.Empty<object>());
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

	public async Task<(bool Ok, string Text)> AnfragenAsync(string mediaType, int tmdbId, string? jellyfinBenutzer, CancellationToken ct)
	{
		if (!Eingerichtet)
		{
			return (Ok: false, Text: "Seerr ist nicht eingerichtet");
		}
		if (mediaType != "movie" && mediaType != "tv")
		{
			return (Ok: false, Text: "Unbekannte Medienart");
		}
		StringBuilder rumpf = new StringBuilder();
		rumpf.Append("{\"mediaType\":\"").Append(mediaType).Append("\",\"mediaId\":")
			.Append(tmdbId.ToString(CultureInfo.InvariantCulture));
		if (mediaType == "tv")
		{
			rumpf.Append(",\"seasons\":\"all\"");
		}
		if (Cfg.MapUsers && !string.IsNullOrWhiteSpace(jellyfinBenutzer))
		{
			int? num = await KontoAsync(jellyfinBenutzer, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (num.HasValue)
			{
				rumpf.Append(",\"userId\":").Append(num.Value.ToString(CultureInfo.InvariantCulture));
			}
		}
		rumpf.Append('}');
		try
		{
			using HttpClient client = Client();
			using HttpRequestMessage m = Anfrage(HttpMethod.Post, "/api/v1/request");
			m.Content = new StringContent(rumpf.ToString(), Encoding.UTF8, "application/json");
			using HttpResponseMessage antwort = await client.SendAsync(m, ct).ConfigureAwait(continueOnCapturedContext: false);
			string json = await antwort.Content.ReadAsStringAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			if (antwort.IsSuccessStatusCode)
			{
				return (Ok: true, Text: "Angefragt");
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
			return (Ok: false, Text: string.IsNullOrEmpty(text) ? ("Seerr antwortete mit " + (int)antwort.StatusCode) : text);
		}
		catch (Exception ex2)
		{
			LoggerExtensions.LogWarning((ILogger)(object)_logger, ex2, "Anfrage an Seerr fehlgeschlagen", Array.Empty<object>());
			return (Ok: false, Text: "Nicht erreichbar");
		}
	}

	public async Task<(bool Ok, string? Keks, string Text)> AnmeldenAsync(string benutzer, string ausweis, CancellationToken ct)
	{
		if (!Eingerichtet)
		{
			return (Ok: false, Keks: null, Text: "Seerr ist nicht eingerichtet");
		}
		try
		{
			using HttpClient client = Client();
			using HttpRequestMessage m = new HttpRequestMessage(HttpMethod.Post, Basis() + "/api/v1/auth/jellyfin");
			m.Content = new StringContent("{\"username\":" + JsonSerializer.Serialize(benutzer) + ",\"password\":" + JsonSerializer.Serialize(ausweis) + "}", Encoding.UTF8, "application/json");
			using HttpResponseMessage antwort = await client.SendAsync(m, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!antwort.IsSuccessStatusCode)
			{
				string text = await antwort.Content.ReadAsStringAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				LoggerExtensions.LogWarning((ILogger)(object)_logger, "Anmeldung bei Seerr fehlgeschlagen ({Code}): {Text}", new object[2]
				{
					(int)antwort.StatusCode,
					text
				});
				return (Ok: false, Keks: null, Text: "Seerr antwortete mit " + (int)antwort.StatusCode);
			}
			if (!antwort.Headers.TryGetValues("Set-Cookie", out IEnumerable<string> values))
			{
				return (Ok: false, Keks: null, Text: "Seerr hat keinen Sitzungskeks geschickt");
			}
			foreach (string item in values)
			{
				if (item.StartsWith("connect.sid=", StringComparison.OrdinalIgnoreCase))
				{
					return (Ok: true, Keks: item, Text: "angemeldet");
				}
			}
			return (Ok: false, Keks: null, Text: "Kein Sitzungskeks in der Antwort");
		}
		catch (Exception ex)
		{
			LoggerExtensions.LogWarning((ILogger)(object)_logger, ex, "Anmeldung bei Seerr fehlgeschlagen", Array.Empty<object>());
			return (Ok: false, Keks: null, Text: "Seerr nicht erreichbar");
		}
	}

	private async Task<int?> KontoAsync(string benutzer, CancellationToken ct)
	{
		if (DateTime.UtcNow - _zuordnungStand > TimeSpan.FromMinutes(30L))
		{
			await ZuordnungLadenAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		lock (_kontoZuordnung)
		{
			int value;
			return _kontoZuordnung.TryGetValue(benutzer, out value) ? new int?(value) : ((int?)null);
		}
	}

	private async Task ZuordnungLadenAsync(CancellationToken ct)
	{
		_ = 3;
		try
		{
			using HttpClient client = Client();
			using HttpRequestMessage m = Anfrage(HttpMethod.Get, "/api/v1/user?take=200");
			using HttpResponseMessage antwort = await client.SendAsync(m, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!antwort.IsSuccessStatusCode)
			{
				return;
			}
			await using Stream strom = await antwort.Content.ReadAsStreamAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
			using (JsonDocument jsonDocument = await JsonDocument.ParseAsync(strom, default(JsonDocumentOptions), ct).ConfigureAwait(continueOnCapturedContext: false))
			{
				if (!jsonDocument.RootElement.TryGetProperty("results", out var value) || value.ValueKind != JsonValueKind.Array)
				{
					goto end_IL_020c;
				}
				lock (_kontoZuordnung)
				{
					_kontoZuordnung.Clear();
					foreach (JsonElement item in value.EnumerateArray())
					{
						if (!item.TryGetProperty("id", out var value2) || value2.ValueKind != JsonValueKind.Number)
						{
							continue;
						}
						string[] array = new string[3] { "jellyfinUsername", "username", "displayName" };
						foreach (string name in array)
						{
							string text = Text(item, name);
							if (!string.IsNullOrWhiteSpace(text))
							{
								_kontoZuordnung[text] = value2.GetInt32();
							}
						}
					}
				}
				_zuordnungStand = DateTime.UtcNow;
				goto end_IL_018a;
				end_IL_020c:;
			}
			return;
			end_IL_018a:;
		}
		catch (Exception ex)
		{
			LoggerExtensions.LogWarning((ILogger)(object)_logger, ex, "Benutzerzuordnung aus Seerr nicht ladbar", Array.Empty<object>());
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
