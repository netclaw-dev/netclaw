// -----------------------------------------------------------------------
// <copyright file="CurationRulesEvaluator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Memory;

/// <summary>
/// Decision made by the curation rules evaluator.
/// </summary>
public enum CurationDecisionKind
{
    /// <summary>Proposal is redundant — skip without writing.</summary>
    Skip,

    /// <summary>An existing memory should be updated in place.</summary>
    Update,

    /// <summary>Multiple anchors should be consolidated into one.</summary>
    Consolidate,

    /// <summary>Proposal is genuinely new — create a new memory entry.</summary>
    Create,

    /// <summary>Rules tier is uncertain — escalate to LLM tier.</summary>
    Ambiguous
}

/// <summary>
/// An existing memory document that is a candidate for matching against a proposal.
/// </summary>
public sealed record ExistingMemoryCandidate(
    string DocumentId,
    string AnchorId,
    string AnchorCanonicalName,
    string Content,
    long? FreshnessAtMs,
    double Confidence,
    bool IsExactAnchorMatch);

/// <summary>
/// Result of curation evaluation for a single proposal.
/// </summary>
public sealed record CurationDecision(
    CurationDecisionKind Kind,
    string? TargetDocumentId,
    IReadOnlyList<string>? ConsolidationTargetIds,
    string? CanonicalAnchorName,
    string Reason);

/// <summary>
/// Deterministic rules-based evaluator for memory curation decisions.
/// Evaluates a proposal against existing candidates and returns a decision
/// without any LLM involvement.
/// </summary>
public static class CurationRulesEvaluator
{
    private const double HighOverlapThreshold = 0.80;
    private const double AmbiguousLowerThreshold = 0.40;

    // Auto-resolution thresholds for Ambiguous decisions when LLM is unavailable.
    // Both thresholds must be exceeded to auto-resolve to Skip.
    private const double AmbiguousAutoResolveContentThreshold = 0.60;
    private const double AmbiguousAutoResolveAnchorJaccard = 0.50;

    /// <summary>
    /// Evaluate a single proposal against existing candidates and return a decision.
    /// </summary>
    public static CurationDecision Evaluate(
        SQLiteMemoryCurationOperation proposal,
        IReadOnlyList<ExistingMemoryCandidate> candidates)
    {
        // Immutable records always create (append-only semantics)
        if (MemoryDomainEnumExtensions.TryFromWireValue(proposal.Kind, out MemoryKind kind)
            && kind == MemoryKind.Record)
        {
            return new CurationDecision(CurationDecisionKind.Create, null, null, null, "immutable record bypass");
        }

        if (candidates.Count == 0)
        {
            return new CurationDecision(CurationDecisionKind.Create, null, null, null, "no existing candidates");
        }

        // Phase 1: Check exact anchor matches first
        var exactMatches = candidates.Where(c => c.IsExactAnchorMatch).ToArray();
        if (exactMatches.Length > 0)
        {
            return EvaluateExactMatch(proposal, exactMatches);
        }

        // Phase 2: Check fuzzy matches (all candidates at this point are fuzzy)
        return EvaluateFuzzyMatch(proposal, candidates);
    }

    private static CurationDecision EvaluateExactMatch(
        SQLiteMemoryCurationOperation proposal,
        ExistingMemoryCandidate[] exactMatches)
    {
        // Pick the most recent exact match
        var best = exactMatches
            .OrderByDescending(c => c.FreshnessAtMs ?? 0)
            .ThenByDescending(c => c.Confidence)
            .First();

        var overlap = AnchorNameMatcher.ComputeContentOverlap(proposal.Content, best.Content);

        // High content overlap on exact anchor match -> Skip (redundant)
        if (overlap > HighOverlapThreshold)
        {
            return new CurationDecision(
                CurationDecisionKind.Skip,
                best.DocumentId,
                null,
                null,
                $"exact anchor match + high content overlap ({overlap:P0})");
        }

        // Different content — check if proposal is fresher
        var proposalFreshness = proposal.FreshnessAtMs ?? 0;
        var existingFreshness = best.FreshnessAtMs ?? 0;

        if (proposalFreshness >= existingFreshness)
        {
            // Proposal is newer — update existing document
            return new CurationDecision(
                CurationDecisionKind.Update,
                best.DocumentId,
                null,
                null,
                $"exact anchor match + newer content (overlap={overlap:P0})");
        }

        // Proposal is older than existing — skip (stale)
        return new CurationDecision(
            CurationDecisionKind.Skip,
            best.DocumentId,
            null,
            null,
            $"exact anchor match + stale proposal (overlap={overlap:P0})");
    }

    private static CurationDecision EvaluateFuzzyMatch(
        SQLiteMemoryCurationOperation proposal,
        IReadOnlyList<ExistingMemoryCandidate> fuzzyMatches)
    {
        // Find the best fuzzy match by confidence and freshness
        var best = fuzzyMatches
            .OrderByDescending(c => c.Confidence)
            .ThenByDescending(c => c.FreshnessAtMs ?? 0)
            .First();

        var overlap = AnchorNameMatcher.ComputeContentOverlap(proposal.Content, best.Content);

        // High content overlap with fuzzy anchor match -> consolidation (auto-merge)
        if (overlap > HighOverlapThreshold)
        {
            var targetIds = fuzzyMatches.Select(c => c.DocumentId).ToArray();
            return new CurationDecision(
                CurationDecisionKind.Consolidate,
                null,
                targetIds,
                best.AnchorCanonicalName,
                $"fuzzy anchor match + high content overlap ({overlap:P0})");
        }

        // Gray zone (40-80% overlap) -> ambiguous, needs LLM if available
        if (overlap >= AmbiguousLowerThreshold)
        {
            var targetIds = fuzzyMatches.Select(c => c.DocumentId).ToArray();
            return new CurationDecision(
                CurationDecisionKind.Ambiguous,
                null,
                targetIds,
                best.AnchorCanonicalName,
                $"fuzzy anchor match + ambiguous content overlap ({overlap:P0})");
        }

        // Low overlap with fuzzy anchor match -> Create (different topic despite similar name)
        return new CurationDecision(
            CurationDecisionKind.Create,
            null,
            null,
            null,
            $"fuzzy anchor match but low content overlap ({overlap:P0})");
    }

    /// <summary>
    /// Attempt to resolve an Ambiguous decision without LLM involvement.
    /// Returns a Skip decision when both content overlap and anchor Jaccard exceed
    /// auto-resolution thresholds. Returns null if the case is too uncertain —
    /// caller should fall back to Create.
    /// </summary>
    public static CurationDecision? TryAutoResolveAmbiguous(
        SQLiteMemoryCurationOperation proposal,
        IReadOnlyList<ExistingMemoryCandidate> candidates)
    {
        if (candidates.Count == 0)
            return null;

        var best = candidates
            .OrderByDescending(c => c.Confidence)
            .ThenByDescending(c => c.FreshnessAtMs ?? 0)
            .First();

        // Check anchor similarity first (cheap: 2-6 tokens) before content overlap (expensive: full text)
        var proposalTokens = AnchorNameMatcher.Tokenize(proposal.AnchorCanonicalName);
        var bestTokens = AnchorNameMatcher.Tokenize(best.AnchorCanonicalName);
        var anchorJaccard = AnchorNameMatcher.ComputeAnchorJaccard(proposalTokens, bestTokens);
        if (anchorJaccard < AmbiguousAutoResolveAnchorJaccard)
            return null;

        var contentOverlap = AnchorNameMatcher.ComputeContentOverlap(proposal.Content, best.Content);
        if (contentOverlap < AmbiguousAutoResolveContentThreshold)
            return null;

        return new CurationDecision(
            CurationDecisionKind.Skip,
            best.DocumentId,
            null,
            null,
            $"auto-resolved ambiguous: content overlap ({contentOverlap:P0}) + anchor similarity ({anchorJaccard:F2})");
    }

    /// <summary>
    /// Lossless guard for UPDATE decisions. Applying an UPDATE overwrites the target
    /// memory's body with the proposal's, so it is only safe when the proposal
    /// preserves the existing content (the existing body is wholly contained in the
    /// proposal). When it is not preserved, overwriting would silently drop information
    /// the existing memory holds — so downgrade to <see cref="CurationDecisionKind.Skip"/>
    /// and keep the existing memory instead. The cost is dropping a narrower or
    /// divergent proposal's new detail, which is recoverable; destroying accumulated
    /// content is not. Non-UPDATE decisions pass through unchanged.
    /// </summary>
    public static CurationDecision GuardDestructiveUpdate(
        CurationDecision decision,
        SQLiteMemoryCurationOperation proposal,
        IReadOnlyList<ExistingMemoryCandidate> candidates)
    {
        if (decision.Kind != CurationDecisionKind.Update)
            return decision;

        var target = candidates.FirstOrDefault(
            c => string.Equals(c.DocumentId, decision.TargetDocumentId, StringComparison.Ordinal));

        // No identifiable target (e.g., an LLM-returned id not in the candidate set):
        // nothing to overwrite and nothing to verify, so leave the decision unchanged.
        if (target is null || PreservesContent(proposal.Content, target.Content))
            return decision;

        return new CurationDecision(
            CurationDecisionKind.Skip,
            target.DocumentId,
            null,
            null,
            $"update guarded: proposal would not preserve existing content — kept {target.DocumentId}");
    }

    private static bool PreservesContent(string proposed, string existing)
    {
        var existingNorm = NormalizeForContainment(existing);
        if (existingNorm.Length == 0)
            return true; // nothing to preserve

        return NormalizeForContainment(proposed).Contains(existingNorm, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lowercase and collapse all whitespace runs to single spaces so formatting differences
    /// don't hide a genuine containment. Case folding happens here so the <c>Contains</c>
    /// check above can stay Ordinal. Internal (not private) because
    /// <see cref="MemoryContentHasher"/> reuses the exact same normalization for its content
    /// hash — the two "does this content actually differ" judgments in the memory subsystem
    /// must agree, so this is the one place either can drift from the other.
    /// </summary>
    internal static string NormalizeForContainment(string value)
    {
        return string.Join(' ', (value ?? string.Empty)
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
