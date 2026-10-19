# MKI Jellyfin-Plugins

Eigene Plugins für Jellyfin 12.2, .NET 10, targetAbi 12.0.0.0.

| Ordner | Plugin | Zweck |
| --- | --- | --- |
| `SeerrBridge` | Jellyseerr Bridge | Seerr in der Jellyfin-Oberfläche: Knopf, eingebettete Ansicht, Suche mit Anfrage, Einmal-Anmeldung |
| `MkiMergeVersions` | MKI Merge Versions | Führt Filme mit mehreren Auflösungen zu einem Eintrag zusammen |
| `MediaHero` | Media Hero | Startseiten-Leiste mit Whitelists, Blacklists und Trendtiteln aus Seerr |

Der Quellcode wurde am 2026-10-19 mit ILSpy aus den installierten DLLs zurückgewonnen
(Originalquellen waren nicht mehr vorhanden), daher ohne die ursprünglichen Kommentare.

## Bauen und installieren
```
dotnet build -c Release <Ordner>
```
Die DLL aus `bin/Release/net10.0/` zusammen mit `meta.json` und `logo.png` nach
`<Jellyfin-Datenordner>/plugins/<Name>_<Version>/` auf dem Server kopieren und Jellyfin neu starten.

Plugin-Einstellungen (API-Schlüssel usw.) liegen nur auf dem Server, nicht im Repo.
