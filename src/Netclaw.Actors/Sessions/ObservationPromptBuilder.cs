using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Builds prompts for the Observer phase of compaction.
/// The Observer compresses messages being discarded into concise observation notes
/// that remain in context for future LLM calls.
/// </summary>
public static class ObservationPromptBuilder
{
    /// <summary>
    /// System prompt instructing the model to compress messages into observations.
    /// </summary>
    public static string BuildObservationSystemPrompt()
    {
        return """
            You are an observation compressor. Your job is to compress conversation messages
            into concise, dated observation notes that preserve the most important information.

            Rules:
            - Preserve key facts, decisions, user preferences, and action outcomes
            - Use bullet points for each observation
            - Mark critical observations with [!] prefix
            - Include "Current task:" line if there is an active task in progress
            - Be extremely concise — aim for 3-10x compression
            - Skip pleasantries, acknowledgments, and filler
            - Preserve tool names and outcomes but not full tool arguments/results
            - If the user corrected the assistant, note the correction

            Output format:
            [observations from earlier in this session]
            - [!] User prefers concise output, no caveats
            - Discussed deployment strategy for homelab services
            - Used shell_execute to check Docker containers — 3 running
            - Decision: use Docker Compose for service orchestration
            Current task: setting up monitoring with Prometheus
            """;
    }

    /// <summary>
    /// Builds the user prompt containing the messages to be compressed.
    /// Only includes messages that will be discarded by extractive reduction.
    /// </summary>
    public static string BuildObservationUserPrompt(
        IReadOnlyList<SerializableChatMessage> messagesToCompress)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Compress the following conversation messages into observation notes:");
        sb.AppendLine();

        foreach (var msg in messagesToCompress)
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
                    sb.AppendLine($"[Called: {tc.Name}]");
                }
                if (!string.IsNullOrEmpty(msg.Content))
                    sb.AppendLine(msg.Content);
                sb.AppendLine();
            }
            else if (!string.IsNullOrEmpty(msg.Content))
            {
                // Truncate very long tool results to keep the prompt manageable
                var content = msg.Content;
                if (msg.Role == ChatRole.Tool && content.Length > 500)
                    content = content[..497] + "...";

                sb.AppendLine($"**{roleLabel}:** {content}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Wraps observation text in the standard delimiter format for inclusion in chat history.
    /// </summary>
    public static string WrapObservations(string observationText)
    {
        var trimmed = observationText.Trim();
        if (trimmed.StartsWith("[observations", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        return $"[observations from earlier in this session]\n{trimmed}";
    }
}
