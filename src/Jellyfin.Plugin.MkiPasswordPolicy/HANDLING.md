# How MKI Password Policy handles every combination

Version 1.0.6.0. Rows marked ✓ were tested end to end on Jellyfin 12.2 (sign-ins, tokens and the web client).

## The five rules behind every case

1. **Nothing happens until you act in the plugin.** A fresh install has no rules, checks nothing and records nothing.
2. **A password is only checked at the moment it is set.** The plugin never stores, logs or re-reads one; Jellyfin keeps its hash.
3. **A password someone else knows is always replaced by the user**: one you set, a temporary password, a reset PIN. This applies to users with a profile or an open request.
4. **No password plus a profile or a request means locked**, right away, on every sign-in path, with no notice period. Nobody can unlock it by knowing the name; only a temporary password from you does.
5. **Nothing loosens by itself.** A second request can only bring a deadline forward. A request ends only when the user sets their own password or you cancel it.

"Profile" below means any profile other than *No rules*. Users with neither a profile nor an open request are left entirely to Jellyfin.

## Jellyfin's own buttons

| Starting point | Action in Jellyfin | What happens | |
|---|---|---|---|
| Profile, no request | **Reset password** (user's Password tab) | Locked at once, all devices signed out. Unlock with a temporary password. | ✓ |
| Notice period running | **Reset password** | The notice period ends; locked at once. | ✓ |
| Request pending or locked | **Reset password** | Stays locked. | ✓ |
| No profile, no request | **Reset password** | Nothing (Jellyfin lets anyone sign in without a password). The review shows *No password* in red. | ✓ |
| Profile, no request | You **set a password** for the user | It counts as temporary: devices signed out, the user replaces it at the next sign-in. | ✓ |
| Locked, no password | You **set a password** for the user | Becomes their temporary password: they sign in with it and choose their own. | ✓ |
| Request pending, no profile | You **set a password** for the user | The request stays open; they replace it at the next sign-in. | |
| No profile, no request | You **set a password** for the user | Nothing. | |
| Any | You change **your own** password | Checked against your profile (unless administrators are exempt). No request. | |
| Profile, no request | User changes **their own** password on their profile page | Checked against the profile; a weak one is refused with the reason. | ✓ |
| Notice period running | User changes **their own** password on their profile page | Checked; a different password completes the request. | ✓ |
| Blocked (deadline passed, or right away) | User wants to change it on the profile page | Not reachable: every request with their token is refused. They use the dialog at sign-in or the change page. | ✓ |
| Profile or request | **Forgot password** with the PIN | The PIN becomes the password and counts as temporary: replaced at the next sign-in. | ✓ |
| Profile for all users set | **New user** with a password | Must set their own at the first sign-in. | ✓ |
| Profile for all users set | **New user** without a password | Locked until you give a temporary password. | ✓ |
| No profile, *New users must set their own password* on | **New user** | Must set their own at the first sign-in; without a password, locked. | |
| Any | **Disable / enable** the user | Rules and requests stay and apply once the account is enabled. | |
| Any | **Delete** the user | The plugin forgets the user. | |

## The plugin's buttons

| Starting point | Action in the plugin | What happens | |
|---|---|---|---|
| User has a password | Give a **profile** (per user or for all) | Nothing else: nobody is signed out. The review shows *Unknown* until they set a password. | ✓ |
| User has no password | Give a **profile** | Asks first, then locked at once on every app, TV and Quick Connect. | ✓ |
| Request pending | Make the profile **stricter** | The new password must meet the new rules. | ✓ |
| Passwords already checked | Make the profile **stricter** | They show *Older rules*. Nobody is signed out; use the bulk button to ask them. | |
| Request pending or locked | Make the profile **looser** or *No rules* | The request stays until it is done or cancelled. | ✓ |
| Has a password | **Require new password**, right away | Open apps get a message, all devices are signed out after the grace time, no sign-in until a new one is set. | ✓ |
| Has a password | **Require new password** with 1 to 14 days | Full access until the deadline; popup or message in every app; then blocked. | ✓ |
| Notice period running | Deadline passes | Every token is refused at once and revoked within a minute; sign-ins fail until a new password is set. | ✓ |
| No password | **Require new password** (any notice) | Locked at once; no notice period. | ✓ |
| Notice period running | Require again **right away** | The shorter deadline wins: blocked now. | ✓ |
| Blocked | Require again with **14 days** | Stays blocked: a deadline never moves back. | ✓ |
| Any | **All users** / **unchecked passwords** | Never includes you; other administrators only when ticked; disabled accounts included. | ✓ |
| Your own row | **Require new password** | Allowed with a warning: you are signed out and set it at the next sign-in. | |
| Request pending | **Cancel request** | They can sign in with their current password again. | ✓ |
| Locked, no password, has a profile | **Cancel request** | Stays locked (it still has no password). Use a temporary password. | ✓ |
| Locked, no password, no profile | **Cancel request** | Unlocked, like Jellyfin without the plugin. | ✓ |
| Any other user | **Temporary password** | Random password meeting their profile, shown to you once with a message to send; all devices signed out; they must replace it at the next sign-in, and may not reuse it. | ✓ |
| Your own account | **Temporary password** | Not offered and refused by the server. | ✓ |
| Administrators | **Exempt administrators** on | No profile and no expiry for them; an explicit request still applies. | |
| Profile | **Password expires after** N days | At the next sign-in after N days they must set a new one. | |

## Sign-in paths while a user is blocked or locked

| Path | Result | |
|---|---|---|
| Browser, Jellyfin app for Android, iOS, desktop | Dialog: set the new password and continue; for an account without a password, "Account locked, ask your administrator for a temporary password". | ✓ |
| TV and other native apps | "Sign-in failed". The user sets the password at the change link on a phone or computer. | |
| Quick Connect | Refused; a blocked account also cannot approve a Quick Connect code. | ✓ |
| Sessions already signed in | Signed out, and any further request with the old token is refused, however the token is sent. | ✓ |
| Any other sign-in that gets through | Undone right away: the new token is revoked. | |
| Change page (link) | Needs the current password. An empty password is never accepted, so an account without a password cannot be claimed there. | ✓ |
| New password set (dialog or change page) | Every other device is signed out. | |
| Your API keys | Unaffected. | |

## What users see during a notice period

- **Browser and the Jellyfin apps for Android, iOS and desktop:** a popup they must answer, with *Set new password now* or *Later*, each time they open the app. ✓
- **TVs and other apps:** Jellyfin's own message window with the deadline and the link, which stays until closed. It is sent at most once a day per device, and only to apps used since the last one. Whether a particular TV app shows server messages depends on that app; it could not be tested here.
- **After the deadline:** the dialog at sign-in (web-based apps) or "sign-in failed" (TVs).

## Limits

- TV apps cannot show the change dialog. Once a user is blocked they only show "sign-in failed", so use a notice period or the **Copy message for users** button to reach those users first.
- While the plugin is disabled or uninstalled nothing is enforced. Reinstalling brings open requests back.
- Accounts of other sign-in providers (LDAP and the like) have no local password and are never treated as *no password*.
- Guest accounts of [MKI Guest Pass](../Jellyfin.Plugin.MkiGuestPass) are left out of everything: no profile, no request, no lock for having no password, no temporary password and no entry in the user list. They have no password by design, cannot sign in with one, and end by themselves. This holds from version 1.1.1.0, also for the moment such an account is being created.
