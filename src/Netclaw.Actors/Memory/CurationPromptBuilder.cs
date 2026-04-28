// -----------------------------------------------------------------------
// <copyright file="CurationPromptBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Text.RegularExpressions;

namespace Netclaw.Actors.Memory;

/// <summary>
/// Builds system and user prompts for LLM-tier memory curation evaluation.
/// Used when the rules tier produces an ambiguous result (fuzzy anchor match
/// with content overlap in the 40-80% gray zone).
/// </summary>
public static class CurationPromptBuilder
{
    private const int ContentPreviewMaxChars = 200;

    public static string SystemPrompt { get; } = """
        You are a memory curator. You decide whether a proposed memory should be
        saved, and if so, how it relates to existing memories.

        You will receive:
        - A PROPOSED memory (title, anchor name, content, timestamp)
        - Zero or more EXISTING memories that may be related (title, anchor name,
          content, timestamp, memory ID)

        Make exactly ONE decision:

        SKIP — The proposed memory is redundant. An existing memory already
        captures this information with equal or greater detail. Do not save.

        UPDATE <memory_id> — The proposed memory contains newer or more accurate
        information than the identified existing memory. Replace the existing
        memory's content with the proposed content. Use this when:
        - A version number, date, price, or status has changed
        - The proposal adds meaningful detail to an existing fact
        - The existing memory is stale (older timestamp, outdated information)

        CONSOLIDATE <memory_id> [<memory_id>...] — The proposed memory and one or
        more existing memories describe the same concept under different names.
        Merge them into a single memory under the best anchor name. Use this when:
        - Anchor names are variations of the same thing
          (e.g., "akka-net-release" and "akka-net-latest-version")
        - Content overlaps substantially but is spread across multiple entries

        CREATE — The proposed memory is genuinely new. No existing memory covers
        this topic. Save it as a new entry.

        Respond with ONLY the decision keyword and any required IDs. No explanation.

        Examples:
          SKIP
          UPDATE doc-abc123
          CONSOLIDATE doc-abc123 doc-def456
          CREATE
        """;

    /// <summary>
    /// Build the user message for a curation evaluation request.
    /// </summary>
    public static string BuildUserMessage(
        SQLiteMemoryCurationOperation proposal,
        IReadOnlyList<ExistingMemoryCandidate> candidates)
    {
        var sb = new StringBuilder();

        sb.AppendLine("PROPOSED:");
        sb.AppendLine($"  anchor: {proposal.AnchorCanonicalName}");
        sb.AppendLine($"  title: {proposal.Title}");
        sb.AppendLine($"  content: {TruncateContent(proposal.Content)}");
        sb.AppendLine($"  timestamp: {proposal.FreshnessAtMs}");
        sb.AppendLine();

        if (candidates.Count == 0)
        {
            sb.AppendLine("EXISTING CANDIDATES: none");
        }
        else
        {
            sb.AppendLine("EXISTING CANDIDATES:");
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                sb.AppendLine($"[{i + 1}] id={c.DocumentId} anchor={c.AnchorCanonicalName}");
                sb.AppendLine($"    content: {TruncateContent(c.Content)}");
                sb.AppendLine($"    timestamp: {c.FreshnessAtMs}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parse a single-keyword LLM response into a curation decision.
    /// Returns null if the response cannot be parsed.
    /// </summary>
    public static CurationDecision? ParseResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return null;

        var trimmed = response.Trim();

        // SKIP
        if (trimmed.StartsWith("SKIP", StringComparison.OrdinalIgnoreCase))
        {
            return new CurationDecision(CurationDecisionKind.Skip, null, null, null, "LLM decision: SKIP");
        }

        // CREATE
        if (trimmed.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
        {
            return new CurationDecision(CurationDecisionKind.Create, null, null, null, "LLM decision: CREATE");
        }

        // UPDATE <id>
        var updateMatch = Regex.Match(trimmed, @"^UPDATE\s+(\S+)", RegexOptions.IgnoreCase);
        if (updateMatch.Success)
        {
            var targetId = updateMatch.Groups[1].Value;
            return new CurationDecision(CurationDecisionKind.Update, targetId, null, null, $"LLM decision: UPDATE {targetId}");
        }

        // CONSOLIDATE <id> [<id>...]
        var consolidateMatch = Regex.Match(trimmed, @"^CONSOLIDATE\s+(.+)$", RegexOptions.IgnoreCase);
        if (consolidateMatch.Success)
        {
            var ids = consolidateMatch.Groups[1].Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();
            if (ids.Length > 0)
            {
                return new CurationDecision(
                    CurationDecisionKind.Consolidate,
                    null,
                    ids,
                    null,
                    $"LLM decision: CONSOLIDATE {string.Join(" ", ids)}");
            }
        }

        return null;
    }

    private static string TruncateContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "(empty)";

        return content.Length <= ContentPreviewMaxChars
            ? content
            : content[..ContentPreviewMaxChars] + "...";
    }
}
