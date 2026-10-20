using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiContinueWatching.Services;

// Adds the web client script to index.html only while the Remove button is turned on, and takes it out again when
// it is turned off or the plugin is uninstalled. Until then the web client is left exactly as Jellyfin ships it.
public class WebPatcher : IHostedService
{
	private const string ScriptTag = "<script src=\"../MkiContinueWatching/client.js\" data-mki-continue-watching-injected=\"1\" defer></script>";

	private static readonly Regex Injected = new("\\s*<script[^>]*mki-continue-watching-injected[^>]*></script>", RegexOptions.IgnoreCase);

	private readonly object _lock = new();

	private readonly IApplicationPaths _paths;

	private readonly ILogger<WebPatcher> _logger;

	public WebPatcher(IApplicationPaths paths, ILogger<WebPatcher> logger)
	{
		_paths = paths;
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
			lock (_lock)
			{
				Write(Plugin.Config.EnableRemoveButton);
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "MKI Continue Watching could not update the web client");
		}
	}

	private void Write(bool on)
	{
		string? root = WebRoot(_paths);
		if (root == null)
		{
			if (on)
			{
				_logger.LogWarning("Web client folder not found - the Remove button will be missing in browsers");
			}
			return;
		}
		string file = Path.Combine(root, "index.html");
		string old = File.ReadAllText(file);
		string updated = Injected.Replace(old, string.Empty);
		if (on)
		{
			int end = updated.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
			if (end < 0)
			{
				_logger.LogWarning("No </body> found in index.html");
				return;
			}
			updated = updated.Insert(end, ScriptTag);
		}
		if (updated != old)
		{
			WriteFile(file, updated);
			_logger.LogInformation("MKI Continue Watching: Remove button script {State} the web client ({Path})", on ? "added to" : "removed from", file);
		}
	}

	// Called when the plugin is uninstalled, so the web client does not keep loading a script that is gone.
	public static void RemoveScript(IApplicationPaths paths)
	{
		string? root = WebRoot(paths);
		if (root == null)
		{
			return;
		}
		string file = Path.Combine(root, "index.html");
		string old = File.ReadAllText(file);
		string updated = Injected.Replace(old, string.Empty);
		if (updated != old)
		{
			WriteFile(file, updated);
		}
	}

	// Jellyfin's own web folder. The usual install folders are tried only when Jellyfin names none, so the plugin
	// never edits a copy of the web client that Jellyfin does not serve.
	private static string? WebRoot(IApplicationPaths paths)
	{
		string? configured = TryGet(() => paths.WebPath);
		if (!string.IsNullOrEmpty(configured))
		{
			return File.Exists(Path.Combine(configured, "index.html")) ? configured : null;
		}
		string[] candidates =
		{
			"/jellyfin/jellyfin-web",
			"/usr/share/jellyfin/web",
			"/usr/lib/jellyfin/bin/jellyfin-web"
		};
		foreach (string path in candidates)
		{
			if (File.Exists(Path.Combine(path, "index.html")))
			{
				return path;
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
	// allows no new files, the file is written in place.
	private static void WriteFile(string file, string content)
	{
		string tmp = file + ".mki-continuewatching.tmp";
		try
		{
			File.WriteAllText(tmp, content);
			File.Move(tmp, file, overwrite: true);
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
		File.WriteAllText(file, content);
	}
}
