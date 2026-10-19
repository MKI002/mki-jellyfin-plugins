using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiMergeVersions;

public class RuheWache : IHostedService, IDisposable
{
	private static readonly TimeSpan Ruhezeit = TimeSpan.FromMinutes(5L);

	private static readonly TimeSpan Takt = TimeSpan.FromMinutes(1L);

	private readonly ILibraryManager _library;

	private readonly ILogger<RuheWache> _log;

	private readonly SemaphoreSlim _schloss = new SemaphoreSlim(1, 1);

	private DateTime _letzteAenderung = DateTime.MinValue;

	private bool _schmutzig;

	private Timer? _timer;

	public RuheWache(ILibraryManager library, ILogger<RuheWache> log)
	{
		_library = library;
		_log = log;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_library.ItemAdded += Beruehrt;
		_library.ItemUpdated += Beruehrt;
		_library.ItemRemoved += Beruehrt;
		_timer = new Timer(delegate(object? _)
		{
			_ = PruefeAsync();
		}, null, Takt, Takt);
		LoggerExtensions.LogInformation((ILogger)(object)_log, "MKI Merge: Ruhewache aktiv - fuehrt zusammen, sobald {Minuten} Minuten lang nichts mehr an der Bibliothek passiert", new object[1] { Ruhezeit.TotalMinutes });
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_library.ItemAdded -= Beruehrt;
		_library.ItemUpdated -= Beruehrt;
		_library.ItemRemoved -= Beruehrt;
		_timer?.Change(-1, -1);
		return Task.CompletedTask;
	}

	private void Beruehrt(object? sender, ItemChangeEventArgs e)
	{
		if (e.Item is Movie)
		{
			_letzteAenderung = DateTime.UtcNow;
			_schmutzig = true;
		}
	}

	private async Task PruefeAsync()
	{
		if (!_schmutzig || DateTime.UtcNow - _letzteAenderung < Ruhezeit || !(await _schloss.WaitAsync(0).ConfigureAwait(continueOnCapturedContext: false)))
		{
			return;
		}
		try
		{
			_schmutzig = false;
			PluginConfiguration pluginConfiguration = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration ?? new PluginConfiguration();
			LoggerExtensions.LogInformation((ILogger)(object)_log, "MKI Merge: Bibliothek ist zur Ruhe gekommen, fuehre zusammen", Array.Empty<object>());
			await new MergeService(_library, (ILogger)(object)_log).RunAsync(pluginConfiguration.DryRun, pluginConfiguration.FixPrimaryOrder, null, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			LoggerExtensions.LogError((ILogger)(object)_log, ex, "MKI Merge: Lauf nach Ruhephase fehlgeschlagen", Array.Empty<object>());
		}
		finally
		{
			_schloss.Release();
		}
	}

	public void Dispose()
	{
		_timer?.Dispose();
		_schloss.Dispose();
		GC.SuppressFinalize(this);
	}
}
