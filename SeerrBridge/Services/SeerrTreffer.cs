namespace Jellyfin.Plugin.SeerrBridge.Services;

public record SeerrTreffer(int TmdbId, string MediaType, string Title, string? Year, string? PosterPath, string? Overview, double Rating, string Status);
