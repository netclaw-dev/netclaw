using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.AI;
using Netclaw.Configuration;

namespace Netclaw.Actors.Memory;

// ── Message protocol ────────────────────────────────────────────────

/// <summary>
/// Sent from LlmSessionActor to its curation child with accepted proposals.
/// </summary>
public sealed record EvaluateProposals(
    IReadOnlyList<SQLiteMemoryCurationOperation> Operations,
    string Domain);

/// <summary>
/// Reply from the curation actor when all proposals in a batch have been processed.
/// </summary>
public sealed record CurationCompleted(
    int Evaluated,
    int Skipped,
    int Updated,
    int Consolidated,
    int Created);

/// <summary>
/// Reply from the curation actor when processing fails.
/// </summary>
public sealed record CurationFailed(string Reason);

// ── Internal messages ───────────────────────────────────────────────

internal sealed record EvaluationBatchResult(
    IReadOnlyList<(SQLiteMemoryCurationOperation Operation, CurationDecision Decision)> Decisions);

internal sealed record WriteBatchResult(CurationCompleted Summary);

internal sealed record WriteBatchFailed(Exception Exception);

// ── Actor ───────────────────────────────────────────────────────────

/// <summary>
/// Per-session actor that evaluates memory proposals before writing them.
/// Uses a three-phase state machine: Idle -> Evaluating -> Writing -> Idle.
/// Created as a child of LlmSessionActor and dies with its parent.
/// </summary>
public sealed class MemoryCurationActor : ReceiveActor, IWithUnboundedStash
{
    private static readonly TimeSpan LlmTimeout = TimeSpan.FromSeconds(10);

    private readonly SQLiteMemoryStore _store;
    private readonly IChatClient? _llmClient;
    private readonly ILoggingAdapter _log;

    private IActorRef? _currentRequester;

    public IStash Stash { get; set; } = null!;

    public MemoryCurationActor(SQLiteMemoryStore store, IChatClientProvider? clientProvider = null)
    {
        _store = store;
        _llmClient = clientProvider != null
            ? clientProvider.GetClient(ModelRole.Compaction)
            : null;
        _log = Context.GetLogger();

        Become(Idle);
    }

    /// <summary>
    /// Create Props for the MemoryCurationActor.
    /// </summary>
    public static Props CreateProps(SQLiteMemoryStore store, IChatClientProvider? clientProvider = null)
        => Props.Create(() => new MemoryCurationActor(store, clientProvider));

    // ── Idle behavior ───────────────────────────────────────────────

    private void Idle()
    {
        Receive<EvaluateProposals>(msg =>
        {
            if (msg.Operations.Count == 0)
            {
                Sender.Tell(new CurationCompleted(0, 0, 0, 0, 0));
                return;
            }

            _currentRequester = Sender;
            _log.Info("curation_actor_evaluating proposalCount={0} domain={1}", msg.Operations.Count, msg.Domain);
            StartEvaluation(msg.Operations, msg.Domain);
        });
    }

    // ── Evaluating behavior ─────────────────────────────────────────

    private void Evaluating()
    {
        Receive<EvaluationBatchResult>(msg =>
        {
            _log.Info("curation_actor_evaluated decisionCount={0}", msg.Decisions.Count);
            StartWriting(msg.Decisions);
        });

        // Stash incoming proposals while evaluating
        Receive<EvaluateProposals>(_ =>
        {
            _log.Debug("curation_actor_busy stashing proposal during evaluation");
            Stash.Stash();
        });
    }

    // ── Writing behavior ────────────────────────────────────────────

    private void Writing()
    {
        Receive<WriteBatchResult>(msg =>
        {
            _log.Info(
                "curation_actor_write_complete evaluated={0} skipped={1} updated={2} consolidated={3} created={4}",
                msg.Summary.Evaluated,
                msg.Summary.Skipped,
                msg.Summary.Updated,
                msg.Summary.Consolidated,
                msg.Summary.Created);

            _currentRequester?.Tell(msg.Summary);
            _currentRequester = null;

            Become(Idle);
            Stash.UnstashAll();
        });

        Receive<WriteBatchFailed>(msg =>
        {
            _log.Warning(msg.Exception, "curation_actor_write_failed");
            _currentRequester?.Tell(new CurationFailed(msg.Exception.Message));
            _currentRequester = null;

            Become(Idle);
            Stash.UnstashAll();
        });

        // Stash incoming proposals while writing
        Receive<EvaluateProposals>(_ =>
        {
            _log.Debug("curation_actor_busy stashing proposal during writing");
            Stash.Stash();
        });
    }

    // ── Evaluation pipeline ─────────────────────────────────────────

    private void StartEvaluation(IReadOnlyList<SQLiteMemoryCurationOperation> operations, string domain)
    {
        Become(Evaluating);
        var self = Self;

        // Run evaluation on the thread pool — all async DB queries happen here
        _ = Task.Run(async () =>
        {
            try
            {
                var decisions = new List<(SQLiteMemoryCurationOperation, CurationDecision)>();

                foreach (var operation in operations)
                {
                    var decision = await EvaluateSingleAsync(operation, domain);
                    decisions.Add((operation, decision));
                }

                self.Tell(new EvaluationBatchResult(decisions));
            }
            catch (Exception ex)
            {
                self.Tell(new WriteBatchFailed(ex));
            }
        });
    }

    private async Task<CurationDecision> EvaluateSingleAsync(
        SQLiteMemoryCurationOperation operation,
        string domain)
    {
        // Immutable records bypass evaluation
        if (MemoryDomainEnumExtensions.TryFromWireValue(operation.Kind, out MemoryKind kind)
            && kind == MemoryKind.Record)
        {
            return new CurationDecision(CurationDecisionKind.Create, null, null, null, "immutable record bypass");
        }

        // Query existing anchors for matches (by name)
        var anchorCandidates = await _store.FindFuzzyAnchorMatchesAsync(
            operation.AnchorCanonicalName,
            domain);

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
                var contentCandidates = await _store.FindCandidatesByContentAsync(
                    contentTerms,
                    domain);

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
            var llmDecision = await TryLlmEvaluationAsync(operation, candidates);
            if (llmDecision is not null)
            {
                _log.Info(
                    "curation_llm_decision anchor={0} decision={1} reason={2}",
                    operation.AnchorCanonicalName,
                    llmDecision.Kind,
                    llmDecision.Reason);
                return llmDecision;
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

        return rulesDecision;
    }

    private async Task<CurationDecision?> TryLlmEvaluationAsync(
        SQLiteMemoryCurationOperation operation,
        IReadOnlyList<ExistingMemoryCandidate> candidates)
    {
        try
        {
            using var cts = new CancellationTokenSource(LlmTimeout);

            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, CurationPromptBuilder.SystemPrompt),
                new(ChatRole.User, CurationPromptBuilder.BuildUserMessage(operation, candidates))
            };

            var options = new ChatOptions
            {
                MaxOutputTokens = 50
            };

            var response = await _llmClient!.GetResponseAsync(messages, options, cts.Token);
            var responseText = response.Text?.Trim();

            if (string.IsNullOrWhiteSpace(responseText))
                return null;

            return CurationPromptBuilder.ParseResponse(responseText);
        }
        catch (OperationCanceledException)
        {
            _log.Warning("curation_llm_timeout anchor={0}", operation.AnchorCanonicalName);
            return null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "curation_llm_error anchor={0}", operation.AnchorCanonicalName);
            return null;
        }
    }

    // ── Write pipeline ──────────────────────────────────────────────

    private void StartWriting(IReadOnlyList<(SQLiteMemoryCurationOperation Operation, CurationDecision Decision)> decisions)
    {
        Become(Writing);
        var self = Self;

        _ = Task.Run(async () =>
        {
            try
            {
                var skipped = 0;
                var updated = 0;
                var consolidated = 0;
                var created = 0;
                var toWrite = new List<SQLiteMemoryCurationOperation>();

                foreach (var (operation, decision) in decisions)
                {
                    switch (decision.Kind)
                    {
                        case CurationDecisionKind.Skip:
                            _log.Info(
                                "curation_skip anchor={0} reason={1}",
                                operation.AnchorCanonicalName,
                                decision.Reason);
                            skipped++;
                            break;

                        case CurationDecisionKind.Update:
                            _log.Info(
                                "curation_update anchor={0} targetDoc={1} reason={2}",
                                operation.AnchorCanonicalName,
                                decision.TargetDocumentId,
                                decision.Reason);

                            // Set the operation's MemoryId to the existing document ID
                            // so the ON CONFLICT UPDATE fires
                            toWrite.Add(operation with { MemoryId = decision.TargetDocumentId });
                            updated++;
                            break;

                        case CurationDecisionKind.Consolidate:
                            _log.Info(
                                "curation_consolidate anchor={0} canonicalAnchor={1} targetIds=[{2}] reason={3}",
                                operation.AnchorCanonicalName,
                                decision.CanonicalAnchorName,
                                decision.ConsolidationTargetIds is not null ? string.Join(",", decision.ConsolidationTargetIds) : "",
                                decision.Reason);

                            await ExecuteConsolidationAsync(operation, decision);
                            // After consolidation, write the new proposal under the canonical anchor
                            var canonicalAnchor = decision.CanonicalAnchorName ?? operation.AnchorCanonicalName;
                            toWrite.Add(operation with { AnchorCanonicalName = canonicalAnchor });
                            consolidated++;
                            break;

                        case CurationDecisionKind.Create:
                            _log.Info(
                                "curation_create anchor={0} reason={1}",
                                operation.AnchorCanonicalName,
                                decision.Reason);
                            toWrite.Add(operation);
                            created++;
                            break;

                        case CurationDecisionKind.Ambiguous:
                            // Should not reach here — ambiguous is resolved before writing
                            _log.Warning("curation_unexpected_ambiguous anchor={0}, defaulting to create", operation.AnchorCanonicalName);
                            toWrite.Add(operation);
                            created++;
                            break;
                    }
                }

                // Write all accepted operations in a single batch
                if (toWrite.Count > 0)
                {
                    await _store.ApplyInlineCurationBatchAsync(toWrite);
                }

                self.Tell(new WriteBatchResult(new CurationCompleted(
                    Evaluated: decisions.Count,
                    Skipped: skipped,
                    Updated: updated,
                    Consolidated: consolidated,
                    Created: created)));
            }
            catch (Exception ex)
            {
                self.Tell(new WriteBatchFailed(ex));
            }
        });
    }

    private async Task ExecuteConsolidationAsync(
        SQLiteMemoryCurationOperation operation,
        CurationDecision decision)
    {
        if (decision.ConsolidationTargetIds is null || decision.ConsolidationTargetIds.Count == 0)
            return;

        var canonicalAnchor = decision.CanonicalAnchorName ?? operation.AnchorCanonicalName;
        var canonicalAnchorId = $"anchor:{canonicalAnchor.Trim().ToLowerInvariant().Replace(' ', '-')}";

        // For each consolidation target document, re-anchor it if it belongs to a different anchor,
        // then tombstone the redundant anchor
        var tombstonedAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var docId in decision.ConsolidationTargetIds)
        {
            // Re-anchor the document to the canonical anchor
            await _store.ReanchorDocumentAsync(docId, canonicalAnchorId);

            _log.Info(
                "curation_reanchor docId={0} newAnchor={1}",
                docId,
                canonicalAnchorId);
        }

        // Find and tombstone anchors that are no longer canonical
        // We need to look up the anchor IDs for the consolidated documents
        var candidates = await _store.FindFuzzyAnchorMatchesAsync(
            operation.AnchorCanonicalName,
            operation.Domain);

        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.AnchorId, canonicalAnchorId, StringComparison.OrdinalIgnoreCase)
                && !tombstonedAnchors.Contains(candidate.AnchorId))
            {
                await _store.TombstoneAnchorAsync(candidate.AnchorId);
                tombstonedAnchors.Add(candidate.AnchorId);

                _log.Info(
                    "curation_tombstone_anchor anchorId={0} canonicalName={1}",
                    candidate.AnchorId,
                    candidate.AnchorCanonicalName);
            }
        }
    }
}
