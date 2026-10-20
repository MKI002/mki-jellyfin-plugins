using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrBridge.Services;

public class MkiRepository
{
	private readonly IApplicationPaths _pfade;

	private readonly ILogger<MkiRepository> _logger;

	private readonly object _schloss = new object();

	private List<MkiPaket>? _pakete;

	private DateTime _stand = DateTime.MinValue;

	public MkiRepository(IApplicationPaths pfade, ILogger<MkiRepository> logger)
	{
		_pfade = pfade;
		_logger = logger;
	}

	public IReadOnlyList<MkiPaket> Pakete()
	{
		lock (_schloss)
		{
			if (_pakete == null || DateTime.UtcNow - _stand > TimeSpan.FromMinutes(10L))
			{
				_pakete = Einlesen();
				_stand = DateTime.UtcNow;
			}
			return _pakete;
		}
	}

	public MkiPaket? Finden(string guid)
	{
		return Pakete().FirstOrDefault((MkiPaket p) => string.Equals(p.Guid.Replace("-", string.Empty), guid.Replace("-", string.Empty), StringComparison.OrdinalIgnoreCase));
	}

	private List<MkiPaket> Einlesen()
	{
		List<MkiPaket> list = new List<MkiPaket>();
		string pluginsPath;
		try
		{
			pluginsPath = _pfade.PluginsPath;
		}
		catch (Exception)
		{
			return list;
		}
		if (string.IsNullOrEmpty(pluginsPath) || !Directory.Exists(pluginsPath))
		{
			return list;
		}
		string[] directories = Directory.GetDirectories(pluginsPath);
		foreach (string text in directories)
		{
			try
			{
				string text2 = Path.Combine(text, "meta.json");
				if (!File.Exists(text2))
				{
					continue;
				}
				using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(text2));
				JsonElement rootElement = jsonDocument.RootElement;
				if (!string.Equals(Text(rootElement, "owner"), "MKI", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				string[] files = Directory.GetFiles(text, "*.dll");
				if (files.Length != 0)
				{
					// Only an image inside the plugin's own folder is ever served, whatever meta.json points to.
					string text3 = Text(rootElement, "imagePath");
					if (!string.IsNullOrEmpty(text3))
					{
						string text4 = Path.Combine(text, Path.GetFileName(text3));
						text3 = (File.Exists(text4) ? text4 : null);
					}
					byte[] array = Packen(files, rootElement);
					DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(files[0]);
					list.Add(new MkiPaket
					{
						Guid = (Text(rootElement, "guid") ?? string.Empty),
						Name = (Text(rootElement, "name") ?? Path.GetFileName(text)),
						Version = (Text(rootElement, "version") ?? "1.0.0.0"),
						Description = (Text(rootElement, "description") ?? string.Empty),
						Overview = (Text(rootElement, "overview") ?? string.Empty),
						Category = (Text(rootElement, "category") ?? "General"),
						Owner = "MKI",
						TargetAbi = (Text(rootElement, "targetAbi") ?? "12.0.0.0"),
						Changelog = (Text(rootElement, "changelog") ?? string.Empty),
						Ordner = text,
						Bild = text3,
						Zip = array,
						Pruefsumme = Convert.ToHexString(MD5.HashData(array)).ToLowerInvariant(),
						Stand = lastWriteTimeUtc
					});
				}
			}
			catch (Exception ex2)
			{
				LoggerExtensions.LogWarning((ILogger)(object)_logger, ex2, "Plugin folder {Ordner} skipped", new object[1] { text });
			}
		}
		return list;
	}

	// The package gets a fresh meta.json with the plugin's public details only. The installed one is written by
	// Jellyfin and holds paths of this server.
	private static readonly string[] Felder = new string[9] { "guid", "name", "overview", "description", "owner", "category", "targetAbi", "version", "changelog" };

	private static byte[] Packen(string[] dlls, JsonElement meta)
	{
		using MemoryStream memoryStream = new MemoryStream();
		using (ZipArchive destination = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach (string text in dlls)
			{
				destination.CreateEntryFromFile(text, Path.GetFileName(text), CompressionLevel.Optimal);
			}
			Dictionary<string, string> sauber = new Dictionary<string, string>();
			foreach (string feld in Felder)
			{
				string wert = Text(meta, feld);
				if (wert != null)
				{
					sauber[feld] = wert;
				}
			}
			ZipArchiveEntry eintrag = destination.CreateEntry("meta.json", CompressionLevel.Optimal);
			using Stream ziel = eintrag.Open();
			JsonSerializer.Serialize(ziel, sauber, new JsonSerializerOptions { WriteIndented = true });
		}
		return memoryStream.ToArray();
	}

	private static string? Text(JsonElement e, string name)
	{
		if (!e.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
		{
			return null;
		}
		return value.GetString();
	}
}
