<img src="logo.png?v=1.0.0" alt="MKI Password Policy" width="100%">

# MKI Password Policy

Password rules and forced password changes for Jellyfin users, enforced on the server so they hold for every client: browser, phone, TV and every app.

## Does nothing until you say so

A fresh install changes nothing about sign-ins or passwords. No rules apply, no password is checked and nothing is recorded. The plugin only acts for users you give a profile to, or when you ask someone for a new password. The one thing you will see right away is a small MKI Password Policy box on each user's **Password** tab in the dashboard, for administrators only, with a shortcut to that user in the plugin settings.

## Features

- **Profiles.** Basic, Standard, Strong, Very strong, Passphrase or your own rules (length, upper and lower case, digits, symbols, no username, no common passwords, no long runs of one character). Pick one for all users, or a different one per user.
- **Require a new password** from one user, from everyone, or from everyone whose password is not known to meet their profile, either right away or with a notice period of 1 to 14 days. Right away: open apps get a notice, then every device is signed out. With a notice period: users keep full access until the deadline, and every app they use shows a message with the deadline and the change link, at most once a day per device; the settings page lists which devices were reminded. Once the deadline passes (or right away), the user cannot sign in anywhere until they have set a new password, and any token they still hold is refused.
- **Change dialog in the web client.** Signing in opens a dialog for the new password with the profile's rules shown live; afterwards the user lands in Jellyfin as usual. This also covers the Jellyfin apps for Android, iOS and desktop, which use the web client.
- **Change page for TVs and native apps.** Once access is blocked, those only show "sign-in failed", so use a notice period to reach them first. The user opens the change page (`/MkiPasswordPolicy/Change`, the link is on the settings page) on any phone or computer, sets the password there and signs in normally. **Copy message for users** on the settings page gives you a ready text with the link and deadline to send them.
- **User review.** For each user: their profile, whether a change is pending, and what is known about the password: *meets the rules*, *older rules*, *no password*, *unknown* or *no rules*. Disabled accounts can get profiles and requests too; they apply once the account is enabled again. With *exempt administrators* on, administrators are greyed out.
- **Shortcut on the user page.** Each user's **Password** tab in the dashboard shows their profile, password status and any pending request, with a button that opens their row in the plugin settings.
- **Optional:** password expiry, new users must set their own password, rules for passwords an admin sets, exempt administrators, live hints on the profile and new-user pages.

## How passwords are handled

- The plugin never stores, logs or re-reads a password. Jellyfin keeps only its own hash.
- A password is checked in exactly one moment: when it is being set. The plugin then remembers which rules it passed, never the password.
- Passwords set before a profile applied cannot be judged, and the plugin does not try: the review shows them as *unknown*. Require a new password if you want certainty.
- Jellyfin's own **Reset password** button on a user's Password tab removes the password completely, so the account can be signed in to without one. The review then shows *no password*.

## Security notes

- Rules are enforced on the server, before Jellyfin saves the password. The web hints are only a convenience.
- Request bodies are read the way Jellyfin reads them (case-insensitive keys, last duplicate wins, numbers as text). A body the plugin cannot read is refused rather than passed on unchecked.
- The anonymous change endpoints answer every failure the same way, only check passwords of accounts with an open request, and lock a client address out for 15 minutes after 10 failures, on top of Jellyfin's own lockout.
- While a change is pending, every request with that user's token is refused, whichever endpoint it targets.
- The change page sends a strict Content Security Policy and cannot be framed.

## Setup

1. Install from the plugin catalog and restart Jellyfin.
2. Open **Dashboard → Plugins → MKI Password Policy**.
3. Pick a profile for all users or for single users and save. To replace existing passwords, use **Require new password** for one user or one of the buttons for many.
