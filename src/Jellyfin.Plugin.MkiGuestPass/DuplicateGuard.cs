using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiGuestPass;

/// <summary>
/// Jellyfin removes old plugin versions by folder name, so after a rename an old copy can stay
/// loaded next to the new one. The older copy then stays inactive, and the newest copy marks
/// older folders as deleted so Jellyfin removes them on the next restart.
/// </summary>
internal static class DuplicateGuard
{
	private static readonly Lazy<bool> Superseded = new Lazy<bool>(() =>
	{
		var me = typeof(DuplicateGuard).Assembly.GetName();
		return AssemblyLoadContext.All
			.SelectMany(context => context.Assemblies)
			.Select(assembly => assembly.GetName())
			.Any(name => string.Equals(name.Name, me.Name, StringComparison.Ordinal) && name.Version > me.Version);
	});

	/// <summary>Gets a value indicating whether a newer copy of this plugin is loaded.</summary>
	public static bool IsSuperseded => Superseded.Value;

	/// <summary>Marks older installed copies of this plugin as deleted. Jellyfin removes them on the next start.</summary>
	public static void RetireOlderCopies(Guid id, ILogger logger)
	{
		try
		{
			var ownFolder = Path.GetDirectoryName(typeof(DuplicateGuard).Assembly.Location);
			var pluginsFolder = Path.GetDirectoryName(ownFolder);
			var ownVersion = typeof(DuplicateGuard).Assembly.GetName().Version;
			if (string.IsNullOrEmpty(ownFolder) || string.IsNullOrEmpty(pluginsFolder) || ownVersion == null)
			{
				return;
			}

			foreach (var folder in Directory.GetDirectories(pluginsFolder))
			{
				var metaPath = Path.Combine(folder, "meta.json");
				if (string.Equals(folder, ownFolder, StringComparison.Ordinal) || !File.Exists(metaPath))
				{
					continue;
				}

				// One unreadable meta.json must not stop the check of the other folders.
				try
				{
					var meta = JsonNode.Parse(File.ReadAllText(metaPath)) as JsonObject;
					if (meta == null
						|| !Guid.TryParse(meta["guid"]?.ToString(), out var guid) || guid != id
						|| !Version.TryParse(meta["version"]?.ToString(), out var version) || version >= ownVersion
						|| string.Equals(meta["status"]?.ToString(), "Deleted", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					meta["status"] = "Deleted";
					var tmp = metaPath + ".tmp";
					File.WriteAllText(tmp, meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
					File.Move(tmp, metaPath, overwrite: true);
					logger.LogWarning("Found an older copy of this plugin in {Folder}. It is marked for removal; restart Jellyfin to finish.", folder);
				}
				catch (Exception ex)
				{
					logger.LogWarning(ex, "Could not check {Folder} for an older copy of this plugin", folder);
				}
			}
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Could not check for older copies of this plugin");
		}
	}
}
