// -----------------------------------------------------------------------
// <copyright file="CompactionPromptBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Builds the session title prompt from recent conversation history.
/// </summary>
public static class CompactionPromptBuilder
{
    /// <summary>
    /// Builds the user prompt for session title generation.
    /// Includes the last few messages for context.
    /// </summary>
    public static string BuildTitleGenerationPrompt(
        IReadOnlyList<SerializableChatMessage> history,
        int maxMessages = 6)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Generate a short title (8 words or fewer) for this conversation. Reply with only the title — no quotes, no punctuation, no explanation.");
        sb.AppendLine();

        var startIndex = 0;
        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].Role == ChatRole.System)
            {
                startIndex = i + 1;
                break;
            }
        }

        var nonSystemMessages = history.Count - startIndex;
        if (nonSystemMessages > maxMessages)
            startIndex = history.Count - maxMessages;

        for (var i = startIndex; i < history.Count; i++)
        {
            var msg = history[i];
            if (msg.Role == ChatRole.System) continue;

            var roleLabel = msg.Role switch
            {
                ChatRole.User => "User",
                ChatRole.Assistant => "Assistant",
                _ => msg.Role.ToString()
            };

            if (!string.IsNullOrEmpty(msg.Content))
                sb.AppendLine($"**{roleLabel}:** {msg.Content}");
        }

        return sb.ToString();
    }

}
