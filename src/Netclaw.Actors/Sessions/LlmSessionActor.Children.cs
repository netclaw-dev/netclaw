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
    private readonly HashSet<SubAgentRunId> _unreportableRuns = [];
    // The child prompts that the last recovery withdrew (DaemonRestarted and the other recovered reasons). Actor-local.
    private IReadOnlyList<ToolInteractionClosed> _restartWithdrawals = [];
    private readonly ChildConsentDigest _consentDigest = new();
    private bool _noticeAdmissionInOpenTurn;
    // durable-child-consent-wait PR 1: the park of each run that waits for consent, and the prompt counter of each run.
    // Both are actor-local. A restart loses a park, and recovery records its run as lost (existing rule).
    private readonly Dictionary<SubAgentRunId, ChildRunPark> _parks = [];
    private readonly Dictionary<SubAgentRunId, int> _promptsIssued = [];

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
                case InputClosed closed: RecoverParentClosure(closed); break;
                case ToolBatchStarted batch: RecoverParentBatch(batch); break;
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
        Command<ChildResultWritten>(HandleChildResultWritten);
        Command<ChildParkReached>(AcceptChildPark);
        Command<ChildEventRejected>(message =>
        {
            // Race: a duplicate answer from the stash can resume the run before the rejection of the first answer
            // arrives. Then the run has no park, and the resumed child owns its end.
            if (!_parks.ContainsKey(message.RunId))
            {
                _log.Warning("A rejected consent event of child run {RunId} arrived after the park ended", message.RunId);
                return;
            }
            EndParkedRun(message.RunId, SubAgentOutcomeReason.ToolExecutionFailed,
                "The journal rejected a consent request or answer of the child.", ActorRefs.Nobody);
        });
        Command<ChildStopFailed>(message => _log.Error(message.Error, "Child cancellation callback failed for {RunId}", message.RunId));
        Command<RecoverChildRuns>(_ => RecoverChildren());
        Command<StartHeldInputs>(_ => StartInputsHeldByThePause());
    }

    private void AcceptChild(AcceptChildStart message)
    {
        var replyTo = Sender;
        var prepared = message.Prepared;
        var existing = _state.ChildRuns.Values.FirstOrDefault(run => run.StartKey == message.StartKey);
        if (existing is not null)
        {
            if (existing.ArgumentsDigest != prepared.ArgumentsDigest || !HasSameChildAuthority(existing.OriginalContext, message.OriginalContext))
                replyTo.Tell(new Status.Failure(new ChildStartRejectedException(SubAgentOutcomeReason.SpawnError,
                    "The child start identity already names a different request.")));
            else
                replyTo.Tell(new ChildRunAcceptance(existing.RunId, existing.ScopeId));
            return;
        }
        // Security boundary (child consent pause, design D5): while a child consent request of the session is open,
        // the model cannot start more children. The text names only the waiting runs that the calling turn may control,
        // and only the count of the others.
        if (_toolWorkflow.RefusesChildStart(message.StartKey))
        {
            replyTo.Tell(new Status.Failure(new ChildStartRejectedException(SubAgentOutcomeReason.SpawnPaused,
                SpawnPausedText(message.OriginalContext))));
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
            replyTo.Tell(new Status.Failure(new ChildStartRejectedException(SubAgentOutcomeReason.SpawnUnavailable,
                "The child start no longer belongs to an active invocation.")));
            return;
        }
        var run = new BackgroundChildRun
        {
            RunId = prepared.RunId, ScopeId = prepared.Execution.Scope.ScopeId, StartKey = message.StartKey,
            AgentName = prepared.AgentName, SourceOperation = message.SourceOperation,
            ArgumentsDigest = prepared.ArgumentsDigest, OriginalContext = message.OriginalContext,
            InitialWorkingSnapshot = prepared.Execution.Scope.InitialWorkingSnapshot,
            AcceptedAtMs = NowMs(), Lifecycle = new ChildRunLifecycle.Accepted(null),
            // A retry turn cannot reach this call today (Tool batch dispatch requires admitted turn authority, #2548).
            SourceReminderId = RunningTurnReminder()
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
            if (StartChildActor(run, prepared))
                EmitOutput(new SubAgentOutput
                {
                    SessionId = _sessionId, AgentName = run.AgentName, Phase = SubAgentPhase.Started,
                    TimestampMs = NowMs(), ToolCount = prepared.ToolCount
                }, OutputFilter.ToolCalls);
        });
    }

    // Starts one incarnation of an accepted run: the first start, or a resume of a park. A failed start records the
    // end of the run and returns false.
    private bool StartChildActor(BackgroundChildRun run, PreparedChildStart prepared)
    {
        try
        {
            var child = Context.ActorOf(prepared.Props, prepared.ActorName);
            Context.Watch(child);
            var execution = _execution.TrackChild(run.RunId, child);
            var scope = prepared.Execution.Scope with
            {
                Authority = prepared.Execution.Scope.Authority with
                { InteractiveApproval = _toolWorkflow.ChildApprovalCapability(run) }
            };
            child.Tell(prepared.Execution with { Scope = scope, OwnedRun = execution, Cancellation = execution.Token }, Self);
            return true;
        }
        catch (Exception error) when (!FatalExceptionPolicy.IsFatal(error))
        {
            _log.Error(error, "Accepted child failed to start for {RunId}", run.RunId);
            RecordChildTerminal(run.RunId, new SubAgentResult
            {
                AgentName = run.AgentName, Completion = new ChildRunCompletion.Failed(SubAgentOutcomeReason.SpawnError),
                Output = $"The accepted child could not start: {error.Message}"
            }, lost: false, warning: "No child relaunch will occur.", ActorRefs.Nobody);
            return false;
        }
    }

    // Design D1 and D3: the owner keeps the park actor-local, releases the child, and asks the run requester once for
    // each parked call. The prompts show after all requests of the park are in the journal, in one atomic write.
    private void AcceptChildPark(ChildParkReached message)
    {
        var child = Sender;
        _state.ChildRuns.TryGetValue(message.RunId, out var run);
        switch (ChildParkRules.AcceptPark(_execution.OwnsChild(message.RunId, child), run?.Lifecycle))
        {
            case ChildParkDecision.StopSender:
                child.Tell(new ChildTerminalCommitted());
                return;
            case ChildParkDecision.EndCancelled:
                RecordChildTerminal(message.RunId, CancelledParkResult(run!), lost: false, warning: null, child);
                return;
        }
        // Security boundary: the prompts bind to the requester of the run record (ForChildCall checks it), never to
        // the requester of the current turn.
        if (!TurnContext.TryFromRecord(run!.OriginalContext, out var authority, out _) ||
            authority is not { SupportsInteractiveApproval: true })
        {
            EndParkedRun(run.RunId, SubAgentOutcomeReason.ToolExecutionFailed,
                "The requester of the run cannot answer a consent request.", child);
            return;
        }
        var requests = new ApprovalRequestBuilder(authority.SessionId, authority.RequesterSenderId,
            authority.RequesterPrincipal, authority.HasAdoptedContext, authority.HasThirdPartyAdoptedContext,
            authority.AdoptedSpeakerIds);
        var issued = _promptsIssued.GetValueOrDefault(run.RunId);
        var dispatches = new List<ToolInteractionRequestDispatch>();
        try
        {
            foreach (var request in message.Requests)
            {
                dispatches.Add(requests.ForChildCall(ChildApprovalPrompt.CreateCallId(run.RunId.Value, ++issued), request) with
                {
                    SourceChildRunId = run.RunId, ChildContext = authority
                });
            }
        }
        catch (ParentApprovalUnavailableException error)
        {
            EndParkedRun(run.RunId, SubAgentOutcomeReason.ToolExecutionFailed, error.Message, child);
            return;
        }
        _promptsIssued[run.RunId] = issued;
        _parks[run.RunId] = message.Park;
        Context.Unwatch(child);
        _execution.ReleaseChild(run.RunId);
        child.Tell(new ChildTerminalCommitted());
        PersistApprovalRequests(dispatches);
    }

    // Design D3, the resume gate. It runs after the journal holds an answer of a parked request.
    private void TryResumeParkedRun(SubAgentRunId runId)
    {
        var hasPark = _parks.TryGetValue(runId, out var park);
        if (ChildParkRules.ResumeGate(hasPark, _toolWorkflow.ChildApprovalCount(runId), RestartDrainRequested)
            != ChildResumeDecision.Resume)
            return;
        _parks.Remove(runId);
        var run = _state.ChildRuns[runId];
        PreparedChildStart prepared;
        try
        {
            // A park with no original call for an approved shell request cannot bind its consent, so the run ends.
            var resume = new ChildRunResume(park!, _toolWorkflow.TakeParkedAnswers(runId, park!));
            prepared = PrepareResume(run, resume);
        }
        catch (Exception error) when (!FatalExceptionPolicy.IsFatal(error))
        {
            _log.Warning("Parked child run {RunId} cannot resume: {Reason}", runId, error.Message);
            EndParkedRun(runId, error is ChildStartRejectedException rejected ? rejected.Reason : SubAgentOutcomeReason.SpawnError,
                error.Message, ActorRefs.Nobody);
            return;
        }
        // The prompt counter makes each incarnation name new, so it never meets an old actor that still stops. A resume
        // emits no second Started output.
        StartChildActor(run, prepared with { ActorName = $"{prepared.ActorName}-{_promptsIssued[runId]}" });
    }

    // Design D4: a resume is a spawn from the park. Security boundary: the authority comes only from the run record,
    // never from the current turn or the current parent directory.
    private PreparedChildStart PrepareResume(BackgroundChildRun run, ChildRunResume resume)
    {
        if (_subAgentSpawner is null || _subAgentRegistry is null ||
            !TurnContext.TryFromRecord(run.OriginalContext, out var authority, out _) || authority is null)
            throw new ChildStartRejectedException(SubAgentOutcomeReason.SpawnUnavailable, "The session cannot start a child.");
        var snapshot = run.InitialWorkingSnapshot ?? throw new ChildStartRejectedException(
            SubAgentOutcomeReason.SpawnUnavailable, "The run has no initial working snapshot.");
        _subAgentLoader?.SyncInto(_subAgentRegistry);
        var scope = new ToolRunScope
        {
            Session = new ToolSessionScope.Bound(_sessionId.Value, _sessionStorage),
            Audience = authority.Audience, Boundary = authority.Boundary, ChannelType = authority.ChannelType?.ToWireValue(),
            DefaultDeliveryTarget = authority.DefaultDeliveryTarget, RequestedDeliveryTarget = authority.RequestedDeliveryTarget,
            InlineOutputBudget = new InlineOutputBudget(_config.Tuning.MaxInlineToolResultChars),
            InteractiveApproval = new InteractiveApprovalCapability.Unavailable(),
            ModelInputModalities = _model.InputModalities,
            ProjectDirectory = resume.Park.ScopeDirectories.ProjectDirectory,
            RecentFiles = snapshot.WorkingContext.RecentFiles
        };
        var profile = _subAgentRegistry.TryGetByName(run.AgentName.Value);
        return _subAgentSpawner.PrepareResume(run, snapshot, resume, profile is { Visibility: SubAgentVisibility.UserFacing } ? profile : null,
            new ToolExecutionContext(scope, new ToolExecutionTimeout(_config.ToolExecutionTimeout)).Invocation);
    }

    // The one refusal path of a park (cut plan): the run ends as failed, and no parked call runs. A second call for an
    // ended run does nothing (RecordChildTerminal), so two rejected requests of one park record one end.
    private void EndParkedRun(SubAgentRunId runId, SubAgentOutcomeReason reason, string text, IActorRef child)
    {
        _parks.Remove(runId);
        if (!_state.ChildRuns.TryGetValue(runId, out var run))
            return;
        RecordChildTerminal(runId, new SubAgentResult
        {
            AgentName = run.AgentName, Completion = new ChildRunCompletion.Failed(reason),
            Output = $"The child could not wait for consent: {text} The calls that needed consent did not run."
        }, lost: false, warning: null, child);
    }

    private static SubAgentResult CancelledParkResult(BackgroundChildRun run) => new()
    {
        AgentName = run.AgentName, Completion = new ChildRunCompletion.Cancelled(SubAgentOutcomeReason.CancelledByParent),
        Output = "The parent cancelled the child. The calls that needed consent did not run."
    };

    private string SpawnPausedText(TurnContextRecord caller)
    {
        var open = _toolWorkflow.OpenChildRequests();
        var yours = open.Select(request => request.SourceChildRunId!.Value).Distinct()
            .Where(runId => HasSameChildRequester(_state.ChildRuns[runId].OriginalContext, caller))
            .Select(runId => runId.Value)
            .ToArray();
        var count = open.Count == 1
            ? "1 child consent request of this session waits for the operator."
            : $"{open.Count} child consent requests of this session wait for the operator.";
        var owned = yours.Length == 0 ? "None of them is yours." : $"Yours: {string.Join(", ", yours)}.";
        return $"spawn_agent is paused. {count}\n{owned} Ask the operator to answer the prompts, or to cancel a child.\n"
               + "A notice arrives when all requests close.";
    }

    // Start idempotency: a repeated start key names the same request only under the same full turn authority.
    private static bool HasSameChildAuthority(TurnContextRecord left, TurnContextRecord right)
        => CompareChildContexts(left, right, static (original, candidate) => original.HasSameAuthority(candidate));

    // Status and cancel: the requester that started the child owns it in each later turn. A reminder turn and a
    // person turn of one session manage each other's runs (TurnContext.CanControlChildRunOf).
    private static bool HasSameChildRequester(TurnContextRecord left, TurnContextRecord right)
        => CompareChildContexts(left, right, static (original, candidate) => candidate.CanControlChildRunOf(original));

    // A record that cannot be read matches nothing.
    private static bool CompareChildContexts(
        TurnContextRecord left,
        TurnContextRecord right,
        Func<TurnContext, TurnContext, bool> matches)
        => TurnContext.TryFromRecord(left, out var original, out _) && original is not null &&
           TurnContext.TryFromRecord(right, out var candidate, out _) && candidate is not null &&
           matches(original, candidate);

    private void ControlChild(ChildRunControl command)
    {
        var replyTo = Sender;
        if (_state.TryGetLegacyChildDisposition(command.RunId, out var legacy) &&
            legacy.AdoptedContext is { } original && HasSameChildRequester(original, command.Requester))
        {
            replyTo.Tell(JsonSerializer.Serialize(new
            {
                run_id = command.RunId.Value, state = "quarantined", reason = legacy.Reason,
                pending_approval_count = 0, terminal = false
            }));
            return;
        }
        if (!_state.ChildRuns.TryGetValue(command.RunId, out var run) || !HasSameChildRequester(run.OriginalContext, command.Requester))
        {
            replyTo.Tell("Error: This child run is not available to the requester.");
            return;
        }
        if (command.Cancel && run.Lifecycle is ChildRunLifecycle.Accepted && _parks.Remove(run.RunId))
        {
            // Design D7: a parked run has no actor, so the cancel records the end at once.
            RecordChildTerminal(run.RunId, CancelledParkResult(run), lost: false, warning: null, ActorRefs.Nobody);
            DeferAsync(run.RunId, runId => replyTo.Tell(ChildStatus(_state.ChildRuns[runId])));
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
            replyTo.Tell(ChildStatus(_state.ChildRuns[run.RunId]));
            var withdrawn = _toolWorkflow.WithdrawChildApprovals(committed, _state.ChildRuns[run.RunId], _sessionId,
                _timeProvider.GetUtcNow());
            EmitClosedPrompts(withdrawn);
            ChildApprovalsClosed(withdrawn);
        });
    }

    private string ChildStatus(BackgroundChildRun run)
    {
        var terminal = (run.Lifecycle as ChildRunLifecycle.Terminal)?.Result;
        var reported = run.Lifecycle as ChildRunLifecycle.Reported;
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
            terminal = terminal is not null || reported is not null,
            outcome = (terminal?.Result.Outcome ?? reported?.Outcome)?.ToString().ToLowerInvariant(),
            reason = (terminal?.Result.OutcomeReason ?? reported?.Reason)?.Value,
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
            var withdrawn = _toolWorkflow.WithdrawChildApprovals(committed, _state.ChildRuns[runId], _sessionId,
                _timeProvider.GetUtcNow());
            // The prompts close before the run reports its end, so no channel shows a live prompt of an ended run.
            EmitClosedPrompts(withdrawn);
            _execution.ReleaseChild(runId);
            _promptsIssued.Remove(runId);
            if (!child.Equals(ActorRefs.Nobody))
            {
                Context.Unwatch(child);
                child.Tell(new ChildTerminalCommitted());
            }
            EmitOutput(new SubAgentOutput
            {
                SessionId = _sessionId, AgentName = run.AgentName, Phase = SubAgentPhase.Completed,
                // A lost run is never a success in the report. Its stored completion can still be a partial run.
                Success = result.Success && !lost, Outcome = result.Outcome, OutcomeReason = result.OutcomeReason,
                TimestampMs = NowMs(), Duration = TimeSpan.FromMilliseconds(Math.Max(0, NowMs() - run.AcceptedAtMs)),
                FindingsCount = result.FindingsCount
            }, OutputFilter.ToolCalls);
            PrepareChildResult(_state.ChildRuns[runId], terminal, legacy: null);
            ChildApprovalsClosed(withdrawn);
        });
    }

    // Off the actor thread: review the child working context, then write result.md. The file is a copy of the
    // terminal event for the model to read. The notice states the outcome from the journal and never from the file.
    // A run that a deployed runtime prepared keeps its reviewed context and cannot be enriched again, because the
    // old state dropped the initial snapshot.
    private void PrepareChildResult(BackgroundChildRun run, ChildRunTerminal terminal, LegacyPreparedResult? legacy)
    {
        var owner = Self;
        var spawner = _subAgentSpawner;
        var storage = _sessionStorage.ForChild(run.RunId, run.ScopeId);
        var resultPath = (storage.ChildFiles ?? throw new InvalidOperationException("A child run requires child storage.")).ResultFile;
        var initialSnapshot = run.InitialWorkingSnapshot;
        if (legacy is null && initialSnapshot is null)
            throw new InvalidOperationException("A terminal child requires its initial context snapshot before preparation.");
        _ = PrepareAsync();
        async Task PrepareAsync()
        {
            var result = terminal.Result;
            WorkingContextDelta? reviewed = legacy?.ReviewedWorkingContext;
            var warning = terminal.EvidenceWarning;
            if (legacy is null)
            {
                if (spawner is null)
                {
                    warning = "Child evidence preparation is unavailable.";
                }
                else
                {
                    try
                    {
                        result = await spawner.EnrichWorkingContextResultAsync(terminal.Result,
                            initialSnapshot!, run.OriginalContext.Audience, CancellationToken.None);
                        reviewed = result.WorkingContext;
                    }
                    catch (Exception error) when (!FatalExceptionPolicy.IsFatal(error))
                    {
                        _log.Error(error, "Child result preparation failed for {RunId}", run.RunId);
                        warning = $"Result evidence preparation failed: {error.Message}";
                    }
                }
            }

            try
            {
                await ChildRunDocuments.WriteAtomicAsync(resultPath,
                    ChildRunDocuments.RenderResult(run, terminal, result, storage, warning), CancellationToken.None);
                owner.Tell(new ChildResultWritten(run.RunId, reviewed, resultPath, null), ActorRefs.NoSender);
            }
            catch (Exception error) when (!FatalExceptionPolicy.IsFatal(error))
            {
                // Any failure of the render or the write ends here. The notice says that the file is missing, so the
                // result is never lost silently and the run is never left without a notice.
                owner.Tell(new ChildResultWritten(run.RunId, reviewed, resultPath, error), ActorRefs.NoSender);
            }
        }
    }

    // The result file is ready, or its write failed. Either way the parent learns that the run ended: the notice says
    // when the file is missing. The notice goes through the ordinary input path, so it waits behind other inputs,
    // joins only a turn of the same requester, and never abandons an open approval.
    private void HandleChildResultWritten(ChildResultWritten written)
    {
        // Another writer can finish first, for instance after an actor restart. The run state decides.
        if (!_state.ChildRuns.TryGetValue(written.RunId, out var run) ||
            run.Lifecycle is not ChildRunLifecycle.Terminal terminal)
            return;
        if (written.WriteFailure is { } failure)
            _log.Error(failure, "The result file of child run {RunId} could not be written", run.RunId);

        var notice = new ChildNotice(run.RunId, written.ReviewedWorkingContext);
        var context = run.OriginalContext with { TurnId = new TurnId(IdGen.ShortId()).Value };
        // No sender: the owner produces this input, and nobody waits for an acknowledgement.
        Self.Tell(new SendUserMessage
        {
            SessionId = _sessionId,
            Content = ChildRunDocuments.RenderNotice(run, terminal.Result.Result, written.ResultPath, written.WriteFailure?.Message),
            Notice = new ChildNoticeInput(notice, context)
        }, ActorRefs.NoSender);
    }

    // Runs whose result the parent has not received, minus runs whose notice the session cannot admit.
    private IEnumerable<BackgroundChildRun> UnreportedRuns()
        => _state.GetUnreportedRuns().Where(run => !_unreportableRuns.Contains(run.RunId));

    // The reducer refused the notice of an ended run, for instance because the recorded authority of the run no
    // longer parses. A retry cannot succeed. The session reports the loss in the log with the path of the result
    // file, and it stops counting the run as owed: the run neither pins the session in memory nor drives the restart
    // reminder. The journal keeps the run as ended, so the next start of the session tries once and logs again.
    // A durable outcome for this case needs its own spec (follow-up).
    private void GiveUpOnChildNotice(SubAgentRunId runId, string reason)
    {
        if (!_state.ChildRuns.TryGetValue(runId, out var run) || run.Lifecycle is not ChildRunLifecycle.Terminal)
            return;

        if (_unreportableRuns.Add(runId))
        {
            var resultPath = _sessionStorage.ForChild(run.RunId, run.ScopeId).ChildFiles?.ResultFile;
            _log.Error(
                "The session cannot report child run {RunId} to the parent: {Reason}. The result file is {ResultFile}.",
                runId, reason, resultPath ?? "(unknown)");
        }
    }

    private bool ChildTerminated(IActorRef child)
    {
        if (_execution.FindChild(child) is not { } id || !_state.ChildRuns.TryGetValue(id, out var run))
            return false;
        RecordChildTerminal(id, LostChildResult(run, SubAgentOutcomeReason.ActorStopped), lost: true,
            warning: "The child stopped without a terminal acknowledgement.", ActorRefs.Nobody);
        return true;
    }

    private static SubAgentResult LostChildResult(BackgroundChildRun run, SubAgentOutcomeReason reason)
    {
        var checkpoint = run.Lifecycle switch
        {
            ChildRunLifecycle.Accepted accepted => accepted.Checkpoint,
            ChildRunLifecycle.Cancelling cancelled => cancelled.Checkpoint,
            _ => null
        };
        var restarted = reason == SubAgentOutcomeReason.OwnerRestarted;
        return new SubAgentResult
        {
            AgentName = run.AgentName,
            Completion = checkpoint is null
                ? new ChildRunCompletion.Failed(reason)
                : new ChildRunCompletion.Partial(reason, checkpoint.ConfirmedActivity),
            Output = (checkpoint, restarted) switch
            {
                (null, true) => $"{ChildRunDocuments.RestartLoss} Its external effects are uncertain.",
                (null, false) => "The child stopped before it sent a final answer. External effects are uncertain. The runtime does not start the child again.",
                ({ } last, true) => $"{ChildRunDocuments.RestartLoss} The last checkpoint is round {last.CompletedRound}. {last.Summary} "
                                    + "External effects after the last checkpoint are uncertain.",
                ({ } last, false) => $"The child stopped before it sent a final answer. The last checkpoint is round {last.CompletedRound}. {last.Summary} "
                                     + "External effects after the last checkpoint are uncertain."
            }
        };
    }

    // Recovery cannot tell a restart drain, a crash, and a stop of the session actor apart. In each case the owner
    // started again and the child did not survive, so the reason is owner_restarted.
    private void RecoverChildren()
    {
        foreach (var run in _state.GetExecutableChildRuns().ToArray())
        {
            if (run.Lifecycle is ChildRunLifecycle.Accepted or ChildRunLifecycle.Cancelling)
                RecordChildTerminal(run.RunId, LostChildResult(run, SubAgentOutcomeReason.OwnerRestarted), lost: true,
                    warning: "Recovery does not relaunch uncertain child work.", ActorRefs.Nobody);
            else if (run.Lifecycle is ChildRunLifecycle.Terminal terminal)
                PrepareChildResult(run, terminal.Result, terminal.LegacyPrepared);
        }
    }

    // A crash can land between the admission of a notice and the turn that consumes it. The notice then stays in the
    // journal as a pending input, and the result would reach the parent only by chance. Rebuild the queue entry under
    // the recorded authority and start its turn, so the notice runs once, ahead of any later input. The caller is the
    // RecoveryCompleted handler: the commands that arrived during recovery run after it, so a message that woke the
    // session buffers behind the notice turn. A person's pending input is not part of this repair (issue 2516):
    // recovery treats it as before. Behind a parked approval the recovery queued every pending input already, and the
    // answer ends the wait. An input of the running turn is not an orphan.
    private void ResumeOrphanNotices(bool pausedAtRecovery)
    {
        if (_toolWorkflow.HasParkedApprovals())
            return;

        // Admission order holds. The repair takes the notices in front of the first pending input of a person. A
        // notice behind that input waits for the restart resume, which runs the older input first. After a crash no
        // restart reminder exists and the older input has no turn (issue 2516), so the notice stays unconsumed. The
        // Warning below makes that loss visible.
        var selected = _input.SelectedInputs.Ids;
        var candidates = _state.GetExecutablePendingInputs()
            .Where(input => !selected.Contains(input.InputId))
            .ToList();
        // One damaged record must not stop the recovery of the other notices and of the lost children. The input stays
        // pending in the journal, so the loss is visible and the next start tries it again. When the session was
        // paused, StartInputsHeldByThePause logs each damaged pending input, so each record gets one log line.
        var (orphans, damaged) = SessionInputFlow.SplitByAuthority(
            candidates.TakeWhile(input => input.ChildNotice is not null));
        if (!pausedAtRecovery)
        {
            foreach (var input in damaged)
                _log.Error("Pending input {InputId} has a damaged authority", input.InputId);
        }

        var restored = _input.RestoreWaitingInputs(orphans.Select(static pair => pair.Input).ToArray(), _sessionId);

        // When the session was paused, StartHeldInputs queues such a notice, so it does not wait for that input.
        if (!pausedAtRecovery)
        {
            foreach (var waiting in candidates.SkipWhile(input => input.ChildNotice is not null)
                         .Where(input => input.ChildNotice is not null))
            {
                _log.Warning(
                    "The notice of child run {RunId} waits behind an older input of a person that has no turn. "
                    + "The notice stays unconsumed until that input runs (issue 2516). The result is in the run folder.",
                    waiting.ChildNotice!.RunId);
            }
        }

        if (restored == 0)
            return;

        _log.Info("Resuming {RestoredCount} child notice(s) that the previous run left unconsumed", restored);
        // A crash inside a tool batch leaves a tool_use with no result, and a provider refuses that history. Close the
        // interrupted batch first. Its journal callback then starts the notice turn.
        if (!AbandonInterruptedToolBatchAfterRecovery())
            StartWaitingInputAfterApproval();
    }

    // A paused session at the crash: an open child request of a run that is still Accepted. Replay never closes a
    // request that a cancel or the end of its run withdrew, so the run state decides.
    private bool WasPausedAtRecovery()
        => _toolWorkflow.OpenChildRequests().Any(request =>
            _state.ChildRuns.TryGetValue(request.SourceChildRunId!.Value, out var run)
            && run.Lifecycle is ChildRunLifecycle.Accepted);

    // A restart withdraws every child request, so the pause ends, and the input that it held can run. Recovery only
    // sends StartHeldInputs. When a restart reminder woke the session, the stashed reminder runs before this message,
    // so the one resume path queues the interrupted inputs first, in admission order. The handler then queues the held
    // input that is not queued yet and starts the queue in an idle session. A crash does not resume a person's input
    // (issue 2516). Behind a parked parent approval, recovery queued every pending input already.
    private void StartInputsHeldByThePause()
    {
        if (_toolWorkflow.HasParkedApprovals())
            return;
        var selected = _input.SelectedInputs.Ids;
        // The split comes first, so a damaged record that is not a notice also gets its log line. It stays pending.
        var (valid, damaged) = SessionInputFlow.SplitByAuthority(_state.GetExecutablePendingInputs()
            .Where(input => !selected.Contains(input.InputId)));
        foreach (var input in damaged)
            _log.Error("Pending input {InputId} has a damaged authority", input.InputId);
        // The pause holds automated input and child result notices (ChildConsentPause, design D5).
        var queued = _input.RestoreWaitingInputs(valid
            .Where(static pair => ChildConsentPause.Holds(pair.Authority, isChildNotice: pair.Input.ChildNotice is not null))
            .Select(static pair => pair.Input)
            .ToArray(), _sessionId);
        if (queued > 0)
            _log.Info("Queued {HeldCount} input(s) that the child consent pause held before the restart", queued);
        StartWaitingInputAfterApproval();
    }

    private sealed record StartHeldInputs : INoSerializationVerificationNeeded;

    // ── Approval notices (design D4) ──

    // A child consent request persisted. The digest is ready, and the session admits it at the next model boundary.
    private void ChildApprovalRequested(BackgroundChildRun run, ToolInteractionRequest prompt)
    {
        _consentDigest.Requested(AgentEventFactory.ApprovalRequested(_sessionId, run, prompt, _timeProvider.GetUtcNow()));
        ChildConsentChanged();
    }

    // A closed child prompt closes on every surface (design D6). Each channel redraws it with the reason and removes it,
    // also a second client that did not answer it. The output is not persisted. A late click reads the reason from the
    // workflow.
    private void EmitClosedPrompts(IReadOnlyList<AgentEvent.ChildApprovalResolved> closed)
    {
        foreach (var resolution in closed)
        {
            ToolInteractionCloseReason reason = resolution.Resolution is ChildApprovalResolution.Withdrawn withdrawn
                ? new ToolInteractionCloseReason.Withdrawn(withdrawn.Reason)
                : new ToolInteractionCloseReason.Answered();
            EmitOutput(new ToolInteractionClosed(resolution.PromptCallId, reason) { SessionId = _sessionId });
        }
    }

    // An answer or a withdrawal closed child requests. Only the last close of the session makes the digest ready, so no
    // text says that spawn_agent is available while another request stays open.
    private void ChildApprovalsClosed(IReadOnlyList<AgentEvent.ChildApprovalResolved> closed)
    {
        if (closed.Count == 0)
            return;
        foreach (var resolution in closed)
            _consentDigest.Resolved(resolution);
        if (!_toolWorkflow.IsChildConsentPaused)
            _consentDigest.MarkClosed();
        ChildConsentChanged();
    }

    // An idle session admits a ready notice at once, and then starts the next input: the notice, or the held input
    // after the last close. A running turn admits the notice at its next tool-batch end, turn end, or compaction end.
    // A turn that waits for a parent prompt runs no model call, so the notice is admitted at once and waits for its
    // own turn.
    private void ChildConsentChanged()
    {
        if (_phase.Current == SessionPhase.Ready)
            AdmitConsentNoticeThen(inOpenTurn: false, StartWaitingInputAfterApproval);
        else if (_phase.Current == SessionPhase.Processing && _toolWorkflow.HasPendingApprovals())
            AdmitConsentNoticeThen(inOpenTurn: true, static () => { });
    }

    // Persists a ready digest as one approval notice input, then runs the next step of the boundary in the journal
    // callback. The notice waits in the queue for its own turn. inOpenTurn tells a rejected admission whether a turn
    // stays open that the person must learn has stopped.
    private void AdmitConsentNoticeThen(bool inOpenTurn, Action next)
    {
        if (!_consentDigest.IsReady || RestartDrainRequested)
        {
            next();
            return;
        }

        _noticeAdmissionInOpenTurn = inOpenTurn;

        // At most one notice waits. The newer notice replaces the waiting one, and it keeps every open request and
        // every answer that the model did not read, so the model never reads a stale state of the pause.
        var waiting = _input.WaitingConsentNotice();
        var events = _consentDigest.Take(replacesUnreadNotice: waiting is not null);
        var text = AgentEventRenderer.Render(events, _input.WaitingFires(_state));
        var notice = SessionInputFlow.PrepareConsentNotice(_sessionId, NoticeOwner(events).OriginalContext, text, NowMs());
        _ = _state.Apply(notice);
        PersistReplacingInput(waiting, notice, "consent_notice_replaced", admitted =>
        {
            _state = _state.Apply(admitted);
            _input.ConsentNoticeCommitted(admitted);
            _log.Info("Admitted approval notice {InputId} ({EventCount} event(s))", admitted.InputId, events.Count);
            next();
        });
    }

    // With one digest for the session, the notice turn takes the context of the owner of the first open request, or
    // of the first request when none is open. All owners of one session share its thread, and the notice turn is
    // automation, so the borrowed audience and boundary only shape the reply.
    private BackgroundChildRun NoticeOwner(IReadOnlyList<AgentEvent> events)
    {
        var resolved = events.OfType<AgentEvent.ChildApprovalResolved>().Select(static evt => evt.PromptCallId).ToHashSet();
        var first = events.OfType<AgentEvent.ChildApprovalRequested>().FirstOrDefault(evt => !resolved.Contains(evt.PromptCallId))
                    ?? events.First();
        return first.Envelope.Sender is AgentAddress.ChildRun child
            ? _state.ChildRuns[child.RunId]
            : throw new InvalidOperationException("An approval notice requires a child run sender.");
    }

    // Approval notices reach the model at most once. A pending notice describes requests that the restart withdrew,
    // so recovery closes it with no turn and rebuilds the digest from the open entries. Today a restart withdraws
    // every child request first, so the rebuilt digest is empty. A rebuilt digest wakes the parent only when
    // recovery closed a notice that the model did not see.
    private void CloseRecoveredConsentNotices()
    {
        var pending = _state.PendingInputs
            .Where(input => TurnContext.TryFromRecord(input.TurnContext, out var context, out _)
                            && context is { IsChildConsentNotice: true })
            .Select(static input => input.InputId)
            .ToArray();
        var open = _toolWorkflow.OpenChildRequests()
            .Select(request => AgentEventFactory.ApprovalRequested(_sessionId, _state.ChildRuns[request.SourceChildRunId!.Value],
                new ToolCallId(request.CallId), new ToolName(request.ToolName),
                DateTimeOffset.FromUnixTimeMilliseconds(request.RequestedAtMs)))
            .ToArray();
        _consentDigest.Rebuild(open, ready: pending.Length > 0);
        if (pending.Length == 0)
            return;

        var closed = new InputClosed { SessionId = _sessionId, InputIds = pending, ClosedAtMs = NowMs(), KeepsTurn = true };
        // The rest of recovery must not see these inputs as pending, so the state changes now.
        _state = _state.Apply(closed);
        Persist(closed, committed =>
            _log.Info("Recovery closed {Count} pending approval notice(s)", committed.InputIds.Count));
    }

    // The notice is admitted input, so the findings review runs once, when the journal accepts it.
    private void ReviewChildFindings(BackgroundChildRun? run)
    {
        if (run?.Lifecycle is not ChildRunLifecycle.Terminal terminal || !terminal.Result.Result.Success)
            return;
        var result = terminal.Result.Result;
        var findings = SessionToolExecutionPipeline.ReviewChildFindings(run.RunId, run.AgentName,
            TimeSpan.Zero, result.Findings, _sessionId);
        ProcessChildFindings([new CompletedSubAgentRun
        {
            RunId = run.RunId, AgentName = run.AgentName, Completion = result.Completion,
            FindingsCount = result.FindingsCount, Duration = TimeSpan.Zero
        }], findings);
    }
}

/// <summary>
/// The one actor-local digest of approval events of a session (design D4, N2). It holds the open requests, the
/// answers that no notice gave yet, and the answers of the notice that waits now. It is ready on a new request and on
/// the last close of the session.
/// </summary>
internal sealed class ChildConsentDigest
{
    private readonly List<AgentEvent.ChildApprovalRequested> _open = [];
    private readonly List<AgentEvent.ChildApprovalResolved> _answers = [];
    private readonly List<AgentEvent.ChildApprovalResolved> _waitingAnswers = [];

    public bool IsReady { get; private set; }

    public void Requested(AgentEvent.ChildApprovalRequested request)
    {
        _open.Add(request);
        IsReady = true;
    }

    public void Resolved(AgentEvent.ChildApprovalResolved resolution)
    {
        _open.RemoveAll(request => request.PromptCallId == resolution.PromptCallId);
        _answers.Add(resolution);
    }

    public void MarkClosed() => IsReady = true;

    // A notice gives each answer once. When the new notice replaces a notice that still waits, the model did not read
    // that notice, so the new notice gives its answers again. When the earlier notice ran, its answers are read. The
    // open requests stay until they close, so each notice lists all of them.
    public IReadOnlyList<AgentEvent> Take(bool replacesUnreadNotice)
    {
        if (!replacesUnreadNotice)
            _waitingAnswers.Clear();
        _waitingAnswers.AddRange(_answers);
        _answers.Clear();
        IsReady = false;
        return [.. _open, .. _waitingAnswers];
    }

    public void Rebuild(IReadOnlyList<AgentEvent.ChildApprovalRequested> open, bool ready)
    {
        _open.Clear();
        _answers.Clear();
        _waitingAnswers.Clear();
        _open.AddRange(open);
        IsReady = ready && open.Count > 0;
    }
}
