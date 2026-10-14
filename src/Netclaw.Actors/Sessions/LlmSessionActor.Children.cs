// -----------------------------------------------------------------------
// <copyright file="LlmSessionActor.Children.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Sessions;

public sealed partial class LlmSessionActor
{
    private void ApplyLegacyCoordination(LegacyCoordinationEvent evt)
    {
        _state = LegacyCoordinationReplay.Apply(_state, evt);
        if (LegacyCoordinationReplay.GetLegacyTaskContext(evt) is { } legacyContext)
        {
            if (!TurnContext.TryFromRecord(legacyContext, out var context, out var reason) || context is null)
                throw new InvalidDataException($"Legacy task authority is invalid: {reason}");
            _toolWorkflow.ValidateRecoveredAuthority(context);
            _input.RestoreTurnAuthority(context, _trustContextDeriver);
        }
        if (evt.Manifest is "ic-v1" or "tbs-v1" or "tcr-v1")
        {
            switch (LegacyCoordinationReplay.GetCoreEvent(_state, evt))
            {
                case InputClosed closed: _state = _state.Apply(closed); break;
                case ToolBatchStarted batch:
                    RestoreConsumedInputs(batch.ConsumedInputIds, batch.UserMessage);
                    ApplyToolBatchStarted(batch);
                    break;
                case ToolCallRecorded result: ApplyToolCallRecorded(result); break;
            }
        }
        HydrateLegacyChildApprovals();
        foreach (var evidence in _state.LegacyCoordinationEvidence.Where(item =>
                     item.Disposition is LegacyCoordinationDisposition.QuarantinedInvalidAdoption or LegacyCoordinationDisposition.UnresolvedLegacyChild))
            _log.Warning("Legacy coordination quarantined manifest={Manifest} reason={Reason} inputIds={InputIds}",
                evidence.Manifest, evidence.Reason, string.Join(",", evidence.InputIds));
    }

    private void HydrateLegacyChildApprovals()
    {
        foreach (var evt in LegacyCoordinationReplay.GetLegacyChildApprovalEvents(_state))
        {
            switch (evt)
            {
                case ToolApprovalRequested requested: ApplyToolApprovalRequested(requested); break;
                case ToolApprovalResolved resolved: ApplyToolApprovalResolved(resolved); break;
            }
        }
        _state = _state.ClearLegacyChildApprovalEvents();
    }

    private void CommandChildMessages()
    {
        Command<AcceptChildStart>(AcceptChild);
        Command<ChildRunControl>(ControlChild);
        Command<ChildCheckpointReached>(message =>
        {
            if (!_execution.OwnsChild(message.RunId, message.Child) ||
                !_state.ChildRuns.TryGetValue(message.RunId, out var run) || run.Lifecycle is not ChildRunLifecycle.Accepted)
            {
                Sender.Tell(new Status.Failure(new OperationCanceledException("The child dispatch is closed.")));
                return;
            }
            var replyTo = Sender;
            var evt = new ChildRunEvent.Checkpointed(message.Checkpoint)
            { SessionId = _sessionId, RunId = message.RunId, RecordedAtMs = NowMs() };
            _ = _state.Apply(evt);
            Persist(evt, committed => { _state = _state.Apply(committed); replyTo.Tell(CommandAck.For(_sessionId)); });
        });
        Command<ChildTerminalReturned>(message =>
        {
            if (!_execution.OwnsChild(message.RunId, Sender))
                return;
            RecordChildTerminal(message.RunId, message.Result, lost: false, warning: null, Sender);
        });
        Command<ChildResultPrepared>(message =>
        {
            if (!_state.ChildRuns.TryGetValue(message.RunId, out var run) ||
                run.Lifecycle is not ChildRunLifecycle.Terminal terminal ||
                !ReferenceEquals(terminal.Result, message.ExpectedTerminal))
                return;
            var evt = new ChildRunEvent.ResultPrepared(message.Call, message.Result, message.ReviewedWorkingContext)
            { SessionId = _sessionId, RunId = message.RunId, RecordedAtMs = NowMs() };
            _ = _state.Apply(evt);
            Persist(evt, committed => { _state = _state.Apply(committed); WakeForChild(); });
        });
        Command<ChildPreparationFailed>(message =>
        {
            _log.Error(message.Error, "Child result preparation failed for {RunId}", message.RunId);
            var run = _state.ChildRuns.GetValueOrDefault(message.RunId);
            if (run?.Lifecycle is ChildRunLifecycle.Terminal terminal && ReferenceEquals(terminal.Result, message.ExpectedTerminal))
                Self.Tell(BuildPreparedChild(run, terminal.Result, terminal.Result.Result,
                    $"Result evidence preparation failed: {message.Error.Message}", reviewedContext: null));
        });
        Command<ChildStopFailed>(message => _log.Error(message.Error, "Child cancellation callback failed for {RunId}", message.RunId));
        Command<RecoverChildRuns>(_ => RecoverChildren());
    }

    private void AcceptChild(AcceptChildStart message)
    {
        var replyTo = Sender;
        var prepared = message.Prepared;
        var existing = _state.ChildRuns.Values.FirstOrDefault(run => run.StartKey == message.StartKey);
        if (existing is not null)
        {
            if (existing.ArgumentsDigest != prepared.ArgumentsDigest || !HasSameChildAuthority(existing.OriginalContext, message.OriginalContext))
                replyTo.Tell(new Status.Failure(new InvalidOperationException("The child start identity already names a different request.")));
            else
                replyTo.Tell(new ChildRunAcceptance(existing.RunId, existing.ScopeId));
            return;
        }
        var authorityIsCurrent = _input.Authority.Context is { } current &&
            current.TurnId.Value == message.StartKey.TurnId &&
            HasSameChildAuthority(current.ToRecord(), message.OriginalContext);
        var invocationIsCurrent = message.StartKey switch
        {
            ChildRunStartKey.Tool tool => _toolWorkflow.IsPendingChildStart(tool.CallId),
            ChildRunStartKey.Slash slash => _input.SelectedInputs.Ids.Contains(slash.InputId),
            _ => false
        };
        if (RestartDrainRequested || message.Cancellation.IsCancellationRequested || !authorityIsCurrent || !invocationIsCurrent)
        {
            replyTo.Tell(new Status.Failure(new InvalidOperationException("The child start no longer belongs to an active invocation.")));
            return;
        }
        var run = new BackgroundChildRun
        {
            RunId = prepared.RunId, ScopeId = prepared.Execution.Scope.ScopeId, StartKey = message.StartKey,
            AgentName = prepared.AgentName, SourceOperation = message.SourceOperation,
            ArgumentsDigest = prepared.ArgumentsDigest, OriginalContext = message.OriginalContext,
            InitialWorkingSnapshot = prepared.Execution.Scope.InitialWorkingSnapshot,
            AcceptedAtMs = NowMs(), Lifecycle = new ChildRunLifecycle.Accepted(null)
        };
        var accepted = new ChildRunAcceptance(run.RunId, run.ScopeId);
        var evt = new ChildRunAccepted
        {
            SessionId = _sessionId, Run = run, RecordedAtMs = NowMs(),
            AcceptanceResult = run.StartKey is ChildRunStartKey.Tool start ? new SerializableChatMessage
            {
                Role = ChatRole.Tool, ToolCallId = start.CallId, Name = run.SourceOperation, Content = accepted.ToJson()
            } : null
        };
        _ = _state.Apply(evt);
        Persist(evt, committed =>
        {
            _state = _state.Apply(committed);
            replyTo.Tell(accepted);
            try
            {
                var child = Context.ActorOf(prepared.Props, prepared.ActorName);
                Context.Watch(child);
                var execution = _execution.TrackChild(run.RunId, child);
                var scope = prepared.Execution.Scope with
                {
                    Authority = prepared.Execution.Scope.Authority with
                    { InteractiveApproval = _toolWorkflow.ChildApprovalCapability(run, Self) }
                };
                child.Tell(prepared.Execution with { Scope = scope, OwnedRun = execution, Cancellation = execution.Token }, Self);
                EmitOutput(new SubAgentOutput
                {
                    SessionId = _sessionId, AgentName = run.AgentName, Phase = SubAgentPhase.Started,
                    TimestampMs = NowMs(), ToolCount = prepared.ToolCount
                }, OutputFilter.ToolCalls);
            }
            catch (Exception error) when (!FatalExceptionPolicy.IsFatal(error))
            {
                RecordChildTerminal(run.RunId, new SubAgentResult
                {
                    AgentName = run.AgentName, Completion = new ChildRunCompletion.Failed(SubAgentOutcomeReason.SpawnError),
                    Output = $"The accepted child could not start: {error.Message}"
                }, lost: false, warning: "No child relaunch will occur.", ActorRefs.Nobody);
            }
        });
    }

    private static bool HasSameChildAuthority(TurnContextRecord left, TurnContextRecord right)
        => TurnContext.TryFromRecord(left, out var original, out _) && original is not null &&
           TurnContext.TryFromRecord(right, out var candidate, out _) && candidate is not null &&
           original.HasSameAuthority(candidate);

    private void ControlChild(ChildRunControl command)
    {
        var replyTo = Sender;
        if (_state.TryGetLegacyChildDisposition(command.RunId, out var legacy) &&
            legacy.AdoptedContext is { } original && HasSameChildAuthority(original, command.Requester))
        {
            replyTo.Tell(JsonSerializer.Serialize(new
            {
                run_id = command.RunId.Value, state = "quarantined", reason = legacy.Reason,
                pending_approval_count = 0, terminal = false
            }));
            return;
        }
        if (!_state.ChildRuns.TryGetValue(command.RunId, out var run) || !HasSameChildAuthority(run.OriginalContext, command.Requester))
        {
            replyTo.Tell("Error: This child run is not available to the requester.");
            return;
        }
        if (!command.Cancel || run.Lifecycle is not ChildRunLifecycle.Accepted)
        {
            replyTo.Tell(ChildStatus(run));
            return;
        }
        var evt = new ChildRunEvent.CancellationRequested
        { SessionId = _sessionId, RunId = run.RunId, RecordedAtMs = NowMs() };
        _ = _state.Apply(evt);
        Persist(evt, committed =>
        {
            _state = _state.Apply(committed);
            _execution.CloseChild(run.RunId);
            _toolWorkflow.ExpireChildApprovals(run.RunId);
            replyTo.Tell(ChildStatus(_state.ChildRuns[run.RunId]));
        });
    }

    private string ChildStatus(BackgroundChildRun run)
    {
        var terminal = run.Lifecycle switch
        {
            ChildRunLifecycle.Terminal raw => raw.Result,
            ChildRunLifecycle.Prepared prepared => prepared.Delivery.Terminal,
            ChildRunLifecycle.Consumed consumed => consumed.Delivery.Terminal,
            _ => null
        };
        var closed = run.Lifecycle as ChildRunLifecycle.Closed;
        var checkpoint = run.Lifecycle switch
        {
            ChildRunLifecycle.Accepted accepted => accepted.Checkpoint,
            ChildRunLifecycle.Cancelling cancelling => cancelling.Checkpoint,
            _ => null
        };
        return JsonSerializer.Serialize(new
        {
            run_id = run.RunId.Value, scope_id = run.ScopeId.Value, agent = run.AgentName.Value,
            source_operation = run.SourceOperation, accepted_at_ms = run.AcceptedAtMs,
            state = run.Lifecycle.GetType().Name.ToLowerInvariant(),
            pending_approval_count = _toolWorkflow.ChildApprovalCount(run.RunId),
            terminal = terminal is not null || closed is not null,
            outcome = (terminal?.Result.Outcome ?? closed?.Outcome)?.ToString().ToLowerInvariant(),
            reason = (terminal?.Result.OutcomeReason ?? closed?.Reason)?.Value,
            summary = terminal?.Result.Output ?? checkpoint?.Summary,
            completed_round = checkpoint?.CompletedRound,
            warning = terminal?.EvidenceWarning
        });
    }

    private void RecordChildTerminal(SubAgentRunId runId, SubAgentResult result, bool lost, string? warning, IActorRef child)
    {
        if (!_state.ChildRuns.TryGetValue(runId, out var run) ||
            run.Lifecycle is not (ChildRunLifecycle.Accepted or ChildRunLifecycle.Cancelling))
            return;
        _execution.CloseChild(runId);
        // The durable owner copies the mutable findings before it hands the event to persistence.
        var terminal = new ChildRunTerminal(result with
        { RunId = runId, ScopeId = run.ScopeId, Findings = result.Findings.Select(finding => finding with { Evidence = finding.Evidence.ToArray() }).ToList() }, lost, warning);
        var evt = new ChildRunEvent.TerminalRecorded(terminal)
        { SessionId = _sessionId, RunId = runId, RecordedAtMs = NowMs() };
        _ = _state.Apply(evt);
        Persist(evt, committed =>
        {
            _state = _state.Apply(committed);
            _toolWorkflow.ExpireChildApprovals(runId);
            _execution.ReleaseChild(runId);
            if (!child.Equals(ActorRefs.Nobody))
            {
                Context.Unwatch(child);
                child.Tell(new ChildTerminalCommitted());
            }
            EmitOutput(new SubAgentOutput
            {
                SessionId = _sessionId, AgentName = run.AgentName, Phase = SubAgentPhase.Completed,
                Success = result.Success, Outcome = result.Outcome, OutcomeReason = result.OutcomeReason,
                TimestampMs = NowMs(), Duration = TimeSpan.FromMilliseconds(Math.Max(0, NowMs() - run.AcceptedAtMs)),
                FindingsCount = result.FindingsCount
            }, OutputFilter.ToolCalls);
            PrepareChildResult(_state.ChildRuns[runId], terminal);
        });
    }

    private void PrepareChildResult(BackgroundChildRun run, ChildRunTerminal terminal)
    {
        var owner = Self;
        var initialSnapshot = run.InitialWorkingSnapshot
            ?? throw new InvalidOperationException("A terminal child requires its initial context snapshot before preparation.");
        var spawner = _subAgentSpawner;
        if (spawner is null)
        {
            owner.Tell(BuildPreparedChild(run, terminal, terminal.Result, "Child evidence preparation is unavailable.", reviewedContext: null));
            return;
        }
        _ = PrepareAsync();
        async Task PrepareAsync()
        {
            try
            {
                var result = await spawner.EnrichWorkingContextResultAsync(terminal.Result,
                    initialSnapshot, run.OriginalContext.Audience, CancellationToken.None);
                if (result.Success)
                {
                    var storage = _sessionStorage.ForChild(run.RunId, run.ScopeId);
                    result = result with { LogPath = storage.LogPath.Value, ArtifactDirectory = storage.ArtifactDirectory.Value };
                }
                owner.Tell(BuildPreparedChild(run, terminal, result, terminal.EvidenceWarning, result.WorkingContext));
            }
            catch (Exception error) when (!FatalExceptionPolicy.IsFatal(error))
            { owner.Tell(new ChildPreparationFailed(run.RunId, terminal, error)); }
        }
    }

    private static ChildResultPrepared BuildPreparedChild(BackgroundChildRun run, ChildRunTerminal terminal,
        SubAgentResult result, string? warning, WorkingContextDelta? reviewedContext)
    {
        var callId = new ToolCallId($"child-result-{run.RunId.Value}");
        var call = new SerializableChatMessage
        {
            Role = ChatRole.Assistant,
            ToolCalls = [new SerializableToolCall
            {
                CallId = callId, Name = new ToolName("child_run_result"),
                ArgumentsJson = JsonSerializer.Serialize(new { run_id = run.RunId.Value, source_operation = run.SourceOperation })
            }]
        };
        var body = new SerializableChatMessage
        {
            Role = ChatRole.Tool, Name = "child_run_result", ToolCallId = callId,
            Content = JsonSerializer.Serialize(new
            {
                run_id = run.RunId.Value, scope_id = run.ScopeId.Value, source_operation = run.SourceOperation,
                outcome = result.Outcome.ToString().ToLowerInvariant(), reason = result.OutcomeReason?.Value,
                output = result.Output, findings = result.Findings, working_context = result.WorkingContext,
                log_path = result.LogPath, artifact_directory = result.ArtifactDirectory, warning, lost = terminal.Lost
            })
        };
        return new ChildResultPrepared(run.RunId, terminal, call, body, reviewedContext);
    }

    private bool ChildTerminated(IActorRef child)
    {
        if (_execution.FindChild(child) is not { } id || !_state.ChildRuns.TryGetValue(id, out var run))
            return false;
        RecordChildTerminal(id, LostChildResult(run), lost: true, warning: "The child stopped without a terminal acknowledgement.", ActorRefs.Nobody);
        return true;
    }

    private static SubAgentResult LostChildResult(BackgroundChildRun run)
    {
        var checkpoint = run.Lifecycle switch
        {
            ChildRunLifecycle.Accepted accepted => accepted.Checkpoint,
            ChildRunLifecycle.Cancelling cancelled => cancelled.Checkpoint,
            _ => null
        };
        return new SubAgentResult
        {
            AgentName = run.AgentName,
            Completion = checkpoint is null
                ? new ChildRunCompletion.Failed(SubAgentOutcomeReason.ActorStopped)
                : new ChildRunCompletion.Partial(SubAgentOutcomeReason.ActorStopped, checkpoint.ConfirmedActivity),
            Output = checkpoint is null ? "The child owner was lost. External effects are uncertain; the child will not restart."
                : $"The child owner was lost after round {checkpoint.CompletedRound}. {checkpoint.Summary} External effects after that checkpoint are uncertain."
        };
    }

    private void RecoverChildren()
    {
        _toolWorkflow.ExpireRecoveredChildApprovals();
        foreach (var run in _state.GetExecutableChildRuns().ToArray())
        {
            if (run.Lifecycle is ChildRunLifecycle.Accepted or ChildRunLifecycle.Cancelling)
                RecordChildTerminal(run.RunId, LostChildResult(run), lost: true,
                    warning: "Recovery does not relaunch uncertain child work.", ActorRefs.Nobody);
            else if (run.Lifecycle is ChildRunLifecycle.Terminal terminal)
                PrepareChildResult(run, terminal.Result);
        }
        WakeForChild();
    }

    private BackgroundChildRun? UnconsumedChild(ChildContinuation? continuation)
        => continuation is not null && _state.ChildRuns.TryGetValue(continuation.RunId, out var run) &&
           run.Lifecycle is ChildRunLifecycle.Prepared ? run : null;

    private void ReviewConsumedChildFindings(BackgroundChildRun? run)
    {
        if (run?.Lifecycle is not ChildRunLifecycle.Prepared prepared || !prepared.Delivery.Terminal.Result.Success)
            return;
        var result = prepared.Delivery.Terminal.Result;
        var findings = SessionToolExecutionPipeline.ReviewChildFindings(run.RunId, run.AgentName,
            TimeSpan.Zero, result.Findings, _sessionId);
        ProcessChildFindings([new CompletedSubAgentRun
        {
            RunId = run.RunId, AgentName = run.AgentName, Completion = result.Completion,
            FindingsCount = result.FindingsCount, Duration = TimeSpan.Zero
        }], findings);
    }

    private bool HasReadyChildResult => _state.GetExecutableChildRuns().Any(run =>
        run.Lifecycle is ChildRunLifecycle.Prepared or ChildRunLifecycle.Consumed);

    private void WakeForChild()
    {
        if (_phase.Current != SessionPhase.Ready || RestartDrainRequested ||
            _toolWorkflow.HasPendingApprovals() || !HasReadyChildResult)
            return;
        if (_toolWorkflow.HasApprovalHistory(_state))
        {
            var abandoned = BuildInterruptedToolBatchAfterRecoveryEvent();
            Persist(abandoned, evt => { ApplyToolBatchAbandoned(evt); WakeForChild(); });
            return;
        }
        _turnState.ResetForNewTurn();
        _modelContext.ResetForNewTurn();
        TransitionTo(SessionPhase.Processing);
        FireLlmCall();
    }
}
