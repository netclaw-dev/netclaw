// -----------------------------------------------------------------------
// <copyright file="MemoryCurationEvaluator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Event;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Configuration;
using SessionId = Netclaw.Actors.Protocol.SessionId;

namespace Netclaw.Actors.Memory;

// ── Logging seam ────────────────────────────────────────────────────

/// <summary>
/// Minimal logging seam so <see cref="MemoryCurationEvaluator"/> emits one set of log
/// markers (<c>curation_dual_search</c>, <c>curation_nominated</c>,
/// <c>curation_nominator_degraded</c>, <c>curation_nominee_no_llm_decision</c>,
/// <c>curation_llm_decision</c>, <c>curation_llm_no_decision</c>, <c>curation_llm_timeout</c>,
/// <c>curation_llm_error</c>, <c>curation_ambiguous_auto_resolved</c>,
/// <c>curation_ambiguous_create_fallback</c>,
/// <c>curation_skip</c>/<c>_update</c>/<c>_consolidate</c>/<c>_create</c>,
/// <c>curation_reanchor</c>, <c>curation_tombstone_anchor</c>) regardless of which of the
/// two callers is driving the evaluator: the inline per-session actor
/// (<see cref="MemoryCurationActor"/>, Akka <see cref="ILoggingAdapter"/>) or the daemon
/// checkpoint worker (<see cref="MemoryCurationEngine"/>, Microsoft.Extensions.Logging
/// <see cref="ILogger"/>). The July 2026 audit tooling greps daemon logs for these exact
/// marker strings, so the message templates and argument order must not drift between the
/// two adapters — this interface is the smallest seam that lets one evaluator body log
/// through either stack without per-caller reformatting.
/// </summary>
internal interface ICurationLog
{
    void Info(string template, params object[] args);

    void Warning(string template, params object[] args);

    void Warning(Exception exception, string template, params object[] args);

    void Debug(string template, params object[] args);
}

internal sealed class AkkaCurationLog(ILoggingAdapter log) : ICurationLog
{
    public void Info(string template, params object[] args) => log.Info(template, args);

    public void Warning(string template, params object[] args) => log.Warning(template, args);

    public void Warning(Exception exception, string template, params object[] args) => log.Warning(exception, template, args);

    public void Debug(string template, params object[] args) => log.Debug(template, args);
}

internal sealed class MicrosoftCurationLog(ILogger log) : ICurationLog
{
    public void Info(string template, params object[] args) => log.LogInformation(template, args);

    public void Warning(string template, params object[] args) => log.LogWarning(template, args);

    public void Warning(Exception exception, string template, params object[] args) => log.LogWarning(exception, template, args);

    public void Debug(string template, params object[] args) => log.LogDebug(template, args);
}

// ── Evaluator ───────────────────────────────────────────────────────

/// <summary>
/// Shared curation decision engine used identically by the inline per-session write
/// pipeline (<see cref="MemoryCurationActor"/>) and the daemon checkpoint-worker write
/// pipeline (<see cref="MemoryCurationEngine"/>). Extracted from
/// <c>MemoryCurationActor.EvaluateSingleAsync</c> (memory-core-redesign Slice 1) so the two
/// pipelines cannot re-diverge the way they had by the July 2026 audit: only the inline
/// path applied <see cref="CurationRulesEvaluator.GuardDestructiveUpdate"/> before this
/// slice (finding D14) and the daemon path performed no relationship evaluation at all.
///
/// Decision flow (<see cref="EvaluateAsync"/>): immutable records bypass evaluation; fuzzy
/// anchor candidates are queried; on an exact anchor match, the deterministic fast path
/// (<see cref="CurationRulesEvaluator.Evaluate"/>'s <c>EvaluateExactMatch</c>) decides Skip/Update
/// with no further evidence gathering. Otherwise, the embedding kNN nominator (memory-core-
/// redesign Slice 3 Stage B, task 3.1, design D4) queries <see cref="MemoryVectorIndex.TopK"/>
/// when the embedder is available: <b>any nominee forces the decision to the LLM tier</b>,
/// regardless of what the lexical rules tier would have decided — the May 2026 measurement
/// (<c>docs/research/memory-recall-findings-2026-05.md</c>; corroborated at corpus scale in
/// <c>docs/research/memory-audit-2026-07.md</c> §5) found no cosine threshold that separates true
/// duplicates from merely-related siblings, so cosine similarity is nomination evidence ONLY —
/// it never auto-merges and never auto-skips. When no nominee fires (or the embedder is
/// unavailable, in which case the pre-Slice-3 lexical content-term search runs instead as an
/// explicitly-logged degraded path), <see cref="CurationRulesEvaluator.Evaluate"/> runs the
/// deterministic tier as before; an Ambiguous result escalates to the LLM tier (when available)
/// followed by <see cref="CurationRulesEvaluator.GuardDestructiveUpdate"/>, or else falls back to
/// <see cref="CurationRulesEvaluator.TryAutoResolveAmbiguous"/> and finally a Create default.
/// <see cref="ApplyDecisionAsync"/> maps the resulting decision to the operation that should
/// be written (or nothing, for Skip), executing Consolidate's re-anchor/tombstone side
/// effects — this mapping is unified too, since a second, hand-copied switch statement per
/// caller is exactly the kind of divergence this slice removes.
///
/// Guard-validated write routing (memory-core-redesign Slice 3, design D5): an LLM-tier
/// UPDATE/CONSOLIDATE decision (<see cref="CurationDecision.FromLlmTier"/>) never overwrites
/// a target's raw body. When it carries a synthesized <see cref="CurationDecision.MergedBody"/>,
/// <see cref="ApplyDecisionAsync"/> validates it with <see cref="MergeGuard"/> against every
/// source body and writes it only on a pass; on guard failure, or when no merged body was
/// produced, the write degrades to a structural append (<see cref="ApplyGuardedMergeOrAppend"/>)
/// so information is never silently dropped. The deterministic tier's UPDATE (the exact-anchor
/// path in <see cref="CurationRulesEvaluator"/>) is the one decision shape that keeps its
/// pre-Slice-3 behavior unchanged: <see cref="CurationRulesEvaluator.GuardDestructiveUpdate"/>
/// already proves the proposal is a content superset of the target before that decision can
/// reach Update, so its raw overwrite is provably non-lossy on its own terms — appending there
/// too would just bloat documents that are legitimately single-value replacements (e.g. a
/// version bump) for no safety benefit. GuardDestructiveUpdate is therefore no longer applied
/// to LLM-tier decisions in <see cref="EvaluateAsync"/>: its raw-proposal containment check
/// would reject a legitimate reworded merge (the unmerged proposal rarely contains the
/// target's exact wording verbatim), and the write-time guard above supersedes it for that
/// tier anyway.
/// </summary>
public sealed class MemoryCurationEvaluator
{
    private readonly SQLiteMemoryStore _store;
    private readonly IChatClient? _llmClient;
    private readonly ICurationLog _log;
    private readonly MemoryCurationConfig _curationConfig;
    private readonly MemoryEmbedderHolder? _embedderHolder;
    private readonly MemoryVectorIndexHolder? _vectorIndexHolder;

    /// <summary>
    /// Constructs an evaluator that logs through Akka's actor logging (the inline
    /// per-session path). <paramref name="llmClient"/> is genuinely optional runtime
    /// state, not a backward-compatibility shim: absence is a real, permanent operating
    /// mode for a session whose configured provider has no compaction-role model, in
    /// which case Ambiguous decisions resolve via
    /// <see cref="CurationRulesEvaluator.TryAutoResolveAmbiguous"/> only.
    /// </summary>
    /// <param name="embedderHolder">
    /// Resolves the process's <see cref="IMemoryEmbedder"/> for the kNN nominator
    /// (memory-core-redesign Slice 3 Stage B). Optional like <paramref name="llmClient"/>: a
    /// null holder is a genuine operating mode (a test harness, or a build with the embedding
    /// subsystem not wired up at all) — <see cref="EvaluateAsync"/> treats it identically to an
    /// unavailable embedder and runs the lexical degraded path.
    /// </param>
    /// <param name="vectorIndexHolder">
    /// Resolves the process's <see cref="MemoryVectorIndex"/> for the same nominator. Optional
    /// for the same reason as <paramref name="embedderHolder"/> — the two are provided together
    /// in production (see <c>Netclaw.Daemon.Program</c>), but each is independently nullable so
    /// a caller missing one still degrades safely rather than throwing.
    /// </param>
    public MemoryCurationEvaluator(
        SQLiteMemoryStore store,
        ILoggingAdapter log,
        MemoryCurationConfig curationConfig,
        IChatClient? llmClient = null,
        MemoryEmbedderHolder? embedderHolder = null,
        MemoryVectorIndexHolder? vectorIndexHolder = null)
        : this(store, (ICurationLog)new AkkaCurationLog(log), curationConfig, llmClient, embedderHolder, vectorIndexHolder)
    {
    }

    /// <summary>
    /// Constructs an evaluator that logs through Microsoft.Extensions.Logging (the daemon
    /// checkpoint-worker path). The daemon worker has no LLM client to give this evaluator
    /// today — that absence is intentional and permanent for this call site, not a
    /// placeholder to be filled in later in this slice. <paramref name="embedderHolder"/> and
    /// <paramref name="vectorIndexHolder"/> ARE wired here (memory-core-redesign Slice 3 Stage
    /// B): the nominator runs on both write pipelines even though only the inline pipeline has
    /// an LLM client — a nominee found with no LLM available still forces the conservative
    /// no-auto-merge Create outcome documented on <see cref="EvaluateAsync"/>.
    /// </summary>
    public MemoryCurationEvaluator(
        SQLiteMemoryStore store,
        ILogger log,
        MemoryCurationConfig curationConfig,
        IChatClient? llmClient = null,
        MemoryEmbedderHolder? embedderHolder = null,
        MemoryVectorIndexHolder? vectorIndexHolder = null)
        : this(store, (ICurationLog)new MicrosoftCurationLog(log), curationConfig, llmClient, embedderHolder, vectorIndexHolder)
    {
    }

    private MemoryCurationEvaluator(
        SQLiteMemoryStore store,
        ICurationLog log,
        MemoryCurationConfig curationConfig,
        IChatClient? llmClient,
        MemoryEmbedderHolder? embedderHolder,
        MemoryVectorIndexHolder? vectorIndexHolder)
    {
        _store = store;
        _log = log;
        _curationConfig = curationConfig;
        _llmClient = llmClient;
        _embedderHolder = embedderHolder;
        _vectorIndexHolder = vectorIndexHolder;
    }

    /// <summary>
    /// Evaluate a single curation proposal against existing memories and return a decision
    /// together with the candidates it was evaluated against — <see cref="ApplyDecisionAsync"/>
    /// needs those same candidate bodies (memory-core-redesign Slice 3) to validate or build a
    /// merged/appended write, and re-querying the store at apply time could see a different
    /// (possibly stale-in-the-other-direction) snapshot than the one the decision was actually
    /// made against.
    /// </summary>
    public async Task<CurationEvaluation> EvaluateAsync(
        SQLiteMemoryCurationOperation operation,
        SessionId sessionId,
        CancellationToken ct = default)
    {
        // Immutable records bypass evaluation
        if (MemoryDomainEnumExtensions.TryFromWireValue(operation.Kind, out MemoryKind kind)
            && kind == MemoryKind.Record)
        {
            return new CurationEvaluation(
                new CurationDecision(CurationDecisionKind.Create, null, null, null, "immutable record bypass"),
                []);
        }

        // Query existing anchors for matches (by name)
        var anchorCandidates = await _store.FindFuzzyAnchorMatchesAsync(operation.AnchorCanonicalName, ct);

        // Build a mutable candidate list — content search may add more candidates below.
        var candidates = new List<ExistingMemoryCandidate>(anchorCandidates);

        // Embedding kNN nomination (memory-core-redesign Slice 3 Stage B, task 3.1, design D4)
        // vs. the pre-Slice-3 lexical content-term search: these are alternatives, not additive.
        // An exact anchor match already resolves deterministically below with no further
        // evidence gathering (the "existing exact-anchor deterministic fast path" design D4
        // calls out as unchanged), so neither runs in that case.
        var hasExactAnchorMatch = anchorCandidates.Any(c => c.IsExactAnchorMatch);
        if (!hasExactAnchorMatch)
        {
            var embedder = _embedderHolder?.Current;
            if (embedder is not null && embedder.IsAvailable && _vectorIndexHolder is not null)
            {
                var (nominees, topCosine) = await NominateAsync(embedder, operation, ct);
                if (nominees.Count > 0)
                {
                    // Merge like the content-term path below: dedup by DocumentId, but a nominee
                    // that is ALSO already present (e.g. it also fuzzy-matched by anchor name)
                    // must keep its cosine tag rather than being dropped as a duplicate.
                    var byDocId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < candidates.Count; i++)
                        byDocId[candidates[i].DocumentId] = i;

                    foreach (var nominee in nominees)
                    {
                        if (byDocId.TryGetValue(nominee.DocumentId, out var existingIndex))
                            candidates[existingIndex] = candidates[existingIndex] with { CosineSimilarity = nominee.CosineSimilarity };
                        else
                            candidates.Add(nominee);
                    }

                    _log.Info(
                        "curation_nominated anchor={0} count={1} topCosine={2}",
                        operation.AnchorCanonicalName,
                        nominees.Count,
                        topCosine.ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            else
            {
                // Degraded path (design D4 point 4): no healthy embedder/index, so fall back to
                // the lexical content-term search exactly as before Slice 3 — this catches
                // semantically identical content under very different anchor names (e.g.,
                // "netclaw-github-repo" vs "netclaw-source-location") when there is no embedding
                // evidence available to do better. Logged unconditionally in this branch (not
                // gated on content being non-blank) so the degraded condition itself is always
                // observable, independent of whether a search ends up running. Debug level, not
                // Warning: "embedder unavailable" is the default operating mode while
                // Memory.Embeddings.Enabled is false (task 3.1's target default), so this would
                // otherwise be Warning-level spam on every single curation evaluation in the
                // common case — the loud, once-per-transition signal already exists at startup
                // (EmbeddingWarmupHostedService's memory_embedding_unavailable /
                // memory_embedding_disabled) and in doctor/status, matching
                // MemoryEmbedOnWriteCoordinator's identical reasoning for its own
                // embedder-unavailable skip log.
                _log.Debug(
                    "curation_nominator_degraded anchor={0} reason={1}",
                    operation.AnchorCanonicalName,
                    embedder is null ? "no_embedder_configured" : !embedder.IsAvailable ? "embedder_unavailable" : "vector_index_unavailable");

                if (!string.IsNullOrWhiteSpace(operation.Content))
                {
                    var contentTerms = operation.Content
                        .Split([' ', '\t', '\n', '\r', '.', ',', ':', ';', '!', '?', '"', '\''],
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(t => t.Length >= 3)
                        .Select(t => t.ToLowerInvariant())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(8)
                        .ToArray();

                    if (contentTerms.Length > 0)
                    {
                        var contentCandidates = await _store.FindCandidatesByContentAsync(contentTerms, ct: ct);

                        // Merge content candidates with anchor candidates, deduplicating by DocumentId.
                        if (contentCandidates.Count > 0)
                        {
                            var existingDocIds = new HashSet<string>(
                                candidates.Select(c => c.DocumentId), StringComparer.OrdinalIgnoreCase);
                            foreach (var cc in contentCandidates)
                            {
                                if (!existingDocIds.Contains(cc.DocumentId))
                                    candidates.Add(cc);
                            }

                            _log.Debug(
                                "curation_dual_search anchor={0} anchor_hits={1} content_hits={2} merged={3}",
                                operation.AnchorCanonicalName,
                                anchorCandidates.Count,
                                contentCandidates.Count,
                                candidates.Count);
                        }
                    }
                }
            }
        }

        // Any nominee forces the LLM tier, regardless of what the lexical rules tier below would
        // have decided (design D4: "no cosine threshold separates duplicates from siblings," so
        // similarity nominates only — it never auto-merges or auto-skips on its own).
        var hasNominee = candidates.Any(c => c.CosineSimilarity.HasValue);
        if (hasNominee)
        {
            if (_llmClient is not null)
            {
                var nomineeLlmDecision = await TryLlmEvaluationAsync(
                    _llmClient, sessionId, operation, candidates, _log, _curationConfig, useFullCandidateContent: true);
                if (nomineeLlmDecision is not null)
                {
                    // GuardDestructiveUpdate is deliberately NOT applied here — see this class's
                    // remarks; write-time MergeGuard/structural-append routing supersedes it for
                    // every LLM-tier decision.
                    _log.Info(
                        "curation_llm_decision anchor={0} decision={1} reason={2}",
                        operation.AnchorCanonicalName,
                        nomineeLlmDecision.Kind,
                        nomineeLlmDecision.Reason);
                    return new CurationEvaluation(nomineeLlmDecision, candidates);
                }

                // LLM failed to produce a parseable decision — fall through to the conservative
                // no-LLM handling below rather than a second (Jaccard-based) attempt.
            }

            // No LLM available, or the LLM call failed: a nominee is semantic-near evidence that
            // TryAutoResolveAmbiguous's Jaccard heuristics cannot safely adjudicate (that is
            // exactly the ambiguity the May 2026 measurement showed deterministic logic cannot
            // resolve), so this does NOT call TryAutoResolveAmbiguous — doing so risks an
            // auto-Skip driven by cosine-adjacent content that is actually a distinct sibling.
            // The conservative outcome is Create: the cost is a possible duplicate document,
            // recoverable by a future curation pass; a wrong auto-merge is not.
            _log.Warning(
                "curation_nominee_no_llm_decision anchor={0} llm_available={1} — conservative create",
                operation.AnchorCanonicalName,
                _llmClient is not null);
            return new CurationEvaluation(
                new CurationDecision(CurationDecisionKind.Create, null, null, null,
                    "nominee present but no LLM decision available: conservative create (no auto-merge on cosine alone)"),
                candidates);
        }

        // Apply rules tier
        var rulesDecision = CurationRulesEvaluator.Evaluate(operation, candidates);

        // If rules tier is ambiguous and LLM is available, escalate
        if (rulesDecision.Kind == CurationDecisionKind.Ambiguous && _llmClient is not null)
        {
            var llmDecision = await TryLlmEvaluationAsync(
                _llmClient, sessionId, operation, candidates, _log, _curationConfig);
            if (llmDecision is not null)
            {
                // GuardDestructiveUpdate is deliberately NOT applied here — see this class's
                // remarks. LLM-tier UPDATE/CONSOLIDATE write safety is now the write-time
                // MergeGuard/structural-append routing in ApplyDecisionAsync, which handles
                // both the has-a-merged-body and no-merged-body cases without needing this
                // decision downgraded first.
                _log.Info(
                    "curation_llm_decision anchor={0} decision={1} reason={2}",
                    operation.AnchorCanonicalName,
                    llmDecision.Kind,
                    llmDecision.Reason);
                return new CurationEvaluation(llmDecision, candidates);
            }

            // LLM failed — fall through to deterministic auto-resolution below
        }

        // Ambiguous (LLM unavailable or failed) — try deterministic auto-resolution
        if (rulesDecision.Kind == CurationDecisionKind.Ambiguous)
        {
            var autoResolved = CurationRulesEvaluator.TryAutoResolveAmbiguous(operation, candidates);
            if (autoResolved is not null)
            {
                _log.Info(
                    "curation_ambiguous_auto_resolved anchor={0} decision={1} reason={2}",
                    operation.AnchorCanonicalName,
                    autoResolved.Kind,
                    autoResolved.Reason);
                return new CurationEvaluation(autoResolved, candidates);
            }

            _log.Warning("curation_ambiguous_create_fallback anchor={0} llm_available={1}",
                operation.AnchorCanonicalName, _llmClient is not null);
            return new CurationEvaluation(
                new CurationDecision(CurationDecisionKind.Create, null, null, null,
                    "ambiguous: auto-resolve insufficient, defaulting to create"),
                candidates);
        }

        return new CurationEvaluation(
            CurationRulesEvaluator.GuardDestructiveUpdate(rulesDecision, operation, candidates),
            candidates);
    }

    /// <summary>
    /// Embedding kNN nomination step (memory-core-redesign Slice 3 Stage B, task 3.1). Embeds
    /// the proposal's <c>"{title}\n{content}"</c> text — the exact concatenation
    /// <see cref="MemoryEmbedOnWriteCoordinator"/> embeds a written document with, so a proposal
    /// and its eventual stored form land in the same region of embedding space — queries
    /// <paramref name="embedder"/>'s current <see cref="MemoryVectorIndex"/> for up to
    /// <see cref="MemoryCurationConfig.NominatorK"/> neighbors at or above
    /// <see cref="MemoryCurationConfig.NominatorSimilarityThreshold"/>, then hydrates the
    /// matched document ids into full-content candidates tagged with their cosine.
    ///
    /// <para>
    /// <b>Cost:</b> curation runs off the interactive turn path (checkpoint/session-boundary
    /// triggered, via the daemon worker or the inline actor's post-turn write phase) — unlike
    /// the sub-150ms recall-query budget (design D6), there is no per-turn latency budget here,
    /// so the ~210ms median / ~280ms mean single-embed cost measured for the nominator model
    /// (<c>docs/research/memory-audit-2026-07.md</c> §4, snowflake-arctic-embed 137M) is
    /// acceptable — it does not block a user-visible response.
    /// </para>
    ///
    /// <para>
    /// <b>Known limitation (intra-batch nomination):</b> a proposal is nominated against the
    /// store's already-committed embeddings only — it cannot nominate itself (it has not been
    /// written yet), which is correct. But when a caller evaluates several proposals as one
    /// batch (both write pipelines evaluate a checkpoint's candidates in a loop before any of
    /// them commits), two mutually-near-duplicate proposals within that SAME batch will not see
    /// each other, because neither is in the index yet when the other is nominated. This is an
    /// accepted gap for this slice, not a design goal: cross-batch and steady-state dedup (the
    /// overwhelming majority of write traffic) both work correctly, and closing the intra-batch
    /// case would require either serializing writes mid-batch or a second batch-local similarity
    /// pass — deferred until evidence shows same-batch near-duplicates are common enough to
    /// justify the added complexity.
    /// </para>
    /// </summary>
    private async Task<(IReadOnlyList<ExistingMemoryCandidate> Nominees, double TopCosine)> NominateAsync(
        IMemoryEmbedder embedder,
        SQLiteMemoryCurationOperation operation,
        CancellationToken ct)
    {
        var vectorIndex = await _vectorIndexHolder!.GetCurrentAsync(embedder, ct);
        if (vectorIndex is null)
            return ([], 0);

        var queryVector = await embedder.EmbedAsync($"{operation.Title}\n{operation.Content}", ct);
        var matches = vectorIndex.TopK(
            queryVector.Span, _curationConfig.NominatorK, _curationConfig.NominatorSimilarityThreshold);
        if (matches.Count == 0)
            return ([], 0);

        // Only "document" items are ever embedded (MemoryEmbedOnWriteCoordinator.DocumentItemKind)
        // — immutable records bypass curation and are never embedded — but filter defensively
        // rather than assume, since a future item kind sharing this model's embedding table
        // would otherwise be silently mis-hydrated as a document candidate.
        var documentIds = matches
            .Where(m => string.Equals(m.ItemKind, MemoryEmbedOnWriteCoordinator.DocumentItemKind, StringComparison.Ordinal))
            .Select(m => m.ItemId)
            .ToArray();
        if (documentIds.Length == 0)
            return ([], 0);

        var hydrated = await _store.GetCandidatesByIdsAsync(documentIds, ct);
        if (hydrated.Count == 0)
            return ([], 0);

        var cosineByDocId = matches.ToDictionary(m => m.ItemId, m => m.Cosine, StringComparer.Ordinal);
        var tagged = hydrated
            .Select(c => cosineByDocId.TryGetValue(c.DocumentId, out var cosine) ? c with { CosineSimilarity = cosine } : c)
            .ToArray();

        // matches is already sorted descending by cosine (MemoryVectorIndex.TopK's contract).
        return (tagged, matches[0].Cosine);
    }

    internal static async Task<CurationDecision?> TryLlmEvaluationAsync(
        IChatClient llmClient,
        SessionId sessionId,
        SQLiteMemoryCurationOperation operation,
        IReadOnlyList<ExistingMemoryCandidate> candidates,
        ICurationLog log,
        MemoryCurationConfig curationConfig,
        bool useFullCandidateContent = false)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(curationConfig.LlmTimeoutSeconds));

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, CurationPromptBuilder.SystemPrompt),
                new(ChatRole.User, CurationPromptBuilder.BuildUserMessage(operation, candidates, useFullCandidateContent))
            };

            // SessionScopedChatOptions carries the session id so this sidecar's chat-client
            // diagnostics route to the session's session.log and correlate in Seq/OTLP (replaces
            // the deleted SessionDiagnosticsContext AsyncLocal).
            var options = new SessionScopedChatOptions
            {
                SessionId = sessionId.Value,
                // Token cap is the THIRD line of defense, so it must never be the
                // binding constraint. Layering: (1) reasoning suppression below is
                // the primary fix — suppressed/non-reasoning models emit just the
                // keyword and never approach any cap; (2) the call timeout above
                // bounds wall-clock when a model ignores suppression and thinks at
                // length. The cap only matters in the remaining window — suppression
                // ignored but thinking finishes inside the timeout — where a tight
                // cap truncates mid-think and reproduces the measured
                // responseLength=0 empty-reply failure (July 2026 audit: at 512, a
                // Qwen3.6-class model produced 0 successful curation decisions
                // ever). Unemitted tokens cost nothing, so this is sized generously by
                // default — see Memory.Curation.LlmMaxOutputTokens (MemoryCurationConfig).
                MaxOutputTokens = curationConfig.LlmMaxOutputTokens,
                // Belt: ask the serving stack not to think at all for this
                // keyword-classification call. This expresses intent only — the raw
                // provider-dialect field name (vLLM/llama.cpp/SGLang's
                // chat_template_kwargs.enable_thinking, Ollama's think) is NOT this
                // call site's business, because the model behind SessionId's
                // provider varies per deployment and strict SDKs (official OpenAI,
                // Anthropic) reject or ignore unknown top-level fields differently
                // than self-hosted servers do. NetclawChatOptionKeys.SuppressReasoning
                // is a provider-agnostic intent key; ReasoningSuppressionChatClient
                // (Netclaw.Daemon, wrapping every chat client the daemon constructs)
                // reads it, removes it, and maps it to the dialect the active
                // provider plugin declares (ILlmProviderPlugin.SuppressionDialect) —
                // or strips it with no replacement for providers with no equivalent.
                // StripThinkBlocks in ParseResponse remains the braces.
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [NetclawChatOptionKeys.SuppressReasoning] = true
                }
            };

            var result = await StreamingResponseReader.ReadAsync(llmClient, messages, options, cts.Token);
            var responseText = result.Response.Text?.Trim();
            var decision = string.IsNullOrWhiteSpace(responseText)
                ? null
                : CurationPromptBuilder.ParseResponse(responseText);

            if (decision is null)
            {
                // No silent fallback: a curation LLM that yields nothing parseable is a
                // real signal (empty/garbled output, or a reasoning model that consumed
                // its token budget). Surface it so the deterministic fallback that
                // follows is observable rather than invisible.
                log.Warning(
                    "curation_llm_no_decision anchor={0} responseLength={1} — using deterministic fallback",
                    operation.AnchorCanonicalName,
                    responseText?.Length ?? 0);
                return null;
            }

            return decision;
        }
        catch (OperationCanceledException)
        {
            log.Warning("curation_llm_timeout anchor={0}", operation.AnchorCanonicalName);
            return null;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "curation_llm_error anchor={0}", operation.AnchorCanonicalName);
            return null;
        }
    }

    // ── Decision application (shared write-mapping + consolidation side effects) ──

    /// <summary>
    /// Maps a decision to the operation that should be written (null for Skip), executing
    /// Consolidate's re-anchor/tombstone side effects against the store. Shared so this
    /// mapping is identical from both the actor's write phase and the daemon's
    /// checkpoint-apply phase — before this slice only the actor performed it at all.
    /// </summary>
    /// <param name="candidates">
    /// The exact candidate set <paramref name="decision"/> was evaluated against
    /// (<see cref="EvaluateAsync"/>'s <see cref="CurationEvaluation.Candidates"/>) — the
    /// source of the target/consolidation-target bodies that guard-validated write routing
    /// (memory-core-redesign Slice 3) merges or appends against.
    /// </param>
    public async Task<SQLiteMemoryCurationOperation?> ApplyDecisionAsync(
        SQLiteMemoryCurationOperation operation,
        CurationDecision decision,
        IReadOnlyList<ExistingMemoryCandidate> candidates,
        CancellationToken ct = default)
    {
        switch (decision.Kind)
        {
            case CurationDecisionKind.Skip:
                _log.Info(
                    "curation_skip anchor={0} reason={1}",
                    operation.AnchorCanonicalName,
                    decision.Reason);
                return null;

            case CurationDecisionKind.Update:
                return ApplyUpdate(operation, decision, candidates);

            case CurationDecisionKind.Consolidate:
                _log.Info(
                    "curation_consolidate anchor={0} canonicalAnchor={1} targetIds=[{2}] reason={3}",
                    operation.AnchorCanonicalName,
                    decision.CanonicalAnchorName!,
                    decision.ConsolidationTargetIds is not null ? string.Join(",", decision.ConsolidationTargetIds) : "",
                    decision.Reason);

                await ExecuteConsolidationAsync(operation, decision, ct);
                return ApplyConsolidate(operation, decision, candidates);

            case CurationDecisionKind.Create:
                _log.Info(
                    "curation_create anchor={0} reason={1}",
                    operation.AnchorCanonicalName,
                    decision.Reason);
                return operation;

            case CurationDecisionKind.Ambiguous:
            default:
                // Should not reach here — ambiguous is resolved before writing
                _log.Warning("curation_unexpected_ambiguous anchor={0}, defaulting to create", operation.AnchorCanonicalName);
                return operation;
        }
    }

    /// <summary>
    /// Write routing for an Update decision. The deterministic tier (exact-anchor path,
    /// <see cref="CurationDecision.FromLlmTier"/> false) keeps its pre-Slice-3 raw overwrite —
    /// see this class's remarks for why that is still provably non-lossy. Every LLM-tier
    /// Update routes through <see cref="ApplyGuardedMergeOrAppend"/> instead.
    /// </summary>
    private SQLiteMemoryCurationOperation ApplyUpdate(
        SQLiteMemoryCurationOperation operation,
        CurationDecision decision,
        IReadOnlyList<ExistingMemoryCandidate> candidates)
    {
        _log.Info(
            "curation_update anchor={0} targetDoc={1} reason={2}",
            operation.AnchorCanonicalName,
            decision.TargetDocumentId!,
            decision.Reason);

        if (!decision.FromLlmTier)
        {
            // Deterministic exact-anchor path: GuardDestructiveUpdate has already verified
            // (in EvaluateAsync) that the proposal preserves the target's content, so this
            // overwrite cannot drop information. Set MemoryId so the ON CONFLICT UPDATE fires
            // against the existing document.
            return operation with { MemoryId = decision.TargetDocumentId };
        }

        var target = candidates.FirstOrDefault(
            c => string.Equals(c.DocumentId, decision.TargetDocumentId, StringComparison.Ordinal));
        if (target is null)
        {
            // The LLM named a document id outside the evaluated candidate set — there is no
            // known existing body to merge with or safely append to. Rather than trust an
            // unverified id for an overwrite, fall through as a plain create.
            _log.Warning(
                "curation_update_target_unknown anchor={0} targetId={1} — creating instead",
                operation.AnchorCanonicalName,
                decision.TargetDocumentId ?? "(null)");
            return operation;
        }

        return ApplyGuardedMergeOrAppend(
            operation, decision.MergedBody, target.DocumentId, target.Content, [target.Content, operation.Content]);
    }

    /// <summary>
    /// Write routing for a Consolidate decision, after <see cref="ExecuteConsolidationAsync"/>'s
    /// re-anchor/tombstone side effects. Both tiers flow through
    /// <see cref="ApplyGuardedMergeOrAppend"/> uniformly: the deterministic tier (fuzzy match
    /// ≥80% overlap, no LLM call) never produces a <see cref="CurationDecision.MergedBody"/>,
    /// so it always takes that method's append branch — the only lossless option available
    /// without an LLM-synthesized merge. This also fixes the pre-Slice-3 gap where a
    /// deterministic Consolidate reached the store with no guard at all
    /// (<see cref="CurationRulesEvaluator.GuardDestructiveUpdate"/> is a no-op for Consolidate).
    /// </summary>
    private SQLiteMemoryCurationOperation ApplyConsolidate(
        SQLiteMemoryCurationOperation operation,
        CurationDecision decision,
        IReadOnlyList<ExistingMemoryCandidate> candidates)
    {
        var canonicalAnchor = decision.CanonicalAnchorName ?? operation.AnchorCanonicalName;
        var operationWithAnchor = operation with { AnchorCanonicalName = canonicalAnchor };

        var consolidationTargets = decision.ConsolidationTargetIds is null
            ? []
            : candidates
                .Where(c => decision.ConsolidationTargetIds.Contains(c.DocumentId, StringComparer.Ordinal))
                .ToArray();

        if (consolidationTargets.Length == 0)
        {
            // No known candidate content to merge/append against (e.g. an id the LLM invented,
            // or a decision with no target ids at all) — nothing to preserve, so the store's
            // own anchor-based lookup resolves the target document as it did before this slice.
            return operationWithAnchor;
        }

        // Deterministic pick of the primary consolidation target: same ordering
        // CurationRulesEvaluator uses for "best" (confidence, then freshness). Pinning the
        // write to this SAME document — rather than trusting the store's separate
        // updated_at-based anchor lookup — guarantees the write lands on the document
        // MergeGuard actually validated content against.
        var primary = consolidationTargets
            .OrderByDescending(c => c.Confidence)
            .ThenByDescending(c => c.FreshnessAtMs ?? 0)
            .First();

        var allSourceBodies = consolidationTargets.Select(c => c.Content).Append(operation.Content).ToArray();

        return ApplyGuardedMergeOrAppend(
            operationWithAnchor, decision.MergedBody, primary.DocumentId, primary.Content, allSourceBodies);
    }

    /// <summary>
    /// Shared write routing for LLM-tier Update and deterministic/LLM-tier Consolidate
    /// (memory-core-redesign Slice 3, design D5): validates a synthesized
    /// <paramref name="mergedBody"/> against every source body via <see cref="MergeGuard"/>;
    /// on pass, writes the merged body with MergeDocument semantics (the existing merge path).
    /// On guard failure, or when no merged body was produced at all, falls back to a
    /// structural append (existing target body + dated separator + proposal) with
    /// AppendDocument semantics — unconditionally lossless because it is concatenation, unlike
    /// an overwrite. This is what makes <see cref="MemoryUpdateSemantics.AppendDocument"/> a
    /// real, reachable write path for the first time.
    /// </summary>
    private SQLiteMemoryCurationOperation ApplyGuardedMergeOrAppend(
        SQLiteMemoryCurationOperation operation,
        string? mergedBody,
        string targetDocumentId,
        string targetBody,
        IReadOnlyList<string> allSourceBodies)
    {
        if (!string.IsNullOrWhiteSpace(mergedBody))
        {
            var guardResult = MergeGuard.Validate(allSourceBodies, mergedBody);
            if (guardResult.Passed)
            {
                _log.Info(
                    "curation_merge_guard_passed anchor={0} targetDoc={1} reason={2}",
                    operation.AnchorCanonicalName,
                    targetDocumentId,
                    guardResult.Reason);

                return operation with
                {
                    MemoryId = targetDocumentId,
                    Content = mergedBody,
                    UpdateSemantics = MemoryUpdateSemantics.MergeDocument.ToWireValue()
                };
            }

            _log.Warning(
                "curation_merge_guard_failed anchor={0} targetDoc={1} missingTokens=[{2}] reason={3}",
                operation.AnchorCanonicalName,
                targetDocumentId,
                string.Join(",", guardResult.MissingTokens),
                guardResult.Reason);
        }

        var appendedBody = BuildAppendedBody(targetBody, operation.Content);
        _log.Info(
            "curation_append_fallback anchor={0} targetDoc={1} hadMergedBody={2}",
            operation.AnchorCanonicalName,
            targetDocumentId,
            !string.IsNullOrWhiteSpace(mergedBody));

        return operation with
        {
            MemoryId = targetDocumentId,
            Content = appendedBody,
            UpdateSemantics = MemoryUpdateSemantics.AppendDocument.ToWireValue()
        };
    }

    /// <summary>
    /// Builds the structural-append body: the existing content, a dated provenance separator,
    /// then the proposal — plain concatenation, so no source content can be lost. The date
    /// comes from the store's own <see cref="TimeProvider"/> (<see cref="SQLiteMemoryStore.TimeProvider"/>)
    /// rather than a second injected clock, so it stays consistent with the row's own
    /// persisted timestamps and stays virtualizable in tests via the same seam.
    /// </summary>
    private string BuildAppendedBody(string existingBody, string proposalContent)
    {
        var isoDate = _store.TimeProvider.GetUtcNow().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return $"{existingBody}\n\n---\n_[merged {isoDate}]_\n{proposalContent}";
    }

    private async Task ExecuteConsolidationAsync(
        SQLiteMemoryCurationOperation operation,
        CurationDecision decision,
        CancellationToken ct)
    {
        if (decision.ConsolidationTargetIds is null || decision.ConsolidationTargetIds.Count == 0)
            return;

        var canonicalAnchor = decision.CanonicalAnchorName ?? operation.AnchorCanonicalName;
        var canonicalAnchorId = MemoryTypedId.AnchorId(canonicalAnchor);

        // For each consolidation target document, re-anchor it if it belongs to a different anchor,
        // then tombstone the redundant anchor
        var tombstonedAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var docId in decision.ConsolidationTargetIds)
        {
            // Re-anchor the document to the canonical anchor
            await _store.ReanchorDocumentAsync(docId, canonicalAnchorId, ct);

            _log.Info(
                "curation_reanchor docId={0} newAnchor={1}",
                docId,
                canonicalAnchorId);
        }

        // Find and tombstone anchors that are no longer canonical
        // We need to look up the anchor IDs for the consolidated documents
        var candidates = await _store.FindFuzzyAnchorMatchesAsync(operation.AnchorCanonicalName, ct);

        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.AnchorId, canonicalAnchorId, StringComparison.OrdinalIgnoreCase)
                && !tombstonedAnchors.Contains(candidate.AnchorId))
            {
                await _store.TombstoneAnchorAsync(candidate.AnchorId, ct);
                tombstonedAnchors.Add(candidate.AnchorId);

                _log.Info(
                    "curation_tombstone_anchor anchorId={0} canonicalName={1}",
                    candidate.AnchorId,
                    candidate.AnchorCanonicalName);
            }
        }
    }
}

/// <summary>
/// A curation decision paired with the exact candidate set it was evaluated against — see
/// <see cref="MemoryCurationEvaluator.EvaluateAsync"/>'s remarks for why
/// <see cref="MemoryCurationEvaluator.ApplyDecisionAsync"/> needs the same candidates rather
/// than re-querying the store.
/// </summary>
public sealed record CurationEvaluation(
    CurationDecision Decision,
    IReadOnlyList<ExistingMemoryCandidate> Candidates);
