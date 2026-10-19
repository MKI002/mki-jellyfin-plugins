using System;

namespace Jellyfin.Plugin.SeerrBridge.Services;

public sealed class MkiPaket
{
	public string Guid { get; init; } = string.Empty;

	public string Name { get; init; } = string.Empty;

	public string Version { get; init; } = string.Empty;

	public string Description { get; init; } = string.Empty;

	public string Overview { get; init; } = string.Empty;

	public string Category { get; init; } = "General";

	public string Owner { get; init; } = "MKI";

	public string TargetAbi { get; init; } = "12.0.0.0";

	public string Changelog { get; init; } = string.Empty;

	public string Ordner { get; init; } = string.Empty;

	public string? Bild { get; init; }

	public byte[] Zip { get; init; } = Array.Empty<byte>();

	public string Pruefsumme { get; init; } = string.Empty;

	public DateTime Stand { get; init; }
}
