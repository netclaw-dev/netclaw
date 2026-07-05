// -----------------------------------------------------------------------
// <copyright file="MemoryCurationActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Netclaw.Configuration;
using SessionId = Netclaw.Actors.Protocol.SessionId;

namespace Netclaw.Actors.Memory;

// ── Message protocol ────────────────────────────────────────────────

/// <summary>
/// Sent from LlmSessionActor to its curation child with accepted proposals.
/// </summary>
public sealed record EvaluateProposals(
    IReadOnlyList<SQLiteMemoryCurationOperation> Operations);

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
    IReadOnlyList<(SQLiteMemoryCurationOperation Operation, CurationEvaluation Evaluation)> Evaluations);

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
    private readonly SQLiteMemoryStore _store;
    private readonly SessionId _sessionId;
    private readonly ILoggingAdapter _log;
    private readonly MemoryCurationEvaluator _evaluator;
    private readonly MemoryEmbedderHolder? _embedderHolder;

    private IActorRef? _currentRequester;

    public IStash Stash { get; set; } = null!;

    /// <param name="curationConfig">
    /// Write-side curation settings (memory-core-redesign Slice 3): nominator threshold/K and
    /// the curation LLM's timeout/token-cap, threaded to <see cref="MemoryCurationEvaluator"/>
    /// in place of the hardcoded constants Slice 1 shipped with.
    /// </param>
    /// <param name="embedderHolder">
    /// Resolves the process's <see cref="IMemoryEmbedder"/> for embed-on-write (memory-core-
    /// redesign Slice 2, task 2.8) AND for the evaluator's embedding kNN nominator (Slice 3
    /// Stage B, task 3.1) — the same holder serves both. Optional like
    /// <paramref name="clientProvider"/> above: a null holder is a genuine operating mode (a
    /// test harness or a session wired without the embedding subsystem), not a placeholder —
    /// both <see cref="MemoryEmbedOnWriteCoordinator"/> and <see cref="MemoryCurationEvaluator"/>
    /// treat a null holder identically to an unavailable embedder and degrade accordingly.
    /// </param>
    /// <param name="vectorIndexHolder">
    /// Resolves the process's <see cref="MemoryVectorIndex"/> for the nominator (Slice 3 Stage
    /// B). Provided alongside <paramref name="embedderHolder"/> in production; independently
    /// nullable for the same test-harness reason.
    /// </param>
    public MemoryCurationActor(
        SQLiteMemoryStore store,
        SessionId sessionId,
        MemoryCurationConfig curationConfig,
        IChatClientProvider? clientProvider = null,
        MemoryEmbedderHolder? embedderHolder = null,
        MemoryVectorIndexHolder? vectorIndexHolder = null)
    {
        _store = store;
        _sessionId = sessionId;
        _log = Context.GetLogger();
        _embedderHolder = embedderHolder;

        var llmClient = clientProvider != null
            ? clientProvider.GetClient(ModelRole.Compaction)
            : null;
        _evaluator = new MemoryCurationEvaluator(
            _store, _log, curationConfig, llmClient, embedderHolder, vectorIndexHolder);

        Become(Idle);
    }

    /// <summary>
    /// Create Props for the MemoryCurationActor.
    /// </summary>
    public static Props CreateProps(
        SQLiteMemoryStore store,
        SessionId sessionId,
        MemoryCurationConfig curationConfig,
        IChatClientProvider? clientProvider = null,
        MemoryEmbedderHolder? embedderHolder = null,
        MemoryVectorIndexHolder? vectorIndexHolder = null)
        => Props.Create(() => new MemoryCurationActor(
            store, sessionId, curationConfig, clientProvider, embedderHolder, vectorIndexHolder));

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
            _log.Info("curation_actor_evaluating proposalCount={0}", msg.Operations.Count);
            StartEvaluation(msg.Operations);
        });
    }

    // ── Evaluating behavior ─────────────────────────────────────────

    private void Evaluating()
    {
        Receive<EvaluationBatchResult>(msg =>
        {
            _log.Info("curation_actor_evaluated decisionCount={0}", msg.Evaluations.Count);
            StartWriting(msg.Evaluations);
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

    private void StartEvaluation(IReadOnlyList<SQLiteMemoryCurationOperation> operations)
    {
        Become(Evaluating);
        var self = Self;

        // Run evaluation on the thread pool — all async DB queries happen here
        _ = Task.Run(async () =>
        {
            try
            {
                var evaluations = new List<(SQLiteMemoryCurationOperation, CurationEvaluation)>();

                foreach (var operation in operations)
                {
                    var evaluation = await EvaluateSingleAsync(operation);
                    evaluations.Add((operation, evaluation));
                }

                self.Tell(new EvaluationBatchResult(evaluations));
            }
            catch (Exception ex)
            {
                self.Tell(new WriteBatchFailed(ex));
            }
        });
    }

    // Decision logic lives in MemoryCurationEvaluator (shared with the daemon checkpoint
    // worker — memory-core-redesign Slice 1) so the two write pipelines cannot diverge.
    private Task<CurationEvaluation> EvaluateSingleAsync(SQLiteMemoryCurationOperation operation)
        => _evaluator.EvaluateAsync(operation, _sessionId);

    // ── Write pipeline ──────────────────────────────────────────────

    private void StartWriting(IReadOnlyList<(SQLiteMemoryCurationOperation Operation, CurationEvaluation Evaluation)> evaluations)
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

                foreach (var (operation, evaluation) in evaluations)
                {
                    var decision = evaluation.Decision;

                    // Decision -> write-operation mapping (including Consolidate's
                    // re-anchor/tombstone side effects and Slice 3's guard-validated
                    // merge/append routing) lives in MemoryCurationEvaluator, shared with
                    // the daemon checkpoint worker.
                    var writeOp = await _evaluator.ApplyDecisionAsync(operation, decision, evaluation.Candidates);

                    switch (decision.Kind)
                    {
                        case CurationDecisionKind.Skip:
                            skipped++;
                            break;
                        case CurationDecisionKind.Update:
                            updated++;
                            break;
                        case CurationDecisionKind.Consolidate:
                            consolidated++;
                            break;
                        case CurationDecisionKind.Create:
                        case CurationDecisionKind.Ambiguous:
                        default:
                            // Ambiguous should not reach here — it is resolved before
                            // writing — but ApplyDecisionAsync defaults it to Create,
                            // so the count follows the same default.
                            created++;
                            break;
                    }

                    if (writeOp is not null)
                        toWrite.Add(writeOp);
                }

                // Write all accepted operations in a single batch
                if (toWrite.Count > 0)
                {
                    var writtenDocs = await _store.ApplyInlineCurationBatchAsync(toWrite);

                    // Embed-on-write (memory-core-redesign Slice 2, task 2.8): runs after the
                    // write above has already committed. Vectors are derived data — a failure
                    // here must never fail this write; MemoryEmbedOnWriteCoordinator isolates
                    // and logs per-item failures instead of propagating them.
                    await MemoryEmbedOnWriteCoordinator.EmbedWrittenDocumentsAsync(
                        _embedderHolder, _store, writtenDocs, _log);
                }

                self.Tell(new WriteBatchResult(new CurationCompleted(
                    Evaluated: evaluations.Count,
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
}
