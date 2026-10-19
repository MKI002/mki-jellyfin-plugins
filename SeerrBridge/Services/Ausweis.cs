using System;

namespace Jellyfin.Plugin.SeerrBridge.Services;

public record Ausweis(Guid UserId, string Benutzer, DateTime GueltigBis);
