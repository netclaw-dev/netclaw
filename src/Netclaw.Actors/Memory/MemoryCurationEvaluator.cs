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
/// markers (<c>curation_dual_search</c>, <c>curation_llm_decision</c>,
/// <c>curation_llm_no_decision</c>, <c>curation_llm_timeout</c>, <c>curation_llm_error</c>,
/// <c>curation_ambiguous_auto_resolved</c>, <c>curation_ambiguous_create_fallback</c>,
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
/// anchor candidates are queried, then content-term candidates are added when there is no
/// exact anchor match; <see cref="CurationRulesEvaluator.Evaluate"/> runs the deterministic
/// tier; an Ambiguous result escalates to the LLM tier (when available) followed by
/// <see cref="CurationRulesEvaluator.GuardDestructiveUpdate"/>, or else falls back to
/// <see cref="CurationRulesEvaluator.TryAutoResolveAmbiguous"/> and finally a Create default.
/// <see cref="ApplyDecisionAsync"/> maps the resulting decision to the operation that should
/// be written (or nothing, for Skip), executing Consolidate's re-anchor/tombstone side
/// effects — this mapping is unified too, since a second, hand-copied switch statement per
/// caller is exactly the kind of divergence this slice removes.
/// </summary>
public sealed class MemoryCurationEvaluator
{
    private static readonly TimeSpan LlmTimeout = TimeSpan.FromSeconds(10);

    private readonly SQLiteMemoryStore _store;
    private readonly IChatClient? _llmClient;
    private readonly ICurationLog _log;

    /// <summary>
    /// Constructs an evaluator that logs through Akka's actor logging (the inline
    /// per-session path). <paramref name="llmClient"/> is genuinely optional runtime
    /// state, not a backward-compatibility shim: absence is a real, permanent operating
    /// mode for a session whose configured provider has no compaction-role model, in
    /// which case Ambiguous decisions resolve via
    /// <see cref="CurationRulesEvaluator.TryAutoResolveAmbiguous"/> only.
    /// </summary>
    public MemoryCurationEvaluator(SQLiteMemoryStore store, ILoggingAdapter log, IChatClient? llmClient = null)
        : this(store, (ICurationLog)new AkkaCurationLog(log), llmClient)
    {
    }

    /// <summary>
    /// Constructs an evaluator that logs through Microsoft.Extensions.Logging (the daemon
    /// checkpoint-worker path). The daemon worker has no LLM client to give this evaluator
    /// today — that absence is intentional and permanent for this call site, not a
    /// placeholder to be filled in later in this slice.
    /// </summary>
    public MemoryCurationEvaluator(SQLiteMemoryStore store, ILogger log, IChatClient? llmClient = null)
        : this(store, (ICurationLog)new MicrosoftCurationLog(log), llmClient)
    {
    }

    private MemoryCurationEvaluator(SQLiteMemoryStore store, ICurationLog log, IChatClient? llmClient)
    {
        _store = store;
        _log = log;
        _llmClient = llmClient;
    }

    /// <summary>
    /// Evaluate a single curation proposal against existing memories and return a decision.
    /// </summary>
    public async Task<CurationDecision> EvaluateAsync(
        SQLiteMemoryCurationOperation operation,
        SessionId sessionId,
        CancellationToken ct = default)
    {
        // Immutable records bypass evaluation
        if (MemoryDomainEnumExtensions.TryFromWireValue(operation.Kind, out MemoryKind kind)
            && kind == MemoryKind.Record)
        {
            return new CurationDecision(CurationDecisionKind.Create, null, null, null, "immutable record bypass");
        }

        // Query existing anchors for matches (by name)
        var anchorCandidates = await _store.FindFuzzyAnchorMatchesAsync(operation.AnchorCanonicalName, ct);

        // Build a mutable candidate list — content search may add more candidates below.
        var candidates = new List<ExistingMemoryCandidate>(anchorCandidates);

        // Run content-based search when there is no exact anchor match.
        // This catches semantically identical content under very different anchor names
        // (e.g., "netclaw-github-repo" vs "netclaw-source-location" — different names, same info).
        var hasExactAnchorMatch = anchorCandidates.Any(c => c.IsExactAnchorMatch);
        if (!hasExactAnchorMatch && !string.IsNullOrWhiteSpace(operation.Content))
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

        // Apply rules tier
        var rulesDecision = CurationRulesEvaluator.Evaluate(operation, candidates);

        // If rules tier is ambiguous and LLM is available, escalate
        if (rulesDecision.Kind == CurationDecisionKind.Ambiguous && _llmClient is not null)
        {
            var llmDecision = await TryLlmEvaluationAsync(_llmClient, sessionId, operation, candidates, _log);
            if (llmDecision is not null)
            {
                var guarded = CurationRulesEvaluator.GuardDestructiveUpdate(llmDecision, operation, candidates);
                _log.Info(
                    "curation_llm_decision anchor={0} decision={1} reason={2}",
                    operation.AnchorCanonicalName,
                    guarded.Kind,
                    guarded.Reason);
                return guarded;
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
                return autoResolved;
            }

            _log.Warning("curation_ambiguous_create_fallback anchor={0} llm_available={1}",
                operation.AnchorCanonicalName, _llmClient is not null);
            return new CurationDecision(CurationDecisionKind.Create, null, null, null,
                "ambiguous: auto-resolve insufficient, defaulting to create");
        }

        return CurationRulesEvaluator.GuardDestructiveUpdate(rulesDecision, operation, candidates);
    }

    internal static async Task<CurationDecision?> TryLlmEvaluationAsync(
        IChatClient llmClient,
        SessionId sessionId,
        SQLiteMemoryCurationOperation operation,
        IReadOnlyList<ExistingMemoryCandidate> candidates,
        ICurationLog log)
    {
        try
        {
            using var cts = new CancellationTokenSource(LlmTimeout);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, CurationPromptBuilder.SystemPrompt),
                new(ChatRole.User, CurationPromptBuilder.BuildUserMessage(operation, candidates))
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
                // keyword and never approach any cap; (2) the 10s call timeout
                // bounds wall-clock when a model ignores suppression and thinks at
                // length. The cap only matters in the remaining window — suppression
                // ignored but thinking finishes inside the timeout — where a tight
                // cap truncates mid-think and reproduces the measured
                // responseLength=0 empty-reply failure (July 2026 audit: at 512, a
                // Qwen3.6-class model produced 0 successful curation decisions
                // ever). Unemitted tokens cost nothing, so size this generously;
                // it becomes the Memory.Curation.LlmMaxOutputTokens config knob in
                // the memory-core-redesign change.
                MaxOutputTokens = 4096,
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
    public async Task<SQLiteMemoryCurationOperation?> ApplyDecisionAsync(
        SQLiteMemoryCurationOperation operation,
        CurationDecision decision,
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
                _log.Info(
                    "curation_update anchor={0} targetDoc={1} reason={2}",
                    operation.AnchorCanonicalName,
                    decision.TargetDocumentId!,
                    decision.Reason);

                // Set the operation's MemoryId to the existing document ID
                // so the ON CONFLICT UPDATE fires
                return operation with { MemoryId = decision.TargetDocumentId };

            case CurationDecisionKind.Consolidate:
                _log.Info(
                    "curation_consolidate anchor={0} canonicalAnchor={1} targetIds=[{2}] reason={3}",
                    operation.AnchorCanonicalName,
                    decision.CanonicalAnchorName!,
                    decision.ConsolidationTargetIds is not null ? string.Join(",", decision.ConsolidationTargetIds) : "",
                    decision.Reason);

                await ExecuteConsolidationAsync(operation, decision, ct);
                // After consolidation, write the proposal INTO the primary consolidated
                // document (explicit target => the store's overwrite path, like Update):
                // Consolidate means near-duplicate content, so the designed outcome is a
                // collapse, not an append. A null TargetDocumentId (no construction site
                // should produce one) flows through with MemoryId null and lands on the
                // store's lossless dedup-append path, which logs curation_dedup_append.
                var canonicalAnchor = decision.CanonicalAnchorName ?? operation.AnchorCanonicalName;
                return operation with { AnchorCanonicalName = canonicalAnchor, MemoryId = decision.TargetDocumentId };

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
