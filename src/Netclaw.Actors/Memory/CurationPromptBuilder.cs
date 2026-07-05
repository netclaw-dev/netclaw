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
    // 200 chars starved the decider: distinguishing detail (dates, readings,
    // qualifiers) routinely sits past that cut, which is how the May 2026 eval
    // measured a dated-observation being UPDATE-clobbered. 700 keeps the whole
    // typical memory body visible while bounding worst-case prompt size.
    private const int ContentPreviewMaxChars = 700;

    // The "balanced" prompt measured in the May 2026 decider eval
    // (docs/research/memory-recall-findings-2026-05.md; decider replay harness in the local research store): vs the
    // original baseline it fixed the dated-observation overwrite, improved
    // hard-negative precision (55%→73% combined keep), and shifted decisions
    // from destructive UPDATE to lossless CONSOLIDATE, at −7pt dupe recall.
    public static string SystemPrompt { get; } = """
        You are a memory curator. You decide whether a proposed memory should be
        saved, and if so, how it relates to existing memories.

        You will receive:
        - A PROPOSED memory (title, anchor name, content, timestamp)
        - Zero or more EXISTING memories that may be related (title, anchor name,
          content, timestamp, memory ID)

        THE TEST: do the proposed memory and a candidate describe the SAME
        underlying fact or thing?
        - If they are the same fact merely worded differently, under a different
          title, or with more/less detail — they are the SAME memory. Merge them.
          Different titles and phrasing do NOT make them different memories; only
          different facts do.
        - If they are about DIFFERENT things — a different date, reading, event,
          entity, metric, or point in time — they are DISTINCT. Keep them separate
          (CREATE), even if they share a topic and look similar.

        Merging is destructive, so when you genuinely cannot tell whether two
        memories are the same fact, prefer CREATE. But do NOT split memories that
        are plainly the same fact in other words — that just creates duplicates,
        which is the problem you exist to prevent.

        Dated observations, logs, check-ins, price checks, and time-series
        readings are append-only: a newer reading NEVER overwrites an older one.
        Keep both (CREATE).

        Make exactly ONE decision:

        SKIP — Redundant: a candidate already captures this exact fact with equal
        or greater detail and the proposal adds nothing.

        UPDATE <memory_id> — The proposal is a newer version of the SAME single
        living value (a current setting/status/canonical fact) and the old value
        is now obsolete. NOT for dated readings in a series.

        CONSOLIDATE <memory_id> [<memory_id>...] — The proposal and one or more
        candidates are the same concept under different names/phrasings; merge
        into one, losing nothing.

        CREATE — Genuinely new, OR distinct from the candidates in what it is
        ABOUT (different date, entity, event, or reading).

        For UPDATE and CONSOLIDATE only: after the keyword line, write a line
        containing only "---", then the complete merged document body — a
        LOSSLESS union of the proposal and every candidate you named. You are
        combining, not summarizing: every fact, identifier, number, URL, and date
        from every source must still appear somewhere in the merged body. State
        the newest value first; keep a superseded dated value inline rather than
        deleting it, e.g. "current value is 42 (previously 30 as of 2026-05-13)".
        SKIP and CREATE never include a body — respond with the keyword alone.

        SAME fact, merge: "DB pool size is 20" and "the database connection pool
        max is set to 20".
        DISTINCT, create: a CPU temperature logged at 14:00 vs the same metric at
        15:00; a staging-server config vs a production-server config.

        Respond with ONLY the decision keyword, any required IDs, and — for
        UPDATE/CONSOLIDATE — the merged body after the "---" separator. No other
        explanation.

        Examples:
          SKIP

          UPDATE doc-abc123
          ---
          Config path is /etc/app/config.yaml (previously /etc/app/config.json as
          of 2026-06-01). Default timeout is 30s.

          CONSOLIDATE doc-abc123 doc-def456
          ---
          Akka.NET GitHub repository: https://github.com/akkadotnet/akka.net.
          Latest stable release is 1.5.62 (previously 1.5.60 as of 2026-04-02).

          CREATE
        """;

    /// <summary>
    /// Build the user message for a curation evaluation request.
    /// </summary>
    /// <param name="useFullCandidateContent">
    /// When false (the legacy default, used by the content-search/fuzzy-anchor candidate
    /// path), each candidate's content is truncated to <see cref="ContentPreviewMaxChars"/>
    /// like the proposal's own content always is. When true, candidates are shown in full —
    /// the decider needs complete bodies to synthesize a lossless merge (memory-core-redesign
    /// Slice 3 task 3.2). Nothing in this change sets it true yet: the embedding kNN
    /// nominator that will pass full-content nominated candidates is Stage B (task 3.1),
    /// still to come — this parameter exists now so that work does not need to touch the
    /// prompt-building signature again.
    /// </param>
    public static string BuildUserMessage(
        SQLiteMemoryCurationOperation proposal,
        IReadOnlyList<ExistingMemoryCandidate> candidates,
        bool useFullCandidateContent = false)
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
                sb.AppendLine($"    content: {(useFullCandidateContent ? c.Content : TruncateContent(c.Content))}");
                sb.AppendLine($"    timestamp: {c.FreshnessAtMs}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Parse an LLM response into a curation decision: a keyword line, optionally (for
    /// UPDATE/CONSOLIDATE) followed by a "---" separator line and a merged markdown body
    /// (memory-core-redesign Slice 3 task 3.2). Returns null if the response cannot be
    /// parsed.
    /// </summary>
    public static CurationDecision? ParseResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return null;

        // Reasoning models may inline hidden chain-of-thought wrapped in
        // <think>...</think>; strip it so the bare decision keyword is what we parse.
        // When the serving stack emits reasoning on a separate channel, the text is
        // already just the keyword (and optional body) and this is a no-op.
        var trimmed = StripThinkBlocks(response).Trim();
        if (trimmed.Length == 0)
            return null;

        // SKIP
        if (trimmed.StartsWith("SKIP", StringComparison.OrdinalIgnoreCase))
        {
            return new CurationDecision(CurationDecisionKind.Skip, null, null, null, "LLM decision: SKIP", FromLlmTier: true);
        }

        // CREATE
        if (trimmed.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
        {
            return new CurationDecision(CurationDecisionKind.Create, null, null, null, "LLM decision: CREATE", FromLlmTier: true);
        }

        // UPDATE <id> [---\n<merged body>]
        var updateMatch = Regex.Match(trimmed, @"^UPDATE\s+(\S+)", RegexOptions.IgnoreCase);
        if (updateMatch.Success)
        {
            var targetId = updateMatch.Groups[1].Value;
            return new CurationDecision(
                CurationDecisionKind.Update,
                targetId,
                null,
                null,
                $"LLM decision: UPDATE {targetId}",
                MergedBody: ExtractMergedBody(trimmed),
                FromLlmTier: true);
        }

        // CONSOLIDATE <id> [<id>...] [---\n<merged body>]
        // Captures only the rest of the FIRST line: unlike the pre-Slice-3 shape (always a
        // single keyword line), a merged body may follow on subsequent lines, and `.`
        // without RegexOptions.Singleline cannot cross the newline before it.
        var consolidateMatch = Regex.Match(trimmed, @"^CONSOLIDATE\s+([^\r\n]+)", RegexOptions.IgnoreCase);
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
                    $"LLM decision: CONSOLIDATE {string.Join(" ", ids)}",
                    MergedBody: ExtractMergedBody(trimmed),
                    FromLlmTier: true);
            }
        }

        return null;
    }

    private static readonly Regex MergedBodySeparatorPattern = new(@"(?m)^-{3,}\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Finds the first "---" separator line and returns everything after it, trimmed.
    /// Returns null when there is no separator, or when the text after it is empty once
    /// trimmed (a malformed/empty body is treated as absent, per task 3.2) — the caller
    /// then falls back to the keyword-only decision semantics (task 3.4's append-fallback
    /// routing for a body-absent LLM UPDATE/CONSOLIDATE).
    /// </summary>
    private static string? ExtractMergedBody(string trimmedResponse)
    {
        var separator = MergedBodySeparatorPattern.Match(trimmedResponse);
        if (!separator.Success)
            return null;

        var body = trimmedResponse[(separator.Index + separator.Length)..].Trim();
        return body.Length == 0 ? null : body;
    }

    private static string StripThinkBlocks(string text)
    {
        // Remove complete <think>...</think> spans (case-insensitive, across newlines),
        // then drop any dangling unclosed <think> left by a truncated reasoning trace.
        var stripped = Regex.Replace(text, "<think>.*?</think>", string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var openIndex = stripped.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        return openIndex >= 0 ? stripped[..openIndex] : stripped;
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
