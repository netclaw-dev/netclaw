// -----------------------------------------------------------------------
// <copyright file="SessionRecentMessageExtractor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

internal static class SessionRecentMessageExtractor
{
    public static IReadOnlyList<ChatMessageDto>? Extract(
        ImmutableList<SerializableChatMessage> history,
        int maxMessages = 20)
    {
        if (history.Count == 0)
            return null;

        const int maxContentLength = 2000;

        var candidates = new List<ChatMessageDto>();
        for (var i = 0; i < history.Count; i++)
        {
            var msg = history[i];

            if (msg.Role is not (ChatRole.User or ChatRole.Assistant))
                continue;

            if (msg.Role == ChatRole.Assistant
                && string.IsNullOrWhiteSpace(msg.Content)
                && msg.ToolCalls.Count > 0)
            {
                continue;
            }

            if (SessionState.IsSystemNudge(msg))
                continue;

            var content = msg.Content;
            if (content.Length > maxContentLength)
                content = string.Concat(content.AsSpan(0, maxContentLength - 3), "...");

            candidates.Add(new ChatMessageDto(
                msg.Role == ChatRole.User ? "user" : "assistant",
                content));
        }

        if (candidates.Count == 0)
            return null;

        if (candidates.Count > maxMessages)
            candidates = candidates.GetRange(candidates.Count - maxMessages, maxMessages);

        return candidates;
    }
}
