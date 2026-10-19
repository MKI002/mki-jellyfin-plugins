using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaHero.Services;

public class WebPatcher : IHostedService
{
	private const string Marke = "mediahero-injected";

	private readonly IApplicationPaths _paths;

	private readonly ILogger<WebPatcher> _logger;

	public WebPatcher(IApplicationPaths paths, ILogger<WebPatcher> logger)
	{
		_paths = paths;
		_logger = logger;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		try
		{
			Eintragen();
		}
		catch (Exception ex)
		{
			LoggerExtensions.LogError((ILogger)(object)_logger, ex, "Media Hero konnte sich nicht in den Web-Client eintragen", Array.Empty<object>());
		}
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		return Task.CompletedTask;
	}

	private void Eintragen()
	{
		string text = WebWurzel();
		if (text == null)
		{
			LoggerExtensions.LogWarning((ILogger)(object)_logger, "Web-Verzeichnis nicht gefunden - Leiste bleibt unsichtbar", Array.Empty<object>());
			return;
		}
		string text2 = Path.Combine(text, "index.html");
		if (!File.Exists(text2))
		{
			LoggerExtensions.LogWarning((ILogger)(object)_logger, "index.html nicht gefunden unter {Pfad}", new object[1] { text });
			return;
		}
		string text3 = File.ReadAllText(text2);
		string text4 = text3;
		text3 = Regex.Replace(text3, "\\s*<script[^>]*mediahero-injected[^>]*></script>", string.Empty, RegexOptions.IgnoreCase);
		string value = "<script src=\"../MediaHero/client.js\" data-mediahero-injected=\"1\" defer></script>";
		int num = text3.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
		if (num < 0)
		{
			LoggerExtensions.LogWarning((ILogger)(object)_logger, "Kein </body> in der index.html gefunden", Array.Empty<object>());
			return;
		}
		text3 = text3.Insert(num, value);
		if (!(text3 == text4))
		{
			File.WriteAllText(text2, text3);
			LoggerExtensions.LogInformation((ILogger)(object)_logger, "Media Hero in den Web-Client eingetragen ({Pfad})", new object[1] { text2 });
		}
	}

	private string? WebWurzel()
	{
		string[] array = new string[4]
		{
			TryGet(() => _paths.WebPath),
			"/jellyfin/jellyfin-web",
			"/usr/share/jellyfin/web",
			"/usr/lib/jellyfin/bin/jellyfin-web"
		};
		foreach (string text in array)
		{
			if (!string.IsNullOrEmpty(text) && File.Exists(Path.Combine(text, "index.html")))
			{
				return text;
			}
		}
		return null;
	}

	private static string? TryGet(Func<string?> f)
	{
		try
		{
			return f();
		}
		catch (Exception)
		{
			return null;
		}
	}
}
