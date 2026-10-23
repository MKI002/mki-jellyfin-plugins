using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MkiPasswordPolicy.Configuration;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Services;

// Who is covered by which rules. A fresh install covers nobody: everything stays off until the admin assigns a profile.
public static class Policy
{
	// Sign-in provider of MKI Guest Pass accounts. It refuses every password, so a guest has none to check, change or
	// expire: guests are left out of everything here.
	public const string GuestProvider = "Jellyfin.Plugin.MkiGuestPass.Services.GuestSignIn";

	// Set by MKI Guest Pass while it creates an account ("name|ticks"): for those milliseconds the account still has
	// Jellyfin's default provider and no password, and must not be locked or flagged as a new user.
	private const string GuestCreatingKey = "MkiGuestPass.Creating";

	public static bool IsGuest(User user)
	{
		if (string.Equals(user.AuthenticationProviderId, GuestProvider, StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (AppContext.GetData(GuestCreatingKey) is not string s)
		{
			return false;
		}
		var teile = s.Split('|');
		return teile.Length == 2
			&& string.Equals(teile[0], user.Username, StringComparison.OrdinalIgnoreCase)
			&& long.TryParse(teile[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
			&& DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < TimeSpan.FromSeconds(30);
	}

	public static string AssignedProfile(Guid userId, PluginConfiguration c)
	{
		string id = userId.ToString("N");
		var eintrag = c.UserProfiles.FirstOrDefault(e => Guid.TryParse(e.UserId, out var g) && g.ToString("N") == id);
		return eintrag?.Profile ?? "default";
	}

	public static string EffectiveProfile(Guid userId, PluginConfiguration c)
	{
		string name = AssignedProfile(userId, c);
		return name == "default" ? c.DefaultProfile : name;
	}

	// null = no rules for this user.
	public static RuleSnapshot? RulesFor(User user)
	{
		var c = Plugin.Config;
		if (Enforcer.IsExempt(user) || IsGuest(user))
		{
			return null;
		}
		return PasswordRules.Profile(EffectiveProfile(user.Id, c), c);
	}

	// True for an account of Jellyfin's own password provider that has no password at all (Jellyfin stores no hash then).
	// Accounts of other providers (LDAP and the like) have no local hash either and are left alone.
	public static bool HasNoPassword(User user)
	{
		return !IsGuest(user)
			&& string.IsNullOrEmpty(user.Password)
			&& (string.IsNullOrEmpty(user.AuthenticationProviderId)
				|| user.AuthenticationProviderId.EndsWith("DefaultAuthenticationProvider", StringComparison.Ordinal));
	}

	public static RuleSnapshot? RulesForNewUser() => PasswordRules.Profile(Plugin.Config.DefaultProfile, Plugin.Config);

	// True once the admin has switched on anything at all.
	public static bool Configured(PluginConfiguration c)
	{
		return PasswordRules.Profile(c.DefaultProfile, c) != null
			|| c.UserProfiles.Any(e => e.Profile != "default" && e.Profile != "none")
			|| c.RequireChangeForNewUsers;
	}
}
