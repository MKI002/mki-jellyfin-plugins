<img src="logo.png?v=1.0.0" alt="MKI Login Showcase" width="100%">

# MKI Login Showcase

The Jellyfin sign-in page is the first thing family and friends see, and by default it shows nothing of what is waiting behind it. This plugin turns it into a showcase of your own library: posters and backdrops in motion, in MKI neon, with the sign-in form as a glass card on top.

## Features

- **Four styles.** *Poster Wall*: tilted columns of posters drifting up and down. *Backdrop Spotlight*: one backdrop at a time, slowly zooming and fading to the next. *Neon Horizon*: posters gliding along a neon horizon above a moving grid floor. *Flip Mosaic*: a wall of posters where single tiles flip to new titles.
- **Neon sign-in card.** Jellyfin's own sign-in form on a glass card in the chosen colours, placed beside the artwork on large screens. It can be turned off to keep the form as Jellyfin draws it.
- **Four colour pairs**: MKI neon, Jellyfin, Sunset and Lime.
- **Calm and still motion.** Calm runs at half speed; Still shows a single frame, good for TVs left on the sign-in page. Devices set to reduce motion always get Still, and the animation pauses while the tab is in the background.
- **Live preview.** The settings page draws the current pick with the same code as the sign-in page, on a computer or a phone screen.
- **A new pick every hour**, at random, the most recently added or the top rated titles, or right away with *New titles now*.
- **Starts switched off.** After installing, no library is ticked and the web client is left exactly as Jellyfin ships it until an administrator turns the showcase on.

## What visitors can see

The sign-in page is shown before anyone signs in, so everything it gets is public. The plugin keeps that to pictures, and to what you turned on:

| | |
|---|---|
| Libraries | Only the libraries you tick. None is ticked after installing, and only movie, show and mixed libraries are offered (never home videos, photos, music or books). Libraries whose name suggests kids or adult content are marked, and ticking one asks first. |
| Age rating | Only titles rated up to the limit you set (12 after installing). Titles without a rating are left out unless you include them, and never come from a mixed library, where Jellyfin files home videos as movies. Raising the limit above 12 asks first. |
| Pictures | Only the kind the chosen style draws (backdrops for Backdrop Spotlight, posters for the others), stored on your server and redrawn by Jellyfin at screen size. The showcase never sends original files, so no file details, camera or location data reach the sign-in page, and artwork Jellyfin cannot redraw (GIF, SVG) is skipped. |
| Item ids, paths, library names | Never. The sign-in page asks for pictures by their place in the current pick, which changes every hour. Pictures of an earlier pick stop working as soon as the settings change. |
| Changes in the library | The pick is checked again every 30 seconds. A title that was re-rated, deleted or moved out of the ticked libraries, or a library removed in the dashboard, leaves the sign-in page then, and an open sign-in page follows within minutes. |
| Title names | Only when you turn them on, and only for Backdrop Spotlight, the one style that shows them, for the backdrops it shows. Turning them on while the showcase is live asks first. |
| Library size | Only when you turn it on, and only for Flip Mosaic, the one style that shows it. It counts only the titles the sign-in page may show. |

Signing in itself is not touched: the form, Quick Connect, Forgot Password, visible user tiles and other plugins on the sign-in page (such as [MKI Password Policy](../Jellyfin.Plugin.MkiPasswordPolicy)'s password change dialog) work as before. The showcase sits behind the page and takes no clicks.

## Setup

1. Install from the plugin catalog and restart Jellyfin.
2. Open **Dashboard → Plugins → MKI Login Showcase**.
3. Under **What visitors can see**, tick the libraries the sign-in page may show and check the age rating limit.
4. Pick a style and colours, check the preview, then turn on **Sign-in page** at the top.
5. Sign out, or open your Jellyfin address in a private window, to see it. Browsers that already have the web client open pick it up after a reload.

## Notes

- The sign-in page script is written to Jellyfin's `index.html` only while the showcase is on, and taken out again when it is turned off or the plugin is uninstalled. The Jellyfin process needs write access to the web folder for that; the official Docker image has it. The settings page says so when it could not be added.
- Three things answer without a sign-in: the sign-in page script, the current pick (style, colours and which pictures there are) and the pictures themselves. Pictures are sent with `Cache-Control: private`, so shared proxies do not keep them.
- The showcase only appears on the sign-in page of the server it came from. A web client signed in to another server shows that server's page unchanged.
- The pick is made from at most 240 titles and holds up to 48 posters, or 12 backdrops for Backdrop Spotlight.
- Jellyfin itself sends a title's artwork to anyone who asks with that title's item id, signed in or not. The showcase never hands out item ids, so it adds no way in, but it cannot close that one either.
