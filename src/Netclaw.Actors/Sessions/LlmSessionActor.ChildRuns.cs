// -----------------------------------------------------------------------
// <copyright file="LlmSessionActor.ChildRuns.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Actor;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using Akka.Event;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Memory;
using Netclaw.Actors.SubAgents;
using Netclaw.Security;
using Netclaw.Tools;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Sessions;

public sealed partial class LlmSessionActor
{
    // Actor references and lifetime tokens cannot enter the durable child ledger.
    private readonly Dictionary<SubAgentRunId, ChildRuntime> _backgroundChildRuntimes = [];
    private sealed record ChildRuntime(IActorRef Actor, CancellationTokenSource Lifetime, ChildRunDispatch Dispatch);
    private sealed record ChildCancellationFinalized(SubAgentRunId RunId, ChildRunTerminal Terminal) : INoSerializationVerificationNeeded;
    private sealed record ChildDispatchClosureCompleted(SubAgentRunId RunId, Exception? Cause) : INoSerializationVerificationNeeded;
    private sealed record DeliverPreparedChildResults;
    private sealed record ChildResultEnriched(SubAgentRunId RunId, ChildRunTerminal Terminal,
        IReadOnlyList<AcceptedSubAgentFinding> Findings) : INoSerializationVerificationNeeded;

    private void RegisterBackgroundChildHandlers()
    {
        Command<ChildApprovalRequested>(HandleChildApprovalRequested);
        Command<ChildApprovalWaitFinished>(HandleChildApprovalWaitFinished);
        Command<DeliverPreparedChildResults>(_ => DeliverPreparedChildResultsInOrder());
        Command<ControlBackgroundChildRun>(HandleChildControl);
        Command<ChildDispatchClosureCompleted>(message =>
        {
            if (!_state.ChildRuns.TryGetValue(message.RunId, out var run) || run.Terminal is not null
                || run.CancellationRequestedAtMs is null || run.DispatchClosedAtMs is not null)
                return;
            if (message.Cause is not null)
                throw new InvalidOperationException("The child dispatch closure failed.", message.Cause);
            Persist(new ChildRunEvent.DispatchClosed
            {
                SessionId = _sessionId, RunId = run.RunId, RecordedAtMs = NowMs()
            }, closed =>
            {
                _state = _state.Apply(closed);
                _ = FinalizeCancelledChildAsync(Self, _state.ChildRuns[run.RunId]);
            });
        });
        Command<ChildCancellationFinalized>(message =>
        {
            if (!_state.ChildRuns.TryGetValue(message.RunId, out var run) || run.Terminal is not null
                || run.CancellationRequestedAtMs is null || run.DispatchClosedAtMs is null)
                return;
            var child = _backgroundChildRuntimes.TryGetValue(run.RunId, out var runtime) ? runtime.Actor : ActorRefs.Nobody;
            RecordChildTerminal(run, message.Terminal, child, EnrichRecordedChild);
        });
        Command<StartBackgroundChildRun>(HandleStartBackgroundChildRun);
        Command<BackgroundChildCheckpoint>(message =>
        {
            if (!_backgroundChildRuntimes.TryGetValue(message.RunId, out var runtime) || !runtime.Actor.Equals(Sender)
                || !_state.ChildRuns.TryGetValue(message.RunId, out var run) || run.Terminal is not null
                || run.DispatchClosedAtMs is not null)
                return;
            if (message.Checkpoint.Summary.Length > _config.Tuning.MaxInlineToolResultChars)
                throw new InvalidDataException("A child checkpoint exceeds the owner output bound.");
            var evt = new ChildRunEvent.Checkpointed(message.Checkpoint)
            {
                SessionId = _sessionId, RunId = run.RunId, RecordedAtMs = NowMs()
            };
            _ = _state.Apply(evt);
            var child = Sender;
            Persist(evt, committed =>
            {
                _state = _state.Apply(committed);
                child.Tell(new BackgroundChildCheckpointAck(run.RunId, message.Checkpoint.CompletedRound), Self);
            });
        });
        Command<BackgroundChildTerminal>(HandleBackgroundChildTerminal);
        Command<ChildResultEnriched>(msg =>
        {
            if (!_state.ChildRuns.TryGetValue(msg.RunId, out var run) || run.Terminal is null || run.PreparedTerminal is not null)
                return;
            Persist(new ChildRunEvent.ResultPrepared(msg.Terminal, run.TerminalSequenceNr!.Value)
            {
                SessionId = _sessionId, RunId = run.RunId, RecordedAtMs = NowMs()
            }, evt =>
            {
                _state = _state.Apply(evt);
                _log.Info("child_run_result_prepared owner={SessionId} runId={RunId} journalSequence={SequenceNr}",
                    _sessionId.Value, run.RunId.Value, LastSequenceNr);
                if (!TurnContext.TryFromRecord(run.OriginalContext, out var original, out var reason) || original is null)
                    throw new InvalidDataException($"The child finding has invalid original authority: {reason}");
                foreach (var finding in msg.Findings)
                {
                    if (finding.Decision != SubAgentFindingReviewDecision.Accepted)
                        continue;
                    // A later parent input cannot replace the accepted child's memory authority.
                    EnqueueCheckpointFireAndForget(new MemoryCheckpointRequest(
                        _sessionId, original.TurnId, CheckpointTriggerType.SubagentFindings, 80,
                        SessionMemoryCheckpointFactory.ForSubAgentFinding(_sessionId,
                            original.Boundary.Value, original.Audience.ToWireValue(), finding)));
                }
                Self.Tell(new DeliverPreparedChildResults());
            });
        });
        Command<Terminated>(terminated =>
        {
            var runtime = _backgroundChildRuntimes.FirstOrDefault(pair => pair.Value.Actor.Equals(terminated.ActorRef));
            if (runtime.Value is null)
                return;
            _backgroundChildRuntimes.Remove(runtime.Key);
            CancelChildLifetime(runtime.Value);
            if (!_state.ChildRuns.TryGetValue(runtime.Key, out var run) || run.Terminal is not null
                || run.CancellationRequestedAtMs is not null)
                return;
            RecordChildTerminal(run, new ChildRunTerminal(new SubAgentResult
            {
                Completion = new ChildRunCompletion.Failed(SubAgentOutcomeReason.ActorStopped),
                Output = "The child stopped before a durable terminal receipt.",
                AgentName = run.AgentName, RunId = run.RunId, ScopeId = run.ScopeId
            }, false, null), ActorRefs.Nobody, EnrichRecordedChild);
        });
    }

    private void HandleStartBackgroundChildRun(StartBackgroundChildRun request)
    {
        var existing = _state.ChildRuns.Values.SingleOrDefault(run => run.StartKey == request.StartKey);
        if (existing is not null)
        {
            if (!SessionState.SameCanonicalContext(existing.OriginalContext, request.InvocationContext)
                || existing.SourceOperation != request.SourceOperation)
            {
                Sender.Tell(new Status.Failure(new InvalidDataException("The child retry differs from its original admitted authority.")));
                return;
            }
            Sender.Tell(existing.ArgumentsDigest == request.Prepared.ArgumentsDigest
                ? new ChildStartReply.Accepted(existing, existing.State)
                : new ChildStartReply.Conflict());
            return;
        }
        if (!OwnsToolExecution(request.ExecutionToken))
        {
            Sender.Tell(new Status.Failure(new OperationCanceledException("The start dispatch no longer owns this request.")));
            return;
        }
        if (_state.AdoptedTaskContext is null
            || !SessionState.SameCanonicalContext(_state.AdoptedTaskContext, request.InvocationContext)
            || request.StartKey.SessionId != _sessionId
            || request.StartKey.TurnId.Value != request.InvocationContext.TurnId)
        {
            Sender.Tell(new Status.Failure(new InvalidDataException("The child start differs from canonical admitted authority.")));
            return;
        }
        var prepared = request.Prepared;
        var childScope = prepared.Execution.Scope;
        var expectedStorage = _sessionStorage.ForChild(prepared.RunId, childScope.ScopeId);
        if (childScope.Authority.Session is not ToolSessionScope.Bound bound
            || bound.SessionId != childScope.ScopeId.Value || bound.Storage != expectedStorage
            || childScope.Authority.Audience != request.InvocationContext.Audience
            || childScope.Authority.Boundary != request.InvocationContext.Boundary
            || prepared.Execution.ActivitySink is not null)
        {
            Sender.Tell(new Status.Failure(new InvalidDataException("The child preparation differs from its assigned authority or storage.")));
            return;
        }
        var accepted = new BackgroundChildRun
        {
            RunId = prepared.RunId, ScopeId = childScope.ScopeId, StartKey = request.StartKey,
            AgentName = prepared.AgentName, ArgumentsDigest = prepared.ArgumentsDigest, SourceOperation = request.SourceOperation,
            OriginalContext = request.InvocationContext, OriginInputIds = _state.AdoptedTaskInputIds.ToArray(),
            InitialWorkingSnapshot = childScope.InitialWorkingSnapshot,
            ParentCheckpoint = _state.LoopCheckpoint, ParentReceiptFailure = _state.LoopReceiptFailure,
            StartBatchSettled = request.StartKey is ChildRunStartKey.Slash, AcceptedAtMs = NowMs()
        };
        var evt = new ChildRunAccepted { SessionId = _sessionId, Run = accepted };
        // Validate the complete consumer representation before the journal accepts it.
        _ = _state.Apply(evt);
        var replyTo = Sender;
        Persist(evt, committed =>
        {
            _state = _state.Apply(committed);
            _log.Info("child_run_accepted owner={SessionId} runId={RunId} journalSequence={SequenceNr}",
                _sessionId.Value, accepted.RunId.Value, LastSequenceNr);
            var response = new ChildStartReply.Accepted(accepted, BackgroundChildState.Accepted);
            var lifetime = new CancellationTokenSource();
            IActorRef child;
            try
            {
                child = Context.ActorOf(prepared.Props, $"child-run-{accepted.RunId.Value}");
            }
            catch (Exception error) when (!FatalExceptionPolicy.IsFatal(error))
            {
                lifetime.Dispose();
                _log.Error(error, "Child creation failed after acceptance runId={RunId}", accepted.RunId.Value);
                RecordChildTerminal(accepted, new ChildRunTerminal(new SubAgentResult
                {
                    Completion = new ChildRunCompletion.Failed(SubAgentOutcomeReason.SpawnError),
                    Output = "The child could not start after durable acceptance.", AgentName = accepted.AgentName,
                    RunId = accepted.RunId, ScopeId = accepted.ScopeId
                }, false, null), ActorRefs.Nobody, EnrichRecordedChild);
                replyTo.Tell(response);
                return;
            }
            _backgroundChildRuntimes.Add(accepted.RunId, new ChildRuntime(child, lifetime, new ChildRunDispatch()));
            Context.Watch(child);
            Persist(new ChildRunEvent.Started
            {
                SessionId = _sessionId, RunId = accepted.RunId, RecordedAtMs = NowMs()
            }, started =>
            {
                _state = _state.Apply(started);
                EmitOutput(new SubAgentOutput
                {
                    SessionId = _sessionId, TimestampMs = NowMs(), AgentName = accepted.AgentName,
                    Phase = SubAgentPhase.Started, ToolCount = prepared.ToolCount
                }, OutputFilter.ToolCalls);
                child.Tell(new RunBackgroundSubAgent(accepted.RunId, WithChildApprovalOwner(accepted, prepared.Execution, _backgroundChildRuntimes[accepted.RunId]) with
                {
                    Cancellation = lifetime.Token, ActivitySink = null
                }, _backgroundChildRuntimes[accepted.RunId].Dispatch), Self);
                replyTo.Tell(response);
            });
        });
    }

    private void HandleBackgroundChildTerminal(BackgroundChildTerminal message)
    {
        if (!_backgroundChildRuntimes.TryGetValue(message.RunId, out var runtime) || !runtime.Actor.Equals(Sender)
            || !_state.ChildRuns.TryGetValue(message.RunId, out var run))
        {
            _log.Warning("Rejected a child terminal with no live owner runId={RunId}", message.RunId.Value);
            return;
        }
        if (run.Terminal is not null)
        {
            Sender.Tell(new BackgroundChildTerminalAck(run.RunId), Self);
            return;
        }
        if (run.CancellationRequestedAtMs is not null)
        {
            // Framework finalization retains the last durable checkpoint after cancellation.
            return;
        }
        RecordChildTerminal(run, new ChildRunTerminal(message.Result, false, null), Sender, EnrichRecordedChild);
    }

    private void RecordChildTerminal(BackgroundChildRun run, ChildRunTerminal terminal, IActorRef child,
        Action<BackgroundChildRun> afterCommit)
    {
        if (_state.ChildRuns[run.RunId].Approvals.Any(static approval => approval.Resolution is null))
        {
            ExpireChildApprovals(run.RunId, () => RecordChildTerminal(_state.ChildRuns[run.RunId], terminal, child, afterCommit));
            return;
        }
        // Queue one terminal Persist until its acknowledgement. LastSequenceNr excludes queued writes.
        var evt = new ChildRunEvent.TerminalRecorded(terminal, LastSequenceNr + 1)
        {
            SessionId = _sessionId, RunId = run.RunId, RecordedAtMs = NowMs()
        };
        _ = _state.Apply(evt);
        Persist(evt, recorded =>
        {
            _state = _state.Apply(recorded);
            _log.Info("child_run_terminal_recorded owner={SessionId} runId={RunId} journalSequence={SequenceNr}",
                _sessionId.Value, run.RunId.Value, LastSequenceNr);
            EmitOutput(new SubAgentOutput
            {
                SessionId = _sessionId, TimestampMs = NowMs(), AgentName = run.AgentName,
                Phase = SubAgentPhase.Completed, Success = terminal.Result.Success,
                Outcome = terminal.Result.Outcome, OutcomeReason = terminal.Result.OutcomeReason,
                Duration = TimeSpan.FromMilliseconds(Math.Max(0, NowMs() - run.AcceptedAtMs)),
                FindingsCount = terminal.Result.Findings.Count
            }, OutputFilter.ToolCalls);
            if (!child.Equals(ActorRefs.Nobody))
                child.Tell(new BackgroundChildTerminalAck(run.RunId), Self);
            if (run.CancellationRequestedAtMs is not null && !child.Equals(ActorRefs.Nobody))
                Context.Stop(child);
            afterCommit(_state.ChildRuns[run.RunId]);
            if (_restartDrainRequested && _phase.Current == SessionPhase.Passivating && !HasLiveChildRuns)
                CompletePassivation();
        });
    }

    private void EnrichRecordedChild(BackgroundChildRun run)
    {
        // Terminal enrichment belongs to the committed receipt, after the child lifetime ends.
        _ = EnrichRecordedChildAsync(Self, run, run.Terminal!, CancellationToken.None);
    }

    private async Task EnrichRecordedChildAsync(IActorRef owner, BackgroundChildRun run, ChildRunTerminal terminal, CancellationToken token)
    {
        ChildRunTerminal prepared;
        try
        {
            var result = await SubAgentSpawner.EnrichWorkingContextResultAsync(
                terminal.Result, run.InitialWorkingSnapshot, run.OriginalContext.Audience, _workingContextSnapshots, token).ConfigureAwait(false);
            if (result.Success)
            {
                var storage = _sessionStorage.ForChild(run.RunId, run.ScopeId);
                result = new EnrichedChildRunResult.SuccessfulRun(result,
                    new EnrichedChildRunResult.RunLocations(storage.LogPath, storage.ArtifactDirectory)).ToProtocolResult();
            }
            prepared = terminal with { Result = result };
        }
        catch (Exception error) when (!FatalExceptionPolicy.IsFatal(error))
        {
            _log.Warning("Child result enrichment failed runId={RunId} reason={Reason}", run.RunId.Value, error.GetType().Name);
            prepared = terminal with { EvidenceWarning = "Result enrichment failed. The original terminal receipt remains unchanged." };
        }
        catch (Exception error) when (FatalExceptionPolicy.IsFatal(error))
        {
            owner.Tell(new WorkingContextSnapshotFatal(error));
            return;
        }
        var findings = new List<AcceptedSubAgentFinding>();
        if (prepared.Result.Success)
        {
            try
            {
                foreach (var finding in prepared.Result.Findings)
                {
                    var review = SessionToolExecutionPipeline.ReviewSubAgentFinding(finding, _sessionId);
                    findings.Add(new AcceptedSubAgentFinding
                    {
                        RunId = run.RunId, AgentName = run.AgentName,
                        Duration = TimeSpan.FromMilliseconds(Math.Max(0, NowMs() - run.AcceptedAtMs)),
                        Shape = finding.Shape, Title = finding.Title, Content = finding.Content, Kind = finding.Kind,
                        Sensitivity = finding.Sensitivity, RecallMode = finding.RecallMode,
                        UpdateSemantics = finding.UpdateSemantics, Confidence = finding.Confidence,
                        Durability = finding.Durability, Reusability = finding.Reusability,
                        Evidence = finding.Evidence, FreshnessAtMs = finding.FreshnessAtMs,
                        Decision = review.Decision, DecisionReason = review.Reason
                    });
                }
            }
            catch (Exception error) when (!FatalExceptionPolicy.IsFatal(error))
            {
                findings.Clear();
                _log.Warning("Child finding review failed runId={RunId} reason={Reason}", run.RunId.Value, error.GetType().Name);
                prepared = prepared with
                {
                    EvidenceWarning = string.Join(" ", new[] { prepared.EvidenceWarning,
                        "Finding review failed. The factual findings remain in the terminal result." }.Where(static text => text is not null))
                };
            }
            catch (Exception error) when (FatalExceptionPolicy.IsFatal(error))
            {
                owner.Tell(new WorkingContextSnapshotFatal(error));
                return;
            }
        }
        owner.Tell(new ChildResultEnriched(run.RunId, prepared, findings));
    }

    private void DeliverPreparedChildResultsInOrder()
    {
        if (_restartDrainRequested)
            return;
        if (_phase.Current is SessionPhase.Recovering or SessionPhase.Passivating)
            return;
        var next = _state.ChildRuns.Values.Where(static run => run.Terminal is not null && run.DeliveryInputId is null)
            .OrderBy(static run => run.TerminalSequenceNr).FirstOrDefault();
        // An earlier terminal owns the next delivery slot, even when enrichment finishes later.
        if (next is null || next.PreparedTerminal is null || !next.StartBatchSettled)
            return;
        var callId = new ToolCallId($"child-result-{Guid.NewGuid():N}");
        var input = new InputAdmitted
        {
            SessionId = _sessionId, InputId = new InputId($"child-result-{next.RunId.Value}"),
            SourceChildRunId = next.RunId, TurnContext = next.OriginalContext, AdmittedAtMs = NowMs(),
            UserMessage = new SerializableChatMessage
            {
                Role = Protocol.ChatRole.Tool, ToolCallId = callId,
                Name = ChildRunDelivery.ToolName(next), Content = ChildRunDelivery.Body(next)
            }
        };
        var evt = new ChildRunEvent.DeliveryAdmitted(input)
        {
            SessionId = _sessionId, RunId = next.RunId, RecordedAtMs = input.AdmittedAtMs
        };
        _ = _state.Apply(evt);
        Persist(evt, committed =>
        {
            _state = _state.Apply(committed);
            _log.Info("child_run_delivery_admitted owner={SessionId} runId={RunId} journalSequence={SequenceNr} inputId={InputId} callId={ToolCallId}",
                _sessionId.Value, next.RunId.Value, LastSequenceNr, input.InputId.Value, callId.Value);
            var command = new SendUserMessage
            {
                SessionId = _sessionId, Content = input.UserMessage.Content!, AdmittedInputId = input.InputId
            };
            if (_phase.Current == SessionPhase.Ready && _state.PendingInputs[0].InputId == input.InputId)
                ContinueIncomingUserMessage(command);
            else
                _buffer.Add((command, false));
            Self.Tell(new DeliverPreparedChildResults());
        });
    }

    private bool CanShareChildContinuation(InputAdmitted first, InputAdmitted next)
    {
        if (first.SourceChildRunId is null || next.SourceChildRunId is null)
            return first.SourceChildRunId is null && next.SourceChildRunId is null;
        var firstRun = _state.GetChildContinuation(first);
        var nextRun = _state.GetChildContinuation(next);
        return SessionState.SameCanonicalContext(firstRun.OriginalContext, nextRun.OriginalContext)
            && BackgroundChildRun.SameCheckpoint(firstRun.ParentCheckpoint, nextRun.ParentCheckpoint)
            && firstRun.ParentReceiptFailure == nextRun.ParentReceiptFailure;
    }

    private void AppendAdmittedInput(InputAdmitted input)
    {
        if (input.SourceChildRunId is not null)
        {
            var run = _state.GetChildContinuation(input);
            var callId = input.UserMessage.ToolCallId!.Value;
            var existing = _state.History.LastOrDefault(message => message.ToolCalls.Any(call => call.CallId == callId));
            if (existing is null || !ParkedToolBatchHistory.HasToolResult(_state.History, existing, callId.Value))
                throw new InvalidDataException("A child result lacks its durably adopted delivery pair.");
            MergeSuccessfulSubAgentWorkingContext(run.PreparedTerminal!.Result.Completion);
            return;
        }
        _state = _state with { History = _state.History.Add(input.UserMessage) };
    }

    private void ContinueAdoptedChildResult(InputAdmitted input)
    {
        _turnEmittedText = false;
        _activeInputIds.Clear();
        _activeInputIds.Add(input.InputId);
        _sessionManagedTemporaryCorrections.Clear();
        _deliveryRetry.Clear();
        _currentTurnSource = null;
        SetSystemPrompt();
        AppendAdmittedInput(input);
        _recallManager.ResetForNewTurn();
        _compactionOverflowRetryCount = 0;
        if (_state.LoopReceiptFailure)
            SettleToolLoop(ToolCycleMessages.MissingReceipt);
        else
        {
            FireLlmCall();
            TransitionTo(SessionPhase.Processing);
        }
    }

    private void HandleChildControl(ControlBackgroundChildRun message)
    {
        if (!OwnsToolExecution(message.ExecutionToken) || _state.AdoptedTaskContext is null
            || !SessionState.SameCanonicalContext(_state.AdoptedTaskContext, message.InvocationContext))
        {
            Sender.Tell(new Status.Failure(new OperationCanceledException("The control dispatch no longer owns this request.")));
            return;
        }
        if (!_state.ChildRuns.TryGetValue(message.Request.RunId, out var run)
            || !TurnContext.TryFromRecord(run.OriginalContext, out var original, out _) || original is null
            || !TurnContext.TryFromRecord(message.InvocationContext, out var current, out _) || current is null
            || original.SessionId != current.SessionId || !TurnContext.HasSameAuthority(original, current))
        {
            Sender.Tell(new ChildControlReply(null));
            return;
        }
        if (!message.Request.Cancel || run.Terminal is not null || run.CancellationRequestedAtMs is not null)
        {
            Sender.Tell(new ChildControlReply(run));
            return;
        }
        var replyTo = Sender;
        Persist(new ChildRunEvent.CancellationRequested
        {
            SessionId = _sessionId, RunId = run.RunId, RecordedAtMs = NowMs()
        }, admitted =>
        {
            _state = _state.Apply(admitted);
            replyTo.Tell(new ChildControlReply(_state.ChildRuns[run.RunId]));
            CloseChildDispatch(run.RunId);
        });
    }

    private void CloseChildDispatch(SubAgentRunId runId)
    {
        if (_backgroundChildRuntimes.TryGetValue(runId, out var runtime))
        {
            CancelChildLifetime(runtime);
            _ = ConfirmChildDispatchClosureAsync(Self, runId, runtime.Dispatch);
        }
        else
            Self.Tell(new ChildDispatchClosureCompleted(runId, null));
    }

    private static async Task ConfirmChildDispatchClosureAsync(IActorRef owner, SubAgentRunId runId, ChildRunDispatch dispatch)
    {
        Exception? failure = null;
        try
        {
            await dispatch.WaitForEnteredInvocationAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }
        owner.Tell(new ChildDispatchClosureCompleted(runId, failure));
    }

    private void CancelChildLifetime(ChildRuntime runtime)
    {
        runtime.Dispatch.Close();
        // The first asynchronous cancellation owns disposal, including later actor termination.
        if (!runtime.Lifetime.IsCancellationRequested)
            _ = ChildRunDispatch.CancelAndDisposeAsync(runtime.Lifetime, _log);
    }

    private async Task FinalizeCancelledChildAsync(IActorRef owner, BackgroundChildRun run)
    {
        var storage = _sessionStorage.ForChild(run.RunId, run.ScopeId);
        var artifact = storage.ArtifactDirectory.Value;
        var anchor = storage.Binding?.EnvelopeRoot.Value ?? storage.SessionDirectory.Value;
        var report = Path.Combine(artifact, "cancelled-results.json");
        string output;
        string? warning = null;
        try
        {
            await CompleteChildReportWithinGraceAsync(async finalizationToken =>
            {
                try
                {
                    finalizationToken.ThrowIfCancellationRequested();
                    if (ToolOutputSpillLocation.EnsureSessionWorkspaceDirectory(anchor) is not null
                        || !ToolOutputSpillLocation.IsSafeForIo(anchor, artifact))
                        throw new IOException("The child artifact directory crosses a filesystem link.");
                    Directory.CreateDirectory(artifact);
                    if (!ToolOutputSpillLocation.IsSafeForIo(anchor, artifact)
                        || !ToolOutputSpillLocation.IsSafeForIo(artifact, report))
                        throw new IOException("The child report destination crosses a filesystem link.");
                    var body = JsonSerializer.Serialize(new
                    {
                        run_id = run.RunId.Value, state = "Cancelled",
                        summary = run.ChildCheckpoint?.Summary,
                        confirmed_activity = run.ChildCheckpoint?.ConfirmedActivity,
                        external_effects = "Recorded receipts describe known local results. They do not prove external effects stopped."
                    });
                    await AtomicFile.WriteAllTextAsync(report, body, AtomicFile.HardenOwnerOnly, finalizationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (!FatalExceptionPolicy.IsFatal(error))
                {
                    _log.Warning("Child report worker failed runId={RunId} reason={Reason}", run.RunId.Value, error.GetType().Name);
                    throw;
                }
            }, _timeProvider).ConfigureAwait(false);
            output = $"The child was cancelled. The last durable partial report is at {report}.";
        }
        catch (Exception error) when (!FatalExceptionPolicy.IsFatal(error))
        {
            output = "The child was cancelled. Netclaw retained its last durable checkpoint.";
            warning = error is OperationCanceledException
                ? "The five-second framework report grace expired. No report path is confirmed."
                : $"The framework report failed: {error.GetType().Name}. No report path is confirmed.";
            _log.Warning("Child report finalization failed runId={RunId} reason={Reason}", run.RunId.Value, error.GetType().Name);
        }
        owner.Tell(new ChildCancellationFinalized(run.RunId, new ChildRunTerminal(new SubAgentResult
        {
            Completion = new ChildRunCompletion.Cancelled(SubAgentOutcomeReason.CancelledByParent)
            { ConfirmedActivity = run.ChildCheckpoint?.ConfirmedActivity },
            AgentName = run.AgentName, RunId = run.RunId, ScopeId = run.ScopeId,
            Output = output, ArtifactDirectory = artifact, LogPath = storage.LogPath.Value
        }, false, warning)));
    }

    internal static async Task CompleteChildReportWithinGraceAsync(
        Func<CancellationToken, Task> operation, TimeProvider timeProvider)
    {
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5), timeProvider);
        var token = grace.Token;
        // Filesystem checks and flushes can block before their first await.
        var work = Task.Run(() => operation(token), token);
        StreamTaskObservation.ObserveSilently(work);
        await work.WaitAsync(token).ConfigureAwait(false);
    }

    private void CloseBackgroundChildLifetimes()
    {
        foreach (var runtime in _backgroundChildRuntimes.Values)
        {
            CancelChildLifetime(runtime);
        }
        _backgroundChildRuntimes.Clear();
    }
}
