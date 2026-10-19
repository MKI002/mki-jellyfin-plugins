using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Services;

// Readable random passwords like "Tq7m-Kp2x-Rw9z-Hf4b": no 0/O or 1/l/I to mix up, and always upper, lower, digit and symbol.
public static class TemporaryPasswords
{
	private const string Gross = "ABCDEFGHJKLMNPQRSTUVWXYZ";

	private const string Klein = "abcdefghijkmnopqrstuvwxyz";

	private const string Ziffern = "23456789";

	private const string Alle = Gross + Klein + Ziffern;

	public static string Create(int minLength)
	{
		int gruppen = Math.Max(4, (minLength + 5) / 5);
		while (true)
		{
			var sb = new StringBuilder();
			for (int g = 0; g < gruppen; g++)
			{
				if (g > 0)
				{
					sb.Append('-');
				}
				for (int i = 0; i < 4; i++)
				{
					sb.Append(Alle[RandomNumberGenerator.GetInt32(Alle.Length)]);
				}
			}
			string p = sb.ToString();
			if (p.IndexOfAny(Gross.ToCharArray()) >= 0 && p.IndexOfAny(Klein.ToCharArray()) >= 0 && p.IndexOfAny(Ziffern.ToCharArray()) >= 0)
			{
				return p;
			}
		}
	}
}
