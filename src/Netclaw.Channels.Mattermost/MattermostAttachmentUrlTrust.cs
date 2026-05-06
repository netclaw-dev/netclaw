// -----------------------------------------------------------------------
// <copyright file="MattermostAttachmentUrlTrust.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels.Mattermost;

internal static class MattermostAttachmentUrlTrust
{
    /// <summary>
    /// Mattermost file URLs originate from the configured server, so we trust
    /// any URL whose authority matches the server URL provided at startup.
    /// </summary>
    public static bool IsAllowedAttachmentUrl(string url, string serverUrl)
    {
        // Append trailing slash to prevent subdomain bypass:
        // "https://mm.example.com" must not match "https://mm.example.com.evil.com/..."
        var normalized = serverUrl.EndsWith('/')
            ? serverUrl
            : serverUrl + '/';
        return url.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
    }
}
