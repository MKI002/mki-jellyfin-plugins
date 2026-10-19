using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Services;

// The web client script is only added to index.html while the plugin has something to do,
// and removed again when everything is switched off. A fresh install leaves the web client untouched.
public class WebPatcher : IHostedService
{
	private const string Skript = "<script src=\"../MkiPasswordPolicy/client.js\" data-mki-password-policy-injected=\"1\" defer></script>";

	private static readonly Regex Eintrag = new("\\s*<script[^>]*mki-password-policy-injected[^>]*></script>", RegexOptions.IgnoreCase);

	private readonly object _sperre = new();

	private readonly IApplicationPaths _paths;

	private readonly PolicyStore _store;

	private readonly ILogger<WebPatcher> _logger;

	public WebPatcher(IApplicationPaths paths, PolicyStore store, ILogger<WebPatcher> logger)
	{
		_paths = paths;
		_store = store;
		_logger = logger;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		Plugin.ConfigSaved += Sync;
		Sync();
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		Plugin.ConfigSaved -= Sync;
		return Task.CompletedTask;
	}

	public void Sync()
	{
		try
		{
			bool noetig = Policy.Configured(Plugin.Config) || _store.AnyMustChange();
			lock (_sperre)
			{
				Schreiben(noetig);
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "MKI Password Policy could not update the web client");
		}
	}

	private void Schreiben(bool eintragen)
	{
		string? wurzel = WebWurzel();
		if (wurzel == null)
		{
			if (eintragen)
			{
				_logger.LogWarning("Web client folder not found - the password dialog will be missing in browsers");
			}
			return;
		}
		string datei = Path.Combine(wurzel, "index.html");
		string alt = File.ReadAllText(datei);
		string neu = Eintrag.Replace(alt, string.Empty);
		if (eintragen)
		{
			int ende = neu.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
			if (ende < 0)
			{
				_logger.LogWarning("No </body> found in index.html");
				return;
			}
			neu = neu.Insert(ende, Skript);
		}
		if (neu != alt)
		{
			File.WriteAllText(datei, neu);
			_logger.LogInformation(eintragen ? "MKI Password Policy added to the web client ({Path})" : "MKI Password Policy removed from the web client ({Path})", datei);
		}
	}

	private string? WebWurzel()
	{
		string?[] kandidaten =
		{
			TryGet(() => _paths.WebPath),
			"/jellyfin/jellyfin-web",
			"/usr/share/jellyfin/web",
			"/usr/lib/jellyfin/bin/jellyfin-web"
		};
		foreach (string? pfad in kandidaten)
		{
			if (!string.IsNullOrEmpty(pfad) && File.Exists(Path.Combine(pfad, "index.html")))
			{
				return pfad;
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
