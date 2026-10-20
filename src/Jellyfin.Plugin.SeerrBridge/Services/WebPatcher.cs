using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrBridge.Services;

public class WebPatcher : IHostedService
{
	private const string Marke = "seerrbridge-injected";

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
			LoggerExtensions.LogError((ILogger)(object)_logger, ex, "Seerr Bridge could not inject itself into the web client", Array.Empty<object>());
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
			LoggerExtensions.LogWarning((ILogger)(object)_logger, "Web directory not found - button stays hidden", Array.Empty<object>());
			return;
		}
		string text2 = Path.Combine(text, "index.html");
		if (!File.Exists(text2))
		{
			LoggerExtensions.LogWarning((ILogger)(object)_logger, "index.html not found under {Pfad}", new object[1] { text });
			return;
		}
		string text3 = File.ReadAllText(text2);
		string text4 = text3;
		text3 = Regex.Replace(text3, "\\s*<script[^>]*seerrbridge-injected[^>]*></script>", string.Empty, RegexOptions.IgnoreCase);
		string value = "<script src=\"../SeerrBridge/client.js\" data-seerrbridge-injected=\"1\" defer></script>";
		int num = text3.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
		if (num < 0)
		{
			LoggerExtensions.LogWarning((ILogger)(object)_logger, "No </body> found in index.html", Array.Empty<object>());
			return;
		}
		text3 = text3.Insert(num, value);
		if (!(text3 == text4))
		{
			DateiSchreiben(text2, text3);
			LoggerExtensions.LogInformation((ILogger)(object)_logger, "Seerr Bridge injected into the web client ({Pfad})", new object[1] { text2 });
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

	// Through a temporary file, so a crash mid-write cannot leave a cut-off index.html behind. Where the folder
	// allows no new files, the file is written in place as before.
	private static void DateiSchreiben(string datei, string inhalt)
	{
		string tmp = datei + ".mki-seerrbridge.tmp";
		try
		{
			File.WriteAllText(tmp, inhalt);
			File.Move(tmp, datei, overwrite: true);
			return;
		}
		catch (UnauthorizedAccessException)
		{
		}
		catch (IOException)
		{
		}
		try
		{
			File.Delete(tmp);
		}
		catch (Exception)
		{
		}
		File.WriteAllText(datei, inhalt);
	}
}
