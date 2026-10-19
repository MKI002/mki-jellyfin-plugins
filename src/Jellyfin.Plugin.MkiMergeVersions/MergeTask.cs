using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiMergeVersions;

public class MergeTask : IScheduledTask
{
	private readonly ILibraryManager _library;

	private readonly ILogger<MergeTask> _log;

	public string Name => "Merge movie versions (MKI)";

	public string Key => "MkiMergeVersionsMovies";

	public string Description => "Links movies that exist in several resolutions into a single entry. The highest resolution becomes the primary entry.";

	public string Category => "Library";

	public MergeTask(ILibraryManager library, ILogger<MergeTask> log)
	{
		_library = library;
		_log = log;
	}

	public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
	{
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Expected O, but got Unknown
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Unknown result type (might be due to invalid IL or missing references)
		//IL_003d: Expected O, but got Unknown
		return (IEnumerable<TaskTriggerInfo>)(object)new TaskTriggerInfo[2]
		{
			new TaskTriggerInfo
			{
				Type = (TaskTriggerInfoType)3
			},
			new TaskTriggerInfo
			{
				Type = (TaskTriggerInfoType)2,
				IntervalTicks = TimeSpan.FromHours(1).Ticks
			}
		};
	}

	public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
	{
		if (DuplicateGuard.IsSuperseded)
		{
			return;
		}

		PluginConfiguration pluginConfiguration = ((BasePlugin<PluginConfiguration>)(object)Plugin.Instance)?.Configuration ?? new PluginConfiguration();
		await new MergeService(_library, (ILogger)(object)_log).RunAsync(pluginConfiguration.DryRun, pluginConfiguration.FixPrimaryOrder, progress, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}
}
