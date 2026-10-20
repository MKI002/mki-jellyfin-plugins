using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MkiContinueWatching.Services;

// Runs daily. Changes nothing while auto cleanup is off in the plugin settings.
public class CleanupTask : IScheduledTask
{
	private readonly IServiceProvider _services;

	public CleanupTask(IServiceProvider services)
	{
		_services = services;
	}

	public string Name => "Clean up Continue Watching (MKI)";

	public string Key => "MkiContinueWatchingCleanup";

	public string Description => "Removes videos nobody has played for the set number of days from each user's Continue Watching. Only the resume position is cleared; nothing is deleted. Does nothing while auto cleanup is off.";

	public string Category => "Library";

	public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
	{
		return new[]
		{
			new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks }
		};
	}

	public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
	{
		// A newer copy of this plugin is loaded (DuplicateGuard): it registered the service, not this one.
		if (DuplicateGuard.IsSuperseded)
		{
			return Task.CompletedTask;
		}
		_services.GetService<ResumeService>()?.RunCleanup("schedule", cancellationToken, progress);
		return Task.CompletedTask;
	}
}
