// -----------------------------------------------------------------------
// <copyright file="ObservationPromptBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Builds prompts for the Observer phase of compaction. The Observer produces
/// a structured 9-section summary of the discarded conversation window that
/// replaces the verbatim messages going forward. The structure (borrowed from
/// Cline's production summarization prompt) is designed to survive successive
/// compactions without decay — the observer prompt explicitly tells the model
/// to preserve any prior <c>[session-summary session:{id}]</c> block verbatim.
/// </summary>
public static class ObservationPromptBuilder
{
    internal const int ToolArgsMaxLength = 120;
    internal const int ToolResultMaxLength = 1500;

    /// <summary>
    /// Header marker used to wrap the structured summary when it is stored as
    /// a System-role message in post-compaction history. Used by consumers to
    /// recognize a prior summary block.
    /// </summary>
    public const string SessionSummaryHeaderPrefix = "[session-summary";

    /// <summary>
    /// Scans <paramref name="messages"/> for the most recent prior
    /// <c>[session-summary ...]</c> block and returns (prior summary text,
    /// remaining messages). When no prior summary is present, the prior
    /// text is null and the message list is returned unchanged. Used by the
    /// compaction pipeline to lift a prior summary out of the discarded
    /// window and inject it into the observer's system prompt as an
    /// explicit "preserve verbatim" block — a structural defense against
    /// summary-over-summary decay.
    /// </summary>
    public static (string? PriorSummary, IReadOnlyList<SerializableChatMessage> Remaining) ExtractPriorSummary(
        IReadOnlyList<SerializableChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var msg = messages[i];
            if (msg.Role != ChatRole.User)
                continue;

            var content = msg.Content;
            if (string.IsNullOrEmpty(content))
                continue;

            if (!content.StartsWith(SessionSummaryHeaderPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var remaining = new List<SerializableChatMessage>(messages.Count - 1);
            for (var j = 0; j < messages.Count; j++)
            {
                if (j != i)
                    remaining.Add(messages[j]);
            }

            return (content, remaining);
        }

        return (null, messages);
    }

    /// <summary>
    /// System prompt instructing the observer LLM to produce a structured
    /// 9-section summary. Takes the self <see cref="SessionId"/> so the
    /// observer can explicitly disambiguate the running session from any
    /// foreign session identifiers referenced in the discarded window.
    /// When <paramref name="priorSummary"/> is non-null, appends a block
    /// containing the verbatim prior summary and instructs the model to
    /// preserve it — the structural anti-decay defense for successive
    /// compactions.
    /// </summary>
    public static string BuildObservationSystemPrompt(SessionId sessionId, string? priorSummary = null)
    {
        var basePrompt = BuildBaseSystemPrompt(sessionId);

        if (string.IsNullOrWhiteSpace(priorSummary))
            return basePrompt;

        return $$"""
            {{basePrompt}}

            ---

            PRIOR SUMMARY (from a previous compaction of this session). You
            MUST preserve the bullets under each of its nine sections verbatim
            in your output. Update sections in place with new observations
            from the discarded messages below — do not rewrite or rephrase
            existing bullets. The prior summary is your anchor; the discarded
            messages are new material to fold in.

            {{priorSummary.Trim()}}
            """;
    }

    private static string BuildBaseSystemPrompt(SessionId sessionId)
    {
        return $$"""
            You are a session summarizer. Your job is to compress conversation messages
            into a structured summary that preserves the grounding needed to continue
            the work after compaction.

            You are summarizing a session with id `{{sessionId.Value}}`. This is the
            SELF session. If observations reference OTHER session ids (from tool calls
            or user content), mark them explicitly as `session:{id}` — never conflate
            them with the self session.

            CRITICAL RULE: If the input already contains a `[session-summary ...]`
            block from a prior compaction, you MUST preserve its sections verbatim
            and update them in place. Do not rewrite, re-summarize, or "improve"
            prior sections — they have already survived compression and will decay
            if touched. Append new observations to the appropriate sections; leave
            the rest unchanged.

            Produce your output using EXACTLY these nine section headers in this
            order. Leave a section's body empty if nothing applies, but do NOT omit
            the header.

            ## 1. Primary Request and Intent
            What the user is fundamentally trying to accomplish in this session.
            One to three sentences. Copy direct phrasing from the user where
            possible — do not paraphrase their stated intent.

            ## 2. Key Technical Concepts
            Named technical entities being actively worked on: file paths, type
            names, method names, struct/class members, identifiers, external APIs,
            frameworks. These are the anchors the agent uses to continue work
            after compaction.

            ## 3. Files and Code Sections
            Paths the agent has read, edited, or referenced, plus the specific
            facts it established about each. Format:
              - `src/Rect.cs`: readonly record struct with `Inset(Thickness)`,
                `Offset(Point)`, `Contains(Point)`, `Intersect(Rect)` methods
              - `src/Thickness.cs`: record struct; `Thickness(left, top, right, bottom)`

            ## 4. Problem Solving
            Problems the agent has diagnosed, and how (tool calls used, evidence
            gathered, hypotheses rejected). Keep tool-call intent compact:
              - `grep_files("Rect", "src/Termina.Layout")` → 7 matches, all in Rect.cs

            ## 5. Pending Tasks
            Work items still open, as a bulleted list. Use `[ ]` / `[x]` markers
            if progress is clear:
              - [ ] Add `Rect.Inflate(int)` method
              - [x] Confirm `Inset` returns new Rect

            ## 6. Task Evolution
            Direct quotes from user messages that changed or clarified the task.
            This section is the single most important anti-drift defense — do NOT
            paraphrase. Include at least one direct quote per major task shift.
            Format:
              - Original: "help me understand Rect"
              - Then: "what about Inset specifically?"
              - Now: "write a unit test for boundary cases"

            ## 7. Current Work
            What the agent is actively doing right now, in one or two sentences.
            This is the resume point if the session is interrupted.

            ## 8. Next Step
            The immediate next action the agent should take when the session
            resumes. One sentence.

            ## 9. Required Files
            Bullet list of file paths the agent should re-read to restore context
            on resume. Relative paths, most relevant first.

            Additional guidance:
            - Skip pleasantries, acknowledgments, and filler
            - Preserve user-requested memories verbatim, marked with [!] prefix
            - For tool calls, preserve arguments that carry intent (search
              patterns, file paths, commands) — not the full argument JSON
            - For tool results, extract the key finding (counts, paths, errors,
              confirmations) — not the full output
            - If the user corrected the assistant, note the correction in section 4
            """;
    }

    /// <summary>
    /// Builds the user prompt containing the messages to be compressed. Only
    /// includes messages that will be discarded by extractive reduction. Tool
    /// calls are rendered with a truncated projection of their arguments so
    /// the observer can see the intent (search pattern, file path, command)
    /// without drowning in raw argument JSON.
    /// </summary>
    public static string BuildObservationUserPrompt(
        IReadOnlyList<SerializableChatMessage> messagesToCompress)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Summarize the following conversation messages using the 9-section template in your system prompt.");
        sb.AppendLine("If any message below is itself a `[session-summary ...]` block from a prior compaction, preserve its sections verbatim and only update them with new facts from subsequent messages.");
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
                    var shortArgs = TruncateArgs(tc.ArgumentsJson);
                    sb.AppendLine($"[Called: {tc.Name}({shortArgs})]");
                }
                if (!string.IsNullOrEmpty(msg.Content))
                    sb.AppendLine(msg.Content);
                sb.AppendLine();
            }
            else if (!string.IsNullOrEmpty(msg.Content))
            {
                var content = msg.Content;
                if (msg.Role == ChatRole.Tool && content.Length > ToolResultMaxLength)
                    content = content[..(ToolResultMaxLength - 3)] + "...";

                sb.AppendLine($"**{roleLabel}:** {content}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Wraps observation text in the standard <c>[session-summary ...]</c>
    /// header format for inclusion in history. The session id is embedded in
    /// the header so consumers can distinguish a self-session summary from
    /// any foreign session id referenced inside the summary body.
    /// </summary>
    public static string WrapObservations(string observationText, SessionId sessionId)
    {
        var header = $"{SessionSummaryHeaderPrefix} session:{sessionId.Value}]";
        var trimmed = observationText.Trim();

        // If the model emitted its own header-like line (possibly from a prior
        // summary it was instructed to preserve), strip that line and use our
        // canonical header so downstream parsers can reliably locate the
        // summary message.
        if (trimmed.StartsWith(SessionSummaryHeaderPrefix, StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("[observations", StringComparison.OrdinalIgnoreCase))
        {
            var newlineIdx = trimmed.IndexOf('\n', StringComparison.Ordinal);
            return newlineIdx >= 0
                ? header + trimmed[newlineIdx..]
                : header;
        }

        return $"{header}\n{trimmed}";
    }

    private static string TruncateArgs(string? argumentsJson)
    {
        if (string.IsNullOrEmpty(argumentsJson) || argumentsJson == "{}")
            return string.Empty;

        var collapsed = System.Text.RegularExpressions.Regex.Replace(
            argumentsJson, @"\s+", " ").Trim();

        return collapsed.Length <= ToolArgsMaxLength
            ? collapsed
            : collapsed[..(ToolArgsMaxLength - 3)] + "...";
    }
}
