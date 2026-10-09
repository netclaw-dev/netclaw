// -----------------------------------------------------------------------
// <copyright file="LlmSessionActor.ChildRecovery.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.SubAgents;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Sessions;

public sealed partial class LlmSessionActor
{
    private bool HasLiveChildRuns => _state.ChildRuns.Values.Any(static run => run.Terminal is null);
    private bool HasUnsettledChildWork => _state.ChildRuns.Values.Any(static run => run.DeliveryInputId is null);

    private void RecoverBackgroundChildren()
    {
        if (_state.ChildRuns.Count == 0)
        {
            TransitionTo(SessionPhase.Ready);
            return;
        }
        Become(AwaitRecoveredChildFacts);
        if ((_state.ChildRuns.Values.Any(static run => !run.StartBatchSettled)
             || _state.PendingInputs.FirstOrDefault()?.SourceChildRunId is not null)
            && ParkedToolBatchHistory.FindRedrivableAssistantMessage(_state.History, null) is not null)
        {
            Persist(BuildInterruptedToolBatchAfterRecoveryEvent(), abandoned =>
            {
                ApplyToolBatchAbandoned(abandoned);
                RecoverNextChild();
            });
            return;
        }
        RecoverNextChild();
    }

    private void AwaitRecoveredChildFacts()
    {
        Command<ChildCancellationFinalized>(message =>
        {
            if (!_state.ChildRuns.TryGetValue(message.RunId, out var run) || run.Terminal is not null
                || run.CancellationRequestedAtMs is null || run.DispatchClosedAtMs is null)
                throw new InvalidDataException("A recovered cancellation has no committed closure.");
            RecordChildTerminal(run, message.Terminal, ActorRefs.Nobody, _ => RecoverNextChild());
        });
        CommandAny(_ => Stash.Stash());
    }

    private void RecoverNextChild()
    {
        var run = _state.ChildRuns.Values.Where(static candidate => candidate.Terminal is null)
            .OrderBy(static candidate => candidate.AcceptedAtMs).ThenBy(static candidate => candidate.RunId.Value, StringComparer.Ordinal)
            .FirstOrDefault();
        if (run is null)
        {
            foreach (var recorded in _state.ChildRuns.Values.Where(static candidate => candidate.PreparedTerminal is null))
                EnrichRecordedChild(recorded);
            TransitionTo(SessionPhase.Ready);
            ResumeAdmittedChildContinuation();
            Stash.UnstashAll();
            return;
        }
        ExpireChildApprovals(run.RunId, () =>
        {
            var current = _state.ChildRuns[run.RunId];
            if (current.CancellationRequestedAtMs is not null)
            {
                if (current.DispatchClosedAtMs is not null)
                    _ = FinalizeCancelledChildAsync(Self, current);
                else
                    Persist(new ChildRunEvent.DispatchClosed
                    {
                        SessionId = _sessionId, RunId = current.RunId, RecordedAtMs = NowMs()
                    }, closed =>
                    {
                        _state = _state.Apply(closed);
                        _ = FinalizeCancelledChildAsync(Self, _state.ChildRuns[current.RunId]);
                    });
                return;
            }
            var storage = _sessionStorage.ForChild(current.RunId, current.ScopeId);
            var terminal = new ChildRunTerminal(new SubAgentResult
            {
                AgentName = current.AgentName, RunId = current.RunId, ScopeId = current.ScopeId,
                Completion = new ChildRunCompletion.Failed(SubAgentOutcomeReason.OwnerRestartLost),
                Output = "The session owner restarted before a durable child terminal receipt. The recorded checkpoint retains known local evidence.",
                LogPath = storage.LogPath.Value, ArtifactDirectory = storage.ArtifactDirectory.Value
            }, true, "External effects can remain uncertain. The framework did not restart the child.");
            // Each callback acknowledges its journal position before the next terminal stamp.
            RecordChildTerminal(current, terminal, ActorRefs.Nobody, _ => RecoverNextChild());
        });
    }

    private void ResumeAdmittedChildContinuation()
    {
        // Input closure excludes failed or completed reviews. Ordinary inputs retain their existing restart contract.
        var pending = _state.PendingInputs.TakeWhile(static input => input.SourceChildRunId is not null).ToArray();
        if (pending.Length == 0)
            return;
        foreach (var input in pending)
            _ = _state.GetChildContinuation(input);

        var adoptedCount = 0;
        if (_state.AdoptedTaskContext is { } adopted
            && pending.Any(input => _state.AdoptedTaskInputIds.Contains(input.InputId)))
        {
            adoptedCount = _state.AdoptedTaskInputIds.Count;
            if (!pending.Take(adoptedCount).Select(static input => input.InputId).SequenceEqual(_state.AdoptedTaskInputIds)
                || pending.Take(adoptedCount).Any(input => !SessionState.SameCanonicalContext(input.TurnContext, adopted))
                || !TurnContext.TryFromRecord(adopted, out var context, out _) || context is null)
                throw new InvalidDataException("A recovered child continuation differs from its durable adoption.");
            _currentTurnContext = context;
            BindTurnTelemetry(context);
            _toolApprovals.StartTurn(context);
            _currentTrustContext = _trustContextDeriver?.DeriveFromTurnContext(context);
            _turnState.RestoreCheckpoint(_state.LoopCheckpoint);
            _recallManager.ResetForNewTurn();
            SetSystemPrompt();
        }
        _currentTurnSource = null;
        for (var index = 0; index < pending.Length; index++)
        {
            var input = pending[index];
            _buffer.Add((new SendUserMessage
            {
                SessionId = _sessionId, Content = input.UserMessage.Content!, AdmittedInputId = input.InputId
            }, index < adoptedCount));
        }
        // Internal replay preserves adopted detector evidence; new child inputs use canonical adoption.
        DrainBufferedUserMessages(() =>
        {
            FireLlmCall();
            TransitionTo(SessionPhase.Processing);
        }, () => TransitionTo(SessionPhase.Ready));
    }

    private void RequestChildRestartDrain()
    {
        var run = _state.ChildRuns.Values.FirstOrDefault(static candidate =>
            candidate.Terminal is null && candidate.CancellationRequestedAtMs is null);
        if (run is null)
            return;
        Persist(new ChildRunEvent.CancellationRequested
        {
            SessionId = _sessionId, RunId = run.RunId, RecordedAtMs = NowMs()
        }, admitted =>
        {
            _state = _state.Apply(admitted);
            CloseChildDispatch(run.RunId);
            RequestChildRestartDrain();
        });
    }
}
