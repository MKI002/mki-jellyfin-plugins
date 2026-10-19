namespace Jellyfin.Plugin.MediaHero.Configuration;

public class LibraryEntry
{
	public string Id { get; set; } = string.Empty;

	public string Name { get; set; } = string.Empty;

	public bool Enabled { get; set; } = true;

	public int Count { get; set; } = 10;
}
