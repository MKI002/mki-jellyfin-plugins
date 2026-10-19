using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaHero.Services;

public class JellyseerrClient
{
	// Discover pages are a few kilobytes; anything far bigger is not Seerr.
	private const int MaxAntwort = 4 * 1024 * 1024;

	// Its own client: no redirects (the API key would follow them to wherever they point) and no cookie jar.
	private static readonly HttpClient Netz = new HttpClient(new SocketsHttpHandler
	{
		AllowAutoRedirect = false,
		UseCookies = false,
		PooledConnectionLifetime = TimeSpan.FromMinutes(5)
	})
	{
		Timeout = TimeSpan.FromSeconds(15L),
		MaxResponseContentBufferSize = MaxAntwort
	};

	// Pages are the same for every user, so each one is fetched once for all of them.
	private static readonly TimeSpan Haltbar = TimeSpan.FromMinutes(10);

	private readonly ConcurrentDictionary<string, (DateTime Geholt, IReadOnlyList<TrendEntry> Eintraege)> _seiten = new ConcurrentDictionary<string, (DateTime, IReadOnlyList<TrendEntry>)>();

	private readonly ILogger<JellyseerrClient> _logger;

	public JellyseerrClient(ILogger<JellyseerrClient> logger)
	{
		_logger = logger;
	}

	public async Task<IReadOnlyList<TrendEntry>> SeiteAsync(string baseUrl, string apiKey, string route, int seite, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey)
			|| !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out Uri basis) || (basis.Scheme != Uri.UriSchemeHttp && basis.Scheme != Uri.UriSchemeHttps))
		{
			return Array.Empty<TrendEntry>();
		}
		string text = (route.Contains('?', StringComparison.Ordinal) ? "&" : "?");
		string requestUri = baseUrl.Trim().TrimEnd('/') + route + text + "page=" + seite.ToString(CultureInfo.InvariantCulture);
		if (_seiten.TryGetValue(requestUri, out var gemerkt) && DateTime.UtcNow - gemerkt.Geholt < Haltbar)
		{
			return gemerkt.Eintraege;
		}
		if (_seiten.Count > 500)
		{
			_seiten.Clear();
		}
		try
		{
			{
				using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, requestUri);
				request.Headers.Add("X-Api-Key", apiKey);
				using HttpResponseMessage response = await Netz.SendAsync(request, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (!response.IsSuccessStatusCode)
				{
					LoggerExtensions.LogWarning((ILogger)(object)_logger, "Seerr answered {Route} (page {Page}) with {Code}", new object[3]
					{
						route,
						seite,
						(int)response.StatusCode
					});
					return Array.Empty<TrendEntry>();
				}
				if (response.Content.Headers.ContentLength > MaxAntwort)
				{
					LoggerExtensions.LogWarning((ILogger)(object)_logger, "Seerr answered {Route} (page {Page}) with an oversized response", new object[2] { route, seite });
					return Array.Empty<TrendEntry>();
				}
				IReadOnlyList<TrendEntry> result;
				// Already read in full by the client, which refuses anything above MaxResponseContentBufferSize.
				byte[] rumpf = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				await using (Stream stream = new MemoryStream(rumpf, writable: false))
				{
					using JsonDocument jsonDocument = await JsonDocument.ParseAsync(stream, default(JsonDocumentOptions), ct).ConfigureAwait(continueOnCapturedContext: false);
					if (!jsonDocument.RootElement.TryGetProperty("results", out var value) || value.ValueKind != JsonValueKind.Array)
					{
						result = Array.Empty<TrendEntry>();
					}
					else
					{
						List<TrendEntry> list = new List<TrendEntry>();
						foreach (JsonElement item in value.EnumerateArray())
						{
							string text2 = Text(item, "mediaType");
							if ((!(text2 != "movie") || !(text2 != "tv")) && item.TryGetProperty("id", out var value2) && value2.ValueKind == JsonValueKind.Number)
							{
								string text3 = Text(item, "title");
								if (string.IsNullOrEmpty(text3))
								{
									text3 = Text(item, "name");
								}
								double popularity = 0.0;
								if (item.TryGetProperty("popularity", out var value3) && value3.ValueKind == JsonValueKind.Number)
								{
									popularity = value3.GetDouble();
								}
								list.Add(new TrendEntry(value2.GetInt32(), text2, text3 ?? string.Empty, popularity));
							}
						}
						result = list;
					}
				}
				_seiten[requestUri] = (DateTime.UtcNow, result);
				return result;
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex2) when (ex2 is not OperationCanceledException || !ct.IsCancellationRequested)
		{
			LoggerExtensions.LogWarning((ILogger)(object)_logger, ex2, "Seerr ({Route}, page {Page}) unreachable", new object[2] { route, seite });
			// Remembered as empty for a minute, so an unreachable Seerr costs one timeout per minute, not one per user.
			_seiten[requestUri] = (DateTime.UtcNow - Haltbar + TimeSpan.FromMinutes(1), Array.Empty<TrendEntry>());
			return Array.Empty<TrendEntry>();
		}
		throw null;
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
