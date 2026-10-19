using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.MkiPasswordPolicy.Services;

public static class Links
{
	public const string ChangePath = "/MkiPasswordPolicy/Change";

	// basePath is Jellyfin's base URL ("" or e.g. "/jellyfin").
	public static string ChangeUrl(HttpRequest request, string basePath)
	{
		string publicUrl = Plugin.Config.PublicUrl?.Trim().TrimEnd('/') ?? string.Empty;
		if (publicUrl.Length > 0)
		{
			return publicUrl + ChangePath;
		}
		return request.Scheme + "://" + request.Host.Value + basePath.TrimEnd('/') + ChangePath;
	}
}
