// -----------------------------------------------------------------------
// <copyright file="LlmSessionActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Persistence;
using Netclaw.Actors.Hosting;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Skills;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Text;
using Netclaw.Configuration;
using Netclaw.Actors.Tools;
using Netclaw.Security;
using Netclaw.Security.Authorization.Consent;
using Netclaw.Tools;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;
using static Netclaw.Actors.Jobs.BackgroundJobProtocol;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Per-session persistent actor managing LLM conversation state.
/// Receives <see cref="SendUserMessage"/>, invokes <see cref="IChatClient"/>,
/// persists <see cref="TurnRecorded"/> events, and sends strongly-typed
/// <see cref="SessionOutput"/> events to subscribers filtered by <see cref="OutputFilter"/>.
///
/// An immutable <see cref="SessionState"/> record holds conversation state.
/// The input and lifetime owners hold their transient facts. The actor keeps subscribers and effects.
///
/// Uses four command behaviors:
/// - Ready: accepts user messages and starts an LLM call
/// - Processing: buffers new messages during the LLM call
/// - Compacting: compacts the context when usage exceeds the threshold
/// - Passivating: completes the stop policy after an idle period
/// </summary>
public sealed partial class LlmSessionActor : ReceivePersistentActor, IWithTimers
{
    private readonly SessionId _sessionId;
    private readonly IChatClient _chatClient;
    private readonly IChatClient _compactionClient;
    private readonly ModelCapabilities _model;
    private readonly SessionConfig _config;
    private readonly IToolExecutor? _toolExecutor;
    private readonly SessionToolExecutionPipeline? _toolExecutionPipeline;
    private readonly Tools.ToolRegistry? _toolRegistry;
    private readonly IToolApprovalService? _approvalService;
    private readonly IMemoryCheckpointSink _memoryCheckpointSink;
    private readonly MemoryProposalGate _memoryProposalGate = new();
    private readonly MemoryConfig _memoryConfig;
    private readonly TimeProvider _timeProvider;
    private readonly IOperationalNotificationSink _notificationSink;
    private readonly SessionStoragePaths _sessionStorage;
    private readonly ISessionLifecycleObserver? _lifecycleObserver;
    private readonly Memory.SQLiteMemoryStore? _memoryStore;
    private readonly Memory.MemoryEmbedderHolder? _memoryEmbedderHolder;
    private readonly Memory.MemoryVectorIndexHolder? _memoryVectorIndexHolder;
    private readonly IChatClientProvider _clientProvider;
    private readonly ILoggingAdapter _log;

    // Transient state (not persisted)
    private readonly SessionInputFlow _input = new();
    private readonly SessionSubscriberManager _subscribers = new();
    private readonly SessionToolWorkflow _toolWorkflow = new();
    private readonly SessionModelContext _modelContext;
    private readonly ToolAccessPolicy? _toolAccessPolicy;
    private readonly TrustContextDeriver? _trustContextDeriver;
    // Owns the exposed tool list (base + discovered) and lease-based eviction.
    private readonly DiscoveredToolCache _discoveredToolCache = new();

    // Last observed input token count from LLM response (for compaction trigger)
    private long _lastInputTokenCount;

    // Per-turn transient counters (tool budget, duplicate detection, empty-response retries)
    private readonly TurnStateTracker _turnState = new();

    // Delivery retry handler (eligibility tracking, retry counting, nudge builders)
    private readonly DeliveryRetryHandler _deliveryRetry = new();

    // Reference to the singleton SessionLogDispatcher; resolved lazily on
    // recovery completion. The dispatcher owns one SessionLogActor child per
    // resolved log path and is the single writer for that file. Audit messages
    // (SendUserMessage, SessionOutput) are forwarded through it.
    private IActorRef? _logActor;

    // Child actor for per-session memory curation (evaluate-before-write pipeline)
    private IActorRef? _curationActor;

    // Child actor for session-level memory observation (distills transcript on idle)
    private IActorRef? _observerActor;

    private SessionExecutionLifetime _execution = null!;

    // Skill registry for slash-command dispatch
    private readonly Skills.SkillRegistry? _skillRegistry;
    private readonly SubAgentDefinitionRegistry? _subAgentRegistry;
    private readonly SubAgentSpawner? _subAgentSpawner;
    private readonly FileSubAgentDefinitionLoader? _subAgentLoader;

    private readonly Telemetry.ISessionMetrics? _sessionMetrics;

    private bool RestartDrainRequested => _execution.RestartDrainRequested;
    private string? _pendingRestartNotice;
    private string? _turnRestartNotice;

    // Persistent state (immutable — replaced on each event)
    private SessionState _state = SessionState.Empty;

    // Explicit state machine phase (metadata + validation layer over Become())
    private readonly SessionPhaseMachine _phase = new();

    public override string PersistenceId { get; }
    public ITimerScheduler Timers { get; set; } = null!;

    protected override void PreStart()
    {
        // Akka injects Timers after construction and before PreStart.
        _execution = new SessionExecutionLifetime(Timers, Self);
        base.PreStart();
    }

    public LlmSessionActor(
        string entityId,
        ModelCapabilities modelCapabilities,
        SessionConfig config,
        SessionServices services,
        IOperationalNotificationSink notificationSink,
        SessionToolServices? tools = null,
        SessionMemoryServices? memory = null,
        SessionObservability? observability = null)
    {
        _sessionId = new SessionId(entityId);
        _sessionMetrics = observability?.Metrics;
        _lifecycleObserver = observability?.LifecycleObserver;
        _clientProvider = services.ClientProvider;
        _chatClient = services.ClientProvider.GetClient(ModelRole.Main);
        // The provider owns role resolution. Its contract states that a role without a
        // configured model falls back to ModelRole.Main, so the actor must not repeat
        // that decision here.
        _compactionClient = services.ClientProvider.GetClient(ModelRole.Compaction);
        _model = modelCapabilities;
        _config = config;
        _skillRegistry = tools?.SkillRegistry;
        _subAgentRegistry = tools?.SubAgentRegistry;
        _subAgentSpawner = tools?.SubAgentSpawner;
        _subAgentLoader = tools?.SubAgentLoader;
        _toolExecutor = tools?.ToolExecutor;
        _toolRegistry = tools?.ToolRegistry;
        _toolAccessPolicy = tools?.AccessPolicy;
        _approvalService = tools?.ApprovalService;
        _memoryCheckpointSink = memory?.CheckpointSink ?? NullMemoryCheckpointSink.Instance;
        _memoryStore = memory?.MemoryStore;
        _memoryEmbedderHolder = memory?.EmbedderHolder;
        _memoryVectorIndexHolder = memory?.VectorIndexHolder;
        _memoryConfig = memory?.MemoryConfig ?? new MemoryConfig();
        _timeProvider = services.TimeProvider;
        _notificationSink = notificationSink;
        _sessionStorage = services.StorageResolver.Resolve(_sessionId);
        _trustContextDeriver = tools?.TrustDeriver;
        PersistenceId = $"session-{entityId}";

        // Enrich logger with session context — all log messages automatically include SessionId
        _log = Context.GetLogger().WithContext(NetclawLogProperties.SessionId, _sessionId.Value);
        _modelContext = new SessionModelContext(
            _sessionId, _sessionStorage, services, memory, tools,
            _discoveredToolCache, _model.InputModalities, _log);
        _toolExecutionPipeline = tools is null
            ? null
            : new SessionToolExecutionPipeline(
                tools.ToolExecutor,
                services.TimeProvider,
                NoLogger.Instance);

        // Load only the explicit core for initial LLM calls. Deferred first-party and
        // MCP tools are discovered through search_tools, activated through load_tool,
        // and share the configured loaded-tool lease.
        if (_toolRegistry is not null)
        {
            _discoveredToolCache.SeedBaseTools(_toolRegistry.GetCoreTools());
        }

        // ── Recovery handlers ──
        Recover<TurnRecorded>(RecoverParentTurn);
        Recover<SessionTitleSet>(evt => _state = _state.Apply(evt));
        Recover<SessionBackgroundJobsReaped>(evt => _state = _state.Apply(evt));
        Recover<SessionCompacted>(evt =>
        {
            _state = _state.Apply(evt);
            _toolWorkflow.CompleteTurn();
            _input.ClearTurnContext();
        });
        Recover<ToolBatchStarted>(RecoverParentBatch);
        Recover<InputAdmitted>(evt =>
        {
            _state = _state.Apply(evt);
            // Live admission of a new input clears the delivery state of the earlier turn. Replay does the same.
            _deliveryRetry.Clear();
        });
        Recover<InputClosed>(RecoverParentClosure);
        Recover<ChildRunAccepted>(evt => _state = _state.Apply(evt));
        Recover<ChildRunEvent>(evt => _state = _state.Apply(evt));
        Recover<LegacyCoordinationEvent>(ApplyLegacyCoordination);
        Recover<ToolCallRecorded>(ApplyToolCallRecorded);
        Recover<ToolApprovalRequested>(ApplyToolApprovalRequested);
        Recover<ToolApprovalResolved>(ApplyToolApprovalResolved);
        Recover<ToolBatchAbandoned>(ApplyToolBatchAbandoned);
        Recover<SnapshotOffer>(offer =>
        {
            if (offer.Snapshot is SessionSnapshot snapshot)
            {
                _state = SessionState.FromSnapshot(snapshot);
                HydrateLegacyChildApprovals();
                if (snapshot.EligibleDeliveryTurnNumber is { } eligibleTurn)
                {
                    _deliveryRetry.MarkEligible(eligibleTurn);

                    // A restore from a snapshot does not replay the TurnRecorded event of the eligible turn,
                    // so the snapshot carries what the event would give. Every new snapshot writes the routed
                    // mark, so its presence tells a new snapshot from an old one. An old snapshot records nothing,
                    // and a report for that turn then finds an unknown turn and writes a Warning.
                    if (snapshot.EligibleDeliveryTurnRoutedSkill is { } routedMark)
                    {
                        _deliveryRetry.RecordTurn(eligibleTurn, snapshot.EligibleDeliveryReminderId, routedMark);
                    }
                }

                _log.Info("Recovered from snapshot (turns={TurnCount})", _state.TurnCount);
            }
        });
        Recover<RecoveryCompleted>(_ =>
        {
            _log.Info("Recovery complete (turns={TurnCount}, history={HistoryCount})",
                _state.TurnCount, _state.History.Count);

            // Always read fresh from disk — identity file edits take effect immediately
            SetSystemPrompt();

            TransitionTo(SessionPhase.Ready);
            RestoreWaitingInputs();
            Self.Tell(new RecoverChildRuns());

            // Resolve once at recovery time. ActorRegistry returns Nobody if
            // the dispatcher hasn't been registered (unit-test scenarios that
            // skip the hosting wiring) — leave _logActor null in that case.
            var registry = ActorRegistry.For(Context.System);
            if (registry.TryGet<SessionLogDispatcherActorKey>(out var dispatcher))
                _logActor = dispatcher;

            if (_memoryStore is not null)
            {
                _curationActor = Context.ActorOf(
                    Memory.MemoryCurationActor.CreateProps(
                        _memoryStore, _sessionId, _memoryConfig.Curation, _clientProvider,
                        _memoryEmbedderHolder, _memoryVectorIndexHolder),
                    "memory-curation");

                // Distillation processes a full transcript — allow 5x normal sidecar timeout
                // for slower local models (e.g., Qwen 3.5 27B)
                var distillationTimeout = TimeSpan.FromTicks(_config.SidecarLlmTimeout.Ticks * 5);
                _observerActor = Context.ActorOf(
                    SessionMemoryObserverActor.CreateProps(
                        _sessionId,
                        _compactionClient,
                        TimeSpan.FromSeconds(Math.Max(10, _config.MemoryObserverIdleSeconds)),
                        distillationTimeout,
                        _config.Tuning.MemoryDistillationTurnInterval,
                        _timeProvider),
                    "memory-observer");
            }
        });
    }

    // ── State machine ──

    /// <summary>
    /// Validated phase transition. Enforces legal transition rules and calls
    /// the corresponding <c>Become()</c> handler. Illegal transitions throw
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    private void TransitionTo(SessionPhase target)
    {
        if (!_phase.TryTransition(target, out var from))
            throw new InvalidOperationException(
                $"Illegal session phase transition: {_phase.Current} → {target}");

        _log.Info("session_phase_transition from={From} to={To}", from, target);

        EmitProcessingStateForPhase(from, target);

        _observerActor?.Tell(new SessionPhaseChanged(target));

        switch (target)
        {
            case SessionPhase.Ready:
                Become(Ready);
                break;
            case SessionPhase.Processing:
                Become(Processing);
                break;
            case SessionPhase.Compacting:
                Become(Compacting);
                break;
            case SessionPhase.Passivating:
                Become(Passivating);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, null);
        }
    }

    private void EmitProcessingStateForPhase(SessionPhase from, SessionPhase target)
    {
        var wasProcessing = from is SessionPhase.Processing or SessionPhase.Compacting;
        var isProcessing = target is SessionPhase.Processing or SessionPhase.Compacting;
        if (wasProcessing == isProcessing)
            return;

        EmitOutput(new ProcessingStateOutput(isProcessing)
        {
            SessionId = _sessionId
        }, OutputFilter.ProcessingState);
    }

    // ── Command behaviors ──

    private void Ready()
    {
        CommandCommonMessages();
        CommandSessionContextMessages();
        CommandSnapshotMessages();

        // Passivation: stop after idle timeout, snapshot first for fast recovery
        if (_config.IdleTimeout > TimeSpan.Zero)
        {
            Context.SetReceiveTimeout(_config.IdleTimeout);
        }

        Command<ReceiveTimeout>(_ =>
        {
            if (HasLiveBackgroundJobs || _execution.HasLiveChildren)
            {
                _log.Info(
                    "Session idle but active background jobs remain; deferring passivation");
                return;
            }

            // Pending tool approvals do NOT defer passivation: approval state is
            // journaled (ToolApprovalRequested/Resolved) and an approval response
            // rehydrates the session and re-drives the parked batch, the same
            // path that already covers daemon restarts. Keeping the actor in
            // memory while a human decides buys nothing but resident memory.
            if (_toolWorkflow.HasPendingApprovals())
            {
                _log.Info(
                    "Session idle with {PendingApprovalCount} journaled approval(s) outstanding; passivating — an approval response will rehydrate and resume",
                    _toolWorkflow.PendingDiagnosticCount);
            }

            if (_toolWorkflow.ShouldAbandonResolvedForIdle())
            {
                _log.Info(
                    "Session idle with {ResolvedApprovalCount} resolved approval(s) but no completed tool result; abandoning parked tool batch before passivation",
                    _toolWorkflow.ResolvedDiagnosticCount);
                var abandoned = BuildToolBatchAbandonedEvent(
                    ParkedToolBatchHistory.AbandonedResultPrefix + "the session became idle before the approved action completed.");
                Persist(abandoned, evt =>
                {
                    ApplyToolBatchAbandoned(evt);
                    if (!_input.HasBufferedContinuation)
                    {
                        TransitionTo(SessionPhase.Passivating);
                        return;
                    }

                    // An acknowledged input waited behind the batch. After the abandonment no approval is parked,
                    // so a recovery would not rebuild it. Run it now and passivate when its work ends.
                    _execution.DeferIdlePassivationUntilWaitingInputEnds();
                    StartWaitingInputAfterApproval();
                    CompleteDeferredIdlePassivation();
                });
                return;
            }

            _log.Info("Session idle, entering passivation (timeout={Timeout})", _config.IdleTimeout);
            TransitionTo(SessionPhase.Passivating);
        });

        Command<ProcessingWatchdogExpired>(_ => { });
        Command<LlmCallFailed>(_ => { }); // stale failure arriving after watchdog timeout
        Command<LlmResponseReceived>(_ => { }); // stale response arriving after transition to Ready
        Command<CompactionWorkCompleted>(_ => { });
        Command<CompactionWorkFailed>(_ => { });
        CommandDistillationAckNoOp();
        Command<DeliveryFailed>(HandleDeliveryFailedWhenReady);

        // Approval click for a tool batch that parked while the session was
        // idle (deferred passivation) or that survived cold recovery. The
        // live-Processing handler does not apply here — there is no in-flight
        // tool-loop task — so re-drive the parked batch from history.
        CommandAsync<ToolInteractionResponse>(HandleToolInteractionResponseWhenIdle);
        CommandAsync<ToolInteractionTextResponse>(HandleToolInteractionTextResponseWhenIdle);

        Command<SendUserMessage>(HandleIncomingUserMessage);
    }

    private void Processing()
    {
        // Disable idle timeout while processing — re-enabled on transition to Ready
        Context.SetReceiveTimeout(null);
        CommandCommonMessages();
        CommandSessionContextMessages();
        CommandSnapshotMessages();

        Command<SendUserMessage>(cmd =>
        {
            if (RestartDrainRequested)
            {
                _log.Info("Rejecting new user message while restart drain is pending");
                TryReplyNack(SessionIngressGate.RestartInProgressMessage);
                return;
            }

            if (IsRestartResume(cmd))
            {
                AckRestartResumeNoOp(cmd);
                return;
            }

            AdmitInput(cmd, InputPlacement.BufferDuringModel);
        });

        Command<DeliveryFailed>(msg => HandleDeliveryFailedWithoutRetry(msg, "processing"));

        Command<CompactionWorkCompleted>(_ => { });
        Command<CompactionWorkFailed>(_ => { });

        Command<LlmResponseReceived>(HandleLlmResponseReceived);

        Command<LlmResponseDeltaReceived>(HandleLlmResponseDeltaReceived);

        Command<ToolExecutionSingleCompleted>(msg =>
        {
            if (!_execution.OwnsToolDispatch(msg.DispatchToken))
                return;
            var result = msg.Result;
            Persist(new ToolCallRecorded
            {
                SessionId = _sessionId,
                ToolResult = result.Message,
                StartedBackgroundJob = result.StartedBackgroundJob,
                RecordedAtMs = NowMs()
            }, evt =>
            {
                ApplyToolCallRecorded(evt);
                ProcessToolCallResult(result);
                TryCompleteStreamedToolBatch();
                TryStopDurableApprovalWaits();
            });
        });

        Command<ToolExecutionBatchCompleted>(msg =>
        {
            if (!_execution.OwnsToolDispatch(msg.DispatchToken))
                return;
            _execution.OnToolBatchMessage();
            _toolWorkflow.MarkExecutionTaskCompleted();
            TryCompleteStreamedToolBatch();
        });

        Command<ToolExecutionFailed>(msg =>
        {
            if (!_execution.OwnsToolDispatch(msg.DispatchToken))
                return;
            _execution.OnToolFailure();
            _toolWorkflow.DiscardPendingMedia();
            TurnLog().Error(msg.Cause, "turn_tool_execution_failed");

            const string errorMessage = "I encountered an error executing a tool. Please try again.";
            var category = msg.Cause is TimeoutException ? ErrorCategory.Timeout : ErrorCategory.ToolFailure;

            // A partial parallel-batch failure leaves the tail assistant tool_calls
            // message with unanswered call(s) — the sibling(s) that faulted. Close
            // those out with synthetic tool-results BEFORE FailCurrentTurn appends the
            // "I encountered an error" assistant reply. Otherwise that reply wedges
            // between the tool_calls message and the rest of its results, which strict
            // OpenAI-compatible providers (DeepSeek/Qwen/vLLM) reject on every later
            // turn with 400 "insufficient tool messages following tool_calls".
            if (_toolWorkflow.PlanFailedRunAbandonment(_state, _sessionId, NowMs()) is { } abandoned)
            {
                Persist(abandoned, evt =>
                {
                    ApplyToolBatchAbandoned(evt);
                    FailCurrentTurn(errorMessage, msg.Cause, category);
                });
                return;
            }

            FailCurrentTurn(errorMessage, msg.Cause, category);
        });



        CommandAsync<ToolInteractionResponse>(HandleProcessingApprovalResponseAsync);

        CommandAsync<ToolInteractionTextResponse>(async msg =>
        {
            if (RestartDrainRequested)
            {
                _log.Info("Rejecting text tool interaction response while restart drain is in progress");
                TryReplyNack(SessionIngressGate.RestartInProgressMessage);
                return;
            }

            if (!TryResolveTextApprovalResponse(msg, out var structured, out var nackReason)
                || structured is null)
            {
                TryReplyNack(nackReason ?? ApprovalNackReasons.PromptExpired);
                return;
            }

            await HandleProcessingApprovalResponseAsync(structured);
        });

        Command<LlmCallFailed>(msg =>
        {
            if (!_execution.AcceptModelResult(msg.CallId)) return;

            // A failed LLM call invalidates the actor-local exposure set. The
            // context-overflow path must do this before recovery compaction starts.
            _discoveredToolCache.EvictAll();
            TurnLog().Info("turn_discovered_tools_evicted — tool list reset to base tools after LLM call failure");

            // Context overflow: roll back the failed turn, buffer the user message,
            // compact the history, and let the normal buffer drain re-deliver it.
            if (IsContextOverflowError(msg.Cause))
            {
                if (!_input.TryStartOverflowRecovery())
                {
                    TurnLog().Error(msg.Cause, "turn_context_overflow_after_compaction — failing turn");
                    FailCurrentTurn(
                        "Context window exceeded even after compaction. The conversation may be too large.",
                        msg.Cause, ErrorCategory.ProviderFailure);
                    return;
                }

                TurnLog().Warning(msg.Cause, "turn_context_overflow — rolling back turn and triggering compaction");

                // Roll back: move the user message (and any recall nudges from this turn)
                // out of history and into the buffer so the normal drain re-delivers it.
                RollBackCurrentTurnIntoBuffer();

                EmitOutput(new ErrorOutput
                {
                    SessionId = _sessionId,
                    Message = "Context window exceeded — compacting session history.",
                    Category = ErrorCategory.ProviderFailure,
                    CorrelationId = Guid.NewGuid(),
                    Cause = msg.Cause
                });

                // Use the configured context window as the token count estimate since
                // the provider rejected the request without returning usage stats.
                Self.Tell(new CompactionTriggered(_model.ContextWindowTokens));
                TransitionTo(SessionPhase.Compacting);
                return;
            }

            // Transient-failure retry is owned entirely by the transport
            // (RetryingChatClient, pre-first-chunk) and is already exhausted by the time
            // the failure reaches here, so a failed turn is terminal.
            TurnLog().Error(msg.Cause, "turn_llm_call_failed");

            var errorMessage = ExtractLlmErrorMessage(msg.Cause);
            var category = msg.Cause is TimeoutException ? ErrorCategory.Timeout : ErrorCategory.ProviderFailure;
            FailCurrentTurn(errorMessage, msg.Cause, category);
        });

        Command<ProcessingWatchdogExpired>(msg =>
        {
            if (!_execution.IsCurrentWatchdog(msg))
                return;

            TimeoutException timeoutCause;
            if (msg.ThinkingCap)
            {
                // One model call streamed only thinking for the whole cap. Thinking
                // resets the no-progress deadline, so only this cap ends the call.
                // The provider streamed without fault and the model gave no answer,
                // so the model gets the blame (ModelBehavior, not Timeout).
                var cap = _config.NoProgressTimeout * ProcessingWatchdog.ThinkingCapMultiplier;
                _execution.StopModelForTimeout();
                _log.Error("Processing watchdog expired for operation {OperationName} (opId={OperationId}, thinkingCap={Cap}s)",
                    msg.OperationName, msg.OperationId, cap.TotalSeconds);
                FailCurrentTurn(
                    $"The model streamed only thinking for {cap.TotalSeconds:F0} seconds. It produced no answer and no tool call. Netclaw stopped the call.",
                    new TimeoutException($"Session LLM call streamed only thinking for {cap.TotalSeconds:F0}s without text or a tool call"),
                    ErrorCategory.ModelBehavior);
                return;
            }

            if (msg.NoProgress)
            {
                // Keepalive-immune deadline: the stream produced no substantive
                // output for the whole budget. Never refreshed by keepalives, so
                // reaching it means a wedge, not a slow-but-healthy prefill.
                timeoutCause = new TimeoutException(
                    $"Session LLM call produced no substantive output within {_config.NoProgressTimeout.TotalSeconds:F0}s");
            }
            else
            {
                // Report the budget actually in force: the generous prefill budget
                // while still waiting for the first token, the tighter inter-delta
                // budget once promoted. (Reporting FirstTokenTimeout during prefill
                // would under-state the wait by ~3x.)
                var timeout = msg.OperationName switch
                {
                    ProcessingWatchdog.LlmCall => _execution.HasContentStreamed ? _config.FirstTokenTimeout : _config.PrefillTimeout,
                    _ => _config.TurnLlmTimeout
                };
                timeoutCause = new TimeoutException(
                    $"Session processing operation '{msg.OperationName}' exceeded watchdog timeout of {timeout.TotalSeconds:F0}s");
            }

            _execution.StopModelForTimeout();

            _log.Error("Processing watchdog expired for operation {OperationName} (opId={OperationId}, noProgress={NoProgress})",
                msg.OperationName, msg.OperationId, msg.NoProgress);
            var errorMessage = ExtractLlmErrorMessage(timeoutCause);
            FailCurrentTurn(errorMessage, timeoutCause, ErrorCategory.Timeout);
        });

        CommandDistillationAckNoOp();
    }

    private void HandleLlmResponseReceived(LlmResponseReceived msg)
    {
        if (!_execution.AcceptModelResult(msg.CallId))
            return;

        var response = msg.Response;
        var lastMessage = response.Messages[^1];
        var analysis = LlmResponseClassifier.Analyze(lastMessage);

        _log.Debug(
            "LLM response content breakdown: text={TextChars}ch thinking={ThinkingChars}ch toolCalls={ToolCallCount} finishReason={FinishReason}",
            analysis.TextChars,
            analysis.ThinkingChars,
            analysis.ToolCalls.Count,
            response.FinishReason?.ToString() ?? "null");

        if (analysis.Kind == LlmResponseKind.ToolCalls && _toolExecutor is not null)
        {
            HandleToolCallResponse(lastMessage, analysis.ToolCalls, response.Usage);
            return;
        }

        if (analysis.Kind is LlmResponseKind.ThinkingOnly or LlmResponseKind.Empty)
        {
            var truncated = response.FinishReason == ChatFinishReason.Length;
            switch (_turnState.EvaluateEmptyResponse(analysis.Kind, truncated))
            {
                case EmptyResponseAction.Retry retry:
                    _log.Warning("LLM produced {Kind} response ({ThinkingChars} chars, truncated={Truncated}) — retrying with nudge",
                        analysis.Kind, analysis.ThinkingChars, truncated);
                    _state = _state.AddSystemNudge(retry.NudgeText);
                    FireLlmCall();
                    return;
                case EmptyResponseAction.Fail fail:
                    _log.Warning("LLM produced {Kind} response — failing turn (finishReason={FinishReason})",
                        analysis.Kind, response.FinishReason?.ToString() ?? "null");
                    FailCurrentTurn(fail.ErrorMessage, fail.Cause, EmptyReplyFailureCategory(response.FinishReason));
                    return;
            }
        }

        // A reply that ended on the output limit is cut off. Its text is a stub, not
        // an answer, so the turn fails instead of posting the stub. A tool call is
        // handled above. A thinking-only or empty reply takes the retry path above.
        // The stream ended on the model's own terms, so the model gets the blame:
        // the same rule as EmptyReplyFailureCategory for a Length finish.
        if (response.FinishReason == ChatFinishReason.Length)
        {
            TurnLog().Warning(
                "turn_reply_truncated textChars={TextChars} thinkingChars={ThinkingChars}",
                analysis.TextChars,
                analysis.ThinkingChars);
            FailCurrentTurn(
                "The model reply was truncated: the model reached its output limit before it finished. The cut-off text was not posted. Try again with a smaller request.",
                new InvalidOperationException("The LLM reply ended with finishReason=length and no tool call."),
                ErrorCategory.ModelBehavior);
            return;
        }

        HandleTextResponse(lastMessage, response.Usage, msg.StreamedText, msg.StreamedThinking);
    }

    /// <summary>
    /// Decides who gets the blame for repeated empty replies. The model gets it only when the stream ended
    /// on its own terms: finish reason Stop or Length. No finish reason means the stream broke (EOF, a proxy
    /// cut): that is a stream fault, not model behavior. Any other reason, such as content_filter, is a
    /// provider result.
    /// </summary>
    private static ErrorCategory EmptyReplyFailureCategory(ChatFinishReason? finishReason)
    {
        if (finishReason is not { } reason)
            return ErrorCategory.StreamFailure;

        return reason == ChatFinishReason.Stop || reason == ChatFinishReason.Length
            ? ErrorCategory.ModelBehavior
            : ErrorCategory.ProviderFailure;
    }

    private void HandleLlmResponseDeltaReceived(LlmResponseDeltaReceived msg)
    {
        if (!_execution.AcceptModelProgress(
                msg.CallId, msg.Activity, _config.PrefillTimeout, _config.FirstTokenTimeout))
            return;

        switch (msg.Content)
        {
            case TextContent text when !string.IsNullOrEmpty(text.Text):
                _input.MarkTextEmitted();
                EmitOutput(new TextDeltaOutput(text.Text)
                {
                    SessionId = _sessionId
                }, OutputFilter.TextStreaming);
                break;

            case TextReasoningContent thinking when !string.IsNullOrEmpty(thinking.Text):
                EmitOutput(new ThinkingDeltaOutput(thinking.Text)
                {
                    SessionId = _sessionId
                }, OutputFilter.Thinking);
                break;
        }
    }

    private void HandleDistillationResult(SessionDistillationCompleted msg)
        => HandleDistillationResult(msg, stopAfterAcceptedProposalPersistence: false);

    private void HandleDistillationResult(SessionDistillationCompleted msg, bool stopAfterAcceptedProposalPersistence)
    {
        _log.Info(
            "session_distillation_completed proposals={ProposalCount} inputTokens={InputTokens} outputTokens={OutputTokens} failure={Failure}",
            msg.Proposals.Count,
            msg.InputTokens,
            msg.OutputTokens,
            msg.FailureReason ?? "-");

        // Emit sidecar token usage through the standard pipeline
        if (msg.InputTokens.HasValue || msg.OutputTokens.HasValue)
        {
            _sessionMetrics?.RecordTokenUsage(msg.InputTokens ?? 0, msg.OutputTokens ?? 0);
            EmitOutput(new UsageOutput
            {
                SessionId = _sessionId,
                InputTokens = msg.InputTokens,
                OutputTokens = msg.OutputTokens,
                TotalTokens = (msg.InputTokens ?? 0) + (msg.OutputTokens ?? 0),
                ContextWindowTokens = _model.ContextWindowTokens
            }, OutputFilter.Usage);
        }

        // Route proposals through the standard gate → curation pipeline.
        // Skip entirely when memory is disabled or the session is Public — no memories should form.
        if (msg.Proposals.Count > 0 && _curationActor is not null
            && CurrentTurnAudience() != TrustAudience.Public && _memoryConfig.Enabled)
        {
            if (ShouldSkipMemoryCurationForThirdPartyAdoptedContext(_input.Authority.Context, _input.Authority.Source))
            {
                TurnLog().Info("memory_curation_skipped third-party adopted-context present; waiting for explicit elevation");
                if (stopAfterAcceptedProposalPersistence)
                    CompletePassivation();
                return;
            }

            var gateResult = _memoryProposalGate.Evaluate(
                msg.Proposals,
                NowMs(),
                boundary: CurrentMemoryBoundary(),
                audience: CurrentTurnAudience());

            var accepted = gateResult.MemoryOperations;

            if (gateResult.AcceptedProposals.Count > 0)
            {
                if (stopAfterAcceptedProposalPersistence && _observerActor is not null)
                {
                    _observerActor.Ask<AcceptedDistillationProposalsRecorded>(
                        new RecordAcceptedDistillationProposals(gateResult.AcceptedProposals),
                        TimeSpan.FromSeconds(2))
                        .PipeTo(Self);
                }
                else
                {
                    _observerActor?.Tell(new RecordAcceptedDistillationProposals(gateResult.AcceptedProposals));
                }
            }

            _log.Info(
                "session_distillation_gate_result total={Total} accepted={Accepted} rejections={Rejections}",
                gateResult.Summary.Total,
                gateResult.Summary.Accepted,
                gateResult.Summary.RejectionReasons.Count == 0
                    ? "-"
                    : string.Join("|", gateResult.Summary.RejectionReasons.Select(x => $"{x.Key}:{x.Value}")));

            if (accepted.Count > 0)
            {
                _curationActor.Tell(new Memory.EvaluateProposals(accepted));
                _sessionMetrics?.RecordMemoriesFormed(accepted.Count);
            }

            if (stopAfterAcceptedProposalPersistence && gateResult.AcceptedProposals.Count == 0)
                CompletePassivation();
        }
        else if (stopAfterAcceptedProposalPersistence)
        {
            CompletePassivation();
        }
    }

    internal static bool ShouldSkipMemoryCurationForThirdPartyAdoptedContext(
        TurnContext? turnContext,
        MessageSource? turnSource)
        => (turnContext?.HasThirdPartyAdoptedContext ?? turnSource?.HasThirdPartyAdoptedContext) == true;

    private void Compacting()
    {
        // Disable idle timeout while compacting — re-enabled on transition to Ready
        Context.SetReceiveTimeout(null);
        CommandCommonMessages();
        CommandSessionContextMessages();
        CommandSnapshotMessages();

        // Buffer user messages during compaction (same as Processing)
        Command<SendUserMessage>(cmd =>
        {
            if (RestartDrainRequested)
            {
                _log.Info("Rejecting new user message while restart drain is pending during compaction");
                TryReplyNack(SessionIngressGate.RestartInProgressMessage);
                return;
            }

            if (IsRestartResume(cmd))
            {
                AckRestartResumeNoOp(cmd);
                return;
            }

            AdmitInput(cmd, InputPlacement.BufferDuringCompaction);
        });

        // Buffer an approval response during compaction — compaction rewrites
        // history, so the parked tool batch cannot be re-driven mid-flight.
        // DrainBufferOrReady replays the buffered response after compaction.
        Command<ToolInteractionResponse>(msg =>
        {
            _log.Info("Buffering tool interaction response (compaction in progress)");
            _input.DeferApproval(msg);
            TryReplyAck();
        });

        Command<ToolInteractionTextResponse>(msg =>
        {
            _log.Info("Buffering text tool interaction response (compaction in progress)");
            _input.DeferApproval(msg);
            TryReplyAck();
        });

        Command<ProcessingWatchdogExpired>(HandleCompactionWatchdogExpired);

        Command<DeliveryFailed>(msg => HandleDeliveryFailedWithoutRetry(msg, "compacting"));

        Command<CompactionTriggered>(HandleCompactionTriggered);

        Command<CompactionWorkCompleted>(HandleCompactionWorkCompleted);

        Command<CompactionWorkFailed>(HandleCompactionWorkFailed);

        CommandDistillationAckNoOp();
    }

    private void HandleCompactionWatchdogExpired(ProcessingWatchdogExpired msg)
    {
        if (!_execution.IsCurrentWatchdog(msg))
            return;

        _log.Error("Compaction watchdog expired for operation {OperationName} (opId={OperationId})",
            msg.OperationName, msg.OperationId);

        _execution.StopWatchdog();

        EmitOutput(new ErrorOutput
        {
            SessionId = _sessionId,
            Message = "Context compaction timed out. The session will continue.",
            Category = ErrorCategory.Timeout,
            CorrelationId = Guid.NewGuid(),
            Cause = new TimeoutException(
                $"Session compaction operation '{msg.OperationName}' exceeded watchdog timeout of {GetCompactionTimeout().TotalSeconds:F0}s")
        });

        DrainBufferOrReady();
    }

    private void HandleCompactionTriggered(CompactionTriggered msg)
    {
        var timeout = GetCompactionTimeout();
        var operationId = _execution.StartCompactionWatchdog(timeout);
        var stateSnapshot = _state;
        var self = Self;
        var log = _log;
        var compactionClient = _compactionClient;

        var compactionParams = new CompactionParameters(
            _sessionId,
            msg.InputTokenCount,
            _config.Tuning.KeepRecentToolResults,
            _config.Tuning.KeepRecentMessages,
            _model.ContextWindowTokens,
            _config.SidecarLlmTimeout,
            timeout);

        _ = SessionCompactionPipeline.ExecuteAsync(
            stateSnapshot,
            compactionParams,
            compactionClient,
            self,
            log,
            operationId);
    }

    private void HandleCompactionWorkCompleted(CompactionWorkCompleted msg)
    {
        if (!IsCurrentCompactionOperation(msg.OperationId))
            return;

        _execution.StopWatchdog();

        var compactedEvent = new SessionCompacted
        {
            SessionId = _sessionId,
            Summary = msg.Summary,
            CompactedMessages = msg.CompactedMessages,
            TurnCountBefore = _state.TurnCount,
            CompactedAtMs = NowMs()
        };

        Persist(compactedEvent, evt =>
        {
            _state = _state.Apply(evt);
            _lastInputTokenCount = 0;
            _modelContext.ResetAfterCompaction();

            if (TryResolveMemoryCheckpointAudience(
                    Memory.CheckpointTriggerType.CompactionBoundary,
                    out var compactionAudience))
            {
                EnqueueCheckpointFireAndForget(new MemoryCheckpointRequest(
                    SessionId: _sessionId,
                    TurnId: _input.Telemetry.TurnId,
                    TriggerType: Memory.CheckpointTriggerType.CompactionBoundary,
                    Priority: 90,
                    Payload: SessionMemoryCheckpointFactory.ForCompactionBoundary(
                        _sessionId,
                        CurrentMemoryBoundary(),
                        compactionAudience,
                        msg.Summary)));
            }

            SaveSnapshot(BuildSnapshot());

            EmitOutput(new CompactionOutput
            {
                SessionId = _sessionId,
                MessagesBefore = msg.MessagesBefore,
                MessagesAfter = _state.History.Count,
                ToolResultsCleared = msg.ClearedCount > 0,
                Summarized = !string.IsNullOrWhiteSpace(msg.Summary),
                ContextWindowTokens = _model.ContextWindowTokens,
                PreCompactionInputTokens = msg.PreCompactionInputTokens,
                KeepCountUsed = msg.KeepCountUsed
            });

            _log.Info("Compaction complete (before={MessagesBefore}, after={MessagesAfter})",
                msg.MessagesBefore, _state.History.Count);

            DrainBufferOrReady();
        });
    }

    private void HandleCompactionWorkFailed(CompactionWorkFailed msg)
    {
        if (!IsCurrentCompactionOperation(msg.OperationId))
            return;

        _execution.StopWatchdog();
        _log.Warning(msg.Cause, "Compaction failed");

        EmitOutput(new ErrorOutput
        {
            SessionId = _sessionId,
            Message = "Context compaction encountered an error. The session will continue.",
            Category = msg.Cause is TimeoutException ? ErrorCategory.Timeout : ErrorCategory.Unknown,
            CorrelationId = Guid.NewGuid(),
            Cause = msg.Cause
        });

        DrainBufferOrReady();
    }

    /// <summary>
    /// Roll back the current turn's messages from history and buffer the user message
    /// for re-delivery after compaction. Removes the user message and any system nudges
    /// (e.g. recall content) that were added after it during this turn.
    /// </summary>
    private void RollBackCurrentTurnIntoBuffer()
    {
        for (var i = _state.History.Count - 1; i >= 0; i--)
        {
            var candidate = _state.History[i];
            if (candidate.Role != Protocol.ChatRole.User)
                continue;

            // Skip system nudges (recall content, empty-response nudges) — they use
            // User role but are not the real user message.
            if (SessionState.IsSystemNudge(candidate))
                continue;

            _input.BufferReplay(new SendUserMessage
            {
                SessionId = _sessionId,
                Content = candidate.Content ?? string.Empty,
                MediaReferences = candidate.MediaReferences
            });
            _state = _state with { History = _state.History.GetRange(0, i) };
            return;
        }

        _log.Warning("RollBackCurrentTurnIntoBuffer: no User message found in history");
    }

    private void DrainBufferOrReady()
    {
        var continuation = _input.AdvanceAfterCompaction(RestartDrainRequested);
        if (continuation.Action == InputProgressAction.Restart)
        {
            RecordRestartReminderEligibility();
            ClearBufferedMessagesForRestartDrain();
            TransitionTo(SessionPhase.Ready);
            TransitionTo(SessionPhase.Passivating);
            return;
        }

        if (continuation.ResumeToolLoop)
            _log.Info("Post-compaction: resuming tool loop with follow-up LLM call");

        if (continuation.Batch is { } batch)
        {
            _log.Info("Post-compaction: draining {BufferCount} buffered message(s)", batch.Messages.Count);
            ApplyBufferedBatch(batch);
        }

        if (continuation.Action == InputProgressAction.FollowUp)
        {
            FireLlmCall();
            TransitionTo(SessionPhase.Processing);
        }
        else
        {
            ClearApprovalTurnState();
            TransitionTo(SessionPhase.Ready);
            CompleteDeferredIdlePassivation();
        }

        // Replay an approval response that arrived during compaction. History
        // is now rebuilt and the phase transition has run, so the parked tool
        // batch can be re-driven by whichever phase the session landed in.
        if (continuation.DeferredApproval is { } deferred)
        {
            _log.Info("Post-compaction: replaying buffered tool interaction response");
            Self.Tell(deferred);
        }
    }

    private const string RestartReminderIdPrefix = "restart-resume-";

    // Bounds the reap-on-passivation handshake; the Ask always resolves within
    // this window (ack or piped failure), so passivation can never wedge on it.
    private static readonly TimeSpan JobReapAckTimeout = TimeSpan.FromSeconds(5);

    private void Passivating()
    {
        // Disable idle timeout — we're shutting down
        Context.SetReceiveTimeout(null);

        // Explicit shutdown or restart reaps session-scoped jobs.
        // Reaped-only records still use the manager's idempotent reap ack.
        IActorRef? jobManager = null;
        if (!_state.ActiveBackgroundJobs.IsEmpty)
        {
            var jobRegistry = ActorRegistry.For(Context.System);
            if (jobRegistry.TryGet<BackgroundJobManagerActorKey>(out var registeredManager))
                jobManager = registeredManager;
            else
            {
                _log.Error(
                    "Session has {JobCount} active background job(s) but no background job manager is registered — processes cannot be reaped",
                    _state.ActiveBackgroundJobs.Count);
            }
        }

        var start = _execution.BeginPassivation(
            !_state.ActiveBackgroundJobs.IsEmpty,
            jobManager is not null,
            _observerActor is not null);
        if (start.ReapEpoch is { } reapEpoch)
        {
            jobManager!.Ask<SessionJobsReaped>(
                    new KillJobsForSession(_sessionId), JobReapAckTimeout)
                .PipeTo(Self,
                    success: ack => new JobReapResolved(reapEpoch, ack.ReapedCount, null),
                    failure: ex => new JobReapResolved(reapEpoch, 0, ex));
        }

        Command<SessionDistillationCompleted>(msg =>
        {
            _log.Info("Passivation distillation complete, finalizing");
            HandleDistillationResult(msg, stopAfterAcceptedProposalPersistence: true);
        });

        Command<AcceptedDistillationProposalsRecorded>(_ =>
        {
            _log.Info("Passivation distillation dedup persistence complete, stopping");
            _execution.CompleteDistillation();
            CompletePassivation();
        });

        CommandCommonMessages();
        CommandSessionContextMessages();
        CommandSnapshotMessages();

        Command<SendUserMessage>(cmd =>
        {
            if (RestartDrainRequested)
            {
                _log.Info("Rejecting new user message while restart passivation is in progress");
                TryReplyNack(SessionIngressGate.RestartInProgressMessage);
                return;
            }

            _log.Info("Aborting passivation due to new user message");
            _execution.AbortPassivation();
            TransitionTo(SessionPhase.Ready);
            HandleIncomingUserMessage(cmd);
        });

        // A session may be passivating WITH pending tool interactions — idle
        // passivation proceeds with journaled approvals outstanding (the Ready
        // ReceiveTimeout handler no longer defers on them). An approval click
        // landing in this window aborts passivation and re-drives the parked
        // batch from history, exactly as it would after a cold respawn.
        CommandAsync<ToolInteractionResponse>(async msg =>
        {
            if (RestartDrainRequested)
            {
                _log.Info("Rejecting tool interaction response while restart passivation is in progress");
                TryReplyNack(SessionIngressGate.RestartInProgressMessage);
                return;
            }

            _log.Info("Aborting passivation due to tool interaction response");
            _execution.AbortPassivation();
            TransitionTo(SessionPhase.Ready);
            await HandleToolInteractionResponseWhenIdle(msg);
        });

        CommandAsync<ToolInteractionTextResponse>(async msg =>
        {
            if (RestartDrainRequested)
            {
                _log.Info("Rejecting text tool interaction response while restart passivation is in progress");
                TryReplyNack(SessionIngressGate.RestartInProgressMessage);
                return;
            }

            _log.Info("Aborting passivation due to text tool interaction response");
            _execution.AbortPassivation();
            TransitionTo(SessionPhase.Ready);
            await HandleToolInteractionTextResponseWhenIdle(msg);
        });

        // Ignore stale processing/compaction messages
        Command<ProcessingWatchdogExpired>(_ => { });
        Command<CompactionWorkCompleted>(_ => { });
        Command<CompactionWorkFailed>(_ => { });

        // Timeout — stop even if distillation didn't finish
        Command<PassivationTimeout>(_ =>
        {
            _log.Warning("Passivation grace period expired, stopping without distillation");
            CompletePassivation();
        });

        // No racing message arrived during the post-snapshot grace window — commit the stop.
        Command<PassivationFinalStop>(_ =>
        {
            if (!_execution.IsCurrentFinalStop(_phase.Current))
                return;
            _log.Info("Passivation grace window elapsed, finalizing stop");
            FinalizePassivation();
        });

        Command<DeliveryFailed>(msg =>
        {
            if (RestartDrainRequested)
            {
                _log.Info("Reporting delivery feedback while restart passivation is in progress");
                ReportReminderPostFailure(msg);
                return;
            }

            _log.Info("Aborting passivation due to delivery feedback");
            _execution.AbortPassivation();
            TransitionTo(SessionPhase.Ready);
            HandleDeliveryFailedWhenReady(msg);
        });

        // Restart drain already preserves accepted input in the journal.
        // Skip the optional memory pass because ingress is closed.
        if (start.RequestDistillation)
        {
            _observerActor!.Tell(new DistillMemories(), Self);
        }
        else
        {
            CompletePassivation();
        }
    }

    // The reap journal callback must finish before the owner grants the final snapshot attempt.
    private void CompletePassivation()
    {
        if (_execution.CompletePassivationWork(_phase.Current) != PassivationWork.AttemptSnapshot)
            return;

        CompleteFinalSnapshotAttempt();
    }

    // The journal event keeps reap status after a crash, even when an unresolved approval blocks the snapshot.
    private void FinishJobReap(long epoch)
    {
        Persist(new SessionBackgroundJobsReaped { SessionId = _sessionId, ReapedAtMs = NowMs() }, evt =>
        {
            _state = _state.Apply(evt);
            if (_execution.ReapCommitted(epoch, _phase.Current) == PassivationWork.AttemptSnapshot)
                CompleteFinalSnapshotAttempt();
        });
    }

    private void CompleteFinalSnapshotAttempt()
    {
        SaveSnapshotIfSafe();
        if (_execution.AfterFinalSnapshotAttempt(_phase.Current))
            FinalizePassivation();
    }

    // Actual termination after the grace window expires. The observer
    // notification is deferred to this point so it never fires for an
    // aborted passivation.
    private void FinalizePassivation()
    {
        var final = _execution.ClaimFinalStop(_phase.Current, _sessionId, _state.GetExecutablePendingInputs());
        if (final is null)
            return;

        if (final.OmissionReason is { } reason)
            _log.Warning("Restart drain cannot resume session {SessionId}: {Reason}.", _sessionId.Value, reason);
        EmitOutput(new SessionDeactivated { SessionId = _sessionId, TimestampMs = NowMs() });
        _lifecycleObserver?.OnSessionDeactivated(_sessionId);
        final.ReplyTo?.Tell(new DaemonRestartPrepared(_sessionId, final.Reminder));
        Context.Stop(Self);
    }

    private bool HasLiveBackgroundJobs
        => _state.ActiveBackgroundJobs.Values.Any(job => job.ReapedAtMs is null);

    // Outer hang-detector for the whole compaction pipeline. The inner observer
    // call already enforces its own SidecarLlmTimeout via a linked CTS, so the
    // outer budget needs headroom for Phase 1 (clear tool results), Phase 2
    // (extractive reducer loop), threadpool scheduling/JIT/GC, plus the full
    // sidecar call.
    private TimeSpan GetCompactionTimeout()
        => _config.SidecarLlmTimeout * 2 + TimeSpan.FromSeconds(5);

    // The observer replies to the fire-and-forget RecordAcceptedDistillationProposals
    // path in HandleDistillationResult. In non-passivation states the reply is purely
    // informational; without a handler it would hit DeadLetters on every curation
    // write. Passivating() has its own handler that uses the reply to gate shutdown.
    private void CommandDistillationAckNoOp()
    {
        Command<AcceptedDistillationProposalsRecorded>(_ => { });
    }

    private void HandleJobReapResolved(JobReapResolved msg)
    {
        if (!_execution.ObserveReapReply(msg.Epoch))
        {
            _log.Debug("Ignoring superseded background-job reap reply (epoch {Stale})", msg.Epoch);
            return;
        }

        if (msg.Error is not null)
            // Fail loud, proceed anyway: the manager's kill is idempotent and no
            // job process outlives the daemon.
            _log.Error(msg.Error,
                "Background job reap was not acknowledged within {Timeout}s — proceeding anyway; processes die with the daemon at the latest",
                JobReapAckTimeout.TotalSeconds);
        else
            _log.Info("Background job reap acknowledged: {ReapedCount} job(s) reaped", msg.ReapedCount);

        FinishJobReap(msg.Epoch);
    }

    private void CommandSessionContextMessages()
    {
        Command<SetSessionPromptOverlay>(msg =>
        {
            _modelContext.SetOverlay(msg.PromptOverlay);
        });
    }

    private bool IsCurrentCompactionOperation(long operationId)
        => _execution.IsCurrentCompaction(operationId);


    /// <summary>
    /// Fire a sidecar LLM call to generate a short session title.
    /// Best-effort — failures are silently ignored.
    /// </summary>
    private void MaybeGenerateTitle()
    {
        if (SessionTitleGenerator.ShouldGenerate(_state.TurnCount, _config.Tuning.TitleGenerationInterval))
            _ = SessionTitleGenerator.GenerateAsync(_compactionClient, _sessionId, _state.History, Self, _log, _config.SidecarLlmTimeout);
    }


    private void HandleToolCallResponse(
        AiChatMessage lastMessage,
        List<FunctionCallContent> toolCalls,
        UsageDetails? usage)
    {
        var toolExecutor = _toolExecutor
            ?? throw new InvalidOperationException("A tool-call response requires a tool executor.");
        var prepared = _toolWorkflow.PrepareFreshBatch(
            lastMessage, toolCalls, _toolRegistry, toolExecutor, _turnState);
        if (prepared.Decision.Kind != ToolCycleDecisionKind.Execute)
        {
            Logging.GetLogger(Context.System, typeof(TurnStateTracker)).Warning(
                "Tool recurrence decision kind={DecisionKind} refused={RefusedCount}",
                prepared.Decision.Kind, prepared.Decision.RefusedCallIds.Count);
        }

        if (prepared is PreparedFreshBatch.Stop)
        {
            // The model repeated a call after the runtime refused it. The turn fails as a model
            // failure with no further model request, in every parent session: Slack, Discord,
            // Mattermost, and the TUI show the text as a warning, and a headless caller sees
            // category=model. The text is for a person, so it is not the model-facing stop text.
            RecordIntermediateUsage(usage);
            FailCurrentTurn(
                ToolCycleMessages.TurnStopped,
                new InvalidOperationException("The model repeated a tool call that the recurrence guard already refused."),
                ErrorCategory.ModelBehavior);
            return;
        }

        var ready = (PreparedFreshBatch.Ready)prepared;
        var userMsg = _state.FindLastUserMessage() ?? new SerializableChatMessage
        {
            Role = Protocol.ChatRole.User,
            Content = string.Empty
        };

        var started = new ToolBatchStarted
        {
            SessionId = _sessionId,
            UserMessage = userMsg,
            AssistantMessage = ready.AssistantMessage,
            StartedAtMs = NowMs(),
            ConsumedInputIds = _input.SelectedInputs.Ids,
            ChildContinuation = _toolWorkflow.ChildDecision(_execution.CurrentModelCallId)
        };
        _ = _state.CommitChildContinuation(started.ChildContinuation, close: false);
        Persist(started, evt =>
        {
            var child = UnconsumedChild(evt.ChildContinuation);
            _state = _state.CloseInputs(evt.ConsumedInputIds);
            _input.ToolBatchCommitted();
            ApplyToolBatchStarted(evt);
            ReviewConsumedChildFindings(child);
            EmitAndDispatchToolBatch(
                lastMessage,
                ready.Calls,
                usage,
                ready.Cycle,
                ready.Decision);
        });
    }

    private void EmitAndDispatchToolBatch(
        AiChatMessage lastMessage,
        List<FunctionCallContent> toolCalls,
        UsageDetails? usage,
        PreparedToolCycleBatch preparedCycleBatch,
        ToolCycleBatchDecision cycleDecision)
    {

        // Surface preamble text immediately before tool execution starts.
        // TextOutput handles the non-streaming (single-delta) path where no
        // TextDeltaOutput was emitted to subscribers.
        // BufferFlush tells streaming adapters to flush their accumulated buffer
        // so the preamble is visible to users before the potentially long tool phase.
        // Consolidate all TextContent items into a single TextOutput to avoid
        // duplicate Slack posts when ToChatResponse() produces non-contiguous
        // TextContent items (e.g. [text, tool_call, text]).
        var preambleText = string.Join("\n\n", lastMessage.Contents
            .OfType<TextContent>()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t)));

        if (preambleText.Length > 0)
        {
            EmitOutput(new TextOutput(preambleText)
            {
                SessionId = _sessionId
            }, OutputFilter.Text);
            EmitOutput(new BufferFlush { SessionId = _sessionId }, OutputFilter.TextStreaming);
        }

        // Emit tool call outputs to subscribers.
        foreach (var tc in toolCalls)
        {
            var argsJson = tc.Arguments is not null
                ? JsonSerializer.Serialize(tc.Arguments)
                : null;
            EmitOutput(new ToolCallOutput
            {
                SessionId = _sessionId,
                CallId = new ToolCallId(tc.CallId),
                ToolName = new ToolName(tc.Name),
                ArgumentsJson = argsJson
            }, OutputFilter.ToolCalls);
        }

        RecordIntermediateUsage(usage);

        DispatchToolBatch(toolCalls, preparedCycleBatch: preparedCycleBatch, refusedCallIds: cycleDecision.RefusedCallIds);
    }

    private void RecordIntermediateUsage(UsageDetails? usage)
    {
        if (usage is null)
            return;

        EmitUsageOutput(usage);
        if (usage.InputTokenCount is > 0)
            _lastInputTokenCount = usage.InputTokenCount.Value;
    }

    /// <summary>
    /// Dispatches a tool batch to <see cref="SessionToolExecutionPipeline"/>.
    /// Both the fresh batch and <see cref="HandleToolInteractionResponseWhenIdle"/>
    /// use this task boundary.
    /// The workflow supplies exact-call consent from the original prompt.
    /// </summary>
    private void DispatchToolBatch(
        List<FunctionCallContent> toolCalls,
        IReadOnlySet<string> refusedCallIds,
        ApprovalRedrivePlan? approvalPlan = null,
        PreparedToolCycleBatch? preparedCycleBatch = null,
        bool recordCompletedCycle = true)
    {
        _toolWorkflow.StartDispatch(toolCalls, preparedCycleBatch, _toolExecutor, recordCompletedCycle);

        // The pipeline sends each result before it signals batch completion.
        TurnLog().Info("turn_tool_call_batch count={Count} tools={Tools}",
            toolCalls.Count,
            string.Join(",", toolCalls.Select(tc => tc.Name)));
        var self = Self;
        var sessionDir = GetSessionDirectory();
        // Per-call inactivity watchdogs in the tool-execution pipeline govern
        // tool liveness; the session ProcessingWatchdog covers only LLM calls
        // and compaction, so no batch tool-execution watchdog is armed here.
        var toolExecutionTimeout = _config.ToolExecutionTimeout;

        // Capture subscriber snapshot for subagent activity notifications.
        // These are emitted directly from the tool execution thread via Tell(),
        // which is thread-safe. The snapshot ensures we don't read _subscribers
        // from a non-actor thread.
        var subscriberSnapshot = _subscribers.Snapshot();
        var logActor = _logActor;
        Action<SubAgentOutput> emitSubAgentOutput = output =>
        {
            SessionSubscriberManager.Emit(subscriberSnapshot, output, OutputFilter.ToolCalls);
            logActor?.Tell(output);
        };

        // Marshal child-actor spawning back onto the session actor thread.
        Func<object, string, CancellationToken, Task<object>> spawnChildActor = async (request, name, ct) =>
            await self.Ask<object>(
                request is Props props ? new SpawnChildActorRequest(props, name) : request,
                timeout: toolExecutionTimeout,
                cancellationToken: ct);

        var registry = ActorRegistry.For(Context.System);
        var backgroundJobs = registry.TryGet<BackgroundJobManagerActorKey>(out var manager)
            ? new BackgroundJobDispatch.Available(manager)
            : (BackgroundJobDispatch)new BackgroundJobDispatch.Unavailable();

        // Pre-compute set_working_directory exposure once per dispatch so the
        // pipeline's deny-path hint logic can run without a policy lookup.
        // The hint is suppressed for audiences that cannot call the tool
        // (Public, audience profiles that explicitly drop it).
        var setWorkingDirectoryTool = GetExposedSetWorkingDirectoryTool();
        var setWorkingDirectoryAvailable = setWorkingDirectoryTool is not null;

        var toolExecutionCt = _execution.BeginTool();
        var turnContext = _input.Authority.Context
            ?? throw new InvalidOperationException("Tool batch dispatch requires admitted turn authority.");
        var runEnvironment = new SessionToolRunEnvironment
        {
            Storage = _sessionStorage,
            InlineOutputBudget = new InlineOutputBudget(_config.Tuning.MaxInlineToolResultChars),
            ModelInputModalities = _model.InputModalities,
            SpawnChildActor = spawnChildActor,
            ProjectDirectory = _state.WorkingContext.ProjectDirectory,
            RecentFiles = _state.WorkingContext.RecentFiles
        };
        var pipeline = _toolExecutionPipeline
            ?? throw new InvalidOperationException("Tool batch dispatch requires tool execution infrastructure.");
        var batch = _toolWorkflow.CreateBatch(
            turnContext,
            runEnvironment,
            toolCalls,
            new ToolExecutionTimeout(toolExecutionTimeout),
            self,
            emitSubAgentOutput,
            request => self.Tell(request),
            backgroundJobs,
            setWorkingDirectoryAvailable,
            setWorkingDirectoryTool is null ? null : setWorkingDirectoryTool.CanDeclare,
            approvalPlan,
            toolExecutionCt,
            refusedCallIds);

        _execution.AttachTool(pipeline.ExecuteAsync(batch));
    }

    private void HandleTextResponse(
        AiChatMessage lastMessage,
        UsageDetails? usage,
        bool streamedText,
        bool streamedThinking)
    {
        // Reset tool counters for potential buffer drain (new logical turn)
        _turnState.ResetToolCounters();

        var reply = ChatMessageConverter.FromAiMessage(lastMessage);
        var userMsg = _state.FindLastUserMessage();

        // Track input token count for compaction threshold check
        if (usage?.InputTokenCount is > 0)
        {
            _lastInputTokenCount = usage.InputTokenCount.Value;
        }

        var selected = _input.SelectedInputs;
        var continuation = _toolWorkflow.ChildDecision(_execution.CurrentModelCallId);
        var deliveredChild = continuation is null ? null : _state.ChildRuns[continuation.RunId];
        var turnEvent = new TurnRecorded
        {
            SessionId = _sessionId,
            UserMessage = userMsg ?? new SerializableChatMessage
            {
                Role = Protocol.ChatRole.User,
                Content = string.Empty
            },
            AssistantReply = reply,
            RecordedAtMs = NowMs(),
            SourceReminderId = RunningTurnReminder(deliveredChild),
            SourceBackgroundJobId = selected.BackgroundJobId,
            ConsumedInputIds = selected.Ids,
            // RoutedSkill stays false. The main model writes the reply of a child-delivery turn, also for a routed
            // skill, so a retry can fix a rejected reply. Only the routed turn that accepts the child never retries.
            ChildContinuation = continuation
        };

        _ = _state.Apply(turnEvent, userAlreadyInHistory: true);
        Persist(turnEvent, evt =>
        {
            var child = UnconsumedChild(evt.ChildContinuation);
            _state = _state.Apply(evt, userAlreadyInHistory: true);
            ReviewConsumedChildFindings(child);
            _input.TurnCommitted(evt);
            _toolWorkflow.ChildContinuationClosed();

            EmitResponseOutputs(lastMessage, usage, includeText: true, includeThinking: true);

            // Record the turn before the snapshot, so a snapshot that this turn triggers keeps the delivery state.
            RecordEligibleTurn(evt);
            MaybeSnapshot();
            MaybeGenerateTitle();

            // Check if compaction should trigger
            if (ShouldCompact())
            {
                _log.Info("Compaction threshold reached ({InputTokens} tokens >= {Threshold} limit), starting compaction",
                    _lastInputTokenCount, _model.CompactionTokenLimit(_config.Tuning.CompactionThreshold));
                Self.Tell(new CompactionTriggered(_lastInputTokenCount));
                TransitionTo(SessionPhase.Compacting);
                return;
            }

            DrainBufferedMessagesOrBecomeReady();
        });
    }

    private void DrainBufferedMessagesOrBecomeReady()
    {
        if (!RestartDrainRequested && HasReadyChildResult)
        {
            FireLlmCall();
            return;
        }
        var next = _input.AdvanceAfterTurn(RestartDrainRequested);
        if (next.Action == InputProgressAction.Restart)
        {
            RecordRestartReminderEligibility();
            ClearBufferedMessagesForRestartDrain();
            TransitionTo(SessionPhase.Ready);
            TransitionTo(SessionPhase.Passivating);
            return;
        }

        if (next is { Action: InputProgressAction.FollowUp, Batch: { } batch })
        {
            TurnLog().Info("turn_buffer_drain count={BufferCount}", batch.Messages.Count);
            ApplyBufferedBatch(batch);
            FireLlmCall();
            // Already in Processing — no transition needed, just fired a new LLM call
            return;
        }

        ClearApprovalTurnState();
        TransitionTo(SessionPhase.Ready);
        CompleteDeferredIdlePassivation();
        WakeForChild();
    }

    private void CompleteDeferredIdlePassivation()
    {
        if (!_execution.ClaimDeferredIdlePassivation(_phase.Current))
            return;

        // The idle path keeps a session with a live background job or a live child run in memory. Work that the
        // waiting input's turn started must not be reaped here. The re-armed idle timeout decides again, with the
        // same check.
        if (HasLiveBackgroundJobs || _execution.HasLiveChildren)
            return;

        TransitionTo(SessionPhase.Passivating);
    }

    private void ApplyBufferedBatch(BufferedInputBatch batch)
    {
        if (batch.AdoptsAuthority)
        {
            // The batch starts a turn for its own requester. Derive trust and the prompt audience from that
            // requester's recorded authority, not from the previous turn.
            _input.DeriveAdoptedTrust(_trustContextDeriver);
            UpdateCrashContext();
            SetSystemPrompt();
        }

        if (batch.StartsNewTurn)
        {
            // A replay resumes the same turn. New input starts a fresh guard
            // window only after the prior batch or response completes.
            _turnState.ResetForNewTurn();
            _modelContext.ResetForNewTurn();
        }

        foreach (var message in batch.Messages)
        {
            var refs = message.MediaReferences.Count > 0 ? message.MediaReferences : null;
            _state = _state.AddUserMessage(message.Content, refs);
        }

    }

    private void PrepareFreshTurnState()
    {
        _turnState.ResetForNewTurn();
        _discoveredToolCache.PrepareForNewTurn(
            _config.Tuning.DiscoveredToolRetentionTurns,
            _config.Tuning.DiscoveredToolMaxCount,
            _toolRegistry);
    }

    /// <summary>
    /// The reminder link of the running turn. A child-delivery turn has no input of its own, so it takes the link
    /// that the child run kept from the turn that started it. A retry turn keeps the link of the turn that it
    /// repeats. Any other turn takes the link of its own input. A child that this turn starts keeps the same link,
    /// so the link also reaches the child result of a child that a child-delivery turn started.
    /// </summary>
    private ReminderId? RunningTurnReminder(BackgroundChildRun? deliveredChild)
        => deliveredChild is not null
            ? deliveredChild.SourceReminderId
            : _input.SelectedInputs.ReminderId ?? _deliveryRetry.RetriedTurnReminder;

    /// <summary>
    /// Makes the turn that <paramref name="evt"/> recorded the eligible turn and the latest recorded turn.
    /// The live text turn, the live routed-skill turn, and the journal replay all call this, so the three
    /// paths cannot drift apart.
    /// </summary>
    private void RecordEligibleTurn(TurnRecorded evt)
    {
        var turn = new TurnNumber(_state.TurnCount);
        _deliveryRetry.MarkEligible(turn);
        _deliveryRetry.RecordTurn(turn, evt.SourceReminderId, evt.RoutedSkill);
    }

    private void HandleDeliveryFailedWhenReady(DeliveryFailed msg)
    {
        if (_deliveryRetry.EligibleTurnNumber != msg.TurnNumber)
        {
            _log.Warning(
                "Ignoring stale delivery feedback channel={Channel} turn={Turn} eligibleTurn={EligibleTurn}",
                msg.ChannelType,
                msg.TurnNumber,
                _deliveryRetry.EligibleTurnNumber?.ToString() ?? "none");
            return;
        }

        // A sub-agent produced the reply of a routed-skill turn. A retry would ask the main model for a
        // reply that it never wrote, so the first report is the final failure. This holds live, after
        // replay, and after a snapshot restore, because the mark is part of all three.
        if (DeliveryRetryHandler.IsRetryable(msg) && _deliveryRetry.IsRoutedSkill(msg.TurnNumber))
        {
            _log.Warning(
                "Not retrying routed-skill turn channel={Channel} turn={Turn} kind={FailureKind}",
                msg.ChannelType,
                msg.TurnNumber,
                msg.FailureKind);
            _deliveryRetry.Exhaust();
            ReportReminderPostFailure(msg);
            return;
        }

        if (!DeliveryRetryHandler.IsRetryable(msg))
        {
            _log.Warning(
                "Non-retryable delivery failure channel={Channel} turn={Turn} kind={FailureKind}; injecting context for next turn",
                msg.ChannelType,
                msg.TurnNumber,
                msg.FailureKind);
            _state = _state.AddSystemNudge(DeliveryRetryHandler.BuildNudge(msg));
            ReportReminderPostFailure(msg);
            return;
        }

        if (_deliveryRetry.RetryCount >= DeliveryRetryHandler.MaxRetries)
        {
            _log.Warning(
                "Delivery retry budget exhausted channel={Channel} turn={Turn} kind={FailureKind}",
                msg.ChannelType,
                msg.TurnNumber,
                msg.FailureKind);
            _deliveryRetry.Exhaust();
            ReportReminderPostFailure(msg);
            return;
        }

        _deliveryRetry.RecordRetry();

        _log.Warning(
            "Retrying after delivery failure channel={Channel} turn={Turn} kind={FailureKind} attempt={Attempt}",
            msg.ChannelType,
            msg.TurnNumber,
            msg.FailureKind,
            _deliveryRetry.RetryCount);

        _state = _state.AddSystemNudge(DeliveryRetryHandler.BuildNudge(msg));
        _modelContext.ResetForNewTurn(); // Delivery feedback changed context — resolve recall fresh
        FireLlmCall();
        TransitionTo(SessionPhase.Processing);
    }

    /// <summary>
    /// Handles delivery feedback while the session is busy (processing or compacting). The session does not
    /// retry in these phases. A restart drain adds no nudge, because the session is about to stop. Compaction adds
    /// no nudge either, because it discards one. In every case the feedback reaches
    /// <see cref="ReportReminderPostFailure"/>, so a failed reminder reply is never dropped.
    /// </summary>
    private void HandleDeliveryFailedWithoutRetry(DeliveryFailed msg, string phase)
    {
        if (RestartDrainRequested)
        {
            _log.Info("Reporting delivery feedback while restart drain is pending");
            ReportReminderPostFailure(msg);
            return;
        }

        if (!DeliveryRetryHandler.IsRetryable(msg))
        {
            if (_phase.Current == SessionPhase.Compacting)
            {
                // Apply(SessionCompacted) builds the history again from the state saved when compaction started.
                // A nudge added now would vanish, so the alert and the Error log are the only signal here.
                _log.Warning(
                    "Non-retryable delivery feedback while {Phase} channel={Channel} turn={Turn} kind={FailureKind}; adds no nudge, because compaction rebuilds the history",
                    phase,
                    msg.ChannelType,
                    msg.TurnNumber,
                    msg.FailureKind);
                ReportReminderPostFailure(msg);
                return;
            }

            _log.Warning(
                "Non-retryable delivery feedback while {Phase} channel={Channel} turn={Turn} kind={FailureKind}; injecting context",
                phase,
                msg.ChannelType,
                msg.TurnNumber,
                msg.FailureKind);
            _state = _state.AddSystemNudge(DeliveryRetryHandler.BuildNudge(msg));
            ReportReminderPostFailure(msg);
            return;
        }

        // The session does not retry while it handles another input, so this reply stays undelivered.
        _log.Warning(
            "Ignoring retryable delivery feedback while {Phase} channel={Channel} turn={Turn}",
            phase,
            msg.ChannelType,
            msg.TurnNumber);
        ReportReminderPostFailure(msg);
    }

    /// <summary>
    /// The session gave up on a reply. When a reminder fire started that turn, nobody reads the thread, so the
    /// history row (which records admission) and the channel cannot show the failure. This is the only place
    /// that reports it: an Error log and one alert. The reminder manager owns the history, so no row is written here.
    /// An ordinary turn raises nothing, because a person in the thread sees that no reply arrived.
    /// </summary>
    private void ReportReminderPostFailure(DeliveryFailed msg)
    {
        var lookup = _deliveryRetry.ReminderOf(msg.TurnNumber);
        if (lookup.Kind == ReminderLookupKind.UnknownTurn)
        {
            // The session does not hold the source of this turn. A quiet return would hide a lost link.
            _log.Warning(
                "Session cannot tell whether a reminder started turn {Turn} channel={Channel} kind={FailureKind}",
                msg.TurnNumber,
                msg.ChannelType,
                msg.FailureKind);
            return;
        }

        // The mark makes one reply raise one alert, even when the reply fails more than once.
        if (lookup.FireKey is not { } fireKey || !_deliveryRetry.TryMarkReported(msg.TurnNumber))
            return;

        // A fire key that does not parse still gets an alert. The whole key then names the reminder.
        string reminderId;
        if (ReminderFireKey.TryParse(fireKey.Value, out var parsed))
        {
            reminderId = parsed.DefinitionId.Value;
        }
        else
        {
            reminderId = fireKey.Value;
            _log.Warning("Source key {SourceKey} is not a reminder fire key; the alert names the whole key", fireKey.Value);
        }

        _log.Error(
            "Reminder {ReminderId} reply was not delivered session={SessionId} channel={Channel} turn={Turn} kind={FailureKind} error={Error}",
            reminderId,
            _sessionId.Value,
            msg.ChannelType,
            msg.TurnNumber,
            msg.FailureKind,
            msg.ErrorMessage);

        _notificationSink.Emit(OperationalAlert.Create(
            _timeProvider,
            "reminder.delivery.failed",
            AlertType.ReminderDeliveryFailed,
            $"Reminder '{reminderId}' ran, but its reply did not reach {msg.ChannelType}. Failure kind: {msg.FailureKind}.",
            AlertSeverity.Warning,
            source: reminderId,
            context: new Dictionary<string, string>
            {
                ["reminderId"] = reminderId,
                ["sessionId"] = _sessionId.Value,
                ["channel"] = msg.ChannelType.ToString(),
                ["failureKind"] = msg.FailureKind.ToString(),
                ["error"] = msg.ErrorMessage
            }));
    }

    private void HandleIncomingUserMessage(SendUserMessage cmd)
    {
        var bgJobId = cmd.Source?.BackgroundJobId;
        if (_input.IsBackgroundJobDuplicate(bgJobId, _state))
        {
            TurnLog().Info(
                "background_job_dedup_hit job={BackgroundJobId}",
                bgJobId);
            TryReplyAck();
            return;
        }

        if (TryResumeInterruptedInput(cmd))
            return;

        if (TryRejectIncompatibleInput(cmd.MediaReferences, cmd.Source))
            return;

        // Decide admission before any side effect. A duplicate, a dropped fire, or a refusal must leave the
        // session as it was, so it must not abandon an open tool batch.
        var decision = _input.PrepareAdmission(cmd, _state, _sessionId, NowMs());
        if (decision is not (AdmissionDecision.Existing or AdmissionDecision.Persist))
        {
            SettleWithoutAdmission(decision);
            return;
        }

        // A new inbound message from a person while a tool batch is still parked
        // on an approval gate means the user abandoned that approval. This is only
        // reachable on a cold-recovered session — a live session parked on
        // approval is in Processing, not Ready — so a non-empty
        // Pending approval state here is the cold-recovery signal. Close the
        // orphaned tool_use blocks before the new turn's LLM call: an assistant
        // tool_use with no matching tool_result is rejected by the provider
        // API, which would otherwise wedge every subsequent turn.
        // Automated input never abandons an open approval. It waits in the buffer
        // until the approval closes (see StartWaitingInputAfterApproval).
        switch (_toolWorkflow.PlanNewInputAbandonment(_state, _sessionId, cmd.Source?.Principal, NowMs()))
        {
            case NewInputPlan.AbandonThenActivate abandon:
                Persist(abandon.Event, evt =>
                {
                    ApplyToolBatchAbandoned(evt);
                    CommitAdmission(decision, cmd, InputPlacement.Activate);
                });
                return;
            case NewInputPlan.WaitForApproval:
                CommitAdmission(decision, cmd, InputPlacement.BufferDuringApproval);
                return;
            case NewInputPlan.Activate:
                CommitAdmission(decision, cmd, InputPlacement.Activate);
                return;
        }

        throw new InvalidOperationException("Unknown new input plan.");
    }

    private void AdmitInput(SendUserMessage cmd, InputPlacement placement)
    {
        var decision = _input.PrepareAdmission(cmd, _state, _sessionId, NowMs());
        if (decision is AdmissionDecision.Existing or AdmissionDecision.Persist)
            CommitAdmission(decision, cmd, placement);
        else
            SettleWithoutAdmission(decision);
    }

    // The session acknowledges a duplicate or a dropped fire, so the reminder system sees success and ends its job.
    private void SettleWithoutAdmission(AdmissionDecision decision)
    {
        switch (decision)
        {
            case AdmissionDecision.Reject rejected:
                _log.Error("Rejecting input with invalid durable authority: {Reason}", rejected.Reason);
                TryReplyNack($"Input authority cannot be stored: {rejected.Reason}");
                return;
            case AdmissionDecision.Duplicate:
                TryReplyAck();
                return;
            case AdmissionDecision.Dropped dropped:
                TurnLog().Info(
                    "reminder_fire_dropped reminder={ReminderId} fire={Fire} waiting={Waiting}",
                    dropped.Fire.DefinitionId,
                    dropped.Fire,
                    dropped.Waiting);
                TryReplyAck();
                return;
        }

        throw new InvalidOperationException("Unknown input admission decision.");
    }

    private void CommitAdmission(AdmissionDecision decision, SendUserMessage cmd, InputPlacement placement)
    {
        switch (decision)
        {
            case AdmissionDecision.Existing:
                _input.AdmissionAlreadyCommitted(cmd, placement);
                CompleteAcceptedInput(cmd, placement);
                return;
            case AdmissionDecision.Persist persist:
                Persist(persist.Event, admitted =>
                {
                    _state = _state.Apply(admitted);
                    var accepted = _input.AdmissionCommitted(admitted, cmd, placement);
                    CompleteAcceptedInput(accepted, placement);
                });
                return;
        }

        throw new InvalidOperationException("Unknown input admission decision.");
    }

    private void CompleteAcceptedInput(SendUserMessage command, InputPlacement placement)
    {
        if (placement == InputPlacement.Activate)
        {
            ContinueIncomingUserMessage(command);
            return;
        }

        if (placement == InputPlacement.BufferDuringModel)
        {
            // The retry of a child-delivery turn has no input, so its link comes only from the pending retry.
            // Clear ends that link: the retry turn then records no reminder, and its final failure raises no alert.
            // A person is in the thread, but a buffered reminder fire may have no reader, so the end of the
            // link must be visible.
            if (_deliveryRetry.RetriedTurnReminder is { } endedLink)
            {
                _log.Warning(
                    "A new input ended the delivery retry of turn {Turn} for reminder {FireKey}",
                    _state.TurnCount,
                    endedLink.Value);
            }

            _deliveryRetry.Clear();
            _log.Info("Buffering user message (LLM call in progress)");
        }
        else if (placement == InputPlacement.BufferDuringApproval)
        {
            _log.Info(
                "Buffering automated input until {PendingApprovalCount} open approval(s) close",
                _toolWorkflow.PendingDiagnosticCount);
        }
        else
        {
            _log.Info("Buffering user message (compaction in progress)");
        }
        TryReplyAck();
    }

    private void RecoverParentTurn(TurnRecorded recorded)
    {
        var evt = LegacyCoordinationReplay.TranslateParentDecision(_state, recorded);
        RestoreConsumedInputs(evt.ConsumedInputIds, evt.UserMessage);
        var userAlreadyInHistory = evt.ChildContinuation is not null || evt.ConsumedInputIds.Count > 0
                                   || _state.FindLastUserMessage() == evt.UserMessage;
        _state = _state.Apply(evt, userAlreadyInHistory);
        _toolWorkflow.CompleteTurn();
        _input.ClearTurnContext();

        // A session that recovers without a snapshot has no eligible turn. Mirror the live path so that
        // late delivery feedback for the latest turn finds the turn, the reminder that started it, and
        // the routed mark.
        RecordEligibleTurn(evt);
    }

    private void RecoverParentBatch(ToolBatchStarted recorded)
    {
        var evt = LegacyCoordinationReplay.TranslateParentDecision(_state, recorded);
        RestoreConsumedInputs(evt.ConsumedInputIds, evt.UserMessage);
        ApplyToolBatchStarted(evt);
    }

    private void RecoverParentClosure(InputClosed recorded)
        => _state = _state.Apply(LegacyCoordinationReplay.TranslateParentDecision(_state, recorded));

    private void RestoreConsumedInputs(IReadOnlyList<InputId> inputIds, SerializableChatMessage lastUserMessage)
    {
        if (inputIds.Count == 0)
            return;

        var pendingById = _state.PendingInputs.ToDictionary(evt => evt.InputId);
        for (var index = 0; index < inputIds.Count; index++)
        {
            var id = inputIds[index];
            if (!pendingById.TryGetValue(id, out var input))
                throw new InvalidOperationException($"Journal input {id} is missing before its terminal event.");

            var message = index == inputIds.Count - 1 ? lastUserMessage : input.UserMessage;
            _state = _state with { History = _state.History.Add(message) };
        }

        _state = _state.CloseInputs(inputIds);
    }

    private void ContinueIncomingUserMessage(SendUserMessage cmd)
    {
        _input.ActivateInput(cmd, _state.GetExecutablePendingInputs(), _trustContextDeriver);
        _toolWorkflow.StartNewTurn();
        _deliveryRetry.Clear();
        UpdateCrashContext();
        PersistAdoptedContextIfNeeded(cmd.Source);

        // Sessions created from Slack/Discord start without transport-derived
        // audience encoded in the session id, so rebuild the prompt now that the
        // actual inbound source is known.
        SetSystemPrompt();

        var userContent = cmd.Content ?? string.Empty;
        var executableUserContent = cmd.Source?.ExecutableText ?? userContent;
        var mediaRefs = cmd.MediaReferences;

        TurnLog().Info(
            "turn_received channel={ChannelType} sender={SenderId} hasMedia={HasMedia} textChars={TextLength}",
            cmd.Source?.ChannelType.ToWireValue() ?? "unknown",
            cmd.Source?.SenderId.Value ?? "unknown",
            mediaRefs.Count > 0,
            userContent.Length);

        _logActor?.Tell(cmd);

        // Quoted adopted thread context is useful for the live turn, but it should not
        // silently become durable memory authority via the automatic observer path.
        if (cmd.Source?.HasThirdPartyAdoptedContext != true)
            _observerActor?.Tell(cmd);

        PrepareFreshTurnState();

        if (TryHandleSlashCommand(executableUserContent, mediaRefs))
            return;

        _state = _state.AddUserMessage(userContent, mediaRefs.Count > 0 ? mediaRefs : null);
        TryReplyAck();
        _modelContext.ResetForNewTurn();
        _input.ResetOverflowRetry();
        FireInitialTurnLlmCall(executableUserContent);
        TransitionTo(SessionPhase.Processing);
    }

    // The restart reminder is not an input. It only resumes inputs that the journal already holds.
    private static bool IsRestartResume(SendUserMessage cmd)
        => cmd.Source?.ReminderId?.Value is { } reminderId
           && reminderId.StartsWith(RestartReminderIdPrefix, StringComparison.Ordinal)
           && string.Equals(cmd.Content, SessionExecutionLifetime.RestartReminderInstruction, StringComparison.Ordinal);

    // Akka.Reminders can redeliver the restart reminder. This message resumes nothing and no turn runs for it, so the
    // session acknowledges and the reminder run ends in success. An interrupted input can stay pending (another turn
    // runs). The session does not resume that input for this message.
    private void AckRestartResumeNoOp(SendUserMessage cmd)
    {
        _log.Info(
            "restart_resume_noop reminder={ReminderId} phase={Phase} pending={PendingCount}",
            cmd.Source?.ReminderId,
            _phase.Current,
            _state.PendingInputs.Count);
        if (Sender.IsNobody() || Equals(Sender, Context.System.DeadLetters))
            return;

        // A caller on the Reminder-origin route (#2491) waits for a terminal turn after an ack. No turn will run here.
        Sender.Tell(new RestartResumeNotNeeded(_sessionId));
    }

    private bool TryResumeInterruptedInput(SendUserMessage cmd)
    {
        if (!IsRestartResume(cmd))
            return false;

        var pending = _state.GetExecutablePendingInputs();
        if (pending.Count == 0)
        {
            AckRestartResumeNoOp(cmd);
            return true;
        }

        var restartContext = _execution.ValidateRestartResume(pending);
        if (restartContext.Context is not { } firstContext)
        {
            _log.Warning(
                "Restart reminder {ReminderId} cannot restore session {SessionId}: {Reason}.",
                cmd.Source!.ReminderId,
                _sessionId.Value,
                restartContext.Reason ?? "invalid stored authority");
            TryReplyNack("The interrupted input authority is invalid.");
            return true;
        }

        _execution.ClearRestartReminderEligibility();
        _input.ActivateRecoveredInput(cmd.Source, firstContext, _trustContextDeriver);
        UpdateCrashContext();
        _toolWorkflow.StartTurn();
        SetSystemPrompt();
        PrepareFreshTurnState();

        if (TryResumeSlashCommand(pending))
            return true;

        foreach (var message in _input.ResumePendingInputs(pending))
            _state = _state with { History = _state.History.Add(message) };

        _modelContext.ResetForNewTurn();
        _input.ResetOverflowRetry();

        TryReplyAck();
        var recallQuery = string.Join(
            '\n',
            pending.Select(static item => item.ExecutableText ?? item.UserMessage.Content ?? string.Empty));
        FireInitialTurnLlmCall(recallQuery);
        TransitionTo(SessionPhase.Processing);
        return true;
    }

    private bool TryResumeSlashCommand(IReadOnlyList<InputAdmitted> pending)
    {
        var first = pending[0];
        if (_input.BeginSlashResume(first) is not { } executableText)
            return false;

        var handled = TryHandleSlashCommand(executableText, first.UserMessage.MediaReferences);
        return _input.CompleteSlashResume(handled, pending, _sessionId, _phase.Current);
    }

    private bool ShouldCompact()
    {
        var limit = _model.CompactionTokenLimit(_config.Tuning.CompactionThreshold);
        return limit > 0
            && _lastInputTokenCount >= limit;
    }

    private void CommandCommonMessages()
    {
        CommandChildMessages();
        Command<ToolInteractionRequest>(msg => HandleToolInteractionRequestDispatch(
            new ToolInteractionRequestDispatch(msg, PersistApprovalState: true)));
        Command<ToolInteractionRequestDispatch>(HandleToolInteractionRequestDispatch);
        Command<SpawnChildActorRequest>(msg => Sender.Tell(Context.ActorOf(msg.Props, msg.ActorName)));
        // Every phase accepts a late reap reply. The epoch gate protects a newer stop attempt.
        Command<JobReapResolved>(HandleJobReapResolved);
        Command<PrepareForDaemonRestart>(_ => RequestRestartDrain());
        Command<ToolPipelineStoppedForRestart>(HandleToolPipelineStoppedForRestart);
        Command<RestartModelGraceExpired>(HandleRestartModelGraceExpired);
        Command<ModelStoppedForRestart>(HandleModelStoppedForRestart);

        Command<WorkingContextSnapshotReady>(HandleWorkingContextSnapshotReady);
        Command<WorkingContextSnapshotCancelled>(msg =>
        {
            if (_modelContext.IsCurrentInspection(msg.Generation))
                TurnLog().Debug("working_context_inspection_cancelled generation={Generation}", msg.Generation);
        });
        Command<WorkingContextSnapshotFailed>(HandleWorkingContextSnapshotFailed);
        Command<WorkingContextSnapshotFatal>(message => throw message.Cause);

        // Title generation result — can arrive in any behavior, always safe to apply
        Command<TitleGenerationCompleted>(msg =>
        {
            var title = msg.Title.Trim();
            if (!string.IsNullOrWhiteSpace(title))
            {
                _log.Info("Title generated: {Title}", title);
                SetTitle(title);
            }
        });

        Command<Memory.CurationCompleted>(msg =>
        {
            TurnLog().Info(
                "memory_curation_completed evaluated={Evaluated} skipped={Skipped} updated={Updated} consolidated={Consolidated} created={Created}",
                msg.Evaluated,
                msg.Skipped,
                msg.Updated,
                msg.Consolidated,
                msg.Created);
        });

        Command<Memory.CurationFailed>(msg =>
        {
            TurnLog().Warning("memory_curation_failed reason={Reason}", msg.Reason);
        });

        Command<SessionDistillationCompleted>(msg => HandleDistillationResult(msg));

        Command<RoutedSkillExecutionCompleted>(HandleRoutedSkillExecutionCompleted);
        Command<RoutedSkillExecutionFailed>(msg =>
        {
            if (!_input.SelectedInputs.Ids.Contains(msg.StartKey.InputId) ||
                _input.Authority.Context?.TurnId.Value != msg.StartKey.TurnId)
                return;
            FailCurrentTurn(
                $"Skill '/{msg.SkillName}' routed to subagent '{msg.SubagentName}' failed: {msg.ErrorMessage}",
                new InvalidOperationException(msg.ErrorMessage),
                ErrorCategory.ToolFailure);
        });
        Command<RoutedSkillSubAgentActivity>(msg =>
        {
            EmitOutput(new SubAgentOutput
            {
                SessionId = _sessionId,
                TimestampMs = msg.TimestampMs,
                AgentName = msg.AgentName,
                Phase = msg.Phase,
                ToolCount = msg.ToolCount,
                Success = msg.Success ?? false,
                Duration = msg.Duration ?? TimeSpan.Zero,
                FindingsCount = msg.FindingsCount
            }, OutputFilter.ToolCalls);
        });

        Command<WarmSession>(cmd =>
        {
            _pendingRestartNotice = cmd.RestartNotice;
            Sender.Tell(CommandAck.For(_sessionId));
        });

        Command<JoinSession>(cmd =>
        {
            var isReJoin = _subscribers.IsReJoin(cmd.Subscriber, cmd.Filter);

            if (!isReJoin)
            {
                _subscribers.AddOrUpdate(cmd.Subscriber, cmd.Filter);
                // Persistence can replay raw Terminated and discard WatchWith's custom payload.
                Context.Watch(cmd.Subscriber);

                _log.Info("{Subscriber} joined (filter={Filter})", cmd.Subscriber, cmd.Filter);
            }

            var joined = new SessionJoined
            {
                SessionId = _sessionId,
                Title = _state.Title,
                TurnCount = _state.TurnCount,
                RecentMessages = SessionRecentMessageExtractor.Extract(_state.History)
            };

            // On re-join, only reply to the Sender (for Ask callers) — don't
            // push a duplicate SessionJoined to the subscriber's output stream.
            if (isReJoin)
            {
                if (!Sender.IsNobody() && !Equals(Sender, Context.System.DeadLetters))
                {
                    Sender.Tell(joined);
                }

                return;
            }

            cmd.Subscriber.Tell(joined);

            // Also reply to Sender so callers can use Ask<SessionJoined> for
            // deterministic confirmation that the join was processed.
            if (!Sender.IsNobody() && !Equals(Sender, Context.System.DeadLetters)
                                   && !Equals(Sender, cmd.Subscriber))
            {
                Sender.Tell(joined);
            }

            // If replay found an approval decision but no durable tool result,
            // do not replay the approved side effect after restart. Close the
            // orphaned tool_use blocks so the next user turn has valid history.
            if (_phase.Current == SessionPhase.Ready)
            {
                if (!AbandonResolvedToolBatchAfterRecovery())
                    AbandonInterruptedToolBatchAfterRecovery();
            }
        });

        Command<LeaveSession>(cmd =>
        {
            if (_subscribers.Remove(cmd.Subscriber))
            {
                _log.Info("{Subscriber} left", cmd.Subscriber);
            }

            Context.Unwatch(cmd.Subscriber);
        });

        Command<Terminated>(msg =>
        {
            if (ChildTerminated(msg.ActorRef))
                return;
            if (_subscribers.Remove(msg.ActorRef))
            {
                _log.Info("{Subscriber} left", msg.ActorRef);
            }
        });
    }

    private void CommandSnapshotMessages()
    {
        Command<SaveSnapshotSuccess>(msg =>
        {
            _log.Info("Snapshot saved (seqNr={SequenceNr})", msg.Metadata.SequenceNr);

            DeleteMessages(msg.Metadata.SequenceNr); // delete all messages in journal up until snapshot was taken
            DeleteSnapshots(new SnapshotSelectionCriteria(msg.Metadata.SequenceNr - 1)); // delete all old snapshots
        });

        Command<SaveSnapshotFailure>(msg =>
        {
            _log.Warning("Snapshot failed: {Reason}", msg.Cause.Message);
        });
    }

    protected override void PreRestart(Exception reason, object message)
    {
        foreach (var buffered in _input.PrepareActorRestartReplay())
            Self.Tell(buffered);
        _execution?.StopChildren();
        _execution?.ReleaseAll();

        base.PreRestart(reason, message);
    }

    protected override void PostStop()
    {
        _execution?.StopChildren();
        _execution?.ReleaseAll();

        // Safety net for non-graceful stop paths (shutdown timeout, OOM, etc.).
        if (_execution is null || _execution.NeedsAbruptStopNotification)
            _lifecycleObserver?.OnSessionDeactivated(_sessionId);

        base.PostStop();
    }

    protected override void OnPersistFailure(Exception cause, object @event, long sequenceNr)
    {
        if (@event is InputAdmitted)
            TryReplyNack("The input journal failed. The session did not start a model call.");

        base.OnPersistFailure(cause, @event, sequenceNr);
    }

    protected override void OnPersistRejected(Exception cause, object @event, long sequenceNr)
    {
        if (@event is InputAdmitted)
            TryReplyNack("The input journal rejected the message. The session did not start a model call.");

        base.OnPersistRejected(cause, @event, sequenceNr);
    }

    // ── Helpers ──

    /// <summary>
    /// Extracts the best user-facing error message from an LLM call failure.
    /// Checks for ProviderException (user-safe message), context overflow,
    /// timeouts, and falls back to a generic message.
    /// </summary>
    private string ExtractLlmErrorMessage(Exception? cause)
        => LlmFailureClassifier.ExtractUserMessage(cause, _model);

    /// <summary>
    /// Detect context-length overflow errors from LLM providers.
    /// Uses two signals: (1) ProviderException with HTTP 400 + overflow keywords,
    /// (2) fallback keyword scan of the full exception chain for providers that
    /// don't use ProviderException.
    /// </summary>
    internal static bool IsContextOverflowError(Exception? ex)
        => LlmFailureClassifier.IsContextOverflow(ex);

    private string GetSessionDirectory() => _sessionStorage.SessionDirectory.Value;

    private long NowMs() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private void SetSystemPrompt()
        => _state = _modelContext.RefreshPrompt(_state, CurrentTurnAudience());

    private void FireLlmCall(string? recallQuery = null, string? slashCommandSkillContent = null)
    {
        var compatibility = ModelInputCompatibility.Evaluate(_model.InputModalities, _state.History);
        if (!compatibility.IsCompatible)
        {
            TurnLog().Warning(
                "turn_media_history_incompatible required={Required} unsupported={Unsupported} unknownCount={UnknownCount} " +
                "model={ModelId} — incompatible media references will be stripped from wire messages by the assembler",
                compatibility.RequiredModalities,
                compatibility.UnsupportedModalities,
                compatibility.UnknownModalityValues.Count,
                _model.ModelId);
            // The assembler removes incompatible history media and adds a notice.
        }

        var (callId, modelToken) = _execution.BeginModel();
        var continuingChild = _toolWorkflow.HasChildContinuation;
        if (_toolWorkflow.SelectChild(_state, callId,
                allowSelection: _input.SelectedCount == 0) is { } child)
        {
            if (!continuingChild)
            {
                _turnState.ResetForNewTurn();
                _modelContext.ResetForNewTurn();
                EmitChildResultDelivery(child);
            }
            if (!TurnContext.TryFromRecord(child.OriginalContext, out var childContext, out var reason) || childContext is null)
                throw new InvalidOperationException($"Child continuation authority is invalid: {reason}");
            _input.ActivateRecoveredInput(null, childContext, _trustContextDeriver);
        }

        var call = _modelContext.StartCall(
            callId,
            _state,
            _toolWorkflow.ChildDecision(callId),
            _input.Authority.Context,
            _input.Authority.Source,
            _input.Authority.Trust,
            recallQuery,
            slashCommandSkillContent,
            _turnRestartNotice,
            modelToken);
        if (call is ContextCall.Inspect { Audit: var audit })
        {
            // The observer audit is independent of model-visible recall content.
            if (audit.Result.Items.Count > 0)
            {
                var content = SessionRecallManager.FormatForHistory(audit.Result);
                _observerActor?.Tell(new ObserverSystemContext("recalled-memory", content));
            }

            var recallIds = audit.Result.Items.Count == 0
                ? "-"
                : string.Join(",", audit.Result.Items.Select(item => item.Id.Value));
            TurnLog().Info(
                "turn_memory_recall degraded={Degraded} stage={Stage} durationMs={DurationMs} itemCount={ItemCount} itemIds={ItemIds}",
                audit.Result.Degraded,
                audit.Result.DegradeStage ?? "-",
                audit.DurationMs,
                audit.Result.Items.Count,
                recallIds);
            if (audit.Result.Items.Count > 0)
                _sessionMetrics?.RecordMemoriesRecalled(audit.Result.Items.Count);
        }

        switch (call)
        {
            case ContextCall.Inspect inspect:
                _ = inspect.Work.PipeTo(Self);
                break;
            case ContextCall.Ready ready:
                ApplyPreparedModelRequest(ready.Request);
                break;
        }
    }

    private bool TryRejectIncompatibleInput(
        IReadOnlyList<SerializableMediaReference> pendingMedia,
        MessageSource? source)
    {
        var compatibility = ModelInputCompatibility.Evaluate(
            _model.InputModalities,
            _state.History,
            pendingMedia);
        if (compatibility.IsCompatible)
            return false;

        // History-only incompatibility: debug log and let the assembler strip
        // incompatible media at wire time. Only new user-supplied media on this
        // specific command triggers a hard rejection.
        if (pendingMedia.Count == 0)
        {
            TurnLog().Info(
                "session_history_media_stripped model={ModelId} required={Required} unsupported={Unsupported} unknown={Unknown} " +
                "— historical media references are incompatible with the current model and will be stripped by the assembler",
                _model.ModelId,
                compatibility.RequiredModalities,
                compatibility.UnsupportedModalities,
                string.Join(",", compatibility.UnknownModalityValues));
            return false;
        }

        var message = ModelInputCompatibility.BuildErrorMessage(_model, compatibility);
        var cause = new InvalidOperationException(message);
        var correlationId = Guid.NewGuid();

        _log.Error(
            cause,
            "session_input_incompatible model={ModelId} supported={Supported} required={Required} unsupported={Unsupported} unknown={Unknown} correlationId={CorrelationId}",
            _model.ModelId,
            _model.InputModalities,
            compatibility.RequiredModalities,
            compatibility.UnsupportedModalities,
            string.Join(",", compatibility.UnknownModalityValues),
            correlationId);

        EmitOutput(new ErrorOutput
        {
            SessionId = _sessionId,
            Message = message,
            Category = ErrorCategory.InputCompatibility,
            CorrelationId = correlationId,
            Cause = cause
        });
        EmitOutput(new TurnCompleted
        {
            SessionId = _sessionId,
            TurnNumber = new TurnNumber(_state.TurnCount),
            Outcome = TurnOutcome.Skipped,
            SourceReminderId = source?.ReminderId,
            OpenChildRunIds = _state.GetOpenChildRunIds()
        });
        TryReplyAck();
        return true;
    }

    private void ApplyPreparedModelRequest(PreparedModelRequest request)
    {
        _state = request.State;
        if (request.Exposure is { } exposure)
            TurnLog().Info(
                "Session tool exposure core={CoreCount} deferredVisible={DeferredVisibleCount} loaded={LoadedCount}",
                exposure.Core,
                exposure.DeferredVisible,
                exposure.Loaded);

        var (callId, modelToken) = _execution.ModelDispatch();
        _execution.StartModelWatchdog(_config.PrefillTimeout, _config.NoProgressTimeout);
        TurnLog().Info("turn_llm_call_start messages={MessageCount} toolsEnabled={ToolsEnabled} callId={CallId}",
            request.Messages.Count,
            request.Options.Tools?.Count > 0,
            callId);
        _execution.AttachModel(callId, SessionLlmInvoker.InvokeAsync(
            _chatClient,
            request.Messages,
            request.Options,
            Self,
            callId,
            _sessionId,
            modelToken));
    }

    private void HandleWorkingContextSnapshotFailed(WorkingContextSnapshotFailed message)
    {
        if (!_execution.CanDispatchPreparedModel(message.Generation)
            || !_modelContext.IsCurrentInspection(message.Generation))
            return;

        TurnLog().Warning(
            message.Cause,
            "working_context_inspection_failed generation={Generation}",
            message.Generation);
        var request = _modelContext.CompleteInspection(
            message, _state, _toolWorkflow.ChildDecision(message.Generation), CurrentTurnAudience(), _input.Authority.Trust);
        if (request is not null)
            ApplyPreparedModelRequest(request);
    }

    private void HandleWorkingContextSnapshotReady(WorkingContextSnapshotReady message)
    {
        if (!_execution.CanDispatchPreparedModel(message.Generation)
            || !_modelContext.IsCurrentInspection(message.Generation))
        {
            TurnLog().Debug(
                "working_context_inspection_stale generation={Generation}",
                message.Generation);
            return;
        }

        var request = _modelContext.CompleteInspection(
            message, _state, _toolWorkflow.ChildDecision(message.Generation), CurrentTurnAudience(), _input.Authority.Trust);
        if (request is not null)
            ApplyPreparedModelRequest(request);
    }

    private TrustAudience CurrentTurnAudience()
        => _input.Authority.Context?.Audience
           ?? _input.Authority.Source?.Audience
           ?? SecurityPolicyDefaults.ResolveAudienceFromSessionId(_sessionId.Value);

    /// <summary>
    /// Returns the audience of the active turn authority: the durable turn
    /// context first, then the turn source. Null means that the session has
    /// no turn authority. Callers must refuse the operation in that case. Do
    /// not replace null with Public or with a value derived from the session id.
    /// </summary>
    internal static TrustAudience? ResolveTurnAuthorityAudience(TurnContext? turnContext, MessageSource? turnSource)
        => turnContext?.Audience ?? turnSource?.Audience;

    private bool TryResolveMemoryCheckpointAudience(
        Memory.CheckpointTriggerType triggerType,
        out string audience)
    {
        if (ResolveTurnAuthorityAudience(_input.Authority.Context, _input.Authority.Source) is { } resolved)
        {
            audience = resolved.ToWireValue();
            return true;
        }

        audience = string.Empty;
        _log.Error(
            "Memory checkpoint dropped reason={Reason} trigger={TriggerType}: the session has no turn context and no turn source, so it cannot resolve the memory audience",
            SecurityPolicyDefaults.AudienceUnresolvedReason,
            triggerType);
        return false;
    }

    private string CurrentMemoryBoundary()
        => _input.Authority.Context?.Boundary.Value
           ?? _input.Authority.Source?.Boundary.Value
           ?? SecurityPolicyDefaults.ResolveBoundaryFromSessionId(_sessionId.Value, CurrentTurnAudience()).Value;

    /// <summary>
    /// Returns the trust context that tool exposure needs. A session with a
    /// tool access policy and no trust context has no resolved audience. That
    /// is a wiring or lifecycle defect. The session then exposes no tools and
    /// writes an Error log. It does not expose the Public tool set.
    /// </summary>
    private bool TryGetToolExposureTrustContext(
        string operation,
        int candidateCount,
        [NotNullWhen(true)] out EffectiveTrustContext? trustContext)
    {
        trustContext = _input.Authority.Trust;
        if (trustContext is not null)
            return true;

        _log.Error(
            "Tool exposure refused reason={Reason} operation={Operation} candidateCount={CandidateCount}: the session has a tool access policy but no resolved trust context",
            SecurityPolicyDefaults.AudienceUnresolvedReason,
            operation,
            candidateCount);
        return false;
    }

    /// <summary>
    /// Returns true when <c>set_working_directory</c> is exposed to the
    /// current turn's audience profile. Used by the deny-path hint logic in
    /// <see cref="SessionToolExecutionPipeline"/>: the hint points the agent
    /// at the tool, so emitting it on a Public-audience turn that cannot call
    /// the tool would be misleading.
    /// </summary>
    private SetWorkingDirectoryTool? GetExposedSetWorkingDirectoryTool()
    {
        // No trust context means no exposed tools, so no hint either.
        if (_toolAccessPolicy is null || _toolRegistry is null || _input.Authority.Trust is not { } trustContext)
            return null;

        var registration = _toolRegistry.GetRegistrationByToolName(SetWorkingDirectoryTool.ToolName);
        return registration?.Tool is SetWorkingDirectoryTool tool
               && _toolAccessPolicy.IsToolExposed(registration, trustContext)
            ? tool
            : null;
    }


    /// <summary>
    /// Handles slash-command dispatch. If the user message starts with / and matches
    /// a registered skill, activation path selection is deterministic:
    /// metadata.subagent route first, then inline injection when metadata is absent.
    /// </summary>
    private bool TryHandleSlashCommand(string userContent, IReadOnlyList<SerializableMediaReference> mediaRefs)
    {
        if (_skillRegistry is null || string.IsNullOrWhiteSpace(userContent) || userContent[0] != '/')
            return false;

        if (_skillRegistry.TryResolveSlashCommand(userContent, out var skill, out var remainder))
        {
            var decision = SkillActivationRouter.Resolve(skill!);
            if (decision.IsError)
                return RejectSlashCommand(decision.ErrorMessage!);

            if (decision.Path == SkillActivationPath.Routed)
                return TryHandleRoutedSlashCommand(skill!, remainder, mediaRefs, decision.RoutedSubagent!);

            return HandleInlineSlashCommand(skill!, remainder, mediaRefs);
        }

        // Unrecognized slash command — send deterministic error
        var commands = _skillRegistry.GetAvailableSlashCommands();
        var errorMsg = $"Unknown command: {userContent.Split(' ')[0]}\n\nAvailable commands:\n";
        foreach (var (cmd, hint) in commands)
        {
            errorMsg += hint is not null ? $"  {cmd} {hint}\n" : $"  {cmd}\n";
        }

        return RejectSlashCommand(errorMsg.TrimEnd());
    }

    private bool RejectSlashCommand(string message)
    {
        void Reply()
        {
            EmitOutput(new TextOutput(message) { SessionId = _sessionId }, OutputFilter.Text);
            EmitOutput(new TurnCompleted
            {
                SessionId = _sessionId,
                TurnNumber = new TurnNumber(_state.TurnCount),
                Outcome = TurnOutcome.Skipped,
                SourceReminderId = _input.Authority.Source?.ReminderId,
                OpenChildRunIds = _state.GetOpenChildRunIds()
            });
            TryReplyAck();
        }

        CloseActiveInputs(Reply);
        return true;
    }

    private bool HandleInlineSlashCommand(SkillEntry skill, string remainder, IReadOnlyList<SerializableMediaReference> mediaRefs)
    {
        if (skill.Source is not FileSkillSource fileSource)
            return RejectSlashCommand($"Skill /{skill.Name} cannot use file-based slash dispatch.");

        string skillBody;
        try
        {
            var content = File.ReadAllText(fileSource.FilePath);
            skillBody = Skills.SkillScanner.ExtractBody(content);
        }
        catch (IOException ex)
        {
            _log.Warning("Failed to read skill file for slash command /{SkillName}: {Error}",
                skill.Name, ex.Message);
            return RejectSlashCommand($"Failed to load skill /{skill.Name}: {ex.Message}\n\nThe skill file may be missing or corrupted.");
        }

        _sessionMetrics?.RecordSkillLoaded(skill.Name, SkillLoadMethod.SlashCommand);

        var effectiveUserContent = string.IsNullOrWhiteSpace(remainder)
            ? $"The user invoked /{skill.Name}. Follow the skill instructions."
            : remainder;

        _state = _state.AddUserMessage(effectiveUserContent, mediaRefs.Count > 0 ? mediaRefs : null);
        TryReplyAck();
        _modelContext.ResetForNewTurn();

        FireInitialTurnLlmCall(effectiveUserContent, skillBody);

        TransitionTo(SessionPhase.Processing);
        return true;
    }

    private bool TryHandleRoutedSlashCommand(SkillEntry skill, string remainder, IReadOnlyList<SerializableMediaReference> mediaRefs, string routedSubagent)
    {
        if (skill.Source is not FileSkillSource fileSource)
            return RejectSlashCommand($"Skill /{skill.Name} cannot use file-based routed dispatch.");

        if (_subAgentRegistry is null || _subAgentSpawner is null)
            return RejectSlashCommand($"Skill '/{skill.Name}' routes to subagent '{routedSubagent}', but subagent routing is not available in this runtime.");

        _subAgentLoader?.SyncInto(_subAgentRegistry);

        var profile = _subAgentRegistry.TryGetByName(routedSubagent);
        if (profile is null)
            return RejectSlashCommand(SkillActivationRouter.UnknownTargetError(skill.Name, routedSubagent));

        if (profile.Visibility != SubAgentVisibility.UserFacing)
            return RejectSlashCommand(SkillActivationRouter.InternalTargetError(skill.Name, routedSubagent));

        string skillBody;
        try
        {
            var content = File.ReadAllText(fileSource.FilePath);
            skillBody = Skills.SkillScanner.ExtractBody(content);
        }
        catch (IOException ex)
        {
            _log.Warning("Failed to read skill file for routed slash command /{SkillName}: {Error}",
                skill.Name, ex.Message);
            return RejectSlashCommand($"Failed to load skill /{skill.Name}: {ex.Message}\n\nThe skill file may be missing or corrupted.");
        }

        if (ResolveTurnAuthorityAudience(_input.Authority.Context, _input.Authority.Source) is not { } routedAudience)
        {
            _log.Error(
                "Routed slash command /{SkillName} refused reason={Reason}: the session has no turn context and no turn source",
                skill.Name,
                SecurityPolicyDefaults.AudienceUnresolvedReason);
            return RejectSlashCommand(
                $"Skill '/{skill.Name}' cannot run: the session cannot resolve the audience for this turn ({SecurityPolicyDefaults.AudienceUnresolvedReason}).");
        }

        _sessionMetrics?.RecordSkillLoaded(skill.Name, SkillLoadMethod.SlashCommand);

        var effectiveTask = string.IsNullOrWhiteSpace(remainder)
            ? $"The user invoked /{skill.Name}. Execute the routed skill instructions and return a concise result."
            : remainder;

        _state = _state.AddUserMessage(effectiveTask, mediaRefs.Count > 0 ? mediaRefs : null);
        TryReplyAck();
        _modelContext.ResetForNewTurn();

        var originalContext = _input.Authority.Context
            ?? throw new InvalidOperationException("A routed skill requires admitted authority.");
        var startKey = new ChildRunStartKey.Slash(_sessionId, originalContext.TurnId.Value,
            _input.SelectedInputs.Ids.First());
        _ = ExecuteRoutedSkillAsync(Self, skill, profile, effectiveTask, skillBody, routedAudience,
            originalContext.ToRecord(), startKey);
        TransitionTo(SessionPhase.Processing);
        return true;
    }

    private async Task ExecuteRoutedSkillAsync(
        IActorRef self,
        SkillEntry skill,
        SubAgentProfile profile,
        string task,
        string skillBody,
        TrustAudience audience,
        TurnContextRecord originalContext,
        ChildRunStartKey.Slash startKey)
    {
        try
        {
            Func<object, string, CancellationToken, Task<object>> spawnChildActor = async (request, name, ct) =>
                await self.Ask<object>(
                    request is PreparedChildStart prepared
                        ? new AcceptChildStart(prepared, startKey, originalContext, $"/{skill.Name}", ct)
                        : new SpawnChildActorRequest((Props)request, name),
                    timeout: _config.ToolExecutionTimeout,
                    cancellationToken: ct);
            var outputs = new ToolExecutionOutputs(info =>
            {
                self.Tell(new RoutedSkillSubAgentActivity(
                    _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    new AgentName(info.AgentName),
                    info.IsStarted ? SubAgentPhase.Started : SubAgentPhase.Completed,
                    info.ToolCount,
                    info.Success,
                    info.Duration,
                    info.Findings.Count));
            });
            var context = new ToolExecutionContext(new ToolRunScope
            {
                Session = new ToolSessionScope.Bound(_sessionId.Value, _sessionStorage),
                // The caller resolved this audience from the turn authority and
                // refused the command when no authority existed.
                Audience = audience,
                InlineOutputBudget = new InlineOutputBudget(_config.Tuning.MaxInlineToolResultChars),
                Boundary = _input.Authority.Context?.Boundary ?? _input.Authority.Source?.Boundary,
                ChannelType = _input.Authority.Context?.ChannelType?.ToWireValue()
                              ?? (_input.Authority.Source is null ? null : _input.Authority.Source.ChannelType.ToWireValue()),
                ProjectDirectory = _state.WorkingContext.ProjectDirectory,
                RecentFiles = _state.WorkingContext.RecentFiles,
                InteractiveApproval = new InteractiveApprovalCapability.Unavailable(),
                SpawnChildActor = spawnChildActor,
            }, new ToolExecutionTimeout(_config.ToolExecutionTimeout), outputs);

            var result = await _subAgentSpawner!.StartRunAsync(
                profile,
                task,
                runtimeContext: null,
                context.Invocation,
                CancellationToken.None,
                systemPromptOverlay: skillBody);

            self.Tell(new RoutedSkillExecutionCompleted(skill.Name, profile.Name, startKey, result));
        }
        catch (Exception ex)
        {
            self.Tell(new RoutedSkillExecutionFailed(skill.Name, profile.Name, startKey, ex.Message));
        }
    }

    private void HandleRoutedSkillExecutionCompleted(RoutedSkillExecutionCompleted msg)
    {
        if (!_input.SelectedInputs.Ids.Contains(msg.StartKey.InputId) ||
            _input.Authority.Context?.TurnId.Value != msg.StartKey.TurnId)
            return;
        var userMsg = _state.FindLastUserMessage();
        var selected = _input.SelectedInputs;
        var turnEvent = new TurnRecorded
        {
            SessionId = _sessionId,
            UserMessage = userMsg ?? new SerializableChatMessage
            {
                Role = Protocol.ChatRole.User,
                Content = string.Empty
            },
            AssistantReply = new SerializableChatMessage
            {
                Role = Protocol.ChatRole.Assistant,
                Content = msg.Result.ToJson()
            },
            RecordedAtMs = NowMs(),
            RoutedSkill = true,
            SourceReminderId = selected.ReminderId,
            SourceBackgroundJobId = selected.BackgroundJobId,
            ConsumedInputIds = selected.Ids
        };

        Persist(turnEvent, evt =>
        {
            _state = _state.Apply(evt, userAlreadyInHistory: true);
            _input.RoutedTurnCommitted(evt);

            EmitOutput(new TextOutput(msg.Result.ToJson())
            {
                SessionId = _sessionId
            }, OutputFilter.Text);

            EmitOutput(new TurnCompleted
            {
                SessionId = _sessionId,
                TurnNumber = new TurnNumber(_state.TurnCount),
                Outcome = TurnOutcome.Completed,
                SourceReminderId = _input.Authority.Source?.ReminderId,
                OpenChildRunIds = _state.GetOpenChildRunIds()
            });

            RecordEligibleTurn(evt);
            MaybeSnapshot();
            MaybeGenerateTitle();
            DrainBufferedMessagesOrBecomeReady();
        });
    }

    private void MergeSuccessfulSubAgentWorkingContext(ChildRunCompletion completion)
    {
        var updated = WorkingContextUpdater.MergeSuccessfulSubAgentWorkingContext(_state.WorkingContext, completion);
        if (!ReferenceEquals(updated, _state.WorkingContext))
            _state = _state with { WorkingContext = updated };
    }

    private void EnqueueCheckpointFireAndForget(MemoryCheckpointRequest request)
    {
        var sink = _memoryCheckpointSink;
        _ = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await sink.EnqueueAsync(request, CancellationToken.None);
                sw.Stop();
                TurnLog().Info(
                    "turn_memory_checkpoint_enqueued trigger={TriggerType} checkpointId={CheckpointId} durationMs={DurationMs}",
                    request.TriggerType,
                    result.CheckpointId,
                    sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _log.Warning(ex, "Failed to enqueue memory checkpoint trigger={TriggerType}", request.TriggerType);
            }
        });
    }

    /// <summary>
    /// Attempt to activate a single Deferred tool by name.
    /// Checks registry, access policy, and adds to the available tools cache.
    /// </summary>
    private bool TryActivateDiscoveredTool(string toolName)
    {
        if (_toolRegistry is null) return false;

        var registration = _toolRegistry.GetRegistrationByToolName(toolName);
        if (registration is null) return false;

        if (_toolAccessPolicy is null
            || !TryGetToolExposureTrustContext("activate_tool", 1, out var trustContext)
            || !_toolAccessPolicy.IsToolExposed(registration, trustContext))
            return false;

        var tool = registration.Tool;
        if (_toolRegistry.IsCoreTool(tool.Name))
            return true;

        // Cache and log under the canonical name regardless of which form
        // the LLM sent (load_tool / search_tools now emit the LLM-facing
        // alias for MCP, but legacy strings may still arrive). Cache key
        // consistency makes per-tool lease accounting unambiguous.
        var canonicalName = tool.Name;
        _discoveredToolCache.Remember(canonicalName, tool,
            _config.Tuning.DiscoveredToolRetentionTurns,
            _config.Tuning.DiscoveredToolMaxCount);
        if (_discoveredToolCache.AddIfMissing(tool.ToAITool()))
            _log.Info(
                "Session deferred tool activated loaded={LoadedCount}",
                _discoveredToolCache.LoadedToolCount);
        return true;
    }

    private SessionSnapshot BuildSnapshot()
    {
        return _state.ToSnapshot() with
        {
            EligibleDeliveryTurnNumber = _deliveryRetry.EligibleTurnNumber,
            EligibleDeliveryReminderId = _deliveryRetry.UnreportedEligibleReminder,
            EligibleDeliveryTurnRoutedSkill = _deliveryRetry.EligibleTurnIsRoutedSkill
        };
    }

    private void ApplyToolBatchStarted(ToolBatchStarted evt)
        => _state = _toolWorkflow.ApplyBatchStarted(_state, evt);

    private void ApplyToolCallRecorded(ToolCallRecorded evt)
    {
        _state = _toolWorkflow.ApplyRecordedResult(_state, evt.ToolResult);
        TrackStartedBackgroundJob(evt.StartedBackgroundJob);
    }

    private void HandleToolInteractionRequestDispatch(ToolInteractionRequestDispatch dispatch)
    {
        var msg = dispatch.Request;
        if (dispatch.SourceChildRunId is { } childRunId &&
            (!_state.ChildRuns.TryGetValue(childRunId, out var childRun) || childRun.Lifecycle is not ChildRunLifecycle.Accepted))
            return;
        var evt = _toolWorkflow.CreateApprovalRequest(
            dispatch, _sessionId, _input.Authority.Context, _input.Authority.Source,
            CurrentTurnAudience(), NowMs());

        if (!dispatch.PersistApprovalState)
        {
            // Live sub-agent approvals need the prompt in the in-memory pending
            // map so responses can be authorized, but there is no durable child
            // actor/tool batch to redrive after restart.
            ApplyToolApprovalRequested(evt, persistApprovalState: false);
            EmitOutput(msg);
            return;
        }

        Persist(evt, e =>
        {
            ApplyToolApprovalRequested(e);
            EmitOutput(msg);
            TryStopDurableApprovalWaits();
        });
    }

    private void ApplyToolApprovalRequested(ToolApprovalRequested evt)
        => ApplyToolApprovalRequested(evt, persistApprovalState: true);

    private void ApplyToolApprovalRequested(ToolApprovalRequested evt, bool persistApprovalState)
    {
        var pending = _toolWorkflow.ApplyApprovalRequested(
            evt,
            persistApprovalState,
            recovered: _phase.Current == SessionPhase.Recovering);

        _log.Info(
            "Tool approval requested authorizationAttemptId={AuthorizationAttemptId} " +
            "sessionId={SessionId} callId={CallId} toolName={ToolName}",
            pending.AuthorizationAttemptId.Value,
            _sessionId.Value,
            evt.CallId,
            evt.ToolName);

        if (evt.SourceChildRunId is null && persistApprovalState && pending.TurnContext is { } turnContext)
            _input.SetTurnContext(turnContext);
        else if (persistApprovalState && pending.TurnContextRestoreFailure is { } restoreFailure)
            _log.Error(
                "Approval request {CallId} could not restore turn context: {Reason}",
                evt.CallId,
                restoreFailure);
    }

    private void ClearApprovalTurnState()
    {
        _toolWorkflow.ClearApprovalTurn();
        _input.ClearTurnContext();
    }

    private void ApplyToolApprovalResolved(ToolApprovalResolved evt)
        => _toolWorkflow.ApplyResolvedResponse(evt);

    private void ApplyToolBatchAbandoned(ToolBatchAbandoned evt)
    {
        _state = _toolWorkflow.ApplyAbandonment(_state, evt);
        _input.ClearTurnContext();
        _execution.ClearToolTracking();
    }

    private void ClearActiveToolBatchTracking()
    {
        _execution.ClearToolTracking();
        _toolWorkflow.ClearCompletedBatch();
    }

    private void MaybeSnapshot()
    {
        if (_config.Tuning.SnapshotInterval > 0 && LastSequenceNr % _config.Tuning.SnapshotInterval == 0)
            SaveSnapshotIfSafe();
    }

    private void SaveSnapshotIfSafe()
    {
        // SessionSnapshot intentionally excludes parked approval state. Writing a
        // snapshot while an assistant tool_use is unanswered would let recovery
        // skip the journal event that rehydrates pending approval context.
        if (_state.GetExecutablePendingInputs().Count > 0 || !_toolWorkflow.AllowsSnapshot(_state))
        {
            _log.Info("Skipping snapshot while a durable input or approval batch is unresolved");
            return;
        }

        SaveSnapshot(BuildSnapshot());
    }

    private void EmitResponseOutputs(
        AiChatMessage message,
        UsageDetails? usage,
        bool includeText = true,
        bool includeThinking = true)
    {
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextReasoningContent thinking when includeThinking:
                    EmitOutput(new ThinkingOutput(thinking.Text ?? string.Empty)
                    {
                        SessionId = _sessionId
                    }, OutputFilter.Thinking);
                    break;

                case FunctionCallContent toolCall:
                    EmitOutput(new ToolCallOutput
                    {
                        SessionId = _sessionId,
                        CallId = new ToolCallId(toolCall.CallId),
                        ToolName = new ToolName(toolCall.Name),
                        ArgumentsJson = toolCall.Arguments is not null
                            ? JsonSerializer.Serialize(toolCall.Arguments)
                            : null
                    }, OutputFilter.ToolCalls);
                    break;
            }
        }

        // Consolidate all TextContent items into a single TextOutput to avoid
        // duplicate posts when ToChatResponse() produces non-contiguous
        // TextContent items (e.g. [text, tool_call, text]). Emitted after
        // thinking to preserve the original content ordering (thinking before text).
        if (includeText)
        {
            var fullText = string.Join("\n\n", message.Contents
                .OfType<TextContent>()
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t)));

            if (fullText.Length > 0)
            {
                EmitOutput(new TextOutput(fullText)
                {
                    SessionId = _sessionId
                }, OutputFilter.Text);
            }
        }

        if (usage is not null)
        {
            EmitUsageOutput(usage);
        }

        EmitOutput(new TurnCompleted
        {
            SessionId = _sessionId,
            TurnNumber = new TurnNumber(_state.TurnCount),
            SourceReminderId = _input.Authority.Source?.ReminderId,
            OpenChildRunIds = _state.GetOpenChildRunIds()
        });
    }

    private void EmitUsageOutput(UsageDetails usage)
    {
        _sessionMetrics?.RecordTokenUsage(usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0);

        var contextWindow = _model.ContextWindowTokens;
        double? usagePercent = usage.InputTokenCount.HasValue && contextWindow > 0
            ? (double)usage.InputTokenCount.Value / contextWindow
            : null;

        // Decode llama.cpp server-side timing from UsageDetails.AdditionalCounts.
        // Canonical encoding lives in Netclaw.Providers.SelfHosted.OpenAiCompatibleChatClient
        // (PromptUsKey, PredictedTokPerSecX100Key). Keep these strings in sync.
        var additional = usage.AdditionalCounts;
        double? promptMs = additional is not null && additional.TryGetValue("prompt_us", out var pUs)
            ? pUs / 1000.0 : null;
        double? predictedPerSec = additional is not null && additional.TryGetValue("predicted_tok_per_sec_x100", out var pps)
            ? pps / 100.0 : null;

        EmitOutput(new UsageOutput
        {
            SessionId = _sessionId,
            InputTokens = usage.InputTokenCount,
            OutputTokens = usage.OutputTokenCount,
            TotalTokens = usage.TotalTokenCount,
            CachedInputTokens = usage.CachedInputTokenCount,
            ReasoningTokens = usage.ReasoningTokenCount,
            ContextWindowTokens = contextWindow,
            UsagePercent = usagePercent,
            PromptMs = promptMs,
            PredictedPerSecond = predictedPerSec,
        }, OutputFilter.Usage);
    }

    private void EmitExpiredPromptNotice()
        => EmitOutput(new TextOutput(
            "That approval prompt has expired — the session moved on or restarted. "
            + "Please re-issue the request and I'll ask again if approval is needed.")
        {
            SessionId = _sessionId
        }, OutputFilter.Text);

    private void EmitAlreadyAnsweredApprovalNotice()
        => EmitOutput(new TextOutput(
            "Approval response ignored: that approval prompt already has an answer.")
        {
            SessionId = _sessionId
        }, OutputFilter.Text);

    private void EmitWrongRequesterApprovalNotice()
        => EmitOutput(new TextOutput(
            "Approval response ignored: only the requesting user can approve this tool action.")
        {
            SessionId = _sessionId
        }, OutputFilter.Text);

    private void EmitUnavailableApprovalOptionNotice()
        => EmitOutput(new TextOutput(
            "Approval response ignored: that option is not available for this tool action.")
        {
            SessionId = _sessionId
        }, OutputFilter.Text);

    private void EmitApprovalRejection(ApprovalResponseResult.Rejected rejection, ToolCallId callId)
    {
        _log.Warning(
            "Ignoring tool interaction response for call {CallId}: {Reason}",
            callId, rejection.Diagnostic);
        switch (rejection.Notice)
        {
            case ApprovalNoticeKind.Expired:
                EmitExpiredPromptNotice();
                break;
            case ApprovalNoticeKind.AlreadyAnswered:
                EmitAlreadyAnsweredApprovalNotice();
                break;
            case ApprovalNoticeKind.WrongRequester:
                EmitWrongRequesterApprovalNotice();
                break;
            case ApprovalNoticeKind.OptionUnavailable:
                EmitUnavailableApprovalOptionNotice();
                break;
        }
        TryReplyNack(rejection.NackReason);
    }

    private bool TryResolveTextApprovalResponse(
        ToolInteractionTextResponse msg,
        out ToolInteractionResponse? structured,
        out string? nackReason)
    {
        if (_toolWorkflow.TryResolveTextResponse(msg, _state, out structured, out nackReason))
            return true;

        switch (nackReason)
        {
            case ApprovalNackReasons.PromptExpired:
                _log.Warning("Ignoring text tool interaction response with no pending approvals for sender {SenderId}", msg.SenderId);
                EmitExpiredPromptNotice();
                break;
            case ApprovalNackReasons.WrongRequester:
                _log.Warning("Ignoring text tool interaction response from unauthorized sender {SenderId}", msg.SenderId);
                EmitWrongRequesterApprovalNotice();
                break;
            case ApprovalNackReasons.OptionUnavailable:
                _log.Warning("Ignoring unparseable text tool interaction response '{Text}'", msg.Text);
                EmitUnavailableApprovalOptionNotice();
                break;
        }
        return false;
    }

    private async Task PersistApprovalGrantIfNeededAsync(
        ApprovalResponseResult.Accepted accepted,
        CancellationToken ct)
    {
        if (_approvalService is null)
            return;
        var grants = _toolWorkflow.CreateApprovalGrants(accepted, GetSessionDirectory());
        if (grants.Count == 0)
            return;
        await _approvalService.RecordApprovalCandidatesAsync(
            (ToolApprovalSessionId)_sessionId.Value,
            accepted.Audience,
            accepted.ToolName,
            grants,
            ct);
    }

    private async Task HandleProcessingApprovalResponseAsync(ToolInteractionResponse msg)
    {
        var response = _toolWorkflow.PrepareLiveResponse(msg, _state.History);
        if (response is ApprovalResponseResult.Rejected rejection)
        {
            EmitApprovalRejection(rejection, msg.CallId);
            return;
        }

        var accepted = (ApprovalResponseResult.Accepted)response;
        try
        {
            _log.Info(
                "Tool approval decision authorizationAttemptId={AuthorizationAttemptId} " +
                "sessionId={SessionId} callId={CallId} decision={Decision}",
                accepted.Pending.AuthorizationAttemptId.Value,
                _sessionId.Value,
                accepted.CallId.Value,
                ConsentAnswerCodec.ToJournalText(accepted.Answer));
            await PersistApprovalGrantIfNeededAsync(accepted, CancellationToken.None);
            switch (accepted)
            {
                case ApprovalResponseResult.LiveOnlyAccepted liveOnly:
                    _toolWorkflow.CompleteLiveResponse(liveOnly);
                    TryReplyAck();
                    return;
                case ApprovalResponseResult.DurableAccepted durable:
                    var resolution = _toolWorkflow.CreateResolution(durable, _sessionId, NowMs());
                    Persist(resolution, evt =>
                    {
                        _toolWorkflow.ApplyResolvedResponse(evt, durable);
                        TryReplyAck();
                    });
                    return;
                default:
                    throw new InvalidOperationException("The accepted approval result has no completion route.");
            }
        }
        catch (Exception ex)
        {
            _toolWorkflow.FailLiveResponse(accepted);
            FailCurrentTurn("I couldn't persist that approval decision. Please try again.", ex, ErrorCategory.ToolFailure);
            TryReplyNack(ApprovalNackReasons.PersistFailed);
        }
    }

    private async Task HandleToolInteractionResponseWhenIdle(ToolInteractionResponse msg)
    {
        if (_toolWorkflow.IsChildApproval(msg.CallId))
        {
            await HandleProcessingApprovalResponseAsync(msg);
            return;
        }
        var response = _toolWorkflow.PrepareRecoveredResponse(msg, _state);
        if (response is ApprovalResponseResult.Rejected rejection)
        {
            EmitApprovalRejection(rejection, msg.CallId);
            return;
        }

        var accepted = (ApprovalResponseResult.DurableAccepted)response;
        try
        {
            _log.Info(
                "Tool approval decision authorizationAttemptId={AuthorizationAttemptId} " +
                "sessionId={SessionId} callId={CallId} decision={Decision}",
                accepted.Pending.AuthorizationAttemptId.Value,
                _sessionId.Value,
                accepted.CallId.Value,
                ConsentAnswerCodec.ToJournalText(accepted.Answer));
            await PersistApprovalGrantIfNeededAsync(accepted, CancellationToken.None);
        }
        catch (Exception ex)
        {
            EmitOutput(new ErrorOutput
            {
                SessionId = _sessionId,
                Message = "I couldn't persist that approval decision. Please try again.",
                Category = ErrorCategory.ToolFailure,
                CorrelationId = Guid.NewGuid(),
                Cause = ex
            });
            TryReplyNack(ApprovalNackReasons.PersistFailed);
            return;
        }

        var resolution = _toolWorkflow.CreateResolution(accepted, _sessionId, NowMs());
        Persist(resolution, evt =>
        {
            _toolWorkflow.ApplyResolvedResponse(evt);
            var next = _toolWorkflow.PlanRecoveredContinuation(_state, msg.CallId.Value, _sessionId, NowMs());
            switch (next)
            {
                case RecoveredContinuation.WaitForSiblings:
                    _log.Info("Deferring parked tool batch re-drive for call {CallId}: sibling approval(s) still pending", msg.CallId);
                    TryReplyAck();
                    return;
                case RecoveredContinuation.Expired expired:
                    _log.Warning("Cannot re-drive tool batch for call {CallId}: {Reason}", msg.CallId, expired.Diagnostic);
                    EmitExpiredPromptNotice();
                    TryReplyNack(ApprovalNackReasons.PromptExpired);
                    StartWaitingInputAfterApproval();
                    return;
                case RecoveredContinuation.Abandon abandon:
                    _log.Warning("Cannot re-drive tool batch for call {CallId}: {Reason}", msg.CallId, abandon.Diagnostic);
                    EmitExpiredPromptNotice();
                    Persist(abandon.Event, ApplyAbandonmentThenStartWaitingInput);
                    TryReplyNack(ApprovalNackReasons.PromptExpired);
                    return;
                case RecoveredContinuation.Dispatch dispatch:
                    _log.Info("Re-driving parked tool batch ({Count} call(s)) after approval response for {CallId}",
                        dispatch.Calls.Count, msg.CallId);
                    _toolWorkflow.RestoreConsumedChild(_state, dispatch.TurnContext, _execution.CurrentModelCallId);
                    _input.RestoreTurnAuthority(dispatch.TurnContext, _trustContextDeriver);
                    UpdateCrashContext();
                    TransitionTo(SessionPhase.Processing);
                    DispatchToolBatch(dispatch.Calls, new HashSet<string>(), approvalPlan: dispatch.ApprovalPlan,
                        recordCompletedCycle: false);
                    TryReplyAck();
                    return;
            }
        });
    }

    private async Task HandleToolInteractionTextResponseWhenIdle(ToolInteractionTextResponse msg)
    {
        if (!TryResolveTextApprovalResponse(msg, out var structured, out var nackReason)
            || structured is null)
        {
            TryReplyNack(nackReason ?? ApprovalNackReasons.PromptExpired);
            return;
        }
        await HandleToolInteractionResponseWhenIdle(structured);
    }

    // An automated input can wait in the buffer for an open approval (NewInputPlan.WaitForApproval). When the
    // approval closes without a re-drive, nothing else drains the buffer, so start the waiting input's turn here.
    private void StartWaitingInputAfterApproval()
    {
        if (_phase.Current != SessionPhase.Ready || _toolWorkflow.HasPendingApprovals())
            return;

        var next = _input.AdvanceAfterTurn(RestartDrainRequested);
        if (next.Action == InputProgressAction.Restart)
        {
            RecordRestartReminderEligibility();
            ClearBufferedMessagesForRestartDrain();
            TransitionTo(SessionPhase.Passivating);
            return;
        }

        if (next is not { Action: InputProgressAction.FollowUp, Batch: { } batch })
            return;

        TurnLog().Info("turn_buffer_drain_after_approval count={BufferCount}", batch.Messages.Count);
        _toolWorkflow.StartNewTurn();
        ApplyBufferedBatch(batch);
        FireLlmCall();
        TransitionTo(SessionPhase.Processing);
    }

    private bool AbandonResolvedToolBatchAfterRecovery()
    {
        if (!_toolWorkflow.ShouldAbandonResolvedAfterRecovery())
            return false;
        _log.Info("Abandoning recovered parked tool batch after restart");
        Persist(BuildResolvedToolBatchInterruptedByRestartEvent(), ApplyAbandonmentThenStartWaitingInput);
        return true;
    }

    private bool AbandonInterruptedToolBatchAfterRecovery()
    {
        if (!_toolWorkflow.ShouldAbandonInterruptedAfterRecovery(_state))
            return false;
        _log.Info("Abandoning recovered interrupted tool batch with no recoverable approval state");
        Persist(BuildInterruptedToolBatchAfterRecoveryEvent(), ApplyAbandonmentThenStartWaitingInput);
        return true;
    }

    private void ApplyAbandonmentThenStartWaitingInput(ToolBatchAbandoned abandoned)
    {
        ApplyToolBatchAbandoned(abandoned);
        StartWaitingInputAfterApproval();
    }

    // The session acknowledged each input that waits behind a parked approval. The journal keeps it as a pending
    // input, and the in-memory queue does not survive a restart or a passivation. Rebuild the queue here, so the
    // input runs once after the approval closes. A parked batch closed the inputs of its own turn
    // (ToolBatchStarted), so every pending input now is one that waits.
    private void RestoreWaitingInputs()
    {
        if (!_toolWorkflow.HasParkedApprovals() || _state.PendingInputs.Count == 0)
            return;

        var restored = _input.RestoreWaitingInputs(_state.PendingInputs, _sessionId);
        _log.Info(
            "Restored {RestoredCount} waiting input(s) behind {PendingApprovalCount} open approval(s)",
            restored,
            _toolWorkflow.PendingDiagnosticCount);
    }

    private ToolBatchAbandoned BuildResolvedToolBatchInterruptedByRestartEvent()
        => BuildToolBatchAbandonedEvent(
            ParkedToolBatchHistory.AbandonedResultPrefix + "the session restarted after approval before the action completed.");

    private ToolBatchAbandoned BuildInterruptedToolBatchAfterRecoveryEvent()
        => BuildToolBatchAbandonedEvent(
            ParkedToolBatchHistory.AbandonedResultPrefix + "the session restarted before the action completed.");

    private ToolBatchAbandoned BuildToolBatchAbandonedEvent(string resultContent)
        => _toolWorkflow.CreateAbandonment(_state, _sessionId, resultContent, NowMs());

    private void FailCurrentTurn(string errorMessage, Exception cause, ErrorCategory category = ErrorCategory.Unknown)
    {
        CloseActiveInputs(() => FinishFailedTurn(errorMessage, cause, category));
    }

    private void CloseActiveInputs(Action onClosed)
    {
        if (_input.SelectedCount == 0 && !_toolWorkflow.HasChildContinuation)
        {
            onClosed();
            return;
        }

        var closed = new InputClosed
        {
            SessionId = _sessionId,
            InputIds = _input.SelectedInputs.Ids,
            SourceBackgroundJobId = _input.SelectedInputs.BackgroundJobId,
            ChildContinuation = _toolWorkflow.ChildDecision(_execution.CurrentModelCallId),
            ClosedAtMs = NowMs()
        };
        _ = _state.Apply(closed);
        Persist(closed, evt =>
        {
            var child = UnconsumedChild(evt.ChildContinuation);
            _state = _state.Apply(evt);
            ReviewConsumedChildFindings(child);
            _toolWorkflow.ChildContinuationClosed();
            _input.InputsClosed(evt);
            onClosed();
        });
    }

    private void FinishFailedTurn(string errorMessage, Exception cause, ErrorCategory category)
    {
        _input.FailedTurn();
        _execution.ReleaseAll();
        _deliveryRetry.Clear();
        _toolWorkflow.ClearFailedTurn();
        _state = _state.AddErrorReply(errorMessage);

        var correlationId = Guid.NewGuid();

        TurnLog().Error(cause,
            "turn_failed category={Category} correlationId={CorrelationId} message={Message}",
            category,
            correlationId,
            errorMessage);

        EmitOutput(new ErrorOutput
        {
            SessionId = _sessionId,
            Message = errorMessage,
            Category = category,
            CorrelationId = correlationId,
            Cause = cause
        });
        EmitOutput(new TurnCompleted
        {
            SessionId = _sessionId,
            TurnNumber = new TurnNumber(_state.TurnCount),
            Outcome = TurnOutcome.Failed,
            SourceReminderId = _input.Authority.Source?.ReminderId,
            OpenChildRunIds = _state.GetOpenChildRunIds()
        });

        DrainBufferedMessagesOrBecomeReady();
    }

    private void EmitOutput(SessionOutput output, OutputFilter requiredFlag = OutputFilter.None)
    {
        _subscribers.Emit(output, requiredFlag);
        _logActor?.Tell(output);
        _observerActor?.Tell(output);
    }

    /// <summary>
    /// Records a background job the pipeline just submitted into
    /// <c>SessionState.ActiveBackgroundJobs</c> (snapshot-persisted) so the
    /// active-jobs context block reflects it and passivation knows there is
    /// something to reap. Keyed by the delivery dedup key so the
    /// <c>TurnRecorded</c> removal on result delivery matches.
    /// </summary>
    private void TrackStartedBackgroundJob(Jobs.ActiveJobInfo? startedJob)
    {
        if (startedJob is null)
            return;

        var jobKey = $"{Jobs.BackgroundJobManagerActor.JobDeliveryKeyPrefix}{startedJob.JobId.Value}";
        _state = _state.TrackBackgroundJob(jobKey, startedJob);
        _log.Info("Tracking background job {JobId} in session state", startedJob.JobId);
    }

    private HashSet<SubAgentRunId> ProcessChildFindings(
        IReadOnlyList<CompletedSubAgentRun> runs, IReadOnlyList<AcceptedSubAgentFinding> findings)
    {
        var emittedRunIds = new HashSet<SubAgentRunId>();
        foreach (var finding in findings)
        {
            if (emittedRunIds.Add(finding.RunId))
            {
                var runSummary = runs
                    .FirstOrDefault(x => x.RunId == finding.RunId);

                EmitOutput(new SubAgentOutput
                {
                    SessionId = _sessionId,
                    TimestampMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    AgentName = finding.AgentName,
                    Phase = Netclaw.Actors.SubAgents.SubAgentPhase.Completed,
                    Success = true,
                    Outcome = runSummary?.Outcome ?? SubAgentRunOutcome.Completed,
                    OutcomeReason = runSummary?.OutcomeReason,
                    Duration = finding.Duration,
                    MemoryDecision = finding.Decision.ToWireValue(),
                    MemoryDecisionReason = finding.DecisionReason,
                    FindingsCount = runSummary?.FindingsCount ?? 1
                }, OutputFilter.ToolCalls);
            }

            if (finding.Decision != SubAgentFindingReviewDecision.Accepted)
                continue;

            if (!TryResolveMemoryCheckpointAudience(
                    Memory.CheckpointTriggerType.SubagentFindings,
                    out var findingAudience))
                continue;

            EnqueueCheckpointFireAndForget(new MemoryCheckpointRequest(
                SessionId: _sessionId,
                TurnId: _input.Telemetry.TurnId,
                TriggerType: Memory.CheckpointTriggerType.SubagentFindings,
                Priority: 80,
                Payload: SessionMemoryCheckpointFactory.ForSubAgentFinding(
                    _sessionId,
                    CurrentMemoryBoundary(),
                    findingAudience,
                    finding)));
        }

        return emittedRunIds;
    }

    private void ProcessToolCallResult(Pipelines.ToolCallResult result)
    {
        _toolWorkflow.ApplyCorrection(result.ManagedTemporaryCorrectionUpdate);

        var emittedRunIds = ProcessChildFindings(result.CompletedSubAgentRuns, result.AcceptedSubAgentFindings);

        foreach (var run in result.CompletedSubAgentRuns)
        {
            MergeSuccessfulSubAgentWorkingContext(run.Completion);
            if (!emittedRunIds.Add(run.RunId))
                continue;

            EmitOutput(new SubAgentOutput
            {
                SessionId = _sessionId,
                TimestampMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                AgentName = run.AgentName,
                Phase = Netclaw.Actors.SubAgents.SubAgentPhase.Completed,
                Success = run.Success,
                Outcome = run.Outcome,
                OutcomeReason = run.OutcomeReason,
                Duration = run.Duration,
                MemoryDecision = run.MemoryDecision,
                MemoryDecisionReason = run.MemoryDecisionReason,
                FindingsCount = run.FindingsCount
            }, OutputFilter.ToolCalls);
        }

        var toolMessage = result.Message;
        var toolCallId = _toolWorkflow.RecordOutcome(result);

        _log.Info(
            "Tool authorization attempt result authorizationAttemptId={AuthorizationAttemptId} " +
            "sessionId={SessionId} callId={CallId} toolName={ToolName} " +
            "outcomeCategory={OutcomeCategory} remediationCode={RemediationCode}",
            result.AuthorizationAttemptId.Value,
            _sessionId.Value,
            toolCallId.Value,
            toolMessage.Name ?? "unknown",
            result.Receipt?.Category.ToString(),
            (result.Receipt as ToolInvocationReceipt.Correction)?.RemediationCode.ToString());

        EmitOutput(new ToolResultOutput
        {
            SessionId = _sessionId,
            CallId = toolCallId,
            ToolName = new ToolName(toolMessage.Name ?? "unknown"),
            Result = toolMessage.Content ?? string.Empty,
            FailureCode = result.FailureCode
        }, OutputFilter.ToolCalls);

        if (result.ExposureRequest is { } exposureRequest)
            TryActivateDiscoveredTool(exposureRequest.ToolName.Value);

        var priorProjectDirectory = _state.WorkingContext.ProjectDirectory;
        var updatedContext = WorkingContextUpdater.AcceptToolResult(
            _state.WorkingContext,
            toolMessage,
            result.Receipt);
        if (!ReferenceEquals(updatedContext, _state.WorkingContext))
            _state = _state with { WorkingContext = updatedContext };

        if (toolMessage.Name is "load_tool" && toolMessage.Content is not null)
            TryActivateDiscoveredTool(toolMessage.Content.Trim());

        if (!string.Equals(priorProjectDirectory, updatedContext.ProjectDirectory, StringComparison.Ordinal))
        {
            SetSystemPrompt();
            _log.Info("Project directory set to {ProjectDir}", updatedContext.ProjectDirectory);
        }

        foreach (var file in result.FileAttachments)
        {
            EmitOutput(new FileOutput
            {
                SessionId = _sessionId,
                TimestampMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                FilePath = file.FilePath,
                FileName = file.FileName,
                MimeType = file.MimeType
            }, OutputFilter.Files);
        }

        // Streaming tool results persist media references on ToolCallRecorded;
        // ApplyToolCallRecorded recreates the nudge during journal replay.
    }

    private void TryCompleteStreamedToolBatch()
    {
        if (_toolWorkflow.CompleteIfReady(
                _state,
                _turnState) is not BatchCompletion.Ready completion)
            return;

        _state = completion.State;
        var advance = _toolWorkflow.HasChildContinuation || HasReadyChildResult
            ? new ToolBatchInputAdvance(null, completion.ChooseNext(ShouldCompact()))
            : _input.AdvanceAfterToolBatch(completion, ShouldCompact());
        if (advance.Batch is { } batch)
        {
            TurnLog().Info("turn_mid_loop_buffer_drain count={BufferCount} iteration={Iteration}",
                batch.Messages.Count, _turnState.ToolIterationCount);
            ApplyBufferedBatch(batch);
        }

        if (advance.Next == ToolBatchNextAction.RequestCompaction)
        {
            _log.Info("Compaction threshold reached during tool loop ({InputTokens} tokens >= {Threshold} limit), starting compaction",
                _lastInputTokenCount, _model.CompactionTokenLimit(_config.Tuning.CompactionThreshold));
            _input.ResumeToolLoopAfterCompaction();
            Self.Tell(new CompactionTriggered(_lastInputTokenCount));
            TransitionTo(SessionPhase.Compacting);
            return;
        }

        _toolWorkflow.ClearNormalBatch();
        _execution.ClearToolTracking();
        TurnLog().Info("turn_tool_execution_complete iteration={Iteration} callCount={CallCount} resultCount={ResultCount}",
            _turnState.ToolIterationCount, _turnState.ToolCallCount, completion.ResultCount);
        FireLlmCall();
    }

    private void PersistAdoptedContextIfNeeded(MessageSource? source)
    {
        if (source?.HasAdoptedContext != true)
            return;

        if (string.IsNullOrWhiteSpace(source.MessageId))
            return;

        if (_state.AdoptedContextRecords.TryGetValue(source.MessageId, out var existing)
            && existing.ProjectionPersisted)
        {
            return;
        }

        var evt = new AdoptedContextRecorded
        {
            SessionId = _sessionId,
            AuthorizedMessageId = source.MessageId,
            AuthorizerSenderId = source.SenderId,
            LowerBound = source.AdoptedContextLowerBound,
            UpperBound = source.AdoptedContextUpperBound ?? source.MessageId,
            Projection = source.AdoptedContextProjection ?? string.Empty,
            HasAdoptedContext = source.HasAdoptedContext,
            HasThirdPartyAdoptedContext = source.HasThirdPartyAdoptedContext,
            AdoptedSpeakerIds = [.. source.AdoptedSpeakerIds],
            ProjectionPersisted = true,
            RecordedAtMs = NowMs(),
            Messages = [.. source.AdoptedContextEntries
                .Select(entry => new AdoptedContextRecorded.AdoptedMessageRecord
                {
                    MessageId = entry.MessageId,
                    SenderId = entry.SenderId,
                    TimestampMs = entry.Timestamp.ToUnixTimeMilliseconds(),
                    AuthorityAtInclusion = entry.AuthorityAtInclusion
                })]
        };

        // Persist the adopted-context audit record before continuing the turn so the
        // same accepted authorized message can reuse that record during replay or retry.
        // Akka.Persistence stashes later commands while this persist is in flight, so the
        // ordering stays deterministic even though the turn continues in the same handler.
        Persist(evt, e => _state = _state.Apply(e));
    }

    private sealed record RoutedSkillExecutionCompleted(
        string SkillName,
        string SubagentName,
        ChildRunStartKey.Slash StartKey,
        ChildRunAcceptance Result) : INoSerializationVerificationNeeded;

    private sealed record RoutedSkillExecutionFailed(
        string SkillName,
        string SubagentName,
        ChildRunStartKey.Slash StartKey,
        string ErrorMessage) : INoSerializationVerificationNeeded;

    private sealed record RoutedSkillSubAgentActivity(
        long TimestampMs,
        AgentName AgentName,
        SubAgentPhase Phase,
        int ToolCount,
        bool? Success,
        TimeSpan? Duration,
        int FindingsCount) : INoSerializationVerificationNeeded;

    private void UpdateCrashContext()
    {
        var telemetry = _input.Telemetry;
        CrashContextSnapshot.Update(
            _sessionId.Value,
            telemetry.TurnId?.Value,
            telemetry.MessageId,
            telemetry.ChannelType?.ToWireValue(),
            _timeProvider.GetUtcNow());
    }

    private ILoggingAdapter TurnLog()
    {
        var log = _log;

        if (_input.Telemetry.TurnId is { Value: { Length: > 0 } turnIdValue })
            log = log.WithContext("TurnId", turnIdValue);

        if (!string.IsNullOrWhiteSpace(_input.Telemetry.MessageId))
            log = log.WithContext("MessageId", _input.Telemetry.MessageId);

        if (_input.Telemetry.ChannelType is { } act)
            log = log.WithContext("ChannelType", act.ToWireValue());

        return log;
    }

    private void RequestRestartDrain()
    {
        var action = _execution.RequestRestart(
            Sender,
            _phase.Current,
            _input.SelectedCount > 0 || _toolWorkflow.HasChildContinuation,
            _input.HasBufferedContinuation,
            _toolWorkflow.CanParkForRestart());
        if (action == RestartAction.EnterPassivation)
            TransitionTo(SessionPhase.Passivating);
        else if (action == RestartAction.StopToolTask)
            _log.Info("Stopping a tool batch that waits only for durable approvals before restart drain");
    }

    private bool TryStopDurableApprovalWaits()
    {
        if (!_execution.TryStopDurableApprovalWaits(
                _phase.Current,
                _input.HasBufferedContinuation,
                _toolWorkflow.CanParkForRestart()))
            return false;

        _log.Info("Stopping a tool batch that waits only for durable approvals before restart drain");
        return true;
    }

    private void HandleRestartModelGraceExpired(RestartModelGraceExpired expired)
    {
        if (_execution.PlanRestartModelStop(expired, _phase.Current, _input.SelectedCount > 0 || _toolWorkflow.HasChildContinuation) is not { } callId)
            return;

        _modelContext.InvalidateInspection();
        _execution.RequestModelStop(callId);
    }

    private void HandleModelStoppedForRestart(ModelStoppedForRestart stopped)
    {
        var action = _execution.ObserveModelStop(stopped, _input.EmittedText);
        if (action == ModelStopAction.Ignore)
            return;

        if (stopped.Failure is { } failure)
            _log.Error(failure, "The model task failed during restart drain");

        if (action == ModelStopAction.CloseInputs)
            CloseActiveInputs(CompleteModelStopForRestart);
        else
            CompleteModelStopForRestart();
    }

    private void CompleteModelStopForRestart()
    {
        RecordRestartReminderEligibility();
        ClearBufferedMessagesForRestartDrain();
        TransitionTo(SessionPhase.Passivating);
    }

    private void RecordRestartReminderEligibility()
    {
        if (_execution.MarkRestartReminderEligible(
                _state.GetExecutablePendingInputs().Count,
                _input.EmittedText,
                _input.SelectedCount,
                _timeProvider.GetUtcNow()) == ReminderEligibility.EmittedText)
        {
            _log.Warning(
                "Restart drain cannot resume session {SessionId} because the interrupted turn emitted text.",
                _sessionId.Value);
        }
    }

    private void HandleToolPipelineStoppedForRestart(ToolPipelineStoppedForRestart stopped)
    {
        var action = _execution.ObserveToolStop(stopped);
        if (action == ToolStopAction.Ignore)
            return;

        if (action == ToolStopAction.FailTurn)
        {
            _log.Error(stopped.Failure, "The tool task failed during approval-only restart drain");
            FailCurrentTurn("The tool task failed during restart drain.", stopped.Failure!, ErrorCategory.ToolFailure);
            return;
        }

        _execution.ReleaseTool();
        ClearActiveToolBatchTracking();
        TransitionTo(SessionPhase.Passivating);
    }

    private void ClearBufferedMessagesForRestartDrain()
    {
        var count = _input.ClearBufferedForRestart();
        if (count == 0)
            return;

        _log.Warning(
            "Clearing {BufferCount} transient buffered message copy or copies; the journal keeps each accepted input.",
            count);
    }

    private void FireInitialTurnLlmCall(string? recallQuery, string? slashCommandSkillContent = null)
    {
        _turnRestartNotice = _pendingRestartNotice;
        _pendingRestartNotice = null;

        try
        {
            FireLlmCall(recallQuery, slashCommandSkillContent: slashCommandSkillContent);
        }
        finally
        {
            _turnRestartNotice = null;
        }
    }

    private void TryReplyAck()
    {
        if (Sender.IsNobody() || Equals(Sender, Context.System.DeadLetters))
            return;

        Sender.Tell(CommandAck.For(_sessionId));
    }

    private void TryReplyNack(string reason)
    {
        if (Sender.IsNobody() || Equals(Sender, Context.System.DeadLetters))
            return;

        Sender.Tell(CommandNack.For(_sessionId, reason));
    }


    internal void SetTitle(string title)
    {
        var evt = new SessionTitleSet
        {
            SessionId = _sessionId,
            Title = title,
            SetAtMs = NowMs()
        };

        Persist(evt, e =>
        {
            _state = _state.Apply(e);
            EmitOutput(new SessionTitleOutput(title)
            {
                SessionId = _sessionId
            });
        });
    }

}
