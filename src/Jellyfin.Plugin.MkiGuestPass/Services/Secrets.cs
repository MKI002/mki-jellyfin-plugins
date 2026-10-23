using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MkiGuestPass.Services;

// Pass links, typed codes and rejoin keys. Only an HMAC of each is stored, keyed with a random server key, so the
// passes file alone is no use to anyone; the plain value exists only in the one answer that shows it.
public class Secrets
{
	// Without look-alikes (0/O, 1/I/L, 5/S, 8/B, 2/Z): 25 characters, 8 of them make about 37 bits. The server-wide
	// limit on wrong codes (see Throttle) allows at most about 9,600 tries a day (100 at once, then a 15-minute pause),
	// so even with 20 passes open, guessing one of their codes takes about a thousand years on average.
	public const string CodeAlphabet = "ACDEFGHJKMNPQRTUVWXY34679";

	public const int CodeLength = 8;

	private readonly byte[] _key;

	public Secrets(PassStore store, ILogger<Secrets> logger)
	{
		_key = LoadKey(Path.Combine(store.Folder, "key.bin"), logger);
	}

	// 32 random bytes, URL-safe: the part after # in a pass link, and a guest's rejoin key.
	public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

	public static string NewCode()
	{
		var chars = new char[CodeLength];
		for (int i = 0; i < chars.Length; i++)
		{
			chars[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
		}
		return new string(chars);
	}

	// Codes are typed: case, spaces and dashes do not matter.
	public static string? NormalizeCode(string? typed)
	{
		if (string.IsNullOrEmpty(typed) || typed.Length > 32)
		{
			return null;
		}
		var sb = new StringBuilder(CodeLength);
		foreach (char raw in typed.ToUpperInvariant())
		{
			if (raw == ' ' || raw == '-' || raw == '.')
			{
				continue;
			}
			if (CodeAlphabet.IndexOf(raw, StringComparison.Ordinal) < 0)
			{
				return null;
			}
			sb.Append(raw);
		}
		return sb.Length == CodeLength ? sb.ToString() : null;
	}

	public static string ShowCode(string code) => code.Length == CodeLength ? code[..4] + "-" + code[4..] : code;

	// A token as sent by a browser: base64url of exactly 32 bytes, nothing else.
	public static bool LooksLikeToken(string? s) => s != null && s.Length == 43 && IsBase64Url(s);

	public string Hash(string kind, string value)
	{
		using var h = new HMACSHA256(_key);
		return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(kind + ":" + value)));
	}

	public bool Matches(string? storedHash, string kind, string value)
	{
		if (string.IsNullOrEmpty(storedHash))
		{
			return false;
		}
		byte[] a, b;
		try
		{
			a = Convert.FromBase64String(storedHash);
			b = Convert.FromBase64String(Hash(kind, value));
		}
		catch (FormatException)
		{
			return false;
		}
		return CryptographicOperations.FixedTimeEquals(a, b);
	}

	private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	private static bool IsBase64Url(string s)
	{
		foreach (char c in s)
		{
			if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_'))
			{
				return false;
			}
		}
		return true;
	}

	private static byte[] LoadKey(string file, ILogger logger)
	{
		try
		{
			if (File.Exists(file))
			{
				var existing = File.ReadAllBytes(file);
				if (existing.Length == 32)
				{
					return existing;
				}
				logger.LogWarning("MKI Guest Pass key file has the wrong size; a new key is made and existing pass links stop working");
			}
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "MKI Guest Pass key file could not be read; a new key is made and existing pass links stop working");
		}
		var key = RandomNumberGenerator.GetBytes(32);
		string tmp = file + ".tmp";
		// Private from the first byte on: created with owner-only rights, never readable by others for a moment.
		var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
		if (!OperatingSystem.IsWindows())
		{
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		}
		using (var stream = new FileStream(tmp, options))
		{
			stream.Write(key);
		}
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
		File.Move(tmp, file, overwrite: true);
		return key;
	}
}
