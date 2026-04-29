// -----------------------------------------------------------------------
// <copyright file="SQLiteMemoryRecallCoordinator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Memory;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Automatic recall coordinator over SQLite-backed durable memory.
/// </summary>
public sealed class SQLiteMemoryRecallCoordinator(
    SQLiteMemoryStore store,
    ILogger<SQLiteMemoryRecallCoordinator> logger,
    SessionTuning? sessionTuning = null) : IMemoryRecallCoordinator
{
    private readonly SessionTuning _sessionTuning = sessionTuning ?? new SessionTuning();
    private readonly DeterministicRetrievalRequestPlanner _deterministicPlanner = new();
    private readonly DeterministicCandidateSelector _candidateSelector = new();

    /// <summary>
    /// Default minimum composite score a candidate must reach to survive
    /// recall. Chosen so that a single lexical-token collision against an
    /// out-of-domain durable fact (selector ~5, composite ~7) is rejected,
    /// while a legitimate two-lexical match in a durable fact (selector ~9,
    /// composite ~11) still passes. Callers who need stricter or looser
    /// filtering can override via <see cref="SessionTuning.MinimumRecallCompositeScore"/>.
    /// See <see cref="MemoryRecallScenarioTests"/> and issue #582 for the
    /// pollution patterns this guards against.
    /// </summary>
    private const double DefaultMinimumRecallCompositeScore = 10.0;

    public async Task<AutomaticRecallResult> RecallAsync(AutomaticRecallRequest request, CancellationToken ct = default)
    {
        try
        {
            if (_sessionTuning.DeterministicRetrievalEnabled)
            {
                DeterministicRetrievalRequestPlan deterministicPlan;
                try
                {
                    deterministicPlan = _deterministicPlanner.Plan(request);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "memory_recall_degraded session={SessionId} stage=planning reason={Reason}", request.SessionId, ex.Message);
                    return new AutomaticRecallResult([], true, ex.Message, "planning");
                }

                logger.LogInformation(
                    "memory_retrieval_request_plan session={SessionId} mode={Mode} candidateLimit={CandidateLimit} facets={Facets} softScopes={SoftScopes} anchorHints={AnchorHints} lexicalTerms={LexicalTerms}",
                    request.SessionId,
                    deterministicPlan.RetrievalMode,
                    deterministicPlan.CandidateLimit,
                    string.Join("|", deterministicPlan.Facets),
                    string.Join("|", deterministicPlan.SoftScopes),
                    string.Join("|", deterministicPlan.AnchorHints),
                    string.Join("|", deterministicPlan.LexicalTerms));

                var effectiveBoundary = Memory.MemoryPolicyScopeResolver.ResolveBoundary(request.Boundary);

                var rawCandidates = await store.SearchByPlanAsync(
                    deterministicPlan.LexicalTerms.Count > 0 ? deterministicPlan.LexicalTerms : [request.Query],
                    deterministicPlan.AllowedMemoryClasses,
                    deterministicPlan.CandidateLimit,
                    effectiveBoundary,
                    request.Audience,
                    allowExpiredEvidence: false,
                    ct);

                var scoredCandidates = _candidateSelector.SelectWithScores(deterministicPlan, rawCandidates);
                logger.LogInformation(
                    "memory_retrieval_candidate_selection session={SessionId} rawCount={RawCount} selectedCount={SelectedCount} scored={Scored}",
                    request.SessionId,
                    rawCandidates.Count,
                    scoredCandidates.Count,
                    string.Join("|", scoredCandidates.Select(x => $"{x.Item.Id}={x.SelectorScore:F1}")));

                // RecallRank dampened by 100x so it acts as a tiebreaker (~2 points
                // for DurableFact+MergeDocument) rather than overriding SelectorScore
                // (~4 points per lexical match).
                const double RecallRankDampeningFactor = 100.0;
                var deterministicMaxItems = request.MaxItems <= 0 ? 3 : request.MaxItems;
                var minimumCompositeScore = _sessionTuning.MinimumRecallCompositeScore ?? DefaultMinimumRecallCompositeScore;
                var rankedCandidates = scoredCandidates
                    .Select(x => (x.Item, x.SelectorScore, Composite: x.SelectorScore + (RecallRank(x.Item) / RecallRankDampeningFactor)))
                    .OrderByDescending(x => x.Composite)
                    .ToArray();
                var aboveFloor = rankedCandidates
                    .Where(x => x.Composite >= minimumCompositeScore)
                    .ToArray();
                var deterministicItems = aboveFloor
                    .Take(deterministicMaxItems)
                    .Select(x => new AutomaticRecallItem(
                        x.Item.Id,
                        x.Item.Title,
                        x.Item.Content,
                        x.Item.Sensitivity,
                        x.Composite))
                    .ToArray();

                logger.LogInformation(
                    "memory_retrieval_final session={SessionId} injectedCount={InjectedCount} filteredByFloor={FilteredByFloor} appliedFloor={AppliedFloor:F1} items={Items}",
                    request.SessionId,
                    deterministicItems.Length,
                    rankedCandidates.Length - aboveFloor.Length,
                    minimumCompositeScore,
                    string.Join("|", deterministicItems.Select(i => $"{i.Id}=score{i.Score:F1}")));

                logger.LogDebug(
                    "memory_retrieval_final_detail session={SessionId} items={Items}",
                    request.SessionId,
                    string.Join("|", deterministicItems.Select(i => $"{i.Id}={i.Title}")));

                return new AutomaticRecallResult(deterministicItems);
            }

            // Deterministic retrieval is the only path. If it's disabled,
            // return nothing rather than falling back to a dead LLM sidecar
            // path. Callers that want zero recall should just not construct
            // a coordinator or set DeterministicRetrievalEnabled = false.
            return new AutomaticRecallResult([]);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "memory_recall_degraded session={SessionId} stage=execution reason={Reason}", request.SessionId, ex.Message);
            return new AutomaticRecallResult([], true, ex.Message, "execution");
        }
    }

    private static int RecallRank(SQLiteMemoryHydratedItem document)
    {
        var score = 0;

        // Prefer deterministic durable classes and explicit/inferred semantics.
        if (string.Equals(document.MemoryClass, Memory.MemoryClass.DurableFact.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score += 120;
        else if (string.Equals(document.MemoryClass, Memory.MemoryClass.Evidence.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score += 40;
        else if (string.Equals(document.MemoryClass, Memory.MemoryClass.Trace.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score -= 400;

        if (string.Equals(document.UpdateSemantics, Memory.MemoryUpdateSemantics.MergeDocument.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score += 80;
        else if (string.Equals(document.UpdateSemantics, Memory.MemoryUpdateSemantics.AppendDocument.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score += 60;

        if (string.Equals(document.UpdateSemantics, Memory.MemoryUpdateSemantics.ImmutableRecord.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score += 30;

        if (string.Equals(document.Title, "verified-tool-finding", StringComparison.OrdinalIgnoreCase))
            score += 25;

        if (document.ExpiresAtMs.HasValue)
            score += 5;

        return score;
    }
}
