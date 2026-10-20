using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaHero.Configuration;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaHero.Services;

public class Vorwaermer : IHostedService, IDisposable
{
	private readonly HeroBuilder _builder;

	private readonly ILogger<Vorwaermer> _logger;

	private Timer? _uhr;

	public Vorwaermer(HeroBuilder builder, ILogger<Vorwaermer> logger)
	{
		_builder = builder;
		_logger = logger;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_uhr = new Timer(delegate
		{
			Takt();
		}, null, TimeSpan.FromSeconds(20L), TimeSpan.FromMinutes(1L));
		return Task.CompletedTask;
	}

	private void Takt()
	{
		try
		{
			PluginConfiguration pluginConfiguration = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration ?? new PluginConfiguration();
			if (pluginConfiguration.Enabled)
			{
				_builder.Vorwaermen();
			}
		}
		catch (Exception ex)
		{
			LoggerExtensions.LogWarning((ILogger)(object)_logger, ex, "Cache pre-warm skipped", Array.Empty<object>());
		}
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_uhr?.Change(-1, 0);
		return Task.CompletedTask;
	}

	public void Dispose()
	{
		_uhr?.Dispose();
		GC.SuppressFinalize(this);
	}
}
