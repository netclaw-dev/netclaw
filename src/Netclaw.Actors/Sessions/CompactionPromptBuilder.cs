using Netclaw.Actors.Protocol;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Builds structured prompts for pre-compaction memory extraction.
/// Summarization prompts were removed when compaction switched from
/// LLM-based abstractive summarization to deterministic extractive reduction.
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

    /// <summary>
    /// Builds the system prompt for pre-compaction memory extraction.
    /// </summary>
    public static string BuildMemoryExtractionSystemPrompt()
    {
        return """
            You are a memory extraction assistant. Your job is to identify durable
            memories from a conversation that should be preserved long-term, beyond
            the current conversation context.

            Extract the following types of information:

            ## Key Facts
            Important facts learned during the conversation (names, preferences,
            configurations, decisions, constraints).

            ## Action Items
            Things the user needs to do or follow up on.

            ## Learned Preferences
            User preferences or working patterns observed during the conversation.

            Be concise. Only extract information that would be valuable in future
            conversations. Skip ephemeral details.
            """;
    }

    /// <summary>
    /// Builds the user prompt for memory extraction.
    /// </summary>
    public static string BuildMemoryExtractionUserPrompt(
        IReadOnlyList<SerializableChatMessage> history)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Extract durable memories from the following conversation:");
        sb.AppendLine();

        foreach (var msg in history)
        {
            if (msg.Role == ChatRole.System) continue;

            var roleLabel = msg.Role switch
            {
                ChatRole.User => "User",
                ChatRole.Assistant => "Assistant",
                ChatRole.Tool => $"Tool ({msg.Name ?? "unknown"})",
                _ => msg.Role.ToString()
            };

            if (msg.ToolCalls.Count > 0)
            {
                sb.AppendLine($"**{roleLabel}:**");
                foreach (var tc in msg.ToolCalls)
                {
                    var meta = ToolCallMeta.Parse(tc.MetaJson);
                    if (meta?.Rationale is { Length: > 0 } rationale)
                    {
                        sb.AppendLine($"→ {tc.Name}: \"{rationale}\"");
                    }
                    else
                    {
                        var args = !string.IsNullOrEmpty(tc.ArgumentsJson) ? $"({tc.ArgumentsJson})" : "";
                        sb.AppendLine($"[Called tool: {tc.Name}{args}]");
                    }
                }
                if (!string.IsNullOrEmpty(msg.Content))
                {
                    sb.AppendLine(msg.Content);
                }
                sb.AppendLine();
            }
            else if (!string.IsNullOrEmpty(msg.Content))
            {
                sb.AppendLine($"**{roleLabel}:** {msg.Content}");
            }
        }

        return sb.ToString();
    }
}
