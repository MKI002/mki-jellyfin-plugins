using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MkiGuestPass.Configuration;

// Passes and guests are not kept here but in passes.json (see PassStore), so saving this page can never touch them.
// Nothing happens after installing: the plugin creates no account until an administrator creates a pass.
public class PluginConfiguration : BasePluginConfiguration
{
	// Days a locked guest account is kept, so a pass can still be extended, before it is deleted. 0 deletes at once.
	public int DeleteAfterDays { get; set; } = 4;

	// Guests get a message this many hours before their access ends. 0 sends none.
	public int WarnHours { get; set; } = 2;

	// A small "Guest · until ..." badge for guests in the web client, with Quick Connect for signing in a TV.
	public bool ShowGuestBadge { get; set; } = true;

	// A "Have a guest code?" link under Jellyfin's sign-in form while a pass with a typed code is open.
	public bool ShowCodeLink { get; set; } = true;

	// Jellyfin plays a video or song to anyone who knows its item id, without signing in. On, a stream needs a valid
	// sign-in, so a guest whose access ended cannot keep playing titles they saw. DLNA players cannot sign in.
	public bool StreamsNeedSignIn { get; set; } = true;

	// Shown to guests as "{name} invited you". Empty: "You're invited". Never filled in from an account name.
	public string InviterName { get; set; } = string.Empty;

	// Address used in pass links and QR codes, e.g. https://jellyfin.example.org. Empty: the address the settings
	// page is open on.
	public string PublicUrl { get; set; } = string.Empty;
}
