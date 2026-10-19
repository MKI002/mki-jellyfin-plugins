using System;
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
	private readonly IHttpClientFactory _httpClientFactory;

	private readonly ILogger<JellyseerrClient> _logger;

	public JellyseerrClient(IHttpClientFactory httpClientFactory, ILogger<JellyseerrClient> logger)
	{
		_httpClientFactory = httpClientFactory;
		_logger = logger;
	}

	public async Task<IReadOnlyList<TrendEntry>> FetchAsync(string baseUrl, string apiKey, string route, CancellationToken ct, int tiefe = 20)
	{
		if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
		{
			return Array.Empty<TrendEntry>();
		}
		List<TrendEntry> alle = new List<TrendEntry>();
		int seiten = Math.Clamp((tiefe + 19) / 20, 1, 20);
		for (int seite = 1; seite <= seiten; seite++)
		{
			IReadOnlyList<TrendEntry> readOnlyList = await SeiteAsync(baseUrl, apiKey, route, seite, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (readOnlyList.Count == 0)
			{
				break;
			}
			alle.AddRange(readOnlyList);
			if (alle.Count >= tiefe)
			{
				break;
			}
		}
		return alle;
	}

	public async Task<IReadOnlyList<TrendEntry>> SeiteAsync(string baseUrl, string apiKey, string route, int seite, CancellationToken ct)
	{
		string text = (route.Contains('?', StringComparison.Ordinal) ? "&" : "?");
		string requestUri = baseUrl.TrimEnd('/') + route + text + "page=" + seite.ToString(CultureInfo.InvariantCulture);
		try
		{
			using (HttpClient client = HttpClientFactoryExtensions.CreateClient(_httpClientFactory))
			{
				client.Timeout = TimeSpan.FromSeconds(15L);
				using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, requestUri);
				request.Headers.Add("X-Api-Key", apiKey);
				using HttpResponseMessage response = await client.SendAsync(request, ct).ConfigureAwait(continueOnCapturedContext: false);
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
				IReadOnlyList<TrendEntry> result;
				await using (Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(continueOnCapturedContext: false))
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
				return result;
			}
			IL_0550:;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex2)
		{
			LoggerExtensions.LogWarning((ILogger)(object)_logger, ex2, "Seerr ({Route}, page {Page}) unreachable", new object[2] { route, seite });
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
