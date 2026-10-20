# Releasing

Every plugin update and every new plugin goes through the same steps before it is released. CI enforces what a
script can check; the rest is the review checklist in the pull request template, and CI fails while an item on it is
open.

| Step | Enforced by |
|---|---|
| 1. Gates | `build/checks.py`, `build/package.py --check` and `dotnet build`, in the Build workflow and again in Release |
| 2. English only | `build/checks.py` (German text) and the checklist |
| 3. Security review | `build/checks.py` (unsafe HTML, HTTP clients, new anonymous endpoints and middleware) and the checklist |
| 4. Functional test | the checklist |
| 5. Settings page and design rating | `build/kit.py --check` and the checklist |
| 6. Images | `build/checks.py` (logo size, banner names every plugin) and the checklist |
| 7. Version and changelog | `build/checks.py --base` (changed plugin must have a higher version and a new changelog) |
| 8. Release | the Release workflow refuses to run unless the Build workflow is green on that commit of main |

## 1. Gates

```sh
python3 build/checks.py --base origin/main
python3 build/package.py --check
dotnet build MkiJellyfinPlugins.sln -c Release
```

`build/checks.py` without `--base` runs the checks that do not need git history.

## 2. English only

Everything a person reads is English: settings pages, the web client, change pages, log messages, READMEs and the
changelog. Existing German code identifiers and wire values (for example status keys shared between server and
client) stay as they are, because renaming them would break saved settings or the API. Never rename configuration
property names or enum values: they are the saved XML keys.

## 3. Security review

Read every changed line with these rules in mind. The safest option always wins. List what you found and fixed in the
pull request.

- **Tokens.** Jellyfin accepts a token from `Authorization` (MediaBrowser scheme) and the `ApiKey` query always, and
  from `X-Emby-Authorization`, `X-Emby-Token`, `X-MediaBrowser-Token` and the `api_key` query when legacy
  authorization is on. A per-user gate checks all of them and fails closed when they disagree.
- **Middleware order.** Plugin middleware runs in alphabetical plugin order. A gate must not rely on another plugin's
  middleware having run first. Wrap request middleware in try/catch so a failure falls through to Jellyfin.
- **Anonymous endpoints.** Only static files and the password change flow are anonymous. A new one needs a reason, and
  an entry in `build/security-baseline.json` after review. The same goes for a new `IStartupFilter`.
- **Per-user data** is cached per user, filtered with `BaseItem.IsVisibleStandalone(user)`, and sent with
  `Cache-Control: private` and `Vary: Authorization`.
- **Seerr.** Users are matched only by Seerr's `jellyfinUserId`, never by name. Unlinked users are refused, never
  served with the admin key.
- **Outbound HTTP.** One static `HttpClient` with `AllowAutoRedirect = false`, `UseCookies = false` and a bounded
  `MaxResponseContentBufferSize`.
- **Web pages.** Server data goes in with `textContent` or `createElement`, never through `innerHTML`.
- **Passwords** are never stored, logged or inspected beyond Jellyfin's own hash.
- **Personal data.** No LAN addresses, server names, folder paths, e-mail addresses or keys in the repository.

## 4. Functional test

Test the change on a Jellyfin 12 server: install the built plugin, restart, use the feature as an admin and as a
regular user, and check the Jellyfin log for errors. Where a live test is not possible, review the logic against the
Jellyfin server source and say so in the pull request.

## 5. Settings page and design rating

Settings pages use the shared Control Deck kit in `ui/`. Edit `ui/mki-kit.css` or `ui/mki-kit.js` and run
`python3 build/kit.py --write`; never edit the generated copy between the `MKI-KIT` markers. The page must work
without any other plugin installed and with several plugin pages in the DOM at once.

When a settings page or the web client changes, take screenshots at 1280 and 390 px wide, in the dark and light
themes, and rate them from 1 to 10 for clarity, consistency with the other pages, phone layout and contrast. Fix
anything below 8 before release, and add the screenshots to the pull request.

## 6. Images

Plugin images are 1200 x 675 PNGs in the neon NCS-grid style with the MKI mark, saved as `src/<Plugin>/logo.png`.
`assets/banner.png` shows every plugin, and its alt text in the README names each one. Regenerate them when a plugin
is added or renamed, or when its features change enough that the image no longer fits.

## 7. Version and changelog

Bump `version` in `src/<Plugin>/meta.json` (four parts, for example `1.5.1.0`) in the same pull request as the change,
and replace `changelog` with what changed for the user, in plain English. Documentation-only changes (`*.md`) need no
bump.

## 8. Release

1. Open a pull request, fill in the checklist and drive CI green.
2. Merge into main and wait for the Build workflow on main to finish green.
3. Check the existing tags (they are shared by all plugins), then run **Actions → Release → Run workflow** with the
   next tag, for example `v1.7.1`.
4. Check that `manifest.json` on main lists the new versions and that the release has a zip for each.

## A new plugin

In addition to the steps above:

- A new GUID, a display name starting with `MKI `, and a `meta.json` with every field `build/package.py` requires.
- Added to `MkiJellyfinPlugins.sln`, so CI builds it.
- A copy of `DuplicateGuard.cs`, so a renamed or older copy of the plugin stands down.
- A settings page built on the kit (`MKI-KIT` markers in `Configuration/configPage.html`).
- A `README.md` in the plugin folder, a section in the main README, a `logo.png`, and a banner that shows it.
