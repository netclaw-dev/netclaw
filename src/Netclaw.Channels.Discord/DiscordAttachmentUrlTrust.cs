// -----------------------------------------------------------------------
// <copyright file="DiscordAttachmentUrlTrust.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels.Discord;

internal static class DiscordAttachmentUrlTrust
{
    public static bool IsAllowedAttachmentDomain(string url)
    {
        return url.StartsWith("https://cdn.discordapp.com/", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://media.discordapp.net/", StringComparison.OrdinalIgnoreCase);
    }
}
