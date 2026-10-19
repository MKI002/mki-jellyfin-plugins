using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Services;

// The web client script runs in one of two modes. "link" (nothing configured, nothing pending) only adds the
// administrator's shortcut on a user's Password tab and changes nothing else. "full" adds the sign-in dialog and
// the live password hints while the plugin has something to enforce.
public class WebPatcher : IHostedService
{
	private const string Skript = "<script src=\"../MkiPasswordPolicy/client.js\" data-mki-password-policy-injected=\"1\" data-mode=\"{0}\" defer></script>";

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
			bool aktiv = Policy.Configured(Plugin.Config) || _store.AnyMustChange();
			lock (_sperre)
			{
				Schreiben(aktiv ? "full" : "link");
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "MKI Password Policy could not update the web client");
		}
	}

	private void Schreiben(string modus)
	{
		string? wurzel = WebWurzel();
		if (wurzel == null)
		{
			_logger.LogWarning("Web client folder not found - the password dialog will be missing in browsers");
			return;
		}
		string datei = Path.Combine(wurzel, "index.html");
		string alt = File.ReadAllText(datei);
		string neu = Eintrag.Replace(alt, string.Empty);
		int ende = neu.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
		if (ende < 0)
		{
			_logger.LogWarning("No </body> found in index.html");
			return;
		}
		neu = neu.Insert(ende, string.Format(CultureInfo.InvariantCulture, Skript, modus));
		if (neu != alt)
		{
			File.WriteAllText(datei, neu);
			_logger.LogInformation("MKI Password Policy web client script set to {Mode} mode ({Path})", modus, datei);
		}
	}

	// Called when the plugin is uninstalled, so the web client does not keep loading a script that is gone.
	public static void Entfernen(IApplicationPaths paths)
	{
		string? wurzel = WebWurzel(paths);
		if (wurzel == null)
		{
			return;
		}
		string datei = Path.Combine(wurzel, "index.html");
		string alt = File.ReadAllText(datei);
		string neu = Eintrag.Replace(alt, string.Empty);
		if (neu != alt)
		{
			File.WriteAllText(datei, neu);
		}
	}

	private string? WebWurzel() => WebWurzel(_paths);

	private static string? WebWurzel(IApplicationPaths paths)
	{
		string?[] kandidaten =
		{
			TryGet(() => paths.WebPath),
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
