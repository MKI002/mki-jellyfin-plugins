<img src="logo.png?v=1.1.0" alt="MKI Seerr Bridge" width="100%">

# MKI Seerr Bridge

Brings [Seerr](https://github.com/seerr-team/seerr) (formerly Jellyseerr) into the Jellyfin web client, so users can discover and request media without leaving Jellyfin.

## Features

- **Menu button.** A Seerr button in the header and in the side menu on mobile. Labelled "Requests" unless you set your own label.
- **Embedded view.** Seerr opens inside Jellyfin instead of in a new tab.
- **Search suggestions.** Seerr results appear below Jellyfin's own search results. Titles already in the library can be hidden, and users can request directly from the list.
- **Requests under the right account.** Requests are attributed to the matching Seerr user, so per-user approval rules and request history keep working.
- **Single sign-on (optional).** Users signed in to Jellyfin are signed in to Seerr too. The plugin issues a one-time pass (valid for one minute, usable once, tied to the account) and completes the login through Jellyfin's own Quick Connect. No password is bypassed.
- **Legacy header bridge.** Jellyfin 12 only reads the `Authorization` header. Seerr and other older apps still send `X-Emby-Authorization` or `X-Emby-Token`, which makes sign-in fail with HTTP 400 and user sync stop with 401. The bridge fills in `Authorization` only when it is missing, with the same credentials.

## Setup

1. Install from the plugin catalog and restart Jellyfin.
2. Open **Dashboard → Plugins → MKI Seerr Bridge** and enter:
   - the **public URL** browsers use to reach Seerr,
   - the **server URL** Jellyfin uses to reach Seerr (leave empty if it is the same),
   - the Seerr **API key** (Seerr → Settings → General). It stays on the server and is never sent to browsers.
3. Click **Test connection**, then save and reload the web client.

### Notes on sign-in

- Seerr's session cookie uses `SameSite=lax`. The embedded view keeps you signed in when Jellyfin and Seerr share a site (same host on different ports, or subdomains of one domain). On unrelated domains Seerr asks for a login every time.
- Single sign-on needs Quick Connect enabled on the server, and Jellyfin and Seerr on subdomains of a shared parent domain (for example `jellyfin.example.org` and `requests.example.org`). It does not work over bare IP addresses.
