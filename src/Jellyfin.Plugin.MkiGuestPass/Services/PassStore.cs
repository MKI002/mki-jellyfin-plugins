using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiGuestPass.Services;

public enum GuestState
{
	// Signed in or able to sign in again with the guest's own rejoin key.
	Active,

	// The pass ended: the account is disabled and waits for deletion. Extending the pass unlocks it again.
	Locked,

	// Ended on its own by an administrator: stays locked even if the pass is extended.
	Ended,

	// The plugin was disabled: locked until it runs again, then unlocked if the pass is still valid.
	Paused
}

public class GuestRecord
{
	public Guid UserId { get; set; }

	// The name the guest typed, as shown to them; the Jellyfin account is "Guest " + this name.
	public string Name { get; set; } = string.Empty;

	public string UserName { get; set; } = string.Empty;

	public DateTime JoinedUtc { get; set; }

	// Joined from outside the home network. The address itself is only written to Jellyfin's log.
	public bool Remote { get; set; }

	public GuestState State { get; set; }

	public DateTime? LockedUtc { get; set; }

	public bool Warned { get; set; }

	// HMAC of the key the guest's browser keeps to come back without using another place.
	public string RejoinHash { get; set; } = string.Empty;
}

public class PassRecord
{
	public Guid Id { get; set; }

	public string Name { get; set; } = string.Empty;

	public List<Guid> Libraries { get; set; } = new();

	public DateTime CreatedUtc { get; set; }

	public DateTime EndUtc { get; set; }

	// Set when an administrator ended the pass before its time.
	public DateTime? EndedEarlyUtc { get; set; }

	public int MaxGuests { get; set; }

	// Accounts created from this pass so far. A place stays used when its guest is deleted.
	public int Used { get; set; }

	public int DevicesPerGuest { get; set; }

	public int StreamsPerGuest { get; set; }

	public int? MaxRating { get; set; }

	public int? MaxSubRating { get; set; }

	public string? RatingName { get; set; }

	public bool RemoteAccess { get; set; }

	public Guid CreatedBy { get; set; }

	public string CreatedByName { get; set; } = string.Empty;

	public string LinkHash { get; set; } = string.Empty;

	public string? CodeHash { get; set; }

	public List<GuestRecord> Guests { get; set; } = new();

	public bool HasEnded(DateTime now) => EndUtc <= now;
}

// Created accounts that were not set up yet: cleaned up at the next start if Jellyfin stopped in between.
public class PendingAccount
{
	// The temporary name the account was created under.
	public string UserName { get; set; } = string.Empty;

	// Known as soon as Jellyfin made the account.
	public Guid? UserId { get; set; }

	public DateTime StartedUtc { get; set; }
}

public class StoreData
{
	public List<PassRecord> Passes { get; set; } = new();

	public List<PendingAccount> Pending { get; set; } = new();
}

// Passes and guests, in the plugin's data folder and not in the plugin configuration: the settings page writes the
// configuration as a whole, and must never be able to overwrite a guest or a pass with an older copy.
// Every change goes through Change(), under one lock, and is written to disk before it returns.
public class PassStore
{
	private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

	private readonly object _lock = new();

	private readonly ILogger<PassStore> _logger;

	private readonly string _file;

	private StoreData _data;

	public PassStore(IApplicationPaths paths, ILogger<PassStore> logger)
	{
		_logger = logger;
		string folder = Path.Combine(paths.PluginConfigurationsPath, "MkiGuestPass");
		Directory.CreateDirectory(folder);
		Folder = folder;
		_file = Path.Combine(folder, "passes.json");
		_data = Load();
	}

	public string Folder { get; }

	public event Action? Changed;

	// A deep copy: callers can never change the stored state by accident.
	public StoreData Snapshot()
	{
		lock (_lock)
		{
			return Copy(_data);
		}
	}

	public T Read<T>(Func<StoreData, T> read)
	{
		lock (_lock)
		{
			return read(_data);
		}
	}

	// The change runs on the live data; if saving fails, the data on disk and in memory stay as they were.
	public T Change<T>(Func<StoreData, T> change)
	{
		T result;
		lock (_lock)
		{
			var work = Copy(_data);
			result = change(work);
			Save(work);
			_data = work;
		}
		Changed?.Invoke();
		return result;
	}

	public void Change(Action<StoreData> change) => Change<bool>(d =>
	{
		change(d);
		return true;
	});

	private StoreData Load()
	{
		try
		{
			if (File.Exists(_file))
			{
				return JsonSerializer.Deserialize<StoreData>(File.ReadAllText(_file), Json) ?? new StoreData();
			}
		}
		catch (Exception ex)
		{
			// Unreadable: keep the file for inspection and start empty. Guest accounts of lost passes stay locked,
			// because they cannot sign in without a pass (their sign-in provider refuses every password).
			_logger.LogError(ex, "MKI Guest Pass could not read {File}; it was kept as passes.json.broken", _file);
			try
			{
				File.Copy(_file, _file + ".broken", overwrite: true);
			}
			catch (Exception)
			{
			}
		}
		return new StoreData();
	}

	private void Save(StoreData data)
	{
		string tmp = _file + ".tmp";
		File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json));
		File.Move(tmp, _file, overwrite: true);
	}

	private static StoreData Copy(StoreData d) => JsonSerializer.Deserialize<StoreData>(JsonSerializer.Serialize(d, Json), Json)!;
}

public static class StoreExtensions
{
	public static PassRecord? Pass(this StoreData d, Guid id) => d.Passes.FirstOrDefault(p => p.Id == id);

	public static IEnumerable<(PassRecord Pass, GuestRecord Guest)> AllGuests(this StoreData d)
		=> d.Passes.SelectMany(p => p.Guests.Select(g => (p, g)));

	public static (PassRecord Pass, GuestRecord Guest)? Guest(this StoreData d, Guid userId)
	{
		foreach (var p in d.Passes)
		{
			var g = p.Guests.FirstOrDefault(x => x.UserId == userId);
			if (g != null)
			{
				return (p, g);
			}
		}
		return null;
	}
}
