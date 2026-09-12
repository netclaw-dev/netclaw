// -----------------------------------------------------------------------
// <copyright file="SQLiteMemoryRecallCoordinator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Netclaw.Actors.Memory;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Automatic recall coordinator over SQLite-backed durable memory.
///
/// <para>
/// <b>Hybrid recall (memory-core-redesign Slice 4, design D6):</b> when
/// <c>embedderHolder</c>'s current embedder is available and <c>vectorIndexHolder</c> is
/// wired, each turn embeds the query once — under a fixed <see cref="VectorEmbedSubBudgetMs"/>
/// sub-budget nested inside the caller's overall <c>Memory.RecallTimeoutMs</c> via a linked
/// CTS — and unions FTS5 lexical candidates with the vector index's top-k cosine matches.
/// Vector-only hits are hydrated through <see cref="SQLiteMemoryStore.GetRecallCandidatesByIdsAsync"/>,
/// which applies the IDENTICAL policy predicates <see cref="SQLiteMemoryStore.SearchByPlanAsync"/>
/// applies to lexical hits — a vector hit can never bypass a gate a lexical one would have to
/// clear. Scoring fuses a weighted cosine + squashed lexical-selector-score + dampened
/// class-prior composite, recency-decayed, then admits by one of THREE cases per candidate
/// (gap-repair fix, corrects the original Slice 4 landing):
/// <list type="number">
/// <item>Embedded for the current model AND cosine at or above
/// <see cref="MemoryRecallConfig.MinCosineSimilarity"/> — admitted, ranked by fused score.</item>
/// <item>Embedded AND cosine below the floor — excluded; the calibrated absolute floor gates
/// admission for every candidate the index actually holds a vector for.</item>
/// <item>No embedding row at all for the current model (a coverage gap — not yet backfilled, or
/// written before embeddings were enabled) — the floor cannot apply to a similarity that was
/// never computed, so the candidate bypasses it and competes on fused score alone (cosine term
/// 0). A rate-limited <c>memory_recall_coverage_gap</c> log fires whenever this happens.</item>
/// </list>
/// Zero survivors across all three cases still means zero injection and a HEALTHY
/// (non-degraded) empty result — the caller
/// (<see cref="SessionMessageAssembler.BuildVolatileContextBlock"/>) already omits the
/// <c>[memory-recall]</c> block entirely for that shape. See <see cref="ScoreHybrid"/> for the
/// implementation and <c>openspec/changes/memory-core-redesign/design.md</c> D6 for the
/// migration-plan rationale (coverage gaps degrade loudly to lexical scoring rather than
/// silently blacking out recall while a corpus backfills).
/// </para>
///
/// <para>
/// <b>Degraded path (embedder unavailable, over its sub-budget, or no holder wired):</b> recall
/// falls back to the pre-Slice-4 lexical-only pipeline VERBATIM — same selector scoring, same
/// composite formula, same <see cref="DefaultMinimumRecallCompositeScore"/> floor — which is
/// exactly what <c>MemoryRecallScenarioTests</c> exercises and pins (constructed without either
/// holder). A rate-limited <c>memory_recall_vector_degraded</c> log fires on every fallback
/// reason: Debug when embeddings are disabled by config (the default, intentional state —
/// mirrors <c>MemoryCurationEvaluator</c>'s <c>curation_nominator_degraded</c> level choice, so
/// this is not Warning-level spam on every turn of a deployment that simply hasn't turned
/// embeddings on), Warning when embeddings are enabled but the turn still degraded (a genuine
/// runtime anomaly worth noticing: timeout, embed failure, missing index).
/// </para>
///
/// <para>
/// <b>Floor resolution (memory-query-prefix, design D3):</b> the query is embedded with
/// <see cref="EmbeddingPurpose.RetrievalQuery"/> — the active model's documented query prefix,
/// if any, is applied inside <c>OnnxMemoryEmbedder</c>, not here. The absolute cosine floor
/// itself resolves per turn: an explicit <see cref="MemoryRecallConfig.MinCosineSimilarity"/>
/// override always wins; otherwise the active embedder's manifest-carried
/// <see cref="MemoryEmbedderHolder.CalibratedMinCosineSimilarity"/> applies. When BOTH are
/// absent — a model whose retrieval mode has not been calibrated, with no operator override —
/// hybrid recall is treated as unavailable for the turn: the query is never embedded, and the
/// turn degrades to lexical-only with reason <c>missing_calibration</c> via the same rate-limited
/// <c>memory_recall_vector_degraded</c> log and cooldown as every other vector-degradation
/// reason. This is what makes "a prefixed encoding measured against a floor calibrated for a
/// different encoding" unrepresentable by default (design D3's motivating failure: F0.5 = 0.0 was
/// measured for the prefixed arctic encoding against the old no-prefix 0.68 floor).
/// <c>memory_retrieval_final</c> logs the resolved <c>appliedFloor</c> and its <c>floorSource</c>
/// (<c>manifest</c> or <c>override</c>; <c>n/a</c> in lexical mode, since the composite floor
/// there has no per-model calibration concept).
/// </para>
///
/// <para>
/// <b>Post-floor relevance gate (memory-relevance-gate, design D5/D6/D8):</b> in hybrid mode
/// only, once <see cref="ScoreHybrid"/> produces its floor survivors, a tiny cross-encoder
/// (<c>relevanceScorerHolder</c>) scores each of the top <c>AutoRecallMaxItems</c> survivors
/// jointly against the query — under a sub-budget capped at <see cref="RelevanceGateSubBudgetMs"/>
/// but never larger than whatever remains of the caller's outer <c>RecallTimeoutMs</c> envelope
/// (2026-07 production-canary finding; see <see cref="RelevanceGateSubBudgetMs"/>'s remarks),
/// linked-CTS-nested exactly like the query-embedding sub-budget above — and drops anything
/// below the active threshold (<see cref="MemoryRelevanceGateConfig.Threshold"/> if set,
/// otherwise the scorer's manifest-carried <see cref="RelevanceScorerHolder.CalibratedThreshold"/>).
/// Zero survivors after the gate reuses the SAME zero-injection path as zero survivors at the
/// floor (a healthy empty result, not degraded) — see <see cref="TryApplyRelevanceGateAsync"/>.
/// Gate activation follows <see cref="MemoryEmbeddingsConfig.Enabled"/> unless
/// <see cref="MemoryRelevanceGateConfig.Enabled"/> explicitly overrides it (design D6, "one
/// mental switch"). Every degradation reason (gate disabled, no scorer configured, scorer
/// unavailable, sub-budget exceeded) degrades to the floor's own result unfiltered, logged via
/// the rate-limited <c>memory_recall_gate_degraded</c> — Debug/Warning split mirrors
/// <c>memory_recall_vector_degraded</c>'s exact reasoning, keyed off the gate's OWN resolved
/// enablement rather than the embeddings flag directly.
/// </para>
/// </summary>
public sealed class SQLiteMemoryRecallCoordinator(
    SQLiteMemoryStore store,
    ILogger<SQLiteMemoryRecallCoordinator> logger,
    MemoryConfig memoryConfig,
    TimeProvider timeProvider,
    SessionTuning? sessionTuning = null,
    MemoryEmbedderHolder? embedderHolder = null,
    MemoryVectorIndexHolder? vectorIndexHolder = null,
    RelevanceScorerHolder? relevanceScorerHolder = null) : IMemoryRecallCoordinator
{
    private readonly SessionTuning _sessionTuning = sessionTuning ?? new SessionTuning();
    private readonly MemoryRecallConfig _recallConfig = memoryConfig.Recall;

    // Outer recall envelope (memory-relevance-gate 2026-07 canary fix): read once at
    // construction, same lifecycle assumption as every other Memory.* setting. Used to derive the
    // relevance gate's ACTUAL sub-budget from how much of the envelope is left when the gate
    // stage is reached, not just the fixed RelevanceGateSubBudgetMs ceiling — see that constant's
    // remarks.
    private readonly int _recallTimeoutMs = memoryConfig.RecallTimeoutMs;

    // memory-query-prefix design D3: null (default) follows the active embedder's
    // manifest-carried calibration (embedderHolder.CalibratedMinCosineSimilarity, resolved per
    // turn in TryEmbedQueryAsync since it depends on which model is loaded); an explicit value
    // is an operator override independent of the active model.
    private readonly double? _minCosineSimilarityOverride = memoryConfig.Recall.MinCosineSimilarity;

    // Read once at construction (DI-resolved MemoryConfig is effectively immutable for the
    // process's lifetime — an operator flip requires a restart, same as every other Memory.*
    // setting). Drives the Debug-vs-Warning split on the degraded log: see this class's summary.
    private readonly bool _embeddingsEnabledByConfig = memoryConfig.Embeddings.Enabled;

    // memory-relevance-gate D6: "one mental switch" — Enabled=null follows Embeddings.Enabled
    // exactly (an operator who turns on embeddings gets the gate with nothing else to flip);
    // Enabled=true/false is an explicit override independent of the embeddings switch. Threshold
    // resolution (config override vs. the active scorer's manifest-carried calibrated value)
    // happens per-turn in TryApplyRelevanceGateAsync, since it depends on which model is loaded.
    private readonly bool _relevanceGateEnabledByConfig =
        memoryConfig.Recall.RelevanceGate.Enabled ?? memoryConfig.Embeddings.Enabled;
    private readonly double? _relevanceGateThresholdOverride = memoryConfig.Recall.RelevanceGate.Threshold;

    private readonly DeterministicRetrievalRequestPlanner _deterministicPlanner = new();
    private readonly DeterministicCandidateSelector _candidateSelector = new();
    private readonly ConcurrentDictionary<string, long> _lastVectorDegradedLogMs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastCoverageGapLogMs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastGateDegradedLogMs = new(StringComparer.Ordinal);

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
    ///
    /// <para>
    /// This floor governs the DEGRADED (lexical-only) path exclusively
    /// (memory-core-redesign Slice 4). When a query vector is available the absolute cosine
    /// floor (<see cref="MemoryRecallConfig.MinCosineSimilarity"/>) governs admission for
    /// EMBEDDED candidates instead — the two floors are never both applied to the same
    /// candidate. A candidate with no embedding row at all (a coverage gap) is gated by
    /// neither floor: see <see cref="ScoreHybrid"/>.
    /// </para>
    /// </summary>
    private const double DefaultMinimumRecallCompositeScore = 14.0;

    // RecallRank dampened by 100x so it acts as a tiebreaker (~2 points
    // for DurableFact+MergeDocument) rather than overriding SelectorScore
    // (~4 points per lexical match). Unchanged by Slice 4 — this constant governs the
    // degraded/lexical composite exclusively; hybrid fusion applies its own further-dampened
    // variant (see HybridClassPriorDampeningFactor) sized for a [0,1]-scale formula.
    private const double RecallRankDampeningFactor = 100.0;

    /// <summary>
    /// Sub-budget, in milliseconds, for the per-turn query embedding call
    /// (memory-core-redesign Slice 4, design D6), applied via a CTS linked to (nested inside)
    /// the caller's overall recall <c>ct</c> (<c>Memory.RecallTimeoutMs</c>, default 300ms).
    /// Not a config knob: design D6 measured dynamic-length embedding (Slice 4 Stage A,
    /// <c>tools/embed-latency-bench</c>) at short-query p50 ≈ 19ms / p95 ≈ 21ms on the
    /// reference box, so 150ms leaves roughly 7x headroom over that measurement before a
    /// moderately loaded host would flap into the degraded path on every turn — a deliberately
    /// generous, fixed ceiling rather than a value operators should be tempted to tune per
    /// environment.
    /// </summary>
    private const int VectorEmbedSubBudgetMs = 150;

    /// <summary>
    /// Sub-budget CEILING, in milliseconds, for the per-turn cross-encoder relevance-gate scoring
    /// call (memory-relevance-gate design D5), applied via a CTS linked to (nested inside) the
    /// caller's overall recall <c>ct</c> — the same nesting pattern as
    /// <see cref="VectorEmbedSubBudgetMs"/>. Not a config knob: design D5 measured ~11ms p50 /
    /// ~35ms p95 to score 3 pairs (quantized int8) on the reference CPU, so this leaves headroom
    /// before the sub-budget itself is hit under normal (warm) conditions.
    ///
    /// <para>
    /// <b>This is a CEILING, not the sub-budget actually applied.</b> <see cref="TryApplyRelevanceGateAsync"/>
    /// clamps the real sub-budget to <c>min(RelevanceGateSubBudgetMs, time remaining in the
    /// caller's outer <see cref="MemoryConfig.RecallTimeoutMs"/> envelope)</c> before calling
    /// <c>CancelAfter</c> — the outer linked CTS is already the hard cap on the whole turn, so
    /// this clamp can never let the gate blow past it: on a turn where earlier stages (query
    /// embed, hybrid fusion) already consumed most of the envelope, the gate gets whatever sliver
    /// is left (possibly far less than this ceiling, possibly ~0 which degrades immediately),
    /// never more than the ceiling on a turn with headroom to spare.
    /// </para>
    ///
    /// <para>
    /// <b>2026-07 production-canary finding (raised from 60ms):</b> two live
    /// <c>memory_recall_gate_degraded</c> events (reason <c>score_failed:TaskCanceledException</c>)
    /// both fired in scheduled-reminder sessions waking from an idle period, on a VM host. Log
    /// timestamps showed total turn latency (plan → candidate selection → query embed → hybrid
    /// fusion → gate) already past the entire 300ms <c>RecallTimeoutMs</c> envelope by the time
    /// the gate reached its own scoring call — a cold ONNX session (paged-out weights after the
    /// idle gap) plus host CPU contention at reminder-fire time, not a per-call latency
    /// regression against design D5's reference-box measurement, which still held. The paired fix
    /// is <see cref="Netclaw.Daemon.Services.EmbeddingWarmupHostedService"/>'s periodic keep-warm
    /// tick (keeps both ONNX sessions' working sets resident across idle gaps) plus this raised,
    /// envelope-clamped ceiling — more headroom on a turn that still has budget left, without
    /// ever exceeding the hard 300ms cap.
    /// </para>
    /// </summary>
    private const int RelevanceGateSubBudgetMs = 120;

    /// <summary>Shared empty instance for turns where the gate never ran (disabled, degraded, or lexical mode).</summary>
    private static readonly IReadOnlyDictionary<string, double> EmptyGateScores = new Dictionary<string, double>(0, StringComparer.Ordinal);

    /// <summary>
    /// Number of nearest-neighbor vector candidates fetched per recall turn (design D6). Sized
    /// well above <c>Memory.AutoRecallMaxItems</c> since the union with lexical candidates and
    /// the absolute cosine floor both shrink the pool before the outer MaxItems/char-budget
    /// bounds apply.
    /// </summary>
    private const int VectorTopK = 50;

    /// <summary>
    /// Minimum interval between two <c>memory_recall_vector_degraded</c> log lines for the SAME
    /// degradation reason, so a long-lived degraded condition (embeddings disabled, model
    /// unprovisioned) does not log on every single turn.
    /// </summary>
    private static readonly TimeSpan VectorDegradedLogCooldown = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Hybrid fusion dampens the class prior further than the lexical/degraded path: cosine
    /// (0..1) and squash(selectorScore) (0..~1) are both already bounded fusion terms, so
    /// applying only the lexical path's /100 dampening (max ≈ 4.8 for DurableFact+MergeDocument)
    /// would let the class prior swamp both fusion terms instead of acting as a tiebreaker the
    /// way it does against an unbounded SelectorScore. Dividing the already-/100-dampened prior
    /// by a further 10x caps it at ≈0.48 — comparable in magnitude to, but never dominant over,
    /// VectorWeight*cosine or LexicalWeight*squash(selectorScore).
    /// </summary>
    private const double HybridClassPriorDampeningFactor = 10.0;

    /// <summary>
    /// Half-saturation constant for <c>squash(s) = s / (s + SquashHalfSaturation)</c>, which maps
    /// <see cref="DeterministicCandidateSelector"/>'s unbounded selector score (baseline 1.0,
    /// +4/lexical term, +6/facet, +2/anchor) into [0, 1) for hybrid fusion. At 8.0: a single
    /// lexical-term collision (score ≈5) squashes to ≈0.38, two independent matches (score ≈9)
    /// to ≈0.53, and a facet-boosted match (score ≈15) to ≈0.65 — so lexical evidence
    /// meaningfully moves the fused score without a bare baseline (score 1.0 → squash ≈0.11,
    /// i.e. no real lexical evidence at all) competing with genuine vector similarity.
    /// </summary>
    private const double SquashHalfSaturation = 8.0;

    /// <summary>
    /// Recency-decay floor for the hybrid fusion multiplier (task 4.4):
    /// <c>0.85 + 0.15 * 2^(-ageDays/RecencyHalfLifeDays)</c>. Structurally bounded in
    /// (0.85, 1.0] for any non-negative age (the decay term is always in (0, 1]), so recency can
    /// only break a tie between otherwise-similar matches, never suppress an old-but-strong
    /// match by more than 15%.
    /// </summary>
    private const double RecencyDecayFloor = 0.85;

    private const double RecencyDecayRange = 0.15;

    public async Task<AutomaticRecallResult> RecallAsync(AutomaticRecallRequest request, CancellationToken ct = default)
    {
        // Turn-start timestamp (memory-relevance-gate 2026-07 canary fix): approximates when the
        // caller's own outer RecallTimeoutMs-bounded CTS started (SessionRecallManager creates it
        // immediately before calling RecallAsync), so the relevance gate can later derive how much
        // of that envelope is actually left rather than assuming a fixed sub-budget is always
        // affordable. TimeProvider-based so tests can virtualize it.
        var turnStartedAtTs = timeProvider.GetTimestamp();
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

                var deterministicMaxItems = request.MaxItems <= 0 ? 3 : request.MaxItems;
                var minimumCompositeScore = _sessionTuning.MinimumRecallCompositeScore ?? DefaultMinimumRecallCompositeScore;

                string mode;
                RankedCandidate[] aboveFloor;
                int totalConsidered;
                double appliedFloor;
                string floorSource;

                // ── Vector query embedding (memory-core-redesign Slice 4, task 4.1) ──
                // Attempted once per turn, sub-budgeted inside the caller's overall ct. ANY
                // failure here (unavailable, missing index, sub-budget timeout, embed error,
                // or — memory-query-prefix design D3 — missing retrieval calibration) degrades
                // to the lexical-only path below, logged but never throws.
                var embedded = await TryEmbedQueryAsync(request, ct);

                if (embedded is { } hybridInput)
                {
                    mode = "hybrid";
                    appliedFloor = hybridInput.EffectiveFloor;
                    floorSource = hybridInput.FloorSource;
                    (aboveFloor, totalConsidered) = await ScoreHybrid(
                        request, deterministicPlan, effectiveBoundary, scoredCandidates, hybridInput, ct);
                }
                else
                {
                    mode = "lexical";
                    // The composite floor isn't a per-model calibration — it has no
                    // manifest/override distinction the way the hybrid cosine floor does.
                    appliedFloor = minimumCompositeScore;
                    floorSource = "n/a";
                    var rankedCandidates = scoredCandidates
                        .Select(x => new RankedCandidate(
                            x.Item,
                            x.SelectorScore + (RecallRank(x.Item) / RecallRankDampeningFactor),
                            Cosine: null))
                        .OrderByDescending(x => x.Composite)
                        .ToArray();

                    totalConsidered = rankedCandidates.Length;
                    aboveFloor = rankedCandidates
                        .Where(x => x.Composite >= minimumCompositeScore)
                        .ToArray();
                }

                // ── Post-floor relevance gate (memory-relevance-gate, design D5, tasks 2.1/2.2) ──
                // Only ever attempted in hybrid mode — the floor's absolute cosine gate is what
                // the gate's calibrated threshold was validated against (shoot-out protocol:
                // "candidates = floor-passing top-3"); lexical mode has no query vector, so it
                // already degrades the floor itself, and that degradation is what
                // memory_recall_vector_degraded already reports — a separate gate-specific log
                // for "we're in lexical mode" would just restate the same root cause. `gated` (not
                // `aboveFloor`) feeds the char-budget loop below so `filteredByFloor` in the final
                // log line keeps meaning exactly what it always has: floor-only accounting.
                var gated = aboveFloor;
                var droppedByGate = 0;
                IReadOnlyDictionary<string, double> gateScores = EmptyGateScores;
                var gateElapsedMs = 0.0;
                if (mode == "hybrid" && aboveFloor.Length > 0)
                {
                    // Envelope-derived sub-budget (memory-relevance-gate 2026-07 canary fix): the
                    // gate never gets more than what's actually left of the caller's outer
                    // RecallTimeoutMs envelope — see RelevanceGateSubBudgetMs's remarks.
                    var remainingEnvelope = TimeSpan.FromMilliseconds(_recallTimeoutMs) - timeProvider.GetElapsedTime(turnStartedAtTs);
                    var gateOutcome = await TryApplyRelevanceGateAsync(request, aboveFloor, deterministicMaxItems, remainingEnvelope, ct);
                    gateElapsedMs = gateOutcome.ElapsedMs;
                    if (gateOutcome.Applied)
                    {
                        gated = gateOutcome.Survivors;
                        gateScores = gateOutcome.Scores;
                        droppedByGate = gateOutcome.Dropped;
                    }
                }

                // Char budget: admit items in rank order until the next item's
                // content would blow the per-turn budget. Whole items are
                // dropped, never truncated — a truncated memory reads as
                // complete while missing its distinguishing detail.
                var charBudget = _sessionTuning.MaxRecallInjectedChars;
                var injectedChars = 0;
                var droppedByBudget = 0;
                var budgeted = new List<AutomaticRecallItem>(deterministicMaxItems);
                foreach (var x in gated)
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
                    "memory_retrieval_final session={SessionId} mode={Mode} injectedCount={InjectedCount} filteredByFloor={FilteredByFloor} appliedFloor={AppliedFloor:F3} floorSource={FloorSource} injectedChars={InjectedChars} droppedByBudget={DroppedByBudget} droppedByGate={DroppedByGate} gateElapsedMs={GateElapsedMs:F1} gateScores={GateScores} items={Items}",
                    request.SessionId,
                    mode,
                    deterministicItems.Length,
                    totalConsidered - aboveFloor.Length,
                    appliedFloor,
                    floorSource,
                    injectedChars,
                    droppedByBudget,
                    droppedByGate,
                    gateElapsedMs,
                    string.Join("|", gateScores.Select(kv => $"{kv.Key}={kv.Value:F3}")),
                    string.Join("|", deterministicItems.Select(i => $"{i.Id.Value}=score{i.Score:F3}")));

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

    /// <summary>
    /// Attempts to embed <paramref name="request"/>'s query for hybrid recall
    /// (memory-core-redesign Slice 4, task 4.1). Returns null — logging the specific
    /// degradation reason via <see cref="LogVectorDegraded"/> — for every failure mode:
    /// no embedder wired, embedder unavailable, missing retrieval calibration (memory-query-
    /// prefix design D3), no vector index wired, index reload failure, sub-budget timeout, or an
    /// embedding call exception. Never throws; callers treat null as "run the lexical-only path,"
    /// identically regardless of which reason produced it.
    /// </summary>
    private async Task<(ReadOnlyMemory<float> QueryVector, MemoryVectorIndex Index, double EffectiveFloor, string FloorSource)?> TryEmbedQueryAsync(
        AutomaticRecallRequest request, CancellationToken ct)
    {
        var embedder = embedderHolder?.Current;
        if (embedder is null)
        {
            LogVectorDegraded(request.SessionId.Value, "no_embedder_configured");
            return null;
        }

        if (!embedder.IsAvailable)
        {
            LogVectorDegraded(request.SessionId.Value, "embedder_unavailable");
            return null;
        }

        // Floor resolution (memory-query-prefix design D3): an explicit config override always
        // wins; otherwise follow the active model's manifest-carried calibration. Resolved BEFORE
        // touching the vector index/embedding call below — a model with no calibration and no
        // override has no way to gate admission, so there is nothing to embed a query for.
        double effectiveFloor;
        string floorSource;
        if (_minCosineSimilarityOverride is { } overrideFloor)
        {
            effectiveFloor = overrideFloor;
            floorSource = "override";
        }
        else if (embedderHolder!.CalibratedMinCosineSimilarity is { } manifestFloor)
        {
            effectiveFloor = manifestFloor;
            floorSource = "manifest";
        }
        else
        {
            // A prefix-without-recalibration combination (e.g. the mxbai fallback entry before
            // its own floor sweep lands) is unrepresentable by default — spec scenario "Missing
            // calibration degrades to lexical-only."
            LogVectorDegraded(request.SessionId.Value, "missing_calibration");
            return null;
        }

        if (vectorIndexHolder is null)
        {
            LogVectorDegraded(request.SessionId.Value, "no_vector_index_configured");
            return null;
        }

        MemoryVectorIndex? index;
        try
        {
            index = await vectorIndexHolder.GetCurrentAsync(embedder, ct);
        }
        catch (Exception ex)
        {
            LogVectorDegraded(request.SessionId.Value, $"vector_index_reload_failed:{ex.GetType().Name}");
            return null;
        }

        if (index is null)
        {
            LogVectorDegraded(request.SessionId.Value, "vector_index_unavailable");
            return null;
        }

        try
        {
            using var vectorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            vectorCts.CancelAfter(VectorEmbedSubBudgetMs);
            var vector = await embedder.EmbedAsync(request.Query, EmbeddingPurpose.RetrievalQuery, vectorCts.Token);
            return (vector, index, effectiveFloor, floorSource);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The sub-budget's own timer fired, not the caller's outer recall ct — degrade to
            // lexical rather than propagating a cancellation that would fail the whole turn.
            LogVectorDegraded(request.SessionId.Value, "sub_budget_exceeded");
            return null;
        }
        catch (Exception ex)
        {
            LogVectorDegraded(request.SessionId.Value, $"embed_failed:{ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// Applies the post-floor cross-encoder relevance gate (memory-relevance-gate, design D5,
    /// tasks 2.1/2.2) to the top <paramref name="maxItems"/> of <paramref name="aboveFloor"/> —
    /// the floor already ordered candidates by composite score descending, so this is exactly
    /// "the ≤AutoRecallMaxItems floor survivors" the shoot-out validated the threshold against.
    /// Candidates ranked below that cut never reach the gate at all (they were never going to be
    /// injected either way, since the char-budget loop already bounds injection to the same
    /// <paramref name="maxItems"/>).
    ///
    /// <para>
    /// Returns a not-applied <see cref="RelevanceGateOutcome"/> for every degradation reason —
    /// gate disabled by config, no scorer configured, scorer unavailable, sub-budget exceeded, or
    /// the scoring call itself throwing — mirroring <see cref="TryEmbedQueryAsync"/>'s "never
    /// throws" contract exactly. Callers treat <see cref="RelevanceGateOutcome.Applied"/> false as
    /// "inject the floor's own result unfiltered," identically regardless of which reason produced
    /// it, while <see cref="RelevanceGateOutcome.ElapsedMs"/> still reports whatever time WAS
    /// spent (2026-07 canary observability follow-up: <c>memory_retrieval_final</c> logs this
    /// unconditionally so soak data can quantify margins even on a degraded turn).
    /// </para>
    /// </summary>
    private async Task<RelevanceGateOutcome> TryApplyRelevanceGateAsync(
        AutomaticRecallRequest request, RankedCandidate[] aboveFloor, int maxItems, TimeSpan remainingEnvelope, CancellationToken ct)
    {
        if (!_relevanceGateEnabledByConfig)
        {
            LogGateDegraded(request.SessionId.Value, "gate_disabled_by_config");
            return RelevanceGateOutcome.NotApplied;
        }

        var scorer = relevanceScorerHolder?.Current;
        if (scorer is null)
        {
            LogGateDegraded(request.SessionId.Value, "no_scorer_configured");
            return RelevanceGateOutcome.NotApplied;
        }

        if (!scorer.IsAvailable)
        {
            LogGateDegraded(request.SessionId.Value, "scorer_unavailable");
            return RelevanceGateOutcome.NotApplied;
        }

        var candidatesToScore = aboveFloor.Length > maxItems ? aboveFloor[..maxItems] : aboveFloor;
        var texts = candidatesToScore.Select(x => x.Item.Content ?? string.Empty).ToArray();

        // Envelope-derived sub-budget (2026-07 production-canary finding; see
        // RelevanceGateSubBudgetMs's remarks): never grants more than what's actually left of the
        // caller's outer RecallTimeoutMs envelope, so the outer linked CTS stays the hard cap
        // regardless of how much of it earlier stages already spent.
        var subBudgetMs = (int)Math.Max(0.0, Math.Min(RelevanceGateSubBudgetMs, remainingEnvelope.TotalMilliseconds));

        var gateStartTs = timeProvider.GetTimestamp();
        IReadOnlyList<double> scores;
        try
        {
            using var budgetCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(subBudgetMs), timeProvider);
            using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(ct, budgetCts.Token);
            scores = await scorer.ScoreAsync(request.Query, texts, gateCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The sub-budget's own timer fired, not the caller's outer recall ct — degrade to
            // floor-only rather than propagating a cancellation that would fail the whole turn.
            var elapsedMs = timeProvider.GetElapsedTime(gateStartTs).TotalMilliseconds;
            LogGateDegraded(request.SessionId.Value, "sub_budget_exceeded", elapsedMs);
            return RelevanceGateOutcome.NotApplied with { ElapsedMs = elapsedMs };
        }
        catch (Exception ex)
        {
            var elapsedMs = timeProvider.GetElapsedTime(gateStartTs).TotalMilliseconds;
            LogGateDegraded(request.SessionId.Value, $"score_failed:{ex.GetType().Name}", elapsedMs);
            return RelevanceGateOutcome.NotApplied with { ElapsedMs = elapsedMs };
        }

        var gateElapsedMs = timeProvider.GetElapsedTime(gateStartTs).TotalMilliseconds;
        var threshold = _relevanceGateThresholdOverride ?? relevanceScorerHolder!.CalibratedThreshold;
        var scoreByItemId = new Dictionary<string, double>(candidatesToScore.Length, StringComparer.Ordinal);
        var survivors = new List<RankedCandidate>(candidatesToScore.Length);
        var dropped = 0;
        for (var i = 0; i < candidatesToScore.Length; i++)
        {
            var candidate = candidatesToScore[i];
            var score = scores[i];
            scoreByItemId[candidate.Item.Id] = score;
            if (score >= threshold)
                survivors.Add(candidate);
            else
                dropped++;
        }

        return new RelevanceGateOutcome(true, survivors.ToArray(), scoreByItemId, dropped, gateElapsedMs);
    }

    /// <summary>
    /// Builds the hybrid-mode ranked candidate pool (memory-core-redesign Slice 4, tasks
    /// 4.2-4.4; gap-repair fix corrects the floor semantics below): vector top-k unioned with the
    /// lexical candidates already selected against the plan, fused per design D6's weighted
    /// formula, recency-decayed, then admitted per the three-case semantics documented on this
    /// class's summary. Vector-only ids are hydrated through
    /// <see cref="SQLiteMemoryStore.GetRecallCandidatesByIdsAsync"/> — the SAME policy gates
    /// <see cref="SQLiteMemoryStore.SearchByPlanAsync"/> applied to the lexical candidates — and
    /// scored via <see cref="DeterministicCandidateSelector.Score"/> so a vector hit that also
    /// happens to match plan terms is not scored as if it had none.
    /// </summary>
    private async Task<(RankedCandidate[] AboveFloor, int TotalConsidered)> ScoreHybrid(
        AutomaticRecallRequest request,
        DeterministicRetrievalRequestPlan deterministicPlan,
        string effectiveBoundary,
        IReadOnlyList<DeterministicCandidateSelector.ScoredCandidate> scoredCandidates,
        (ReadOnlyMemory<float> QueryVector, MemoryVectorIndex Index, double EffectiveFloor, string FloorSource) hybridInput,
        CancellationToken ct)
    {
        var (queryVector, vectorIndex, effectiveFloor, _) = hybridInput;

        // embeddedItemIds is read from the IDENTICAL snapshot vectorMatches was scored against
        // (MemoryVectorIndex.TopK's out-parameter overload) so the case-2-vs-case-3 distinction
        // below can never straddle a concurrent index reload. Only matches at or above
        // MinCosineSimilarity are ever returned as vectorMatches, but embeddedItemIds reports
        // EVERY item the index holds a vector for regardless of cosine — that's what lets a
        // candidate embedded-but-below-floor (case 2, excluded) be told apart from a candidate
        // never embedded at all (case 3, a coverage gap that bypasses the floor).
        var vectorMatches = vectorIndex.TopK(
                queryVector.Span, VectorTopK, minCosine: effectiveFloor, out var embeddedItemIds)
            .Where(m => string.Equals(m.ItemKind, MemoryEmbedOnWriteCoordinator.DocumentItemKind, StringComparison.Ordinal))
            .ToArray();
        var cosineByItemId = vectorMatches.ToDictionary(m => m.ItemId, m => m.Cosine, StringComparer.Ordinal);

        var lexicalIds = new HashSet<string>(scoredCandidates.Select(x => x.Item.Id), StringComparer.Ordinal);
        var vectorOnlyIds = vectorMatches
            .Select(m => m.ItemId)
            .Where(id => !lexicalIds.Contains(id))
            .ToArray();

        IReadOnlyList<SQLiteMemoryHydratedItem> vectorOnlyHydrated = vectorOnlyIds.Length == 0
            ? []
            : await store.GetRecallCandidatesByIdsAsync(
                vectorOnlyIds,
                deterministicPlan.AllowedMemoryClasses,
                effectiveBoundary,
                request.Audience,
                allowExpiredEvidence: false,
                ct);

        var pool = new List<(SQLiteMemoryHydratedItem Item, double SelectorScore)>(scoredCandidates.Count + vectorOnlyHydrated.Count);
        foreach (var x in scoredCandidates)
            pool.Add((x.Item, x.SelectorScore));
        foreach (var item in vectorOnlyHydrated)
            pool.Add((item, DeterministicCandidateSelector.Score(deterministicPlan, item)));

        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var gapCandidateCount = 0;
        var fused = pool
            .Select(x =>
            {
                // Only "document" items are ever embedded (MemoryEmbedOnWriteCoordinator.
                // DocumentItemKind), so embeddedItemIds needs no further kind filtering here.
                var isCoverageGap = !embeddedItemIds.Contains(x.Item.Id);
                if (isCoverageGap)
                    gapCandidateCount++;

                // GetValueOrDefault is exact for case 1 (cleared the TopK floor) and a harmless
                // placeholder for case 2 (embedded but below the floor, so TopK never returned a
                // cosine for it) -- 0.0 is guaranteed below any positive floor, so case 2 is
                // rejected below regardless of its true, unrecorded cosine. For case 3 the
                // fusedScore's cosine term is legitimately 0: there is no vector to score.
                var cosine = cosineByItemId.GetValueOrDefault(x.Item.Id, 0.0);
                var squash = x.SelectorScore / (x.SelectorScore + SquashHalfSaturation);
                var classPrior = (RecallRank(x.Item) / RecallRankDampeningFactor) / HybridClassPriorDampeningFactor;
                var fusedScore = (_recallConfig.VectorWeight * cosine) + (_recallConfig.LexicalWeight * squash) + classPrior;
                var recencyMultiplier = RecencyMultiplier(x.Item, nowMs);
                // Cosine is null ONLY for a genuine coverage gap (case 3) -- that null is the
                // floor check's signal below to admit on fused score alone. Case 1 and case 2
                // both carry a non-null cosine (real or the case-2 placeholder above).
                return new RankedCandidate(x.Item, fusedScore * recencyMultiplier, isCoverageGap ? null : cosine);
            })
            .OrderByDescending(x => x.Composite)
            .ToArray();

        if (gapCandidateCount > 0)
            LogCoverageGap(request.SessionId.Value, gapCandidateCount, fused.Length);

        // THE absolute floor (design D6, corrected by the gap-repair fix): cosine gates
        // admission only for a candidate the index actually holds a vector for. A coverage-gap
        // candidate (Cosine null) has no similarity signal to gate on, so it degrades to
        // competing on fused score alone (its cosine term already 0) instead of being dropped
        // outright -- this is the fix: the original Slice 4 landing applied this same floor to
        // EVERY candidate, including ones with no embedding row, which made an unembedded
        // document structurally unrecallable while the embedder was healthy and blacked out
        // recall on any un-backfilled corpus. See this class's summary and design.md D6. Zero
        // survivors overall is still intended, not an error: the "nothing relevant" spec
        // scenario, returned as a healthy empty result by the caller.
        var aboveFloor = fused
            .Where(x => x.Cosine is not { } cosine || cosine >= effectiveFloor)
            .ToArray();

        return (aboveFloor, fused.Length);
    }

    /// <summary>
    /// Recency-decay multiplier applied to a candidate's fused score in hybrid mode only
    /// (memory-core-redesign Slice 4, task 4.4) — see <see cref="RecencyDecayFloor"/>/
    /// <see cref="RecencyDecayRange"/>'s remarks for the formula and its bounds. A
    /// non-positive <see cref="MemoryRecallConfig.RecencyHalfLifeDays"/> disables decay entirely
    /// (multiplier always 1.0) — the schema floors this at 1, but an operator-edited raw config
    /// bypassing the doctor check should degrade to "no decay," not divide by zero.
    /// </summary>
    private double RecencyMultiplier(SQLiteMemoryHydratedItem item, long nowMs)
    {
        var halfLifeDays = _recallConfig.RecencyHalfLifeDays;
        if (halfLifeDays <= 0)
            return 1.0;

        var ageDays = Math.Max(0.0, (nowMs - item.UpdatedAtMs) / 86_400_000.0);
        return RecencyDecayFloor + (RecencyDecayRange * Math.Pow(2.0, -ageDays / halfLifeDays));
    }

    /// <summary>
    /// Rate-limited <c>memory_recall_vector_degraded</c> log (memory-core-redesign Slice 4,
    /// task 4.1): at most one line per <paramref name="reason"/> per
    /// <see cref="VectorDegradedLogCooldown"/>. Debug when embeddings are disabled by config —
    /// the default, intentional operating mode, so this must not be Warning-level spam on every
    /// turn of a deployment that has simply never turned embeddings on (mirrors
    /// <c>MemoryCurationEvaluator</c>'s <c>curation_nominator_degraded</c> reasoning). Warning
    /// when embeddings are enabled but the turn still degraded — a genuine runtime condition an
    /// operator should notice (loud, not silent, per the spec's degradation contract).
    ///
    /// <para>
    /// Best-effort throttle: a race between two concurrent recall calls hitting the same reason
    /// at the same instant could both pass the check and both log once. Acceptable for a
    /// diagnostic throttle, not a correctness gate, so no lock is taken here.
    /// </para>
    /// </summary>
    private void LogVectorDegraded(string sessionId, string reason)
    {
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (_lastVectorDegradedLogMs.TryGetValue(reason, out var lastMs)
            && nowMs - lastMs < VectorDegradedLogCooldown.TotalMilliseconds)
            return;

        _lastVectorDegradedLogMs[reason] = nowMs;

        if (_embeddingsEnabledByConfig)
            logger.LogWarning("memory_recall_vector_degraded session={SessionId} reason={Reason}", sessionId, reason);
        else
            logger.LogDebug("memory_recall_vector_degraded session={SessionId} reason={Reason}", sessionId, reason);
    }

    /// <summary>
    /// Rate-limited <c>memory_recall_coverage_gap</c> log (gap-repair fix to
    /// memory-core-redesign Slice 4, design D6): fires whenever <see cref="ScoreHybrid"/> admits
    /// one or more candidates with no embedding row for the current model, following the exact
    /// same rate-limiting pattern as <see cref="LogVectorDegraded"/> — at most one line per
    /// <see cref="VectorDegradedLogCooldown"/>, tracked in its own dictionary since this is a
    /// distinct condition (a coverage gap in an otherwise-healthy hybrid turn, not a fallback to
    /// the degraded path). Warning when embeddings are enabled — an operator running hybrid
    /// recall should know a corpus gap is being carried by lexical scoring alone until gap
    /// repair / embed-on-write catches up. Debug when embeddings are disabled by config: this
    /// path should be unreachable in that state (no query vector means <see cref="ScoreHybrid"/>
    /// itself never runs), but the level split is kept consistent with
    /// <see cref="LogVectorDegraded"/> rather than asserting unreachability here.
    /// </summary>
    private void LogCoverageGap(string sessionId, int gapCandidateCount, int totalCandidateCount)
    {
        const string reason = "coverage_gap";
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (_lastCoverageGapLogMs.TryGetValue(reason, out var lastMs)
            && nowMs - lastMs < VectorDegradedLogCooldown.TotalMilliseconds)
            return;

        _lastCoverageGapLogMs[reason] = nowMs;

        if (_embeddingsEnabledByConfig)
            logger.LogWarning(
                "memory_recall_coverage_gap session={SessionId} gapCandidates={GapCandidates} totalCandidates={TotalCandidates}",
                sessionId, gapCandidateCount, totalCandidateCount);
        else
            logger.LogDebug(
                "memory_recall_coverage_gap session={SessionId} gapCandidates={GapCandidates} totalCandidates={TotalCandidates}",
                sessionId, gapCandidateCount, totalCandidateCount);
    }

    /// <summary>
    /// Rate-limited <c>memory_recall_gate_degraded</c> log (memory-relevance-gate, design D8,
    /// task 2.2): at most one line per <paramref name="reason"/> per
    /// <see cref="VectorDegradedLogCooldown"/> — the exact same cooldown pattern as
    /// <see cref="LogVectorDegraded"/>, tracked in its own dictionary since gate degradation is a
    /// distinct condition from vector degradation. Debug when the gate is off by config
    /// (following <see cref="MemoryRecallConfig.RelevanceGate"/>'s resolved
    /// <c>Enabled</c> — either it follows a disabled <c>Memory.Embeddings.Enabled</c>, or an
    /// explicit override) — the default, intentional state, so this must not be Warning-level
    /// spam on every turn. Warning when the gate is enabled but the turn still degraded (scorer
    /// unavailable, sub-budget exceeded, scoring threw) — a genuine runtime condition an operator
    /// should notice.
    /// </summary>
    /// <param name="elapsedMs">
    /// Milliseconds actually spent before this degradation was detected (2026-07 canary
    /// observability follow-up) — 0 for reasons where no scoring attempt ever started
    /// (<c>gate_disabled_by_config</c>, <c>no_scorer_configured</c>, <c>scorer_unavailable</c>),
    /// the measured elapsed time for <c>sub_budget_exceeded</c>/<c>score_failed:*</c>.
    /// </param>
    private void LogGateDegraded(string sessionId, string reason, double elapsedMs = 0)
    {
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        if (_lastGateDegradedLogMs.TryGetValue(reason, out var lastMs)
            && nowMs - lastMs < VectorDegradedLogCooldown.TotalMilliseconds)
            return;

        _lastGateDegradedLogMs[reason] = nowMs;

        if (_relevanceGateEnabledByConfig)
            logger.LogWarning("memory_recall_gate_degraded session={SessionId} reason={Reason} elapsedMs={ElapsedMs:F1}", sessionId, reason, elapsedMs);
        else
            logger.LogDebug("memory_recall_gate_degraded session={SessionId} reason={Reason} elapsedMs={ElapsedMs:F1}", sessionId, reason, elapsedMs);
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

    /// <summary>
    /// A candidate after fusion scoring, in either mode. In the degraded/lexical path
    /// <see cref="Cosine"/> is always null (no query vector existed to compute one against). In
    /// hybrid mode it is null ONLY for a genuine coverage gap (no embedding row at all for the
    /// current model — case 3 on this class's summary, bypasses the absolute floor) and non-null
    /// otherwise: the real cosine when it cleared <see cref="MemoryRecallConfig.MinCosineSimilarity"/>
    /// (case 1), or a placeholder 0.0 when it did not (case 2 — the exact below-floor value was
    /// never recorded, but any value below a positive floor rejects identically).
    /// </summary>
    private readonly record struct RankedCandidate(SQLiteMemoryHydratedItem Item, double Composite, double? Cosine);

    /// <summary>
    /// Outcome of one <see cref="TryApplyRelevanceGateAsync"/> attempt (memory-relevance-gate
    /// 2026-07 canary observability follow-up). <see cref="Applied"/> false covers every
    /// degradation reason (gate disabled, no scorer, unavailable, sub-budget exceeded, scoring
    /// threw) — callers treat it identically to the pre-canary-fix "returns null" contract,
    /// falling back to the floor's own unfiltered result. <see cref="ElapsedMs"/> is populated
    /// whenever a scoring attempt actually started (success or failure) so
    /// <c>memory_retrieval_final</c> can log gate latency regardless of outcome; it stays 0 only
    /// when the gate was never engaged at all (disabled/no scorer/unavailable), since no time was
    /// spent gating in those cases.
    /// </summary>
    private readonly record struct RelevanceGateOutcome(
        bool Applied,
        RankedCandidate[] Survivors,
        IReadOnlyDictionary<string, double> Scores,
        int Dropped,
        double ElapsedMs)
    {
        public static readonly RelevanceGateOutcome NotApplied = new(false, [], EmptyGateScores, 0, 0.0);
    }
}
