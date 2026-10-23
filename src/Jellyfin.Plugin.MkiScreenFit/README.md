<img src="logo.png?v=1.0.0" alt="MKI Screen Fit" width="100%">

# MKI Screen Fit

A 4K movie started on a Full HD laptop makes Jellyfin do the most work for nothing: it sends the 4K file, and when the browser cannot play it (HDR, an unsupported codec, burned-in subtitles) it transcodes the whole 4K picture, only for the laptop to shrink it again. When the same movie also exists in Full HD, that file would have looked the same on that screen. This plugin plays it.

## Features

- **Full HD version on Full HD screens.** When a movie has a 4K and a Full HD version (for example merged by [MKI Merge Versions](../Jellyfin.Plugin.MkiMergeVersions)) and someone starts it on a screen up to 1200 pixels on its shorter side (1080p and 1200p monitors, most laptops, phones), the Full HD file plays instead, also when the movie plays directly. 4K screens still get 4K.
- **Transcodes only as large as the screen.** When Jellyfin transcodes a video anyway (for example for a codec or HDR the device cannot play, burned-in subtitles or a bitrate limit), the plugin has it transcoded only as large as the screen, never below 1280×720. Example: a movie that only exists in 4K, started on a Full HD laptop, is transcoded to 1920×1080 instead of 4K. It looks the same there, and the server encodes a quarter of the pixels.
- **Screens are measured, or set per device.** Browsers and the Jellyfin apps built on the web client (desktop, Android, iOS) report their screen size. TV apps such as Android TV, Fire TV or Swiftfin cannot, so the settings page lists every device with its screen and lets you mark one as a Full HD screen, or leave it alone.
- **Recent decisions.** The settings page lists what the plugin did lately, and why it kept 4K where it did.
- **Starts switched off.** After installing, the plugin changes nothing, not even the web client, until an administrator turns a part on.

## What it never changes

| Left exactly as Jellyfin decides | |
|---|---|
| Bitrate limits (per user and for remote playback) and transcode throttling | The plugin never touches bitrate or throttling settings. |
| Direct playing, remuxing and direct streaming | The transcode size applies only when Jellyfin transcodes the video itself anyway (another codec, HDR to SDR, burned-in subtitles, a lower bitrate). A video that is copied stays a copy. |
| Work for the player | The Full HD version is used only when the player can take it with no more work than the 4K one, judged with the player's own device profile and the bitrate limit Jellyfin applies to it. If only the Full HD file had image subtitles that would have to be burned in, a codec the player cannot play, or a bitrate above the limit, 4K stays. A player that sends no device profile keeps 4K. |
| A version picked by hand | When someone picks a version that is not the default, it plays as picked, and it stays their choice when they resume. |
| Different cuts | Versions whose lengths differ by more than 15 seconds (theatrical, extended) are never swapped. |
| Audio and subtitles | The Full HD version is used only when it has the chosen audio and subtitle language; otherwise 4K stays. |
| Unknown screens | Without a trustworthy screen size the plugin plays no Full HD version and limits no transcode. TV browsers, which draw at 1080p even on a 4K panel, and browsers that hide the screen size for privacy count as unknown. The one thing it still does there is described below. |

The resume position carries over between versions. Jellyfin offers the version that was watched last first; when that is the Full HD version only because the plugin played it on a laptop, every screen that is not a known Full HD screen (a 4K TV, or a screen the plugin does not know) gets the 4K version again, at the same spot. That is what Jellyfin would have offered without the plugin. It does not happen when the 4K version would mean more work for that player than the Full HD one, or when the Full HD part is turned off: then Jellyfin offers the version watched last, as it always does.

## Setup

1. Install from the plugin catalog and restart Jellyfin.
2. Open **Dashboard → Plugins → MKI Screen Fit**.
3. Turn on **Play the Full HD version on Full HD screens**, **Transcode only as large as the screen**, or both.
4. Browsers and the Jellyfin apps built on the web client report their screen the next time they open (reloading the page is enough). For TV apps, choose **Full HD screen** under **Devices** for each TV or stick that is connected to a Full HD screen.

## Notes

- The web client script is written to Jellyfin's `index.html` only while a part of the plugin is on, and taken out again when both are off or the plugin is uninstalled. It sends the screen's width and height, and whether the browser hid them, for the device the user is signed in on, and changes nothing on screen. In full screen it sends nothing.
- Screen sizes are kept per user and device in the plugin's configuration folder and are deleted after 60 days; the Full HD versions the plugin played are kept there for 180 days so the TV can resume in 4K. Both go when the plugin is uninstalled. The recent decisions are kept in memory only.
- A device setting changes only what plays on that device, for whoever uses it. It does not grant or block anything.
- The plugin works on Jellyfin's playback information request, which every app sends before it plays a video. It has been tested with the web client; TV apps use the same answer from the server.
