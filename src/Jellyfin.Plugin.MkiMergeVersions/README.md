<img src="logo.png?v=1.1.0" alt="MKI Merge Versions" width="100%">

# MKI Merge Versions

Two resolutions, one entry. If a movie exists as 1080p and 2160p in separate folders, this plugin links them into a single library item, and the version you choose (1080p or the highest resolution) becomes the primary version.

## Features

- **Safe matching.** Movies are grouped by TMDb ID, falling back to IMDb ID. Never by title and year. TV series are left untouched.
- **Respects permissions.** Versions are only linked when exactly the same users can see them (library access, parental controls, tags) and they share a production year, so merging never shows anyone a file they could not open before. Links that break this rule, for example after a library's access changed, are split again. Groups of more than eight versions are skipped as suspicious.
- **You pick the primary version.** Either the highest resolution (the default, usually 4K) or 1080p leads: it is the version that opens and plays first, and the version picker on the movie page still offers every version. With 1080p, the version closest to 1920 px across wins, measured as a ratio: a movie without a 1080p file uses 720p over 4K, but 4K over a DVD copy. Files Jellyfin has not measured yet never lead.
- **Existing merges follow.** With *Apply to existing merges* on (the default), each run rebuilds earlier merges whose primary version does not match the choice, so switching from the highest resolution to 1080p switches every merged movie on the next run. Played state and resume points are shared across versions, and favourites, ratings and likes are carried over to the new primary version. Versions Jellyfin links itself (several files in one movie folder) are left to Jellyfin.
- **Runs on its own.** A scheduled task (*Library → Merge movie versions (MKI)*) runs at startup and every hour, and an extra run starts once the library has been quiet for five minutes after changes.
- **Dry run.** Log what would be merged without writing anything.

## Setup

1. Install from the plugin catalog and restart Jellyfin.
2. Open **Dashboard → Plugins → MKI Merge Versions**. Pick the primary version (1080p or highest resolution). Turn on the dry run first if you want to check the result in the Jellyfin log. Settings save as soon as you change them.
3. Click **Run now** or wait for the scheduled task.
