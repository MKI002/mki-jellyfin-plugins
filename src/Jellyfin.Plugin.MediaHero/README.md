<img src="logo.png?v=1.1.0" alt="MKI Media Hero" width="100%">

# MKI Media Hero

A cinematic hero carousel at the top of the Jellyfin home screen.

## Features

- **Server-side list.** The carousel is assembled and cached on the server, so the home screen loads it in one request. A background pre-warmer keeps it fresh.
- **Sources.** Mix recently added, recently released, next up, favorites, top rated, random, Seerr trending, Seerr popular movies or series, and a hand-picked whitelist. Each source has its own upper limit.
- **Per-library targets.** Choose how many titles each library contributes.
- **Filters.** Whitelists and blacklists for titles, genres and tags, blocked age ratings, minimum community rating, minimum production year, hide watched items, and require a backdrop or logo.
- **Looks.** Five layouts (full width, contained, compact, poster left, centered), four transitions (fade, slide, Ken Burns, none) and five indicators (dots, thumbnails, progress bar, numbers, none). Height, corner radius, scrim, text position and scale, and custom CSS are all adjustable.
- **Buttons and details.** Logo or title, overview, year, rating, runtime, genres, age rating, and play, details, trailer and favorite buttons, each switchable.
- **Behaviour.** Autoplay interval, pause on hover, keyboard and swipe navigation, and respect for the system's reduced-motion setting.

## Setup

1. Install from the plugin catalog and restart Jellyfin.
2. Open **Dashboard → Plugins → MKI Media Hero**, pick your libraries and sources, and save.
3. For Seerr sources, enter your Seerr URL and API key (Seerr → Settings → General). The key stays on the server.

The carousel appears on the home screen after a page reload.
