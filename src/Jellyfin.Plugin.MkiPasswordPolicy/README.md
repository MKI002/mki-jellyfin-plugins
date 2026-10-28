<img src="logo.png?v=1.0.0" alt="MKI Password Policy" width="100%">

# MKI Password Policy

Password rules and forced password changes for Jellyfin users, enforced on the server so they hold for every client: browser, phone, TV and every app.

## Does nothing until you say so

A fresh install changes nothing about sign-ins or passwords. No rules apply, no password is checked and nothing is recorded. The plugin only acts for users you give a profile to, or when you ask someone for a new password. The one thing you will see right away is a small MKI Password Policy box on each user's **Password** tab in the dashboard, for administrators only, with a shortcut to that user in the plugin settings.

## Features

- **Profiles.** Basic, Standard, Strong, Very strong, Passphrase or your own rules (length, upper and lower case, digits, symbols, no username, no common passwords, no long runs of one character). Pick one for all users, or a different one per user.
- **Require a new password** from one user, from everyone, or from everyone whose password is not known to meet their profile, either right away or with a notice period of 1 to 14 days. Right away: open apps get a notice, then every device is signed out. With a notice period: users keep full access until the deadline, and every app they use shows a message with the deadline and the change link, at most once a day per device; the settings page lists which devices were reminded. Once the deadline passes (or right away), the user cannot sign in anywhere until they have set a new password, and any token they still hold is refused.
- **Change dialog in the web client.** Signing in opens a dialog for the new password with the profile's rules shown live; afterwards the user lands in Jellyfin as usual. This also covers the Jellyfin apps for Android, iOS and desktop, which use the web client.
- **Popup during a notice period.** In the web client and the Jellyfin apps for Android, iOS and desktop, users get a popup they have to answer (*Set new password now* or *Later*) every time they open the app. TVs and other apps get Jellyfin's own message window, which stays until it is closed.
- **Accounts without a password are locked.** An account without any password that has a profile or an open request is locked right away on every app, TV and Quick Connect, with no notice period. Nobody can unlock it by knowing the name: you give it a **temporary password**, the user signs in with it and chooses their own.
- **Temporary password.** One click creates a random password that meets the user's profile, shows it to you once (with a ready message to send) and signs the user out everywhere. The user must replace it at the next sign-in. Use it for forgotten passwords and locked accounts.
- **Change page for TVs and native apps.** Once access is blocked, those only show "sign-in failed", so use a notice period to reach them first. The user opens the change page (`/MkiPasswordPolicy/Change`, the link is on the settings page) on any phone or computer, sets the password there and signs in normally. **Copy message for users** on the settings page gives you a ready text with the link and deadline to send them.
- **User review.** For each user: their profile, whether a change is pending, and what is known about the password: *meets the rules*, *older rules*, *no password*, *unknown* or *no rules*. Disabled accounts can get profiles and requests too; they apply once the account is enabled again. With *exempt administrators* on, administrators are greyed out.
- **Shortcut on the user page.** Each user's **Password** tab in the dashboard shows their profile, password status and any pending request, with a button that opens their row in the plugin settings.
- **Optional:** password expiry, new users must set their own password, rules for passwords an admin sets, exempt administrators, live hints on the profile and new-user pages.
- **No Save button.** Every change on the settings page takes effect immediately; anything that signs people out or locks accounts asks first.

## Jellyfin's own password buttons

For users with a profile or an open request (users without either are left to Jellyfin):

| What happens | Result |
|---|---|
| **Reset password** on the user's Password tab | The account has no password and is locked right away until you give it a password. |
| You **set a password** for the user on their Password tab, or create the user with one | That password is temporary: the user has to replace it with their own at the next sign-in. Open requests stay open. |
| The user changes **their own password** on their profile page | Checked against their profile; it completes an open request during a notice period. |
| **Forgot password** with the PIN | The PIN becomes the password, so it is temporary too: the user replaces it at the next sign-in. |

A second request can only bring a deadline forward, never push it back. Bulk requests never include the administrator who sends them. The full list of combinations is in [HANDLING.md](HANDLING.md).

## How passwords are handled

- The plugin never stores, logs or re-reads a password. Jellyfin keeps only its own hash.
- A password is checked in exactly one moment: when it is being set. The plugin then remembers which rules it passed, never the password.
- Passwords set before a profile applied cannot be judged, and the plugin does not try: the review shows them as *unknown*. Require a new password if you want certainty.
- Jellyfin's own **Reset password** button on a user's Password tab removes the password completely. With a profile or an open request the account is then locked until you give it a password; without, Jellyfin lets anyone sign in to it without a password, and the review shows *no password*.
- Temporary passwords are generated on the server, shown to the administrator once and never kept by the plugin.
- Guest accounts of MKI Guest Pass (a plugin that is no longer offered here) are left alone: they have no password by design and cannot sign in with one, so they get no profile, no request and no lock, and do not appear in the plugin's user list.

## Security notes

- Rules are enforced on the server, before Jellyfin saves the password. The web hints are only a convenience.
- Request bodies are read the way Jellyfin reads them (case-insensitive keys, last duplicate wins, numbers as text). A body the plugin cannot read is refused rather than passed on unchecked.
- The anonymous change endpoints answer every failure the same way, only check passwords of accounts with an open request, and lock a client address out for 15 minutes after 10 failures, on top of Jellyfin's own lockout.
- Once access is blocked, every request carrying that user's token is refused, whichever endpoint it targets and however the token is sent (any authorization header, the older header names or the query string). A request whose tokens cannot be matched to exactly one user is refused too.
- When a deadline passes, the user's tokens are revoked within a minute, and a sign-in that gets through on any path is undone right away.
- Setting a new password signs the account out on every other device, so a token someone else may hold stops working.
- The change page sends a strict Content Security Policy and cannot be framed.

## Setup

1. Install from the plugin catalog and restart Jellyfin.
2. Open **Dashboard → Plugins → MKI Password Policy**.
3. Pick a profile for all users or for single users; it applies at once. To replace existing passwords, use **Require new password** for one user or one of the buttons for many.
