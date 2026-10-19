using System;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Services;

public class UserState
{
	public Guid UserId { get; set; }

	public bool MustChange { get; set; }

	// admin, expired or new
	public string? Reason { get; set; }

	public DateTime? RequiredSinceUtc { get; set; }

	// Existing sessions keep working until then, so open apps can show the notice first.
	public DateTime? EnforceAfterUtc { get; set; }

	// Notice period: until EnforceAfterUtc the user keeps full access, including new sign-ins,
	// and every app they open gets a reminder with the deadline and ChangeUrl.
	public bool Notice { get; set; }

	public string? ChangeUrl { get; set; }

	public DateTime? LastChangedUtc { get; set; }

	// The rules the current password was checked against when it was set. Null means it was never checked:
	// the plugin never sees or keeps a password apart from that one check, so it cannot judge it afterwards.
	public RuleSnapshot? VerifiedRules { get; set; }

	// Set when the plugin saw the password being set to empty (new user without one, or a reset).
	public bool KnownEmpty { get; set; }
}

public class RuleSnapshot
{
	public int MinLength { get; set; }

	public bool RequireUpper { get; set; }

	public bool RequireLower { get; set; }

	public bool RequireDigit { get; set; }

	public bool RequireSymbol { get; set; }

	public bool ForbidUsername { get; set; }

	public bool BlockCommon { get; set; }

	public int MaxRepeat { get; set; }
}
