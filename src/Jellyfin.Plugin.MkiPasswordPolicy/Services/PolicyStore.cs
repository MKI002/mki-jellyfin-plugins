using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Services;

// Per-user state lives in its own file so saving the settings page never overwrites it.
public class PolicyStore
{
	private readonly object _sperre = new();

	private readonly ILogger<PolicyStore> _logger;

	private Dictionary<Guid, UserState>? _staende;

	// Users with an open change request; checked on every request, so kept as a cheap lookup.
	private volatile HashSet<Guid> _offen = new();

	public PolicyStore(ILogger<PolicyStore> logger)
	{
		_logger = logger;
	}

	private static string Datei => Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "users.json");

	public UserState Get(Guid userId)
	{
		lock (_sperre)
		{
			return Kopie(Laden().GetValueOrDefault(userId) ?? new UserState { UserId = userId });
		}
	}

	public bool MustChange(Guid userId)
	{
		EnsureLoaded();
		return _offen.Contains(userId);
	}

	public bool AnyMustChange()
	{
		EnsureLoaded();
		return _offen.Count > 0;
	}

	// True when existing sessions of the user must be refused now.
	public bool SessionsBlocked(Guid userId)
	{
		if (!MustChange(userId))
		{
			return false;
		}
		var s = Get(userId);
		return s.MustChange && (s.EnforceAfterUtc == null || s.EnforceAfterUtc <= DateTime.UtcNow);
	}

	public void Require(Guid userId, string reason, TimeSpan grace)
	{
		Aendern(userId, s =>
		{
			if (!s.MustChange)
			{
				s.RequiredSinceUtc = DateTime.UtcNow;
				s.EnforceAfterUtc = DateTime.UtcNow + grace;
			}
			s.MustChange = true;
			s.Reason = reason;
		});
	}

	public void Release(Guid userId)
	{
		Aendern(userId, s =>
		{
			s.MustChange = false;
			s.Reason = null;
			s.RequiredSinceUtc = null;
			s.EnforceAfterUtc = null;
		});
	}

	// Any password change: the new password is unchecked until a checked path says otherwise.
	public void MarkChanged(Guid userId)
	{
		Aendern(userId, s =>
		{
			s.LastChangedUtc = DateTime.UtcNow;
			s.VerifiedRules = null;
			s.KnownEmpty = false;
		});
	}

	public void RestartClock(Guid userId)
	{
		Aendern(userId, s => s.LastChangedUtc = DateTime.UtcNow);
	}

	public void MarkVerified(Guid userId, RuleSnapshot rules)
	{
		Aendern(userId, s => s.VerifiedRules = rules);
	}

	public void MarkEmpty(Guid userId)
	{
		Aendern(userId, s =>
		{
			s.VerifiedRules = null;
			s.KnownEmpty = true;
		});
	}



	// Users without a known change date count from the first time the plugin saw them, so expiry never hits everyone at once after install.
	public DateTime LastChangedOrBaseline(Guid userId)
	{
		lock (_sperre)
		{
			var s = Laden().GetValueOrDefault(userId);
			if (s?.LastChangedUtc is DateTime bekannt)
			{
				return bekannt;
			}
		}
		RestartClock(userId);
		return DateTime.UtcNow;
	}

	public void Remove(Guid userId)
	{
		lock (_sperre)
		{
			if (Laden().Remove(userId))
			{
				_offen = _staende!.Values.Where(x => x.MustChange).Select(x => x.UserId).ToHashSet();
				Speichern();
			}
		}
	}

	private void Aendern(Guid userId, Action<UserState> aenderung)
	{
		lock (_sperre)
		{
			var staende = Laden();
			if (!staende.TryGetValue(userId, out var s))
			{
				s = new UserState { UserId = userId };
				staende[userId] = s;
			}
			aenderung(s);
			_offen = staende.Values.Where(x => x.MustChange).Select(x => x.UserId).ToHashSet();
			Speichern();
		}
	}

	private void EnsureLoaded()
	{
		if (_staende == null)
		{
			lock (_sperre)
			{
				Laden();
			}
		}
	}

	private Dictionary<Guid, UserState> Laden()
	{
		if (_staende != null)
		{
			return _staende;
		}
		try
		{
			if (File.Exists(Datei))
			{
				var liste = JsonSerializer.Deserialize<List<UserState>>(File.ReadAllText(Datei)) ?? new List<UserState>();
				_staende = liste.ToDictionary(s => s.UserId);
				_offen = liste.Where(s => s.MustChange).Select(s => s.UserId).ToHashSet();
				return _staende;
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Could not read user states from {File} - starting empty", Datei);
		}
		_staende = new Dictionary<Guid, UserState>();
		return _staende;
	}

	private void Speichern()
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Datei)!);
			string tmp = Datei + ".tmp";
			File.WriteAllText(tmp, JsonSerializer.Serialize(_staende!.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
			File.Move(tmp, Datei, overwrite: true);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Could not save user states");
		}
	}

	private static UserState Kopie(UserState s) => new()
	{
		UserId = s.UserId,
		MustChange = s.MustChange,
		Reason = s.Reason,
		RequiredSinceUtc = s.RequiredSinceUtc,
		LastChangedUtc = s.LastChangedUtc,
		EnforceAfterUtc = s.EnforceAfterUtc,
		VerifiedRules = s.VerifiedRules,
		KnownEmpty = s.KnownEmpty
	};
}
