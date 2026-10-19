namespace Jellyfin.Plugin.MediaHero.Services;

public record TrendEntry(int TmdbId, string MediaType, string Title, double Popularity);
