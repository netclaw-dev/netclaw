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
    private readonly SQLiteMemoryStore _store;
    private readonly SessionId _sessionId;
    private readonly ILoggingAdapter _log;
    private readonly MemoryCurationEvaluator _evaluator;

    private IActorRef? _currentRequester;

    public IStash Stash { get; set; } = null!;

    public MemoryCurationActor(SQLiteMemoryStore store, SessionId sessionId, IChatClientProvider? clientProvider = null)
    {
        _store = store;
        _sessionId = sessionId;
        _log = Context.GetLogger();

        var llmClient = clientProvider != null
            ? clientProvider.GetClient(ModelRole.Compaction)
            : null;
        _evaluator = new MemoryCurationEvaluator(_store, _log, llmClient);

        Become(Idle);
    }

    /// <summary>
    /// Create Props for the MemoryCurationActor.
    /// </summary>
    public static Props CreateProps(SQLiteMemoryStore store, SessionId sessionId, IChatClientProvider? clientProvider = null)
        => Props.Create(() => new MemoryCurationActor(store, sessionId, clientProvider));

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

    private void StartEvaluation(IReadOnlyList<SQLiteMemoryCurationOperation> operations)
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
                    var decision = await EvaluateSingleAsync(operation);
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

    // Decision logic lives in MemoryCurationEvaluator (shared with the daemon checkpoint
    // worker — memory-core-redesign Slice 1) so the two write pipelines cannot diverge.
    private Task<CurationDecision> EvaluateSingleAsync(SQLiteMemoryCurationOperation operation)
        => _evaluator.EvaluateAsync(operation, _sessionId);

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
                    // Decision -> write-operation mapping (including Consolidate's
                    // re-anchor/tombstone side effects) lives in MemoryCurationEvaluator,
                    // shared with the daemon checkpoint worker.
                    var writeOp = await _evaluator.ApplyDecisionAsync(operation, decision);

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
}
