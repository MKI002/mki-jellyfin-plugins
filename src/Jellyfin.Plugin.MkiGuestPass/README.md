<img src="logo.png?v=1.0.0" alt="MKI Guest Pass" width="100%">

# MKI Guest Pass

Friends are over for the weekend and want to watch something on their own phone or the guest room TV. Giving them your account shows them everything you have and everything you watched; making them an account means a password to pass on, settings to get right and an account to remember to delete. A guest pass does all of that by itself: you send a link, they type a first name, and on Sunday night the account locks itself.

## Features

- **A pass in four choices.** Pick the libraries, how long it lasts (3 hours, a day, until Sunday, a week or a time you set, up to 30 days), how many people can use it (up to 10, each gets their own account), and how many devices and streams each guest gets. Optionally a highest rating, and watching from outside the home network (off unless you turn it on, and changeable on the pass later with **Where**).
- **A link, a QR code and, if you want, a code.** The pass is shown once with a QR code drawn on the page, the link and, when you ask for one, an 8-character code to read out, plus a ready-made invitation to paste into a message or share from your phone.
- **Joining takes a minute.** The guest opens the link, types a first name and taps **Start watching**. The plugin creates a hidden account called "Guest Name" and Jellyfin opens right there, already signed in: no password, nothing to install, no other address to open. On the same browser they can come back later without using another place on the pass, also after signing out: the sign-in page offers **Continue as Name** and a button to make the browser forget them. On a shared or public computer they tick **Shared or public computer**: Jellyfin then asks for a sign-in again once that window is closed, and signing out there makes the browser forget them at once.
- **Never stuck on the join page.** When a guest cannot join, the page says why and what to do next, with **Try again** where waiting or reconnecting helps. On a pass for the home network only, opened from elsewhere, it tells them to join your Wi-Fi or to ask you to allow the pass from anywhere; you do that with **Where** on the pass, and the same link then works.
- **TVs with Quick Connect.** The guest opens Jellyfin on the TV, chooses Quick Connect and types the TV's code into **Guest → Sign in a TV** on their phone. The TV counts as one of their devices.
- **Ends by itself.** Guests see a small badge with their end time, which turns orange shortly before the end. At the end, playback stops, every device is signed out and the account is locked. A few days later (you choose) the account and its watch history are deleted. Until then, **Open again** with a new end lets the same guests back in.
- **Ending one guest.** **End** signs one guest out and locks them. While the pass has places left, the dialog offers **End and make a new link** in one go, so they cannot join again with the old link.
- **Everything at hand.** The settings page lists every pass with a timeline of when it locks and when it is deleted, its guests, their devices and what they are watching, with **New link**, **Change end**, **Where**, **End now** and **Delete**, and **End** or **Delete** for single guests. When the browser you create a pass in counts as away from home, the page says so, since a link for the home network only would not open there.

## What guests can and can't do

| Guests can | Guests can't |
|---|---|
| Watch the libraries of their pass, up to the rating you set | See your other users, other libraries or any admin page |
| Keep their own Continue Watching and favourites until the end | Download, delete, edit or share anything, make playlists, or use Live TV and SyncPlay |
| Sign in a TV with Quick Connect, within their device limit | Request titles through Seerr or start library scans |
| Watch from outside your home network, if the pass allows it | Sign in with a password: guest accounts have none |
| | Read your server's settings, folders or network setup |
| | Keep watching after the end, on any device |
| | Play titles outside their pass, even with a direct address |

## Security

A guest pass opens your server to people without an account, so the plugin is strict by design:

- **Unguessable, revocable links.** A link carries 256 random bits after the `#`, which browsers never send to a server, not even as a referrer. Codes have 8 characters from an alphabet without look-alikes. The plugin stores only a keyed fingerprint (HMAC) of each, with a key of its own, so its files cannot be used to join. **New link** replaces the link and the code at once.
- **Rate limits.** Every address may try 5 wrong codes and a limited number of links in 15 minutes; after 100 wrong codes on the whole server, code entry pauses for 15 minutes while links keep working.
- **Reverse proxies.** Add your proxy under **Dashboard → Networking → Known proxies**, so Jellyfin sees each visitor's own address, and let it add that address to `X-Forwarded-For`. Until then every visitor seems to come from the proxy, which is usually on your network: the plugin notices the proxy's headers, refuses home-only passes for those visitors (joining and watching) and says so on the settings page, instead of treating the whole internet as home. It does the same for a known proxy that sends no `X-Forwarded-For` Jellyfin can use, or an `X-Real-IP` that disagrees with the address Jellyfin takes from it, which happens when the proxy passes on what the visitor sent. Two setups cannot be told apart from a visitor at home, so behind them **Home network only** does not keep anyone out:
  - a proxy that sends no forwarding headers at all and is not listed under Known proxies by its own address;
  - a known proxy that passes on the visitor's own `X-Forwarded-For` without setting `X-Real-IP`. The form for a new pass reminds you of this when links use an outside address.
- **Minimal accounts.** Guest accounts are never administrators, hidden from the sign-in list, limited to the pass's libraries and to their device and stream counts, and have every management right turned off. They use a sign-in provider of their own that refuses every password, so a guest account cannot be taken over by guessing one, and only the plugin can sign it in.
- **Requests a guest may not make are refused before Jellyfin sees them.** Jellyfin lets every signed-in user do a few things a stranger should not, so for guests the plugin narrows them: the user list shows only their own account, the server information and settings are cut down to what apps need (no folder paths, network settings or TV guide sign-ins), and library scans, metadata and image searches, file downloads, client log uploads, profile pictures, playlists, SyncPlay groups, Seerr Bridge and other users' details are refused. Videos, songs and subtitles play only for titles of the pass, also when asked for by their address (Jellyfin itself does not check this for some kinds of stream). Quick Connect is refused once their access ends or their device limit is reached. Guest accounts can only be signed in by a guest link or Quick Connect; any other sign-in to one is undone.
- **Streams need a sign-in.** Jellyfin plays a video or song to anyone who knows its address, without signing in. With **Streams need a sign-in** (on by default), a stream needs a signed-in account or an API key, so a guest whose pass ended cannot keep playing titles they saw. Jellyfin's own apps sign in anyway; DLNA players cannot, so the settings page warns when the DLNA plugin is installed.
- **A hard end.** At the end, running streams stop, all devices are signed out (tokens revoked) and the account is disabled. Guest accounts are also locked whenever Jellyfin stops and unlocked when the plugin runs again, so a Jellyfin that no longer loads the plugin (after an update, say) leaves no guest usable. Turning the plugin off signs every guest out and locks them until it is turned on again; uninstalling it deletes every guest account. Accounts on the guest sign-in provider that no pass knows are locked.
- **Small public surface.** Without signing in, only the join page and its three requests answer: they take small JSON bodies only, are never cached, and the page loads no outside code and cannot be framed.

## Setup

1. Install from the plugin catalog and restart Jellyfin.
2. Open **Dashboard → Plugins → MKI Guest Pass → Settings** and, if guests reach your server under another address than the one you use (for example through a reverse proxy), enter it under **Address for links**. Behind a reverse proxy, also add it under **Dashboard → Networking → Known proxies**.
3. For TVs, turn on Quick Connect under **Dashboard → General**.
4. Create a pass under **New pass** and share the link, the QR code or the code.

## Notes

- Works with **MKI Password Policy 1.1.1.0** or later, which leaves guest accounts alone. Older versions do not know them and sign a guest out right after joining, so while one is installed nobody can join: the join page asks the guest to let you know, and the settings page tells you to update it.
- The web client script (badge and the "Have a guest code?" link on the sign-in page) is written to Jellyfin's `index.html` only while guest accounts exist or a pass with a code is open, and taken out again afterwards and when the plugin is uninstalled.
- Passes are kept in the plugin's configuration folder (`MkiGuestPass/passes.json`), with fingerprints instead of links and codes. Ended passes without guests are dropped a day after their end.
- Each join, end and deletion is written to Jellyfin's activity log.
- The device limit is firm: a guest cannot sign in on more devices. The stream limit stops the newest stream in apps that follow Jellyfin's remote commands (the web client and the official apps do).
- Like every Jellyfin user, a guest's app is told the file names of the titles it plays.
- A guest who clears the browser's storage can still join again with the link, as long as the pass has places left.
- The plugin enforces a pass only while Jellyfin runs it. A clean stop locks every guest first. The one gap: if Jellyfin crashes and afterwards does not load the plugin, devices that were signed in as a guest stay signed in. New sign-ins are refused either way, since the guest sign-in provider is gone. Disable or delete such guests under **Dashboard → Users**.
- Uninstall the plugin while it is turned on: it then deletes every guest account. Uninstalled while turned off, it cannot run, so the locked guest accounts stay until you delete them under **Dashboard → Users**.
- Like any streaming, a guest's browser receives what it plays and could save it. The pass limits what they can play, not what they do with the picture on their screen.
- With **Streams need a sign-in** off, Jellyfin's usual rule applies: anyone who knows a title's stream address can play it.
- The join page shows the Jellyfin logo your own server serves (its web client's app icon), so nothing of it is copied into the plugin. Jellyfin and the Jellyfin logo are trademarks of Jellyfin, Inc.; this plugin is not made or endorsed by the Jellyfin project.
- Limiting a pass to the home network stops playback on its guests' devices that are away at that moment. From then on, the plugin refuses every request of those guests from outside the home network, streams and subtitles included: Jellyfin's stream addresses need no sign-in, so Jellyfin does not check those itself. A guest's web client then signs out with a note and a way back, and the same sign-in works again at home. **Where** also works on an ended pass, so it opens again only where you want it.
- Home network only means the guest's device must be on your network, so a guest on your Wi-Fi is on your network like any visitor. Jellyfin lets any device there restart the server without signing in, and the DLNA plugin, if installed, shows every library to it; the plugin does not change either.
