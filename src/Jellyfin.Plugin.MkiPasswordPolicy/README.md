<img src="logo.png?v=1.0.0" alt="MKI Password Policy" width="100%">

# MKI Password Policy

Password rules and forced password changes for Jellyfin users, enforced on the server so they hold for every client: browser, phone, TV and every app.

## Does nothing until you say so

A fresh install changes nothing. No rules apply, no password is checked, nothing is recorded and the web client is left untouched. The plugin only acts for users you give a profile to, or when you ask someone for a new password.

## Features

- **Profiles.** Basic, Standard, Strong, Very strong, Passphrase or your own rules (length, upper and lower case, digits, symbols, no username, no common passwords, no long runs of one character). Pick one for all users, or a different one per user.
- **Require a new password** from one user, from everyone, or from everyone whose password is not known to meet their profile. Open apps get a notice, then every device is signed out. Until the user has set a new password, they cannot sign in anywhere and any token they still hold is refused.
- **Change dialog in the web client.** Signing in opens a dialog for the new password with the profile's rules shown live; afterwards the user lands in Jellyfin as usual. This also covers the Jellyfin apps for Android, iOS and desktop, which use the web client.
- **Change page for TVs and native apps.** Those only show "sign-in failed". The user opens the change page (`/MkiPasswordPolicy/Change`, the link is on the settings page) on any phone or computer, sets the password there and signs in normally.
- **User review.** For each user: their profile, whether a change is pending, and what is known about the password: *meets the rules*, *older rules*, *no password*, *unknown* or *no rules*.
- **Optional:** password expiry, new users must set their own password, rules for passwords an admin sets, exempt administrators, live hints on the profile and new-user pages.

## How passwords are handled

- The plugin never stores, logs or re-reads a password. Jellyfin keeps only its own hash.
- A password is checked in exactly one moment: when it is being set. The plugin then remembers which rules it passed, never the password.
- Passwords set before a profile applied cannot be judged, and the plugin does not try: the review shows them as *unknown*. Require a new password if you want certainty.

## Security notes

- Rules are enforced on the server, before Jellyfin saves the password. The web hints are only a convenience.
- Request bodies are read the way Jellyfin reads them (case-insensitive keys, last duplicate wins, numbers as text). A body the plugin cannot read is refused rather than passed on unchecked.
- The anonymous change endpoints answer every failure the same way, only check passwords of accounts with an open request, and lock a client address out for 15 minutes after 10 failures, on top of Jellyfin's own lockout.
- While a change is pending, every request with that user's token is refused, whichever endpoint it targets.
- The change page sends a strict Content Security Policy and cannot be framed.

## Setup

1. Install from the plugin catalog and restart Jellyfin.
2. Open **Dashboard → Plugins → MKI Password Policy**.
3. Pick a profile for all users or for single users and save. To replace existing passwords, use **Neues Passwort verlangen** for one user or one of the buttons for many.
