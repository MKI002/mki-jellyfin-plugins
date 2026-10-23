using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiGuestPass.Services;

// Warns guests before the end, locks them at the end and deletes them after the grace period. Checks every
// 30 seconds, right at the end of a pass and right at start, so a pass that ended while Jellyfin was off is locked
// before anyone can use it. Whenever Jellyfin stops, every guest account is locked and unlocked again when the
// plugin runs: a Jellyfin that does not load the plugin any more (after an update, say) leaves no guest usable.
public class ExpiryService : IHostedService
{
	private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

	// Every tenth tick: also accounts deleted elsewhere, locked accounts switched back on, accounts of no pass.
	private const int ThoroughEvery = 10;

	private readonly GuestService _guests;

	private readonly ISessionManager _sessions;

	private readonly IPluginManager _plugins;

	private readonly IApplicationPaths _paths;

	private readonly ILogger<ExpiryService> _logger;

	private CancellationTokenSource? _stop;

	private Task? _loop;

	public ExpiryService(GuestService guests, ISessionManager sessions, IPluginManager plugins, IApplicationPaths paths, ILogger<ExpiryService> logger)
	{
		_guests = guests;
		_sessions = sessions;
		_plugins = plugins;
		_paths = paths;
		_logger = logger;
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		_stop = new CancellationTokenSource();
		_sessions.PlaybackStart += OnPlaybackStart;
		Plugin.Uninstalling += OnUninstalling;
		try
		{
			// The guests locked when Jellyfin stopped carry on (the first check locks those whose pass ended).
			await _guests.Resume().WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "MKI Guest Pass could not unlock the guest accounts yet; the first check does it");
		}
		_loop = Task.Run(() => Loop(_stop.Token));
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		_sessions.PlaybackStart -= OnPlaybackStart;
		Plugin.Uninstalling -= OnUninstalling;
		_stop?.Cancel();
		if (_loop != null)
		{
			try
			{
				await _loop.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
			}
			catch (Exception)
			{
			}
		}
		// Disabled in the dashboard: Jellyfin will not load the plugin again, so guests are signed out for good. Any other
		// stop: locked, signed in still, and unlocked when the plugin runs again.
		bool disabled = BeingDisabled();
		try
		{
			await _guests.PauseAll(disabled).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "MKI Guest Pass stops but could not lock the guest accounts");
		}
		if (disabled)
		{
			try
			{
				WebPatcher.TurnOff(_paths, false);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "MKI Guest Pass is being disabled but could not take its script out of the web client");
			}
		}
	}

	private async Task Loop(CancellationToken stop)
	{
		int n = 0;
		while (!stop.IsCancellationRequested)
		{
			bool first = n == 0;
			try
			{
				if (!first && BeingDisabled())
				{
					// Switched off in the dashboard: locked now, not only when Jellyfin stops, which a crash could skip.
					await _guests.PauseAll(true).ConfigureAwait(false);
					WebPatcher.TurnOff(_paths, false);
				}
				else
				{
					if (!first)
					{
						await _guests.Resume().ConfigureAwait(false);
						WebPatcher.TurnOn();
					}
					if (first || n % ThoroughEvery == 0)
					{
						await _guests.LockStrays().ConfigureAwait(false);
					}
					await _guests.Sweep(first, first || n % ThoroughEvery == 0).ConfigureAwait(false);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "MKI Guest Pass expiry check failed; it runs again in {Seconds} s", Tick.TotalSeconds);
			}
			n++;
			try
			{
				await Task.Delay(Wait(), stop).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}

	// Until the next check, or right after the next end of a pass so its guests are locked on time.
	private TimeSpan Wait()
	{
		var next = _guests.NextEnd();
		if (next == null)
		{
			return Tick;
		}
		var left = next.Value - DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
		return left >= Tick ? Tick : left < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : left;
	}

	private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
	{
		var session = e.Session;
		if (session == null || session.UserId == Guid.Empty || _guests.Find(session.UserId) == null)
		{
			return;
		}
		_ = Task.Run(async () =>
		{
			try
			{
				await _guests.CheckStreams(session, session.UserId).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Could not check the streams of a guest");
			}
		});
	}

	private void OnUninstalling()
	{
		Task.Run(() => _guests.DeleteAll()).Wait(TimeSpan.FromSeconds(60));
	}

	// This copy is disabled and no other copy of the plugin will run after the restart.
	private bool BeingDisabled()
	{
		try
		{
			var copies = _plugins.Plugins.Where(p => p.Id == Guid.Parse(Plugin.PluginId)).ToList();
			if (copies.Count == 0)
			{
				return false;
			}
			bool anyWillRun = copies.Any(p => p.Manifest.Status == PluginStatus.Active || p.Manifest.Status == PluginStatus.Restart);
			bool disabled = copies.Any(p => p.Manifest.Status == PluginStatus.Disabled);
			return disabled && !anyWillRun;
		}
		catch (Exception)
		{
			return false;
		}
	}
}
