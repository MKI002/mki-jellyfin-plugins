using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.SeerrBridge.Configuration;
using MediaBrowser.Common.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.SeerrBridge.Services;

public class LegacyAuthHeader : IStartupFilter
{
	private readonly ILogger<LegacyAuthHeader> _logger;

	private bool _gemeldet;

	public LegacyAuthHeader(ILogger<LegacyAuthHeader> logger)
	{
		_logger = logger;
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
					LoggerExtensions.LogWarning((ILogger)(object)_logger, ex, "Kopfzeilen-Bruecke uebersprungen", Array.Empty<object>());
				}
				await weiter().ConfigureAwait(continueOnCapturedContext: false);
			});
			next(app);
		};
	}

	private unsafe void Uebersetzen(HttpContext kontext)
	{
		//IL_0103: Unknown result type (might be due to invalid IL or missing references)
		//IL_0135: Unknown result type (might be due to invalid IL or missing references)
		//IL_013a: Unknown result type (might be due to invalid IL or missing references)
		if (!(((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration ?? new PluginConfiguration()).LegacyAuthHeader)
		{
			return;
		}
		IHeaderDictionary headers = kontext.Request.Headers;
		if (((IDictionary<string, StringValues>)headers).TryGetValue("Authorization", out StringValues value) && !string.IsNullOrWhiteSpace(((object)(*(StringValues*)(&value))/*cast due to .constrained prefix*/).ToString()))
		{
			return;
		}
		string text = null;
		if (((IDictionary<string, StringValues>)headers).TryGetValue("X-Emby-Authorization", out StringValues value2) && !string.IsNullOrWhiteSpace(((object)(*(StringValues*)(&value2))/*cast due to .constrained prefix*/).ToString()))
		{
			text = ((object)(*(StringValues*)(&value2))/*cast due to .constrained prefix*/).ToString();
		}
		else
		{
			string[] array = new string[2] { "X-Emby-Token", "X-MediaBrowser-Token" };
			foreach (string key in array)
			{
				if (((IDictionary<string, StringValues>)headers).TryGetValue(key, out StringValues value3) && !string.IsNullOrWhiteSpace(((object)(*(StringValues*)(&value3))/*cast due to .constrained prefix*/).ToString()))
				{
					text = "MediaBrowser Token=\"" + ((object)(*(StringValues*)(&value3))/*cast due to .constrained prefix*/).ToString() + "\"";
					break;
				}
			}
		}
		if (text != null)
		{
			headers["Authorization"] = StringValues.op_Implicit(text);
			if (!_gemeldet)
			{
				_gemeldet = true;
				LoggerExtensions.LogInformation((ILogger)(object)_logger, "Alte Anmeldekopfzeile uebersetzt (erstmals von {Pfad}) - aeltere Anwendungen wie Jellyseerr koennen sich wieder anmelden", new object[1] { ((object)kontext.Request.Path/*cast due to .constrained prefix*/).ToString() });
			}
		}
	}
}
