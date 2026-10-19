using System;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MkiPasswordPolicy.Configuration;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Services;

// Who is covered by which rules. A fresh install covers nobody: everything stays off until the admin assigns a profile.
public static class Policy
{
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
		if (Enforcer.IsExempt(user))
		{
			return null;
		}
		return PasswordRules.Profile(EffectiveProfile(user.Id, c), c);
	}

	// True for an account of Jellyfin's own password provider that has no password at all (Jellyfin stores no hash then).
	// Accounts of other providers (LDAP and the like) have no local hash either and are left alone.
	public static bool HasNoPassword(User user)
	{
		return string.IsNullOrEmpty(user.Password)
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
