using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Services;

// Once a minute and whenever the settings are saved: blocks accounts without a password that have rules or a request,
// signs out users whose deadline has passed, and reminds apps of users in a notice period. Does nothing while the
// plugin is switched off.
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
		Plugin.ConfigSaved += Tick;
		_uhr = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		Plugin.ConfigSaved -= Tick;
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
			// Profiles may just have been given to accounts without a password: those are blocked right away.
			_enforcer.SweepNoPassword();
			_enforcer.SignOutBlocked().GetAwaiter().GetResult();
			_enforcer.Remind();
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Password reminders failed");
		}
	}
}
