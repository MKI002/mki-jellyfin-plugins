using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Services;

// Checks once a minute for apps of users in a notice period that have not seen the reminder yet.
// Does nothing while no password request is open.
public class Reminder : IHostedService, IDisposable
{
	private readonly Enforcer _enforcer;

	private readonly ILogger<Reminder> _logger;

	private Timer? _uhr;

	public Reminder(Enforcer enforcer, ILogger<Reminder> logger)
	{
		_enforcer = enforcer;
		_logger = logger;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_uhr = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_uhr?.Change(Timeout.Infinite, Timeout.Infinite);
		return Task.CompletedTask;
	}

	public void Dispose()
	{
		_uhr?.Dispose();
		GC.SuppressFinalize(this);
	}

	private void Tick()
	{
		try
		{
			_enforcer.Remind();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Password reminders failed");
		}
	}
}
