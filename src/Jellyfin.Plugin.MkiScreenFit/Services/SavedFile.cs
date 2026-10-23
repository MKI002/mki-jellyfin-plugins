using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiScreenFit.Services;

// A small JSON file in the plugin's own folder, written a few seconds after a change through a temporary file.
// A steady stream of changes never postpones the write, nothing is written while nothing changed, and nothing
// at all once the plugin is uninstalled.
public sealed class SavedFile : IDisposable
{
	private static readonly TimeSpan Delay = TimeSpan.FromSeconds(5);

	private readonly object _lock = new();

	private readonly object _writeLock = new();

	private readonly string _file;

	private readonly Func<object> _snapshot;

	private readonly ILogger _logger;

	private readonly Timer _timer;

	private bool _pending;

	public SavedFile(string file, Func<object> snapshot, ILogger logger)
	{
		_file = file;
		_snapshot = snapshot;
		_logger = logger;
		_timer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
	}

	public T? Load<T>()
		where T : class
	{
		try
		{
			return File.Exists(_file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(_file)) : null;
		}
		catch (Exception ex)
		{
			// Only measurements and hints: they come back with the next reports.
			_logger.LogWarning(ex, "MKI Screen Fit could not read {File}; it starts empty", Path.GetFileName(_file));
			return null;
		}
	}

	public void Changed()
	{
		lock (_lock)
		{
			if (_pending)
			{
				return;
			}
			_pending = true;
			_timer.Change(Delay, Timeout.InfiniteTimeSpan);
		}
	}

	public void Save()
	{
		lock (_lock)
		{
			if (!_pending)
			{
				return;
			}
			_pending = false;
		}
		if (Plugin.Uninstalled)
		{
			return;
		}
		lock (_writeLock)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
				string tmp = _file + ".tmp";
				File.WriteAllText(tmp, JsonSerializer.Serialize(_snapshot()));
				File.Move(tmp, _file, overwrite: true);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "MKI Screen Fit could not save {File}", Path.GetFileName(_file));
			}
		}
	}

	public void Dispose()
	{
		_timer.Dispose();
		Save();
	}
}
