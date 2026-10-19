using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaHero.Services;

public class HeroPayload
{
	public IReadOnlyList<HeroItem> Items { get; set; } = Array.Empty<HeroItem>();

	public long BuiltAtUnix { get; set; }

	public object? Display { get; set; }
}
