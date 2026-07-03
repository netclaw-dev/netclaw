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
    /// recall. Calibrated against the new score shape (DurableFact RecallRank
    /// bonus 480 → +4.8 composite, demoted anchor/soft-scope weights) so that
    /// a durable fact needs at least two independent lexical matches
    /// (selector ~9 + class prior ~5.6 = ~14.6) or one lexical match plus a
    /// facet match to clear the floor, while a single-token collision
    /// (selector ~5, composite ~10.6) is rejected. Returning ZERO items when
    /// nothing clears the floor is intended behavior: the July 2026 audit
    /// measured that on 65% of real queries nothing relevant existed to
    /// inject. The <see cref="MemoryRecallScenarioTests"/> gold suite pins
    /// the admit side (pointed two-term questions must still recall); the
    /// audit floor sweep pins the reject side. Override via
    /// <see cref="SessionTuning.MinimumRecallCompositeScore"/>. See issue
    /// #582 and docs/research/memory-audit-2026-07.md.
    /// </summary>
    private const double DefaultMinimumRecallCompositeScore = 14.0;

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

                // Char budget: admit items in rank order until the next item's
                // content would blow the per-turn budget. Whole items are
                // dropped, never truncated — a truncated memory reads as
                // complete while missing its distinguishing detail.
                var charBudget = _sessionTuning.MaxRecallInjectedChars;
                var injectedChars = 0;
                var droppedByBudget = 0;
                var budgeted = new List<AutomaticRecallItem>(deterministicMaxItems);
                foreach (var x in aboveFloor)
                {
                    if (budgeted.Count >= deterministicMaxItems)
                        break;
                    var content = x.Item.Content ?? string.Empty;
                    if (charBudget > 0 && budgeted.Count > 0 && injectedChars + content.Length > charBudget)
                    {
                        droppedByBudget++;
                        continue;
                    }

                    injectedChars += content.Length;
                    budgeted.Add(new AutomaticRecallItem(
                        x.Item.Id,
                        x.Item.Title,
                        content,
                        x.Item.Sensitivity,
                        x.Composite));
                }

                var deterministicItems = budgeted.ToArray();

                logger.LogInformation(
                    "memory_retrieval_final session={SessionId} injectedCount={InjectedCount} filteredByFloor={FilteredByFloor} appliedFloor={AppliedFloor:F1} injectedChars={InjectedChars} droppedByBudget={DroppedByBudget} items={Items}",
                    request.SessionId,
                    deterministicItems.Length,
                    rankedCandidates.Length - aboveFloor.Length,
                    minimumCompositeScore,
                    injectedChars,
                    droppedByBudget,
                    string.Join("|", deterministicItems.Select(i => $"{i.Id.Value}=score{i.Score:F1}")));

                logger.LogDebug(
                    "memory_retrieval_final_detail session={SessionId} items={Items}",
                    request.SessionId,
                    string.Join("|", deterministicItems.Select(i => $"{i.Id.Value}={i.Title}")));

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
        // DurableFact 480 (May-2026 tuned set): after /100 dampening this is a
        // +4.8 composite class prior, sized against the floor of 20 so durable
        // facts clear it on ~3 lexical matches while other classes need a
        // near-perfect lexical hit — evidence/records effectively leave the
        // automatic pool unless the match is overwhelming.
        if (string.Equals(document.MemoryClass, Memory.MemoryClass.DurableFact.ToWireValue(), StringComparison.OrdinalIgnoreCase))
            score += 480;
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
