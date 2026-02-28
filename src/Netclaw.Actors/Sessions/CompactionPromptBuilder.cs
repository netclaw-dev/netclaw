using System.Text.RegularExpressions;
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Builds structured prompts for the compaction summarization LLM call.
/// The compaction LLM acts as an intelligent agent that analyzes the conversation,
/// determines what matters, and decides what to preserve vs summarize.
/// </summary>
public static partial class CompactionPromptBuilder
{
    /// <summary>
    /// Builds the system prompt for the compaction summarization call.
    /// The compaction agent produces a structured summary and declares which
    /// recent messages to preserve verbatim.
    /// </summary>
    public static string BuildSummarizationSystemPrompt()
    {
        return """
            You are a conversation compaction agent. Your job is to analyze a conversation
            and produce a structured output that preserves critical context while reducing
            message count.

            First, determine what type of conversation this is (e.g., debugging session,
            feature planning, ticket investigation, code review, general Q&A) and what
            information is critical to preserve.

            Then identify the "active thread" — the recent coherent exchange the user
            expects to continue from. Everything before the active thread gets summarized;
            messages from the active thread onward are preserved verbatim.

            Produce your output in exactly this format:

            ## SUMMARY
            Write a context summary in past tense covering everything before the active
            thread. This is historical context, not instructions — use phrases like
            "The user was working on..." or "We investigated..." rather than imperatives.

            Include these subsections as relevant (omit empty ones):
            - **Goal**: What the user is working on
            - **Completed**: What has been accomplished
            - **Decisions**: Key decisions made and their rationale
            - **Key Facts**: Names, paths, URLs, config values, identifiers
            - **Tool Findings**: Essential outcomes from tool calls (not full outputs)
            - **Open Items**: Pending questions or next steps

            ## PRESERVE_FROM_INDEX
            <N>

            Where N is the 0-based message index (counting from the start of the
            conversation, excluding the system prompt) from which all messages should
            be preserved verbatim. Choose this boundary so the active thread of
            conversation remains intact.

            Guidelines for choosing the preservation boundary:
            - Preserve at least the last 2 user/assistant turn pairs
            - If the user asked a question that hasn't been fully answered, preserve from that question
            - If a multi-step task is in progress, preserve from the step that's currently active
            - When in doubt, preserve more rather than less
            """;
    }

    /// <summary>
    /// Builds the user prompt containing the conversation history to summarize.
    /// Messages are numbered with 0-based indices (excluding system prompt) so the
    /// compaction agent can reference them in PRESERVE_FROM_INDEX.
    /// </summary>
    public static string BuildSummarizationUserPrompt(
        IReadOnlyList<SerializableChatMessage> history)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Analyze the following conversation and produce the structured compaction output described above.");
        sb.AppendLine("Messages are numbered with 0-based indices for your PRESERVE_FROM_INDEX reference.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        var index = 0;
        foreach (var msg in history)
        {
            // Skip system prompt — it's preserved separately and not indexed
            if (msg.Role == ChatRole.System)
                continue;

            var roleLabel = msg.Role switch
            {
                ChatRole.User => "User",
                ChatRole.Assistant => "Assistant",
                ChatRole.Tool => $"Tool ({msg.Name ?? "unknown"})",
                _ => msg.Role.ToString()
            };

            sb.AppendLine($"[{index}] **{roleLabel}:**");

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
            index++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parses the compaction agent's structured output into a summary and preservation boundary.
    /// </summary>
    /// <param name="llmResponse">The raw LLM response text.</param>
    /// <returns>
    /// A tuple of (Summary, PreserveFromIndex). If parsing fails, returns the full response
    /// as the summary and -1 to signal "use config default".
    /// </returns>
    public static (string Summary, int PreserveFromIndex) ParseCompactionOutput(string llmResponse)
    {
        if (string.IsNullOrWhiteSpace(llmResponse))
            return (string.Empty, -1);

        // Find ## SUMMARY section
        var summaryMatch = SummarySection().Match(llmResponse);
        if (!summaryMatch.Success)
            return (llmResponse.Trim(), -1);

        // Find ## PRESERVE_FROM_INDEX section
        var preserveMatch = PreserveFromIndexSection().Match(llmResponse);

        var summary = summaryMatch.Groups[1].Value.Trim();
        var preserveFromIndex = -1;

        if (preserveMatch.Success && int.TryParse(preserveMatch.Groups[1].Value.Trim(), out var parsed))
        {
            preserveFromIndex = parsed;
        }

        return (summary, preserveFromIndex);
    }

    // Matches ## SUMMARY followed by content up to ## PRESERVE_FROM_INDEX or end
    [GeneratedRegex(@"##\s*SUMMARY\s*\n(.*?)(?=##\s*PRESERVE_FROM_INDEX|$)", RegexOptions.Singleline)]
    private static partial Regex SummarySection();

    // Matches ## PRESERVE_FROM_INDEX followed by a number (with optional angle brackets)
    [GeneratedRegex(@"##\s*PRESERVE_FROM_INDEX\s*\n\s*<?(\d+)>?")]
    private static partial Regex PreserveFromIndexSection();

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
