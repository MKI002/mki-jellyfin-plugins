<img src="logo.png?v=1.0.0" alt="MKI Screen Fit" width="100%">

# MKI Screen Fit

A 4K movie started on a Full HD laptop makes Jellyfin do the most work for nothing: it sends the 4K file, and when the browser cannot play it (HDR, an unsupported codec, burned-in subtitles) it re-encodes the whole 4K picture, only for the laptop to shrink it again. When the same movie also exists in Full HD, that file would have looked the same on that screen. This plugin plays it.

## Features

- **Full HD version on Full HD screens.** When a movie has a 4K and a Full HD version (for example merged by [MKI Merge Versions](../Jellyfin.Plugin.MkiMergeVersions)) and someone starts it on a screen up to 1200 pixels tall (1080p and 1200p monitors, most laptops, phones), the Full HD file plays instead. 4K screens still get 4K.
- **Transcodes no larger than the screen.** When Jellyfin has to re-encode the video anyway, the plugin keeps the picture no larger than the screen (never below 1280×720). A 4K-to-4K transcode for a Full HD laptop becomes a 4K-to-1080p one, a quarter of the pixels to encode.
- **Screens are measured, or set per device.** Browsers and the Jellyfin apps built on the web client (desktop, Android, iOS) report their screen size. TV apps such as Android TV, Fire TV or Swiftfin cannot, so the settings page lists every device with its screen and lets you mark one as a Full HD screen, or leave it alone.
- **Recent decisions.** The settings page lists what the plugin did lately, and why it kept 4K where it did.
- **Starts switched off.** After installing, the plugin changes nothing, not even the web client, until an administrator turns a part on.

## What it never changes

| Left exactly as Jellyfin decides | |
|---|---|
| Bitrate limits (per user and for remote playback) and transcode throttling | The plugin never touches bitrate or throttling settings. |
| Direct play and remuxing | The size limit applies only when the video is re-encoded anyway. A transcode that copies the video stays a copy. |
| A version picked by hand | When someone picks a version that is not the default, it plays as picked. |
| Different cuts | Versions whose lengths differ by more than a minute (theatrical, extended) are never swapped. |
| Audio and subtitles | The Full HD version is used only when it has the chosen audio and subtitle language; otherwise 4K stays. |
| Unknown screens | Without a trustworthy screen size nothing changes. TV browsers, which draw at 1080p even on a 4K panel, and browsers that hide the screen size for privacy count as unknown. |

The resume position carries over: a movie started in 4K on the TV continues in Full HD on the laptop at the same spot.

## Setup

1. Install from the plugin catalog and restart Jellyfin.
2. Open **Dashboard → Plugins → MKI Screen Fit**.
3. Turn on **Play the Full HD version on Full HD screens**, **Keep transcodes no larger than the screen**, or both.
4. Browsers and the Jellyfin apps built on the web client report their screen the next time they open (reloading the page is enough). For TV apps, choose **Full HD screen** under **Devices** for each TV or stick that is connected to a Full HD screen.

## Notes

- The web client script is written to Jellyfin's `index.html` only while a part of the plugin is on, and taken out again when both are off or the plugin is uninstalled. It sends two numbers, the screen's width and height, for the signed-in user's own device, and changes nothing on screen.
- Screen sizes are kept per user and device in the plugin's configuration folder and expire after 60 days. The recent decisions are kept in memory only.
- The plugin works on Jellyfin's playback information request, which every app sends before it plays a video. It has been tested with the web client; TV apps use the same answer from the server.
