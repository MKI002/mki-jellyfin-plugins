using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MkiPasswordPolicy.Configuration;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Services;

// Same checks as Web/client.js; keep both in step.
public static class PasswordRules
{
	public static readonly string[] Common =
	{
		"123456", "1234567", "12345678", "123456789", "1234567890", "12345678910", "0123456789", "111111", "000000",
		"123123", "654321", "666666", "121212", "112233", "123321", "987654321", "147258369", "11111111", "88888888",
		"password", "passwort", "passw0rd", "pa55word", "kennwort", "geheim", "geheimnis", "qwerty", "qwertz", "qwertyuiop",
		"qwertzuiop", "asdfgh", "asdfghjkl", "yxcvbnm", "zxcvbnm", "azerty", "abc", "abcdef", "abcdefg", "abcdefgh", "abc123",
		"admin", "administrator", "root", "user", "guest", "gast", "login", "welcome", "willkommen", "letmein", "hallo",
		"hello", "test", "changeme", "secret", "master", "default", "jellyfin", "emby", "plex", "kodi", "media", "movies",
		"filme", "netflix", "server", "iloveyou", "ichliebedich", "schatz", "liebe", "love", "sunshine", "princess",
		"dragon", "monkey", "football", "fussball", "baseball", "soccer", "superman", "batman", "starwars", "pokemon",
		"shadow", "michael", "thomas", "daniel", "andreas", "stefan", "markus", "alexander", "jessica", "jennifer",
		"charlie", "freedom", "whatever", "trustno1", "computer", "internet", "samsung", "google", "apple", "summer",
		"sommer", "winter", "fruehling", "herbst", "schalke", "dortmund", "bayern", "hamburg", "berlin", "muenchen",
		"deutschland", "germany", "familie", "family", "mustermann", "lol", "lollol", "killer", "hunter", "ninja",
		"mustang", "ferrari", "porsche", "bmw", "mercedes", "audi", "matrix", "hockey", "tigger", "cookie", "chocolate",
		"schokolade", "maggie", "ginger", "pepper", "buster", "zaq12wsx", "1q2w3e4r", "1q2w3e", "q1w2e3r4", "1qaz2wsx",
		"qazwsx", "passwort1", "password1", "aa123456", "a123456", "123abc", "iamadmin", "service", "access", "zugang",
	};

	private static readonly HashSet<string> CommonSet = new(Common, StringComparer.OrdinalIgnoreCase);

	public static readonly IReadOnlyDictionary<string, RuleSnapshot> Presets = new Dictionary<string, RuleSnapshot>
	{
		["basic"] = new() { MinLength = 8, RequireDigit = true, ForbidUsername = true, BlockCommon = true },
		["standard"] = new() { MinLength = 10, RequireUpper = true, RequireLower = true, RequireDigit = true, ForbidUsername = true, BlockCommon = true },
		["strong"] = new() { MinLength = 12, RequireUpper = true, RequireLower = true, RequireDigit = true, RequireSymbol = true, ForbidUsername = true, BlockCommon = true, MaxRepeat = 3 },
		["verystrong"] = new() { MinLength = 16, RequireUpper = true, RequireLower = true, RequireDigit = true, RequireSymbol = true, ForbidUsername = true, BlockCommon = true, MaxRepeat = 2 },
		["passphrase"] = new() { MinLength = 20, ForbidUsername = true, BlockCommon = true, MaxRepeat = 3 },
	};

	// null = no rules.
	public static RuleSnapshot? Profile(string? name, PluginConfiguration c)
	{
		if (string.IsNullOrEmpty(name) || name == "none")
		{
			return null;
		}
		if (name == "custom")
		{
			return Snapshot(c);
		}
		return Presets.TryGetValue(name, out var p) ? p : null;
	}

	public static IReadOnlyList<string> Check(string? password, string? username, RuleSnapshot c)
	{
		string pw = password ?? string.Empty;
		var fehler = new List<string>();
		if (pw.Length < c.MinLength)
		{
			fehler.Add($"mindestens {c.MinLength} Zeichen");
		}
		if (c.RequireUpper && !pw.Any(char.IsUpper))
		{
			fehler.Add("ein Grossbuchstabe");
		}
		if (c.RequireLower && !pw.Any(char.IsLower))
		{
			fehler.Add("ein Kleinbuchstabe");
		}
		if (c.RequireDigit && !pw.Any(char.IsDigit))
		{
			fehler.Add("eine Ziffer");
		}
		if (c.RequireSymbol && !pw.Any(ch => !char.IsLetterOrDigit(ch)))
		{
			fehler.Add("ein Sonderzeichen");
		}
		if (c.ForbidUsername && !string.IsNullOrEmpty(username) && username.Length >= 3
			&& pw.Contains(username, StringComparison.OrdinalIgnoreCase))
		{
			fehler.Add("darf den Benutzernamen nicht enthalten");
		}
		if (c.BlockCommon && pw.Length > 0 && IsCommon(pw))
		{
			fehler.Add("darf kein bekanntes, leicht zu erratendes Passwort sein");
		}
		if (c.MaxRepeat > 0 && LongestRun(pw) > c.MaxRepeat)
		{
			fehler.Add($"hoechstens {c.MaxRepeat} gleiche Zeichen hintereinander");
		}
		return fehler;
	}

	public static RuleSnapshot Snapshot(PluginConfiguration c) => new()
	{
		MinLength = Math.Max(1, c.MinLength),
		RequireUpper = c.RequireUpper,
		RequireLower = c.RequireLower,
		RequireDigit = c.RequireDigit,
		RequireSymbol = c.RequireSymbol,
		ForbidUsername = c.ForbidUsername,
		BlockCommon = c.BlockCommon,
		MaxRepeat = c.MaxRepeat
	};

	// True when a password that passed the snapshot's rules also passes the current ones.
	public static bool Covers(RuleSnapshot s, RuleSnapshot c)
	{
		return s.MinLength >= c.MinLength
			&& (s.RequireUpper || !c.RequireUpper)
			&& (s.RequireLower || !c.RequireLower)
			&& (s.RequireDigit || !c.RequireDigit)
			&& (s.RequireSymbol || !c.RequireSymbol)
			&& (s.ForbidUsername || !c.ForbidUsername)
			&& (s.BlockCommon || !c.BlockCommon)
			&& (c.MaxRepeat == 0 || (s.MaxRepeat > 0 && s.MaxRepeat <= c.MaxRepeat));
	}

	public static string Describe(IReadOnlyList<string> fehler)
	{
		return "Das Passwort erfuellt die Regeln nicht: " + string.Join(", ", fehler) + ".";
	}

	// "Sommer2024!" counts as "sommer": trailing digits and symbols do not make a common word safe.
	public static bool IsCommon(string pw)
	{
		string klein = pw.ToLowerInvariant();
		if (CommonSet.Contains(klein))
		{
			return true;
		}
		string kern = klein.TrimEnd(ch => !char.IsLetter(ch)).TrimStart(ch => !char.IsLetter(ch));
		return kern.Length > 0 && CommonSet.Contains(kern);
	}

	private static string TrimEnd(this string s, Func<char, bool> weg)
	{
		int ende = s.Length;
		while (ende > 0 && weg(s[ende - 1]))
		{
			ende--;
		}
		return s.Substring(0, ende);
	}

	private static string TrimStart(this string s, Func<char, bool> weg)
	{
		int start = 0;
		while (start < s.Length && weg(s[start]))
		{
			start++;
		}
		return s.Substring(start);
	}

	private static int LongestRun(string pw)
	{
		int best = 0;
		int lauf = 0;
		for (int i = 0; i < pw.Length; i++)
		{
			lauf = i > 0 && pw[i] == pw[i - 1] ? lauf + 1 : 1;
			best = Math.Max(best, lauf);
		}
		return best;
	}
}
