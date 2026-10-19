using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
	// Nothing applies until the admin assigns a profile: "none", a preset name or "custom".
	public string DefaultProfile { get; set; } = "none";

	// Per-user choice; "default" (or no entry) follows DefaultProfile.
	public List<UserProfileEntry> UserProfiles { get; set; } = new();

	// The "custom" profile.
	public int MinLength { get; set; } = 10;

	public bool RequireUpper { get; set; } = true;

	public bool RequireLower { get; set; } = true;

	public bool RequireDigit { get; set; } = true;

	public bool RequireSymbol { get; set; }

	public bool ForbidUsername { get; set; } = true;

	public bool BlockCommon { get; set; } = true;

	// Longest allowed run of the same character, 0 = no limit.
	public int MaxRepeat { get; set; }

	public bool ExemptAdministrators { get; set; }

	public bool ApplyToAdminChanges { get; set; } = true;

	// Only for users with a profile.

	public int ExpiryDays { get; set; }

	public bool RequireChangeForNewUsers { get; set; }

	public bool NotifySessions { get; set; } = true;

	public int GraceSeconds { get; set; } = 20;

	// Last notice period picked on the settings page: 0 = sign out right away, otherwise days of continued access with reminders.
	public int NoticeDays { get; set; }

	public string NotifyText { get; set; } = string.Empty;

	public string PublicUrl { get; set; } = string.Empty;

	public bool WebHints { get; set; } = true;
}

public class UserProfileEntry
{
	public string UserId { get; set; } = string.Empty;

	public string Profile { get; set; } = "default";
}
