using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiGuestPass.Services;

// Adds the web client script to index.html only while it has something to do: while there are guests (their badge)
// or an open pass with a typed code (the "Have a guest code?" link on the sign-in page). Otherwise the web client is
// left exactly as Jellyfin ships it, and the script is taken out again when the plugin is uninstalled or disabled.
public class WebPatcher : IHostedService
{
	private const string Marker = "data-mki-guest-pass-injected";

	private static readonly Regex Injected = new("\\s*<script[^>]*" + Marker + "[^>]*></script>", RegexOptions.IgnoreCase);

	private static readonly TimeSpan Every = TimeSpan.FromSeconds(60);

	private static readonly object Lock = new();

	// Set while the plugin is disabled, and for good once it is uninstalled: the script is taken out and stays out.
	private static volatile bool _off;

	private static volatile bool _gone;

	private readonly IApplicationPaths _paths;

	private readonly PassStore _store;

	private readonly ILogger<WebPatcher> _logger;

	private Timer? _timer;

	private Timer? _soon;

	public WebPatcher(IApplicationPaths paths, PassStore store, ILogger<WebPatcher> logger)
	{
		_paths = paths;
		_store = store;
		_logger = logger;
	}

	// Why the web client could not be changed while it should have been; shown on the settings page.
	public static string? Problem { get; private set; }

	public Task StartAsync(CancellationToken cancellationToken)
	{
		Plugin.ConfigSaved += Soon;
		_store.Changed += Soon;
		// Also on a timer: a pass with a code ends by the clock, without any change to save.
		_timer = new Timer(_ => Sync(), null, TimeSpan.Zero, Every);
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		Plugin.ConfigSaved -= Soon;
		_store.Changed -= Soon;
		_timer?.Dispose();
		_soon?.Dispose();
		return Task.CompletedTask;
	}

	// Many changes in a row (a sweep, a join) write index.html at most once.
	private void Soon()
	{
		lock (Lock)
		{
			_soon?.Dispose();
			_soon = new Timer(_ => Sync(), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
		}
	}

	public void Sync()
	{
		if (_off || _gone)
		{
			return;
		}
		try
		{
			var now = DateTime.UtcNow;
			var (guests, codes) = _store.Read(d => (
				d.Passes.Any(p => p.Guests.Count > 0),
				d.Passes.Any(p => p.CodeHash != null && !p.HasEnded(now))));
			bool badge = guests && Plugin.Config.ShowGuestBadge;
			bool link = codes && Plugin.Config.ShowCodeLink;
			lock (Lock)
			{
				if (!_off && !_gone)
				{
					Write(badge || link ? Tag(link) : null);
				}
			}
		}
		catch (Exception ex)
		{
			Problem = "Jellyfin's web folder could not be changed (" + ex.GetType().Name + ").";
			_logger.LogError(ex, "MKI Guest Pass could not update the web client");
		}
	}

	private static string Tag(bool codeLink) => "<script src=\"../MkiGuestPass/client.js\" " + Marker + "=\"1\"" + (codeLink ? " data-code-link=\"1\"" : string.Empty) + " defer></script>";

	private void Write(string? tag)
	{
		string? root = WebRoot(_paths);
		if (root == null)
		{
			Problem = tag == null ? null : "Jellyfin's web folder was not found, so guests see no badge and the sign-in page no code link.";
			if (tag != null)
			{
				_logger.LogWarning("Web client folder not found - guests get no badge in browsers");
			}
			return;
		}
		string file = Path.Combine(root, "index.html");
		string old = File.ReadAllText(file);
		string updated = Injected.Replace(old, string.Empty);
		if (tag != null)
		{
			int end = updated.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
			if (end < 0)
			{
				Problem = "Jellyfin's index.html has no </body>, so the guest badge could not be added.";
				return;
			}
			updated = updated.Insert(end, tag);
		}
		if (updated != old)
		{
			WriteFile(file, updated);
			_logger.LogInformation("MKI Guest Pass: web client script {State} ({Path})", tag == null ? "removed" : "updated", file);
		}
		Problem = null;
	}

	// Called when the plugin is uninstalled or disabled, so the web client does not keep loading a script that is gone.
	public static void TurnOff(IApplicationPaths paths, bool forGood)
	{
		lock (Lock)
		{
			_off = true;
			_gone |= forGood;
			RemoveScript(paths);
		}
	}

	// Switched back on in the dashboard before Jellyfin restarted; the next regular check adds the script again.
	public static void TurnOn()
	{
		_off = false;
	}

	private static void RemoveScript(IApplicationPaths paths)
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
		return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "index.html")));
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
		string tmp = file + ".mki-guestpass.tmp";
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
