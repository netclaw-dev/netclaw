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
        return url.StartsWith(serverUrl, StringComparison.OrdinalIgnoreCase);
    }
}
