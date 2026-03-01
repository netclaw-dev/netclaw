using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Builds structured prompts for pre-compaction memory extraction.
/// Summarization prompts were removed when compaction switched from
/// LLM-based abstractive summarization to deterministic extractive reduction.
/// </summary>
public static class CompactionPromptBuilder
{
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
                    var args = !string.IsNullOrEmpty(tc.ArgumentsJson) ? $"({tc.ArgumentsJson})" : "";
                    sb.AppendLine($"[Called tool: {tc.Name}{args}]");
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
