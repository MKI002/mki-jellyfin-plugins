using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SeerrBridge.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.QuickConnect;
using MediaBrowser.Model.QuickConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrBridge.Services;

public class SsoBruecke : IStartupFilter
{
	private static readonly ConcurrentDictionary<string, Ausweis> Ausweise = new ConcurrentDictionary<string, Ausweis>(StringComparer.Ordinal);

	private readonly IServiceProvider _dienste;

	private readonly ILogger<SsoBruecke> _logger;

	public SsoBruecke(IServiceProvider dienste, ILogger<SsoBruecke> logger)
	{
		_dienste = dienste;
		_logger = logger;
	}

	public static string Ausstellen(Guid userId, string benutzer)
	{
		Aufraeumen();
		string text = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
		Ausweise[text] = new Ausweis(userId, benutzer, DateTime.UtcNow.AddMinutes(1.0));
		return text;
	}

	private static void Aufraeumen()
	{
		foreach (KeyValuePair<string, Ausweis> item in Ausweise)
		{
			if (item.Value.GueltigBis < DateTime.UtcNow)
			{
				Ausweise.TryRemove(item.Key, out Ausweis _);
			}
		}
	}

	private static Ausweis? Einloesen(string? schluessel, string? benutzer)
	{
		if (string.IsNullOrEmpty(schluessel) || !Ausweise.TryRemove(schluessel, out Ausweis value))
		{
			return null;
		}
		if (value.GueltigBis < DateTime.UtcNow)
		{
			return null;
		}
		if (!string.Equals(value.Benutzer, benutzer, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		return value;
	}

	public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
	{
		return delegate(IApplicationBuilder app)
		{
			UseExtensions.Use(app, (Func<HttpContext, Func<Task>, Task>)async delegate(HttpContext kontext, Func<Task> weiter)
			{
				bool behandelt;
				try
				{
					behandelt = await BehandeltAsync(kontext).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch (Exception ex) when (!kontext.Response.HasStarted)
				{
					// Never let single sign-on break normal sign-in.
					LoggerExtensions.LogWarning((ILogger)(object)_logger, ex, "Single sign-on skipped", Array.Empty<object>());
					behandelt = false;
				}
				if (!behandelt)
				{
					await weiter().ConfigureAwait(continueOnCapturedContext: false);
				}
			});
			next(app);
		};
	}

	private async Task<bool> BehandeltAsync(HttpContext kontext)
	{
		if ((((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration ?? new PluginConfiguration()).EnableSso)
		{
			PathString path = kontext.Request.Path;
			if (path.StartsWithSegments(("/Users/AuthenticateByName")) && HttpMethods.IsPost(kontext.Request.Method))
			{
				string json;
				try
				{
					HttpRequestRewindExtensions.EnableBuffering(kontext.Request);
					using StreamReader leser = new StreamReader(kontext.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 1024, leaveOpen: true);
					json = await leser.ReadToEndAsync().ConfigureAwait(continueOnCapturedContext: false);
					kontext.Request.Body.Position = 0L;
				}
				catch (Exception)
				{
					return false;
				}
				string benutzer = null;
				string schluessel = null;
				try
				{
					using JsonDocument jsonDocument = JsonDocument.Parse(json);
					string[] array = new string[2] { "Username", "username" };
					foreach (string propertyName in array)
					{
						if (jsonDocument.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
						{
							benutzer = value.GetString();
							break;
						}
					}
					array = new string[4] { "Pw", "pw", "Password", "password" };
					foreach (string propertyName2 in array)
					{
						if (jsonDocument.RootElement.TryGetProperty(propertyName2, out var value2) && value2.ValueKind == JsonValueKind.String)
						{
							schluessel = value2.GetString();
							break;
						}
					}
				}
				catch (JsonException)
				{
					return false;
				}
				Ausweis ausweis = Einloesen(schluessel, benutzer);
				if ((object)ausweis == null)
				{
					return false;
				}
				try
				{
					string text = await UeberQuickConnectAsync(ausweis, kontext).ConfigureAwait(continueOnCapturedContext: false);
					if (text == null)
					{
						return false;
					}
					kontext.Response.StatusCode = 200;
					kontext.Response.ContentType = "application/json; charset=utf-8";
					await HttpResponseWritingExtensions.WriteAsync(kontext.Response, text, default(CancellationToken)).ConfigureAwait(continueOnCapturedContext: false);
					LoggerExtensions.LogInformation((ILogger)(object)_logger, "Passed sign-in for {Benutzer} to Seerr via one-time pass", new object[1] { ausweis.Benutzer });
					return true;
				}
				catch (Exception ex3)
				{
					LoggerExtensions.LogError((ILogger)(object)_logger, ex3, "Passing on the sign-in failed", Array.Empty<object>());
					return false;
				}
			}
		}
		return false;
	}

	private async Task<string?> UeberQuickConnectAsync(Ausweis ausweis, HttpContext kontext)
	{
		IQuickConnect qc = ServiceProviderServiceExtensions.GetService<IQuickConnect>(_dienste);
		if (qc == null)
		{
			LoggerExtensions.LogWarning((ILogger)(object)_logger, "Quick Connect not available", Array.Empty<object>());
			return null;
		}
		if (!qc.IsEnabled)
		{
			LoggerExtensions.LogWarning((ILogger)(object)_logger, "Quick Connect is disabled on the server - without it the sign-in cannot be passed on", Array.Empty<object>());
			return null;
		}
		AuthorizationInfo val = new AuthorizationInfo
		{
			Client = "Seerr",
			Version = "1.0",
			Device = "Seerr Bridge",
			DeviceId = "seerrbridge-" + ausweis.UserId.ToString("N")
		};
		QuickConnectResult start = qc.TryConnect(val);
		if (start == null || string.IsNullOrEmpty(start.Code) || string.IsNullOrEmpty(start.Secret))
		{
			return null;
		}
		if (!(await qc.AuthorizeRequest(ausweis.UserId, start.Code).ConfigureAwait(continueOnCapturedContext: false)))
		{
			return null;
		}
		AuthenticationResult authorizedRequest = qc.GetAuthorizedRequest(start.Secret);
		if (authorizedRequest == null)
		{
			return null;
		}
		return JsonSerializer.Serialize<AuthenticationResult>(authorizedRequest, new JsonSerializerOptions
		{
			PropertyNamingPolicy = null,
			DefaultIgnoreCondition = JsonIgnoreCondition.Never
		});
	}
}
