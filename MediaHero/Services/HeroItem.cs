using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaHero.Services;

public class HeroItem
{
	public string Id { get; set; } = string.Empty;

	public string Name { get; set; } = string.Empty;

	public string? Overview { get; set; }

	public string Type { get; set; } = string.Empty;

	public int? Year { get; set; }

	public double? Rating { get; set; }

	public string? OfficialRating { get; set; }

	public int? RuntimeMinutes { get; set; }

	public IReadOnlyList<string> Genres { get; set; } = Array.Empty<string>();

	public string? BackdropTag { get; set; }

	public string? LogoTag { get; set; }

	public string? PosterTag { get; set; }

	public string? ThumbTag { get; set; }

	public string? PrimaryColor { get; set; }

	public string Source { get; set; } = string.Empty;

	public string? PlayId { get; set; }
}
