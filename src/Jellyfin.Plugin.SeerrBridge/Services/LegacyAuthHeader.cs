using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.SeerrBridge.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.SeerrBridge.Services;

public class LegacyAuthHeader : IStartupFilter
{
	// Jellyfin tokens and API keys are hex strings. Anything else is not copied, so a header cannot smuggle extra
	// fields into the Authorization header it is turned into.
	private static readonly Regex TokenForm = new Regex("^[A-Za-z0-9]{16,128}$", RegexOptions.Compiled);

	private readonly ILogger<LegacyAuthHeader> _logger;

	private readonly IServerConfigurationManager _server;

	private bool _gemeldet;

	public LegacyAuthHeader(ILogger<LegacyAuthHeader> logger, IServerConfigurationManager server)
	{
		_logger = logger;
		_server = server;
	}

	public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
	{
		return delegate(IApplicationBuilder app)
		{
			UseExtensions.Use(app, (Func<HttpContext, Func<Task>, Task>)async delegate(HttpContext kontext, Func<Task> weiter)
			{
				try
				{
					Uebersetzen(kontext);
				}
				catch (Exception ex)
				{
					LoggerExtensions.LogWarning((ILogger)(object)_logger, ex, "Header bridge skipped", Array.Empty<object>());
				}
				await weiter().ConfigureAwait(continueOnCapturedContext: false);
			});
			next(app);
		};
	}

	private void Uebersetzen(HttpContext kontext)
	{
		//IL_0103: Unknown result type (might be due to invalid IL or missing references)
		//IL_0135: Unknown result type (might be due to invalid IL or missing references)
		//IL_013a: Unknown result type (might be due to invalid IL or missing references)
		if (!(((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration ?? new PluginConfiguration()).LegacyAuthHeader)
		{
			return;
		}
		// With legacy sign-in switched on in Jellyfin itself, Jellyfin reads these headers on its own.
		if (_server.Configuration.EnableLegacyAuthorization)
		{
			return;
		}
		IHeaderDictionary headers = kontext.Request.Headers;
		if (((IDictionary<string, StringValues>)headers).TryGetValue("Authorization", out StringValues value) && !string.IsNullOrWhiteSpace(value.ToString()))
		{
			return;
		}
		string text = null;
		if (((IDictionary<string, StringValues>)headers).TryGetValue("X-Emby-Authorization", out StringValues value2) && !string.IsNullOrWhiteSpace(value2.ToString()))
		{
			text = value2.ToString();
		}
		else
		{
			string[] array = new string[2] { "X-Emby-Token", "X-MediaBrowser-Token" };
			foreach (string key in array)
			{
				if (((IDictionary<string, StringValues>)headers).TryGetValue(key, out StringValues value3) && !string.IsNullOrWhiteSpace(value3.ToString()))
				{
					if (value3.Count == 1 && TokenForm.IsMatch(value3.ToString().Trim()))
					{
						text = "MediaBrowser Token=\"" + value3.ToString().Trim() + "\"";
					}
					break;
				}
			}
		}
		if (text != null)
		{
			headers["Authorization"] = (text);
			if (!_gemeldet)
			{
				_gemeldet = true;
				LoggerExtensions.LogInformation((ILogger)(object)_logger, "Translated legacy auth header (first seen from {Pfad}) - older apps like Seerr can sign in again", new object[1] { ((object)kontext.Request.Path/*cast due to .constrained prefix*/).ToString() });
			}
		}
	}
}
