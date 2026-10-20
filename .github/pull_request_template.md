Before:

After:

How:

## Release checklist

Tick every item, or write n/a and why. CI fails while an item is open on a pull request that changes `src/` or `ui/`. The steps are explained in [RELEASING.md](https://github.com/MKI002/mki-jellyfin-plugins/blob/main/RELEASING.md).

- [ ] Gates: `python3 build/checks.py --base origin/main` and `dotnet build MkiJellyfinPlugins.sln -c Release` pass locally
- [ ] English only: every new or changed text (settings page, web client, logs, README, changelog) is English
- [ ] Control Deck: settings page changes are made in `ui/` or on top of the kit, and `python3 build/kit.py --write` was run
- [ ] Security review: the changed code was reviewed against RELEASING.md step 3, with findings fixed or listed here
- [ ] Functional test: what was tested, and how (live server, or code review where a live test is not possible)
- [ ] Design rating: settings page or web client screenshots at 1280 and 390 px, light and dark, rated and fixed
- [ ] Images: `logo.png` and `assets/banner.png` are up to date with the plugin's name and features
- [ ] Version and changelog: `meta.json` version bumped and the changelog says what changed for the user
