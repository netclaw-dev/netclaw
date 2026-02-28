using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Builds structured prompts for the compaction summarization LLM call.
/// The compaction LLM produces a context summary; recent messages are preserved
/// verbatim by the caller based on a fixed config value.
/// </summary>
public static class CompactionPromptBuilder
{
    /// <summary>
    /// Builds the system prompt for the compaction summarization call.
    /// </summary>
    public static string BuildSummarizationSystemPrompt()
    {
        return """
            You are a conversation compaction agent. Your job is to produce a context
            summary of a conversation that will be injected as background context for
            the assistant to continue working.

            Write the summary in past tense — this is historical context, not
            instructions. Use phrases like "The user was working on..." or
            "We investigated..." rather than imperatives like "Do X" or "Continue Y".

            Include these sections as relevant (omit empty ones):

            **Goal**: What the user is working on — the high-level objective.

            **Completed**: What has been accomplished so far.

            **Decisions**: Key decisions made during the conversation and their rationale.

            **Key Facts**: Names, file paths, URLs, configuration values, identifiers,
            or other specifics needed to continue the work.

            **Tool Findings**: Essential outcomes from tool calls — not full outputs,
            just the conclusions that matter.

            **Open Items**: Pending questions, next steps, or unresolved issues.

            Keep the summary concise but complete. Prioritize information that the
            assistant would need to continue the conversation without the user having
            to repeat themselves.
            """;
    }

    /// <summary>
    /// Builds the user prompt containing the conversation history to summarize.
    /// </summary>
    public static string BuildSummarizationUserPrompt(
        IReadOnlyList<SerializableChatMessage> history)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Summarize the following conversation into the structured format described above.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var msg in history)
        {
            // Skip system prompt — it's preserved separately
            if (msg.Role == ChatRole.System)
                continue;

            var roleLabel = msg.Role switch
            {
                ChatRole.User => "User",
                ChatRole.Assistant => "Assistant",
                ChatRole.Tool => $"Tool ({msg.Name ?? "unknown"})",
                _ => msg.Role.ToString()
            };

            sb.AppendLine($"**{roleLabel}:**");

            if (msg.ToolCalls.Count > 0)
            {
                foreach (var tc in msg.ToolCalls)
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
