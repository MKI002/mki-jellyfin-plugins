<img src="logo.png?v=1.0.0" alt="MKI Continue Watching Cleanup" width="100%">

# MKI Continue Watching Cleanup

Jellyfin's Continue Watching row only ever grows: the web client has no way to take something off it, and things you gave up on months ago stay there forever. This plugin adds both.

## Features

- **Remove button.** Continue Watching cards in the web client get a Remove button: on computers next to Mark played, Favorite and More when you hover a card, on phones and tablets as a small round button in the corner. A short **Undo** follows every removal. Everyone removes items from their own row only.
- **Auto cleanup.** Once a day, videos a user has not played for a set number of days (30 by default, at least 7) leave that user's Continue Watching. It works per person and for every app, TVs included. Something never played at all is always kept.
- **Preview and undo.** The settings page shows per user how many items the next run would clear, can run a cleanup right away, and lists recent runs with an **Undo** button. Undo works for 30 days and never overwrites an item that was played again since.
- **Starts switched off.** After installing, the plugin changes nothing, not even the web client, until an administrator turns a part on.

## What removing does

Jellyfin shows a video in Continue Watching while it has a saved spot to resume from and is not marked as played. Removing sets that spot back to the start, and that is all:

| Changes | Stays exactly as it was |
|---|---|
| The resume position (the video plays from the start next time) | The file and the library entry |
| | Watched or unwatched, play count, last played date |
| | Favourites, ratings, audio and subtitle choices |
| | Other users' Continue Watching |

Only videos are touched; audiobooks and music keep their place. For a movie with several versions, the version that holds the resume position is the one cleared.

## Setup

1. Install from the plugin catalog and restart Jellyfin.
2. Open **Dashboard → Plugins → MKI Continue Watching Cleanup**.
3. Turn on **Show a Remove button on Continue Watching**. People may need to reload the web client once to see it.
4. Optionally turn on **Clear items nobody has played for a while** and pick the number of days. The page tells you how many items that clears before it is saved. The cleanup runs as the scheduled task *Library → Clean up Continue Watching (MKI)*, every day at 04:00 by default.

## Notes

- The Remove button lives in the Jellyfin web client, so it shows in browsers and in the apps built on it (desktop, Android, iOS). TV apps such as Android TV or Swiftfin do not show it; auto cleanup still covers them.
- The web client script is written to Jellyfin's `index.html` only while the Remove button is on, and taken out again when you turn it off or uninstall the plugin.
- The undo data (user and item ids, the old position and dates) is kept in the plugin's configuration folder for 30 days.
