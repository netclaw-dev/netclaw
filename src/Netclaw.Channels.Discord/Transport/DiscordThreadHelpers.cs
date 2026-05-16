// -----------------------------------------------------------------------
// <copyright file="DiscordThreadHelpers.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Discord;

namespace Netclaw.Channels.Discord.Transport;

internal static class DiscordThreadHelpers
{
    /// <summary>
    /// Locates the thread anchored on <paramref name="anchorMessageId"/> when a
    /// <c>CreateThreadAsync</c> call lost a race and failed with HTTP 400.
    /// A thread created from a message shares that message's id.
    /// </summary>
    public static async Task<IThreadChannel?> FindExistingThreadAsync(
        ITextChannel textChannel, ulong anchorMessageId)
    {
        var anchorMsg = await textChannel.GetMessageAsync(anchorMessageId);
        if (anchorMsg?.Thread is { } thread)
            return thread;

        var activeThreads = await textChannel.GetActiveThreadsAsync();
        return activeThreads.FirstOrDefault(t => t.Id == anchorMessageId);
    }
}
