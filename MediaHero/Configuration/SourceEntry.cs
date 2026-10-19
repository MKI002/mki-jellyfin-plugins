namespace Jellyfin.Plugin.MediaHero.Configuration;

public class SourceEntry
{
	public HeroSource Source { get; set; }

	public int Take { get; set; } = 30;

	public bool Enabled { get; set; } = true;

	public int MinYear { get; set; }
}
