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
    // The inputs that were pending at RecoveryCompleted. The expiry signal closes only these, so it never closes work
    // that arrived after the restart (#2659).
    private IReadOnlySet<InputId> _recoveredPendingInputIds = new HashSet<InputId>();
    // The held continuations that a fresh restart reminder authorized (ContinuationAuthorized). Rebuilt by replay.
    private readonly HashSet<InputId> _authorizedContinuations = [];
    private string? _turnRestartNotice;
    // A person input ended an automation turn, and no batch of a person's requester has started since (#2633).
    // Actor-local: a restart drops it, and the restart path posts its own notice.
    private bool _pendingStopNote;
    // The current turn takes the stop note in its first model call. Actor-local.
    private bool _turnStopNote;
    // Replay only: the requester of the journal turn that the last replayed tool batch belongs to. Actor-local.
    private TurnContext? _replayTurnOwner;
    // The continuation input of a stopped turn that the drain writes, and the stop that waits for it (#2632).
    private (InputAdmitted Event, Action<int> Stop)? _continuationWrite;

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
        Recover<ContinuationAuthorized>(evt => _authorizedContinuations.Add(evt.InputId));
        Recover<ChildRunAccepted>(evt => _state = _state.Apply(evt));
        // Replay does not close child approvals here. RecoveryCompleted closes every child entry that replay left open.
        Recover<ChildRunEvent>(evt => _state = _state.Apply(evt));
        Recover<LegacyCoordinationEvent>(ApplyLegacyCoordination);
        Recover<ToolCallRecorded>(ApplyToolCallRecorded);
        Recover<ToolApprovalRequested>(ApplyToolApprovalRequested);
        Recover<ToolApprovalResolved>(ApplyToolApprovalResolved);
        Recover<ToolBatchAbandoned>(evt =>
        {
            // An abandoned batch ends its turn, so the noticed runs of that turn end too.
            ApplyToolBatchAbandoned(evt);
            _input.ClearNoticeRuns();
        });
        Recover<SnapshotOffer>(offer =>
        {
            if (offer.Snapshot is SessionSnapshot snapshot)
            {
                _state = SessionState.FromSnapshot(snapshot);
                _authorizedContinuations.Clear();
                _authorizedContinuations.UnionWith(snapshot.AuthorizedContinuationInputIds);
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
            _recoveredPendingInputIds = _state.GetExecutablePendingInputs().Select(static input => input.InputId).ToHashSet();

            // A child owner never survives a restart. Close the child entries that remain before a stashed command
            // runs, then log the approvals that stay open. Replay itself logs none (see ApplyToolApprovalRequested).
            var pausedAtRecovery = WasPausedAtRecovery();
            // Recovery runs before any subscriber joins, so each new subscriber also gets these closes at its join.
            // A channel that restored a tracked child prompt from its own journal then redraws and clears it.
            _restartWithdrawals = _toolWorkflow.WithdrawRecoveredChildApprovals(_state)
                .Select(closed => new ToolInteractionClosed(closed.CallId,
                    new ToolInteractionCloseReason.Withdrawn(closed.Reason)) { SessionId = _sessionId })
                .ToArray();
            RestoreParkedRequests();
            CloseRecoveredConsentNotices();
            foreach (var open in _toolWorkflow.OpenApprovals())
                LogToolApprovalRequested(open.AuthorizationAttemptId.Value, open.Request.CallId, open.Request.ToolName, recovered: true);

            TransitionTo(SessionPhase.Ready);
            // Only an approval re-drive continues the open journal turn. Any other next turn starts with no noticed run.
            if (!_toolWorkflow.HasParkedApprovals())
                _input.ClearNoticeRuns();
            RestoreWaitingInputs();
            Self.Tell(new RecoverChildRuns([.. _state.GetExecutableChildRuns().Select(run =>
                (run.RunId, ChildParkRules.RecoveryAction(run.Lifecycle)))]));

            // Resolve once at recovery time. ActorRegistry returns Nobody if
            // the dispatcher hasn't been registered (unit-test scenarios that
            // skip the hosting wiring) — leave _logActor null in that case.
            var registry = ActorRegistry.For(Context.System);
            if (registry.TryGet<SessionLogDispatcherActorKey>(out var dispatcher))
                _logActor = dispatcher;
            // After the log actor is known, so the session log records the restart closes.
            foreach (var closed in _restartWithdrawals)
                EmitOutput(closed);

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

            // Akka.Persistence unstashes the commands that arrived during recovery right after this handler.
            // The orphan notices must start their turn here, so a message that woke the session buffers behind them
            // and does not run first.
            ResumeOrphanNotices(pausedAtRecovery || _toolWorkflow.IsChildConsentPaused);
            // A restart that withdrew the requests ends the pause, so the held input runs. An open parked request keeps
            // the pause: the held input goes into the queue now, before the stashed commands, so a stashed fire finds
            // and replaces the held fire of its reminder (design F3). The queue holds it until the last answer.
            if (_toolWorkflow.IsChildConsentPaused)
                QueueInputsHeldByThePause();
            else if (pausedAtRecovery)
                Self.Tell(new StartHeldInputs(), ActorRefs.NoSender);
            if (_consentDigest.IsReady)
                ChildConsentChanged();
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
            if (HasLiveBackgroundJobs || HasUnreportedChildren)
            {
                _log.Info(
                    "Session idle but active background jobs or unreported child runs remain; deferring passivation");
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

            if (TryCloseExpiredRestartWork(cmd))
                return;

            if (IsRestartResume(cmd))
            {
                QueueRestartResume(cmd);
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

        // An approval notice turn may end with no text. The turn then ends quietly, with no empty-reply retry and no
        // fallback post: the notice is runtime state, and the model owes the person no reply to it.
        if (analysis.Kind is LlmResponseKind.ThinkingOnly or LlmResponseKind.Empty
            && _input.Authority.Context is { IsChildConsentNotice: true })
        {
            TurnLog().Info("turn_consent_notice_quiet_end kind={Kind}", analysis.Kind);
            RecordIntermediateUsage(response.Usage);
            CloseActiveInputs(FinishTurnWithoutReply);
            return;
        }

        if (analysis.Kind is LlmResponseKind.ThinkingOnly or LlmResponseKind.Empty)
        {
            var truncated = response.FinishReason == ChatFinishReason.Length;
            switch (_turnState.EvaluateEmptyResponse(analysis.Kind, truncated))
            {
                case EmptyResponseAction.Retry when RestartDrainRequested:
                    StopTurnForRestart();
                    return;
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

            if (TryCloseExpiredRestartWork(cmd))
                return;

            if (IsRestartResume(cmd))
            {
                QueueRestartResume(cmd);
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

    // Compaction end: a ready approval notice is admitted first, so the drain sees it.
    // An overflow recovery compaction keeps the turn open: the rolled-back message waits as a replay item.
    private void DrainBufferOrReady()
        => AdmitConsentNoticeThen(inOpenTurn: _input.ResumesToolLoopAfterCompaction || _input.StartsWithReplay,
            DrainBufferOrReadyNow);

    private void DrainBufferOrReadyNow()
    {
        // Read these facts before AdvanceAfterCompaction resets them. A drain after a tool-loop or replay compaction
        // stops an open turn. A compaction at a turn end stops no turn, so it posts no notice.
        var stopsTurn = _input.ResumesToolLoopAfterCompaction || _input.StartsWithReplay;
        var continuation = _input.AdvanceAfterCompaction(RestartDrainRequested, _toolWorkflow.IsChildConsentPaused);
        if (continuation.Action == InputProgressAction.Restart)
        {
            KeepStoppedTurnThen(stopsTurn, pendingTurnInputs =>
            {
                var eligibility = RecordRestartReminderEligibility();
                if (stopsTurn)
                    EmitRestartStoppedTurnNotice(eligibility, pendingTurnInputs);
                ClearBufferedMessagesForRestartDrain();
                TransitionTo(SessionPhase.Ready);
                TransitionTo(SessionPhase.Passivating);
            });
            return;
        }

        // A person waits behind the automation turn whose tool loop this compaction stopped. The turn ends, and the
        // drain then runs again with no tool-loop resume, so the restart check and the deferred approval still apply.
        if (continuation.Action == InputProgressAction.EndTurn)
        {
            TurnLog().Warning("turn_person_input_end_at_compaction reminder={ReminderId} job={BackgroundJobId}",
                _input.Authority.Source?.ReminderId?.Value, _input.Authority.Source?.BackgroundJobId?.Value);
            EndAutomationTurnForPerson(DrainBufferOrReady);
            return;
        }

        if (continuation.ResumeToolLoop)
            _log.Info("Post-compaction: resuming tool loop with follow-up LLM call");

        if (continuation.Batch is { DispatchesCommand: true } command)
            DispatchBufferedCommand(command);
        else if (continuation.Action == InputProgressAction.FollowUp)
        {
            if (continuation.Batch is { } batch)
            {
                _log.Info("Post-compaction: draining {BufferCount} buffered message(s)", batch.Messages.Count);
                ApplyBufferedBatch(batch);
            }

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
        // A restart drain waits for the park or terminal record of each live child (ContinueDrainAfterChild), or for the
        // end of the child wait.
        if (_execution.WaitsForLiveChildren)
            return;
        var final = _execution.ClaimFinalStop(
            _phase.Current, _sessionId, _state.GetExecutablePendingInputs(), UnreportedRuns().ToArray());
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
        // The model returned tool calls inside the drain grace period. A restart can interrupt a new tool batch after
        // its effects start, so the session drops the calls and stops the turn as a model stop for restart does.
        if (RestartDrainRequested)
        {
            TurnLog().Info("turn_restart_drops_tool_calls count={CallCount}", toolCalls.Count);
            if (_input.EmittedText)
                CloseActiveInputs(StopTurnForRestart);
            else
                StopTurnForRestart();
            return;
        }

        var toolExecutor = _toolExecutor
            ?? throw new InvalidOperationException("A tool-call response requires a tool executor.");
        var prepared = _toolWorkflow.PrepareFreshBatch(
            lastMessage, toolCalls, _toolRegistry, toolExecutor, _turnState);
        if (prepared.Decision.Kind != ToolCycleDecisionKind.Execute)
        {
            Logging.GetLogger(Context.System, typeof(TurnStateTracker)).Warning(
                "Tool recurrence decision kind={DecisionKind} refused={RefusedCount} repeated={RepeatedCount}",
                prepared.Decision.Kind, prepared.Decision.RefusedCallIds.Count, prepared.Decision.RepeatedCallIds.Count);
        }

        if (prepared.Decision.RefusedRepeat == RefusedRepeatAction.EndTurn)
        {
            EndTurnAfterIgnoredEscalation(lastMessage, usage);
            return;
        }

        // The progress axis: the calls already ran and failed, so this refuses nothing. At the end threshold
        // the turn ends quietly with the text of the model, the same way an ignored escalation does.
        if (prepared.Decision.Progress == RefusedRepeatAction.EndTurn)
        {
            TurnLog().Warning("turn_no_progress_end failedCalls={FailedCalls}",
                prepared.Decision.FailedCallsSinceProgress);
            EndTurnAfterIgnoredEscalation(lastMessage, usage);
            return;
        }

        // A repeat of a refused call does not stop the turn at once (Aaron, 18 Oct 2026). The pipeline refuses
        // it again, and the batch adds a model-facing nudge after its results. No error reaches the person.
        var userMsg = _state.FindLastUserMessage() ?? new SerializableChatMessage
        {
            Role = Protocol.ChatRole.User,
            Content = string.Empty
        };

        var started = new ToolBatchStarted
        {
            SessionId = _sessionId,
            UserMessage = userMsg,
            AssistantMessage = prepared.AssistantMessage,
            StartedAtMs = NowMs(),
            ConsumedInputIds = _input.SelectedInputs.Ids
        };
        Persist(started, evt =>
        {
            _state = _state.CloseInputs(evt.ConsumedInputIds);
            _input.ToolBatchCommitted();
            ApplyToolBatchStarted(evt);
            EmitAndDispatchToolBatch(
                lastMessage,
                prepared.Calls,
                usage,
                prepared.Cycle,
                prepared.Decision);
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

        var repeatNudge = ToolCycleMessages.ForRefusedRepeat(cycleDecision, toolCalls, childRun: false);
        DispatchToolBatch(toolCalls, cycleDecision.RefusedCallIds, repeatNudge,
            preparedCycleBatch: preparedCycleBatch);
    }

    /// <summary>
    /// The model sent a refused call again after the escalated nudge (Aaron, 18 Oct 2026). The turn ends with no
    /// dispatch and no further model request. No call of the response runs, also a new call next to the repeat.
    /// No error output, error reply, or failed turn reaches the person: the person sees only text of the model.
    /// </summary>
    private void EndTurnAfterIgnoredEscalation(AiChatMessage lastMessage, UsageDetails? usage)
    {
        var withoutCalls = new AiChatMessage(lastMessage.Role,
            lastMessage.Contents.Where(static content => content is not FunctionCallContent).ToList());
        var hasText = withoutCalls.Contents.OfType<TextContent>()
            .Any(static text => !string.IsNullOrWhiteSpace(text.Text));
        TurnLog().Warning("turn_refused_repeat_end refusedRepeats={RefusedRepeats} hasText={HasText}",
            TurnStateTracker.EscalatedRefusedRepeat + 1, hasText);

        if (hasText)
        {
            // The normal text path records the text as the reply, sends it, and completes the turn.
            HandleTextResponse(withoutCalls, usage, streamedText: false, streamedThinking: false);
            return;
        }

        // No text. An empty assistant reply is not safe, because some providers reject an empty text block.
        // So the turn records no reply. The person keeps the text that the model wrote before.
        RecordIntermediateUsage(usage);
        CloseActiveInputs(FinishTurnWithoutReply);
    }

    // The cleanup of a failed turn, without the error reply and the error output. The turn completes.
    private void FinishTurnWithoutReply()
    {
        CompleteTurnQuietly();
        DrainBufferedMessagesOrBecomeReady();
    }

    private void CompleteTurnQuietly()
    {
        _input.FailedTurn();
        _execution.ReleaseAll();
        _deliveryRetry.Clear();
        _toolWorkflow.ClearFailedTurn();
        // A failed turn also counts without a journal event, so the next turn gets a new turn number.
        _state = _state with { TurnCount = _state.TurnCount + 1 };

        EmitOutput(new TurnCompleted
        {
            SessionId = _sessionId,
            TurnNumber = new TurnNumber(_state.TurnCount),
            Outcome = TurnOutcome.Completed,
            SourceReminderId = _input.Authority.Source?.ReminderId,
            // A channel posts nothing for this turn, not even its empty-turn fallback: that text is Netclaw text.
            QuietEnd = true,
            OpenChildRunIds = _state.GetOpenChildRunIds()
        });
    }

    // A person's input waits behind this automation turn at a safe boundary (SessionInputFlow.PersonWaitsBehindAutomation).
    // The turn ends quietly, with no further model call, and the next person turn gets the stop note. Then the
    // boundary continues: a tool-batch end compacts when due, and a compaction end drains.
    private void EndAutomationTurnForPerson(Action thenContinue)
    {
        _toolWorkflow.ClearNormalBatch();
        _execution.ClearToolTracking();
        CloseActiveInputs(() =>
        {
            CompleteTurnQuietly();
            _pendingStopNote = true;
            thenContinue();
        });
    }

    // The first batch that adopts a person's requester after a stop ends the pending stop note. The turn takes the note
    // when the batch holds a person's own message. A batch of child result notices only, or a slash command, does not.
    private void SettleStopNote(BufferedInputBatch batch)
    {
        if (!_pendingStopNote || !batch.AdoptsAuthority
            || _input.Authority.Context?.RequesterPrincipal is not (PrincipalClassification.Operator or PrincipalClassification.TrustedInternal))
            return;
        _pendingStopNote = false;
        _turnStopNote = !batch.DispatchesCommand && batch.Messages.Any(static message => message.Notice is null);
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
        string? repeatNudge,
        ApprovalRedrivePlan? approvalPlan = null,
        PreparedToolCycleBatch? preparedCycleBatch = null,
        bool recordCompletedCycle = true)
    {
        _toolWorkflow.StartDispatch(toolCalls, preparedCycleBatch, _toolExecutor, recordCompletedCycle);
        if (repeatNudge is not null)
            _toolWorkflow.AddRepeatNudgeAfterBatch(repeatNudge);

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
        var trust = _input.Authority.Trust
            ?? throw new InvalidOperationException("Tool batch dispatch requires the effective trust context.");
        var runEnvironment = new SessionToolRunEnvironment
        {
            Storage = _sessionStorage,
            EffectiveAudience = trust.EffectiveAudience,
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
            request => self.Tell(request, ActorRefs.NoSender),
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
            SourceReminderId = RunningTurnReminder(),
            SourceBackgroundJobId = selected.BackgroundJobId,
            ConsumedInputIds = selected.Ids
        };

        _ = _state.Apply(turnEvent, userAlreadyInHistory: true);
        Persist(turnEvent, evt =>
        {
            _state = _state.Apply(evt, userAlreadyInHistory: true);
            _input.TurnCommitted(evt);

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

    // Turn end: a ready approval notice is admitted first, so the drain sees it.
    private void DrainBufferedMessagesOrBecomeReady()
        => AdmitConsentNoticeThen(inOpenTurn: false, DrainBufferedMessagesOrBecomeReadyNow);

    private void DrainBufferedMessagesOrBecomeReadyNow()
    {
        var next = _input.AdvanceAfterTurn(RestartDrainRequested, _toolWorkflow.IsChildConsentPaused);
        if (next.Action == InputProgressAction.Restart)
        {
            RecordRestartReminderEligibility();
            ClearBufferedMessagesForRestartDrain();
            TransitionTo(SessionPhase.Ready);
            TransitionTo(SessionPhase.Passivating);
            return;
        }

        if (next.Batch is { DispatchesCommand: true } command)
        {
            DispatchBufferedCommand(command);
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
    }

    private void CompleteDeferredIdlePassivation()
    {
        if (!_execution.ClaimDeferredIdlePassivation(_phase.Current))
            return;

        // The idle path keeps a session with a live background job or a live child run in memory. Work that the
        // waiting input's turn started must not be reaped here. The re-armed idle timeout decides again, with the
        // same check.
        if (HasLiveBackgroundJobs || HasUnreportedChildren)
            return;

        TransitionTo(SessionPhase.Passivating);
    }

    // A live child, an ended child whose notice is still to come, or a park with every answer that waits only for a
    // drain to end. The notice and the resume need this actor. A park with an open request is in the run record, so
    // the session can passivate, and a click wakes it (UnreportedRuns leaves that park out).
    private bool HasUnreportedChildren => _execution.HasLiveChildren
        || UnreportedRuns().Any(run => run.Lifecycle is ChildRunLifecycle.Terminal or ChildRunLifecycle.Parked);

    private void ApplyBufferedBatch(BufferedInputBatch batch)
    {
        SettleStopNote(batch);
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
            // The input clears the refusals, so the model can run a refused call again. It starts a new refused
            // repeat count only for a new turn or for a person. Automation that joins an automation turn keeps
            // the count: its requester is a fixed system sender, so the fire rate would otherwise set the bound.
            if (!batch.JoinsAutomationTurn)
                _turnState.ResetRefusedRepeatCount();
            _modelContext.ResetForNewTurn();
        }

        foreach (var message in batch.Messages)
        {
            // The continuation input of a stopped turn has no text for the model (#2632). Only its marker counts: an
            // empty person input reaches the model as a user message (#2659).
            if (message.AdmittedInputId is { } admittedId
                && _state.PendingInputs.Any(input => input.InputId == admittedId && SessionInputFlow.IsContinuation(input)))
                continue;
            var refs = message.MediaReferences.Count > 0 ? message.MediaReferences : null;
            _state = _state.AddUserMessage(message.Content, refs);
        }

    }

    private void PrepareFreshTurnState()
    {
        _turnState.ResetForNewTurn();
        // A new turn counts from zero, whoever starts it. A mid-loop child result and a wake for a child result
        // clear the evidence only (they call ResetForNewTurn): they bring no new person input.
        _turnState.ResetRefusedRepeatCount();
        _discoveredToolCache.PrepareForNewTurn(
            _config.Tuning.DiscoveredToolRetentionTurns,
            _config.Tuning.DiscoveredToolMaxCount,
            _toolRegistry);
    }

    /// <summary>
    /// The reminder link of the running turn. A turn takes the link of its own input first. A child result notice has
    /// no reminder of its own, so a notice turn takes the link that the child run kept from the turn that started it:
    /// the first noticed run that has one. A retry turn keeps the link of the turn that it repeats. A child that this
    /// turn starts keeps the same link, so the link also reaches the result of a child that a notice turn started.
    /// </summary>
    private ReminderId? RunningTurnReminder()
    {
        var selected = _input.SelectedInputs;
        return selected.ReminderId
            ?? selected.NoticeRunIds.Select(runId => _state.ChildRuns[runId].SourceReminderId).FirstOrDefault(link => link is not null)
            ?? _deliveryRetry.RetriedTurnReminder;
    }

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

        if (TryCloseExpiredRestartWork(cmd) || TryResumeInterruptedInput(cmd))
            return;

        if (TryRejectIncompatibleInput(cmd.MediaReferences, cmd.Source))
            return;

        // Decide admission before any side effect. A duplicate, a dropped fire, or a refusal must leave the
        // session as it was, so it must not abandon an open tool batch.
        var decision = _input.PrepareAdmission(cmd, _state, _sessionId, NowMs(), _toolWorkflow.IsChildConsentPaused);
        if (decision is not (AdmissionDecision.Existing or AdmissionDecision.Persist or AdmissionDecision.ReplaceWaitingFire))
        {
            SettleWithoutAdmission(decision, cmd);
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
        // until the approval closes (see StartWaitingInputAfterApproval). A child result
        // notice is automated input by kind, although its requester is a person.
        switch (_toolWorkflow.PlanNewInputAbandonment(_state, _sessionId, cmd.Source?.Principal, cmd.Notice is not null, NowMs()))
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
        var decision = _input.PrepareAdmission(cmd, _state, _sessionId, NowMs(), _toolWorkflow.IsChildConsentPaused);
        if (decision is AdmissionDecision.Existing or AdmissionDecision.Persist or AdmissionDecision.ReplaceWaitingFire)
            CommitAdmission(decision, cmd, placement);
        else
            SettleWithoutAdmission(decision, cmd);
    }

    // The session acknowledges a duplicate or a dropped fire, so the reminder system sees success and ends its job.
    private void SettleWithoutAdmission(AdmissionDecision decision, SendUserMessage cmd)
    {
        switch (decision)
        {
            case AdmissionDecision.Reject rejected:
                _log.Error("Rejecting {InputKind}: {Reason}", cmd.Notice is null ? "input" : "child notice", rejected.Reason);
                if (cmd.Notice is { } rejectedNotice)
                    GiveUpOnChildNotice(rejectedNotice.Notice.RunId, rejected.Reason);
                TryReplyNack($"The session cannot admit the input: {rejected.Reason}");
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
                PersistAdmission(persist.Event, cmd, placement);
                return;
            case AdmissionDecision.ReplaceWaitingFire replace:
                // The waiting fire never ran, so the close consumes it with no turn. Then the newer fire waits.
                PersistAdmission(replace.Event, cmd, placement, replace.Waiting);
                return;
        }

        throw new InvalidOperationException("Unknown input admission decision.");
    }

    private void PersistAdmission(InputAdmitted evt, SendUserMessage cmd, InputPlacement placement, InputId? replaces = null)
        => PersistReplacingInput(replaces, evt, "reminder_fire_replaced", admitted =>
        {
            // The review reads the ended run, which the notice turns into a reported run.
            var endedRun = admitted.ChildNotice is { } notice ? _state.ChildRuns[notice.RunId] : null;
            _state = _state.Apply(admitted);
            ReviewChildFindings(endedRun);
            var accepted = _input.AdmissionCommitted(admitted, cmd, placement);
            CompleteAcceptedInput(accepted, placement);
        });

    // Every admission comes here: an ordinary input, a fire that replaces a waiting fire, and an approval notice. One
    // atomic write holds the close of the waiting input and the admission of the newer input, so a crash keeps both or
    // neither. The same write closes the continuation input of a stopped turn that no turn selected (#2632). The stopped
    // turn never runs after that, so the person gets the held-back restart notice once. Only the notices that the
    // session makes itself (an approval notice, a child result notice) keep a continuation that the pause holds in the
    // queue, so it runs after the last answer. A person input or a fire closes it.
    private void PersistReplacingInput(
        InputId? waiting, InputAdmitted admitted, string replacedLogEvent, Action<InputAdmitted> onAdmitted)
    {
        var selected = _input.SelectedInputs.Ids;
        var keepsQueued = admitted.ChildNotice is not null
                          || TurnContext.TryFromRecord(admitted.TurnContext, out var authority, out _) && authority is { IsChildConsentNotice: true };
        var stale = _state.GetExecutablePendingInputs()
            .FirstOrDefault(input => SessionInputFlow.IsContinuation(input) && !selected.Contains(input.InputId)
                                     && !(keepsQueued && _input.IsBuffered(input.InputId)))?.InputId;
        InputId[] closes = [.. new[] { waiting, stale }.OfType<InputId>()];
        if (closes.Length == 0)
        {
            Persist(admitted, onAdmitted);
            return;
        }

        var closed = new InputClosed { SessionId = _sessionId, InputIds = closes, ClosedAtMs = NowMs(), KeepsTurn = true };
        _ = _state.Apply(closed);
        PersistAll<ISessionEvent>([closed, admitted], committed =>
        {
            if (committed is InputClosed close)
            {
                ApplyInputClosed(close);
                if (waiting is { } waitingId)
                    TurnLog().Info("{ReplacedEvent} waiting={Waiting} replacement={Replacement}",
                        replacedLogEvent, waitingId, admitted.InputId);
                if (stale is { } stopped)
                {
                    TurnLog().Info("continuation_closed waiting={Waiting} replacement={Replacement}", stopped, admitted.InputId);
                    EmitRestartStoppedTurnNotice(ReminderEligibility.None, 0);
                }
            }
            else
                onAdmitted((InputAdmitted)committed);
        });
    }

    // Live code keeps the noticed runs at a close. The activation of the next turn clears them.
    private void ApplyInputClosed(InputClosed closed)
    {
        _state = _state.Apply(closed);
        _input.InputsClosed(closed);
    }

    private void CompleteAcceptedInput(SendUserMessage command, InputPlacement placement)
    {
        if (placement == InputPlacement.Activate)
        {
            ContinueIncomingUserMessage(command, replyAck: true);
            return;
        }

        if (placement == InputPlacement.BufferDuringModel)
        {
            // The retry of a notice turn has no reminder input, so its link comes only from the pending retry.
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
        _input.ClearNoticeRuns();

        // A session that recovers without a snapshot has no eligible turn. Mirror the live path so that
        // late delivery feedback for the latest turn finds the turn, the reminder that started it, and
        // the routed mark.
        RecordEligibleTurn(evt);
    }

    private void RecoverParentBatch(ToolBatchStarted recorded)
    {
        var evt = LegacyCoordinationReplay.TranslateParentDecision(_state, recorded);
        // The consumed notices name the runs of this turn. RestoreConsumedInputs closes the inputs, so read them first.
        var pendingById = _state.PendingInputs.ToDictionary(input => input.InputId);
        if (evt.ConsumedInputIds.Count > 0 && pendingById.TryGetValue(evt.ConsumedInputIds[0], out var first))
        {
            // Live, an input joins a running turn only when it has the requester of the turn (SessionInputFlow.JoinsTurn).
            // An input of another requester starts a new turn, and AdoptAuthority clears the noticed runs. A failed turn
            // can end with no journal event (#2574), so replay ends the old turn here. A failed turn that is followed by
            // a turn of the same requester stays ambiguous in the journal, so replay keeps its runs.
            TurnContext.TryFromRecord(first.TurnContext, out var requester, out _);
            if (requester is null || _replayTurnOwner is null || !requester.HasSameRequester(_replayTurnOwner))
                _input.ClearNoticeRuns();
            _replayTurnOwner = requester;
        }
        _input.RestoreNoticeRuns(evt.ConsumedInputIds
            .Select(id => pendingById.TryGetValue(id, out var input) ? input.ChildNotice?.RunId : null)
            .OfType<SubAgentRunId>());
        RestoreConsumedInputs(evt.ConsumedInputIds, evt.UserMessage);
        ApplyToolBatchStarted(evt);
    }

    // A closure ends a failed turn, or a turn with no reply. A close that keeps the turn closes a waiting input, so
    // the noticed runs of the open turn stay.
    private void RecoverParentClosure(InputClosed recorded)
    {
        var closed = LegacyCoordinationReplay.TranslateParentDecision(_state, recorded);
        _state = _state.Apply(closed);
        if (!closed.KeepsTurn)
            _input.ClearNoticeRuns();
    }

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

    private void ContinueIncomingUserMessage(SendUserMessage cmd, bool replyAck)
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

        if (TryHandleSlashCommand(executableUserContent, mediaRefs, replyAck))
            return;

        _state = _state.AddUserMessage(userContent, mediaRefs.Count > 0 ? mediaRefs : null);
        if (replyAck)
            TryReplyAck();
        _modelContext.ResetForNewTurn();
        _input.ResetOverflowRetry();
        FireInitialTurnLlmCall(executableUserContent);
        TransitionTo(SessionPhase.Processing);
    }

    // The restart reminder arrives while a turn runs, for example the turn of an orphan notice that recovery started.
    // The one resume path then queues each pending input that no turn selected, and the normal drain runs them after
    // the running turn, in admission order. The queue keeps one copy of an input that recovery queued already.
    // No turn of this message runs: each queued input runs later under its own recorded source. So the reply is
    // RestartResumeNotNeeded, and a caller on the Reminder-origin route (#2491) settles at once. This message resumes
    // no leading input, so a damaged pending input gets no nack. The session logs it and keeps it pending (#2576).
    private void QueueRestartResume(SendUserMessage cmd)
    {
        var selected = _input.SelectedInputs.Ids;
        var queued = QueueValidWaitingInputs(
            cmd, _state.GetExecutablePendingInputs().Where(input => !selected.Contains(input.InputId)));
        if (queued > 0)
            _execution.ClearRestartReminderEligibility();
        _log.Info("restart_resume_queued count={QueuedCount} phase={Phase}", queued, _phase.Current);
        ReplyRestartResumeNotNeeded();
    }

    // RestoreWaitingInputs throws for a damaged authority record. A damaged input never runs, so the session logs it,
    // keeps it pending in the journal, and queues only the valid inputs (#2576).
    private int QueueValidWaitingInputs(SendUserMessage cmd, IEnumerable<InputAdmitted> waiting)
    {
        var (valid, damaged) = SessionInputFlow.SplitByAuthority(waiting);
        foreach (var input in damaged)
            _log.Error("Restart reminder {ReminderId}: pending input {InputId} has a damaged authority",
                cmd.Source?.ReminderId, input.InputId);
        return _input.RestoreWaitingInputs(valid.Select(static pair => pair.Input).ToArray(), _sessionId);
    }

    // The restart reminder is not an input. It only resumes inputs that the journal already holds.
    private static bool IsRestartResume(SendUserMessage cmd)
        => IsRestartReminder(cmd, SessionExecutionLifetime.RestartReminderInstruction);

    private static bool IsRestartReminder(SendUserMessage cmd, string instruction)
        => cmd.Source?.ReminderId?.Value is { } reminderId
           && reminderId.StartsWith(ReminderIdGenerator.RestartReminderIdPrefix, StringComparison.Ordinal)
           && string.Equals(cmd.Content, instruction, StringComparison.Ordinal);

    // The expiry signal (#2659 design D1, D2): startup registers a restart reminder that expired before recovery with
    // RestartExpiredInstruction. The signal is not an input and grants nothing. It closes every input that was pending
    // at recovery and that no turn selected, in one write, and posts the restart notice once in the journal callback.
    // It starts no model call. It leaves the session-made notices and the inputs that the pause holds, so a child park
    // and its consent prompts stay. A continuation input is stopped work also when the pause holds it. An open parent
    // approval owns the next turn, as on the resume path, so the signal then closes nothing.
    private bool TryCloseExpiredRestartWork(SendUserMessage cmd)
    {
        if (!IsRestartReminder(cmd, SessionProtocol.RestartExpiredInstruction))
            return false;

        if (_toolWorkflow.HasParkedApprovals())
        {
            _log.Info("restart_expired_skipped reminder={ReminderId} reason=open_parent_approval", cmd.Source?.ReminderId);
            AckRestartResumeNoOp(cmd);
            return true;
        }

        var selected = _input.SelectedInputs.Ids;
        var paused = _toolWorkflow.IsChildConsentPaused;
        InputId[] closes = [.. _state.GetExecutablePendingInputs()
            .Where(input => _recoveredPendingInputIds.Contains(input.InputId) && !selected.Contains(input.InputId)
                            && (SessionInputFlow.IsContinuation(input)
                                || input.ChildNotice is null
                                && TurnContext.TryFromRecord(input.TurnContext, out var authority, out _)
                                && authority is { IsChildConsentNotice: false }
                                && !(paused && ChildConsentPause.Holds(authority, isChildNotice: false))))
            .Select(static input => input.InputId)];
        _log.Info("restart_expired reminder={ReminderId} closes={CloseCount}", cmd.Source?.ReminderId, closes.Length);
        if (closes.Length == 0)
        {
            ReplyRestartResumeNotNeeded();
            return true;
        }

        var closed = new InputClosed { SessionId = _sessionId, InputIds = closes, ClosedAtMs = NowMs(), KeepsTurn = true };
        _ = _state.Apply(closed);
        Persist(closed, committed =>
        {
            ApplyInputClosed(committed);
            EmitRestartStoppedTurnNotice(ReminderEligibility.None, 0);
            ReplyRestartResumeNotNeeded();
        });
        return true;
    }

    // Akka.Reminders can redeliver the restart reminder. In an idle session with nothing to resume, this message
    // resumes nothing and no turn runs for it, so the session acknowledges and the reminder run ends in success.
    private void AckRestartResumeNoOp(SendUserMessage cmd)
    {
        _log.Info(
            "restart_resume_noop reminder={ReminderId} phase={Phase} pending={PendingCount}",
            cmd.Source?.ReminderId,
            _phase.Current,
            _state.PendingInputs.Count);
        ReplyRestartResumeNotNeeded();
    }

    // A caller on the Reminder-origin route (#2491) waits for a terminal turn after an ack. No turn of this message runs.
    private void ReplyRestartResumeNotNeeded()
    {
        if (Sender.IsNobody() || Equals(Sender, Context.System.DeadLetters))
            return;
        Sender.Tell(new RestartResumeNotNeeded(_sessionId));
    }

    private bool TryResumeInterruptedInput(SendUserMessage cmd)
    {
        if (!IsRestartResume(cmd))
            return false;

        // The approval check comes before the resume. An open approval owns the next turn: the answer ends the wait,
        // and the inputs that wait behind it are queued already (RestoreWaitingInputs). A resume now would start a
        // model turn while the tool batch is parked. No turn runs for this message.
        if (_toolWorkflow.HasParkedApprovals())
        {
            AckRestartResumeNoOp(cmd);
            return true;
        }

        var pending = _state.GetExecutablePendingInputs();
        // N1 (durable-child-park-recovery): an open parked request holds automated input and child notices, also the
        // continuation of an automation turn. The reminder starts no turn for them. They run after the last answer.
        // Recovery leaves a held continuation out of the queue, so this fresh reminder queues it (#2659 design D3), and
        // the journal keeps that authority for a recovery after a passivation.
        if (_toolWorkflow.IsChildConsentPaused)
        {
            var (held, free) = SplitByThePause(pending);
            AuthorizeHeldContinuations(held);
            pending = free;
        }
        if (pending.Count == 0)
        {
            AckRestartResumeNoOp(cmd);
            return true;
        }

        // One turn serves one requester. The resume takes the leading inputs of the first requester. The other inputs
        // wait in the queue and run in their own turns, in admission order.
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

        var waiting = pending.Skip(restartContext.ResumeCount).ToArray();
        pending = pending.Take(restartContext.ResumeCount).ToArray();
        _execution.ClearRestartReminderEligibility();
        _input.ActivateRecoveredInput(cmd.Source, firstContext, _trustContextDeriver);
        UpdateCrashContext();
        _toolWorkflow.StartTurn();
        SetSystemPrompt();
        PrepareFreshTurnState();

        if (TryResumeSlashCommand(pending))
        {
            // A rejected slash command keeps the session in Ready. The close of its input then starts the queue
            // (RejectSlashCommand), so the other requesters are queued in each phase (#2587).
            QueueValidWaitingInputs(cmd, waiting);
            return true;
        }

        foreach (var message in _input.ResumePendingInputs(pending))
            _state = _state with { History = _state.History.Add(message) };
        QueueValidWaitingInputs(cmd, waiting);

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

    private void AuthorizeHeldContinuations(IReadOnlyList<InputAdmitted> held)
    {
        var selected = _input.SelectedInputs.Ids;
        var continuations = held.Where(input => SessionInputFlow.IsContinuation(input) && !selected.Contains(input.InputId)).ToArray();
        _input.RestoreWaitingInputs(continuations, _sessionId);
        var authorized = continuations.Where(input => !_authorizedContinuations.Contains(input.InputId))
            .Select(input => new ContinuationAuthorized { SessionId = _sessionId, InputId = input.InputId, AuthorizedAtMs = NowMs() })
            .ToArray();
        if (authorized.Length > 0)
            PersistAll(authorized, committed => _authorizedContinuations.Add(committed.InputId));
    }

    private bool TryResumeSlashCommand(IReadOnlyList<InputAdmitted> pending)
    {
        var first = pending[0];
        if (_input.BeginSlashResume(first) is not { } executableText)
            return false;

        var handled = TryHandleSlashCommand(executableText, first.UserMessage.MediaReferences, replyAck: true);
        return _input.CompleteSlashResume(handled, pending, _sessionId);
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
            new ToolInteractionRequestDispatch(msg)));
        Command<ToolInteractionRequestDispatch>(HandleToolInteractionRequestDispatch);
        Command<SpawnChildActorRequest>(msg => Sender.Tell(Context.ActorOf(msg.Props, msg.ActorName)));
        // Every phase accepts a late reap reply. The epoch gate protects a newer stop attempt.
        Command<JobReapResolved>(HandleJobReapResolved);
        Command<PrepareForDaemonRestart>(RequestRestartDrain);
        Command<ToolPipelineStoppedForRestart>(HandleToolPipelineStoppedForRestart);
        Command<RestartModelGraceExpired>(HandleRestartModelGraceExpired);
        Command<RestartChildWaitExpired>(HandleRestartChildWaitExpired);
        Command<ModelStoppedForRestart>(HandleModelStoppedForRestart);

        Command<WorkingContextSnapshotReady>(HandleWorkingContextSnapshotReady);
        Command<WorkingContextSnapshotCancelled>(msg =>
        {
            if (_modelContext.IsCurrentInspection(msg.Generation))
                TurnLog().Debug("working_context_inspection_cancelled generation={Generation}", msg.Generation);
        });
        Command<WorkingContextSnapshotFailed>(HandleWorkingContextSnapshotFailed);
        Command<WorkingContextSnapshotFatal>(message => throw message.Cause);

        // A stashed cancel or watchdog message can end the rejected turn first, and the buffer can then start
        // the next turn in Processing. The turn id tells the two turns apart, so a new healthy turn stays alive.
        Command<PersistRejectedTurnFailure>(msg =>
        {
            if (_phase.Current == SessionPhase.Processing && _input.Telemetry.TurnId == msg.TurnId)
                FailCurrentTurn(PersistRejectedMessage, msg.Cause, PersistFailureCategory(msg.Cause));
            else
                _log.Info("Skipped the persist-rejected turn failure; the rejected turn {TurnId} already ended", msg.TurnId);
        });

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
            foreach (var closed in _restartWithdrawals)
                cmd.Subscriber.Tell(closed);

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
            // The snapshot drops the approval events of the closed child prompts, so replay cannot rebuild their closes.
            // Forget them here too, so a late click gets the same reply before and after a restart.
            _toolWorkflow.ForgetClosedChildCalls();
            _restartWithdrawals = [];

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
        // Base stops the actor, and the journal has no continuation input, so nothing resumes the stopped turn (#2632).
        if (TakeContinuationWrite(@event) is not null)
            EmitRestartStoppedTurnNotice(ReminderEligibility.None, 0);
        else if (@event is InputAdmitted)
        {
            TryReplyNack("The input journal failed. The session did not start a model call.");
        }
        else if (TurnIsRunning)
        {
            // Base stops the actor, and a stopped actor sends the channel nothing. Tell the channel first.
            // The failed event can be one of several that a tool batch wrote, so the user must check the actions.
            EmitFailedTurnOutputs(PersistStoppedMessage, cause, PersistFailureCategory(cause));
        }
        else
        {
            _log.Warning(cause,
                "Persist failed for {Event} while no turn was active; the session stops and the next message starts a new one",
                @event.GetType().Name);
        }

        base.OnPersistFailure(cause, @event, sequenceNr);
    }

    protected override void OnPersistRejected(Exception cause, object @event, long sequenceNr)
    {
        // The journal keeps no continuation input, so no reminder resumes the stopped turn (#2632). The normal stop then
        // posts the notice, and the drain acknowledges without a reminder. No sender waits for this write.
        if (TakeContinuationWrite(@event) is { } stop)
        {
            _log.Error(cause, "The journal rejected the continuation input of the stopped turn of session {SessionId}.",
                _sessionId.Value);
            stop(0);
            base.OnPersistRejected(cause, @event, sequenceNr);
            return;
        }

        // A rejected park, answer, or resume of a parked child ends that run, never the parent turn
        // (durable-child-consent-wait, design D3). This branch runs before the turn failure below. The person who
        // answered gets a nack, because no resume follows the rejected answer.
        (SubAgentRunId RunId, ChildRejectedEvent Kind)? childEvent = @event switch
        {
            ChildRunEvent.Parked parked => (parked.RunId, ChildRejectedEvent.Parked),
            ChildRunEvent.Resumed resumed => (resumed.RunId, ChildRejectedEvent.Resumed),
            ToolApprovalResolved { SourceChildRunId: { } answered } => (answered, ChildRejectedEvent.Answer),
            _ => null
        };
        if (childEvent is { } rejected)
        {
            _log.Error(cause, "The journal rejected the {Kind} record of child run {RunId}", rejected.Kind, rejected.RunId);
            if (rejected.Kind == ChildRejectedEvent.Answer)
                TryReplyNack(ApprovalNackReasons.PersistFailed);
            Self.Tell(new ChildEventRejected(rejected.RunId, rejected.Kind,
                @event is ChildRunEvent.Parked { Requests.Count: 0 } drainPark ? drainPark.Park : null));
            base.OnPersistRejected(cause, @event, sequenceNr);
            return;
        }

        // Base only logs a rejection; the actor keeps running. A turn that nothing ends would hold
        // the session until the processing watchdog fires, so fail the turn here.
        // A rejected approval notice stops the session, as a persist failure does: the boundary that admitted it cannot
        // continue, and a pause with no notice would hold automated input. Recovery withdraws the child requests,
        // closes the notices, and queues the held input. A close of a waiting input names no selected input: the
        // waiting input stays pending, and the running turn continues.
        if (@event is InputAdmitted notice && IsConsentNotice(notice))
        {
            _log.Error(cause, "The journal rejected approval notice {InputId}. The session stops.", notice.InputId);
            // A stopped actor sends the channel nothing. A turn that stays open tells the person first, as a persist
            // failure does. At the end of a finished turn the session stays quiet.
            if (_noticeAdmissionInOpenTurn)
                EmitFailedTurnOutputs(PersistStoppedMessage, cause, PersistFailureCategory(cause));
            base.OnPersistRejected(cause, @event, sequenceNr);
            Context.Stop(Self);
            return;
        }

        if (@event is InputAdmitted)
            TryReplyNack("The input journal rejected the message. The session did not start a model call.");
        else if (@event is ContinuationAuthorized authorization)
            _log.Error(cause, "The journal rejected the authority of continuation {InputId}. A restart closes it with the notice.",
                authorization.InputId);
        else if (@event is InputClosed { KeepsTurn: true } waitingClose)
            _log.Error(cause, "The journal rejected the close of waiting input(s) {InputIds}", string.Join(",", waitingClose.InputIds));
        else if (_phase.Current == SessionPhase.Processing)
        {
            // A rejected InputClosed cannot close itself: a second close would be rejected the same way.
            if (@event is InputClosed)
                FinishFailedTurn(PersistRejectedMessage, cause, PersistFailureCategory(cause));
            else
            {
                // Akka flushes a Persist call only after a command. A Persist call here would not reach the
                // journal, so the turn failure (which closes the inputs in the journal) runs as a command.
                // Without the close, a replay would run the failed input again.
                Self.Tell(new PersistRejectedTurnFailure(cause, _input.Telemetry.TurnId));
            }
        }
        else
            _log.Warning(cause, "Persist was rejected for {Event} while no turn was active", @event.GetType().Name);

        base.OnPersistRejected(cause, @event, sequenceNr);
    }

    private static bool IsConsentNotice(InputAdmitted admitted)
        => TurnContext.TryFromRecord(admitted.TurnContext, out var context, out _) && context is { IsChildConsentNotice: true };

    private sealed record PersistRejectedTurnFailure(Exception Cause, TurnId? TurnId) : INoSerializationVerificationNeeded;

    private const string PersistStoppedMessage =
        "The session could not save its progress and stopped. Some actions may already have run. Check them before you send your message again.";

    private const string PersistRejectedMessage =
        "The session could not save its progress, so this turn failed. Some actions may already have run. Check them before you send your message again.";

    private static ErrorCategory PersistFailureCategory(Exception cause)
        => cause is TimeoutException ? ErrorCategory.Timeout : ErrorCategory.Unknown;

    // The first tool batch commit clears the selected inputs, so a selection does not show that a turn runs.
    // The stop path loses any running turn, whichever event failed, so Processing and Compacting both count.
    private bool TurnIsRunning => _phase.Current is SessionPhase.Processing or SessionPhase.Compacting;

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

        // The stop note joins the per-turn notice. Only the first call of a turn inspects the context and writes that
        // block into history, so the note is taken only by that call, and later calls of the turn keep one copy.
        var stopNote = _turnStopNote ? SessionModelContext.AutomationStopNote : null;
        var turnNotice = stopNote is null || _turnRestartNotice is null
            ? stopNote ?? _turnRestartNotice
            : $"{_turnRestartNotice}\n\n{stopNote}";
        var call = _modelContext.StartCall(
            callId,
            _state,
            _input.Authority.Context,
            _input.Authority.Source,
            _input.Authority.Trust,
            recallQuery,
            slashCommandSkillContent,
            turnNotice,
            modelToken);
        if (call is ContextCall.Inspect)
            _turnStopNote = false;
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
        // A drain request arrived while the session prepared the call. The call does not start.
        if (RestartDrainRequested)
        {
            _execution.ReleaseAll();
            StopTurnForRestart();
            return;
        }

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
            message, _state, CurrentTurnAudience(), _input.Authority.Trust);
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
            message, _state, CurrentTurnAudience(), _input.Authority.Trust);
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
    private bool TryHandleSlashCommand(string userContent, IReadOnlyList<SerializableMediaReference> mediaRefs, bool replyAck)
    {
        if (_skillRegistry is null || !SessionInputFlow.IsSlashCommand(userContent))
            return false;

        if (_skillRegistry.TryResolveSlashCommand(userContent, out var skill, out var remainder))
        {
            var decision = SkillActivationRouter.Resolve(skill!);
            if (decision.IsError)
                return RejectSlashCommand(decision.ErrorMessage!, replyAck);

            if (decision.Path == SkillActivationPath.Routed)
                return TryHandleRoutedSlashCommand(skill!, remainder, mediaRefs, decision.RoutedSubagent!, replyAck);

            return HandleInlineSlashCommand(skill!, remainder, mediaRefs, replyAck);
        }

        // Unrecognized slash command — send deterministic error
        var commands = _skillRegistry.GetAvailableSlashCommands();
        var errorMsg = $"Unknown command: {userContent.Split(' ')[0]}\n\nAvailable commands:\n";
        foreach (var (cmd, hint) in commands)
        {
            errorMsg += hint is not null ? $"  {cmd} {hint}\n" : $"  {cmd}\n";
        }

        return RejectSlashCommand(errorMsg.TrimEnd(), replyAck);
    }

    private bool RejectSlashCommand(string message, bool replyAck)
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
            if (replyAck)
                TryReplyAck();
            // A rejected command ends no turn, so no turn end starts the queue (#2587). Two paths leave queued input
            // here in Ready: a restart resume queues the inputs behind the slash command, and a live command closes
            // a parked approval (AbandonThenActivate) while a fire waits behind it. Start the next queued input as a
            // turn end does, and admit a ready consent notice first.
            AdmitConsentNoticeThen(inOpenTurn: false, StartWaitingInputAfterApproval);
        }

        CloseActiveInputs(Reply);
        return true;
    }

    private bool HandleInlineSlashCommand(SkillEntry skill, string remainder, IReadOnlyList<SerializableMediaReference> mediaRefs, bool replyAck)
    {
        if (skill.Source is not FileSkillSource fileSource)
            return RejectSlashCommand($"Skill /{skill.Name} cannot use file-based slash dispatch.", replyAck);

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
            return RejectSlashCommand($"Failed to load skill /{skill.Name}: {ex.Message}\n\nThe skill file may be missing or corrupted.", replyAck);
        }

        _sessionMetrics?.RecordSkillLoaded(skill.Name, SkillLoadMethod.SlashCommand);

        var effectiveUserContent = string.IsNullOrWhiteSpace(remainder)
            ? $"The user invoked /{skill.Name}. Follow the skill instructions."
            : remainder;

        _state = _state.AddUserMessage(effectiveUserContent, mediaRefs.Count > 0 ? mediaRefs : null);
        if (replyAck)
            TryReplyAck();
        _modelContext.ResetForNewTurn();

        FireInitialTurnLlmCall(effectiveUserContent, skillBody);

        TransitionTo(SessionPhase.Processing);
        return true;
    }

    private bool TryHandleRoutedSlashCommand(SkillEntry skill, string remainder, IReadOnlyList<SerializableMediaReference> mediaRefs, string routedSubagent, bool replyAck)
    {
        if (skill.Source is not FileSkillSource fileSource)
            return RejectSlashCommand($"Skill /{skill.Name} cannot use file-based routed dispatch.", replyAck);

        if (_subAgentRegistry is null || _subAgentSpawner is null)
            return RejectSlashCommand($"Skill '/{skill.Name}' routes to subagent '{routedSubagent}', but subagent routing is not available in this runtime.", replyAck);

        _subAgentLoader?.SyncInto(_subAgentRegistry);

        var profile = _subAgentRegistry.TryGetByName(routedSubagent);
        if (profile is null)
            return RejectSlashCommand(SkillActivationRouter.UnknownTargetError(skill.Name, routedSubagent), replyAck);

        if (profile.Visibility != SubAgentVisibility.UserFacing)
            return RejectSlashCommand(SkillActivationRouter.InternalTargetError(skill.Name, routedSubagent), replyAck);

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
            return RejectSlashCommand($"Failed to load skill /{skill.Name}: {ex.Message}\n\nThe skill file may be missing or corrupted.", replyAck);
        }

        // The routed child gets the posture-narrowed audience, as a child that spawn_agent starts does.
        if (_input.Authority.Trust?.EffectiveAudience is not { } routedAudience)
        {
            _log.Error(
                "Routed slash command /{SkillName} refused reason={Reason}: the session has no effective trust context",
                skill.Name,
                SecurityPolicyDefaults.AudienceUnresolvedReason);
            return RejectSlashCommand(
                $"Skill '/{skill.Name}' cannot run: the session cannot resolve the audience for this turn ({SecurityPolicyDefaults.AudienceUnresolvedReason}).", replyAck);
        }

        _sessionMetrics?.RecordSkillLoaded(skill.Name, SkillLoadMethod.SlashCommand);

        var effectiveTask = string.IsNullOrWhiteSpace(remainder)
            ? $"The user invoked /{skill.Name}. Execute the routed skill instructions and return a concise result."
            : remainder;

        _state = _state.AddUserMessage(effectiveTask, mediaRefs.Count > 0 ? mediaRefs : null);
        if (replyAck)
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
            {
                if (request is PreparedChildStart prepared)
                {
                    // The task file exists before the owner accepts the start.
                    await prepared.WriteTaskFileAsync(originalContext, ct);
                    return await self.Ask<object>(
                        new AcceptChildStart(prepared, startKey, originalContext, $"/{skill.Name}", ct),
                        timeout: _config.ToolExecutionTimeout,
                        cancellationToken: ct);
                }

                return await self.Ask<object>(
                    new SpawnChildActorRequest((Props)request, name),
                    timeout: _config.ToolExecutionTimeout,
                    cancellationToken: ct);
            };
            var outputs = new ToolExecutionOutputs(info =>
            {
                self.Tell(new RoutedSkillSubAgentActivity(
                    _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    new AgentName(info.AgentName),
                    info.IsStarted ? SubAgentPhase.Started : SubAgentPhase.Completed,
                    info.ToolCount,
                    info.Success,
                    info.Duration,
                    info.Findings.Count), ActorRefs.NoSender);
            });
            var context = new ToolExecutionContext(new ToolRunScope
            {
                Session = new ToolSessionScope.Bound(_sessionId.Value, _sessionStorage),
                // The caller resolved this audience from the effective trust context and
                // refused the command when no trust context existed.
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

            self.Tell(new RoutedSkillExecutionCompleted(skill.Name, profile.Name, startKey, result), ActorRefs.NoSender);
        }
        catch (Exception ex)
        {
            self.Tell(new RoutedSkillExecutionFailed(skill.Name, profile.Name, startKey, ex.Message), ActorRefs.NoSender);
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
            EligibleDeliveryTurnRoutedSkill = _deliveryRetry.EligibleTurnIsRoutedSkill,
            AuthorizedContinuationInputIds = [.. _state.PendingInputs.Select(static input => input.InputId)
                .Where(_authorizedContinuations.Contains)]
        };
    }

    private void ApplyToolBatchStarted(ToolBatchStarted evt)
        => _state = _toolWorkflow.ApplyBatchStarted(_state, evt);

    private void ApplyToolCallRecorded(ToolCallRecorded evt)
    {
        _state = _toolWorkflow.ApplyRecordedResult(_state, evt.ToolResult);
        TrackStartedBackgroundJob(evt.StartedBackgroundJob);
    }

    // Only a call of the session sends a dispatch here. The owner of a park writes its child requests directly.
    private void HandleToolInteractionRequestDispatch(ToolInteractionRequestDispatch dispatch)
        => Persist(CreateApprovalRequest(dispatch), e => ApprovalRequestCommitted(e, PromptOf(dispatch)));

    // The journal holds the request: a parent request record, or a request inside a Parked record.
    private void ApprovalRequestCommitted(ToolApprovalRequested request, ToolInteractionRequest prompt)
    {
        ApplyToolApprovalRequested(request);
        EmitOutput(prompt);
        TryStopDurableApprovalWaits();
        if (request.SourceChildRunId is { } requestingRun)
            ChildApprovalRequested(_state.ChildRuns[requestingRun], prompt);
    }

    // A parent prompt ends with its turn. A child prompt lives until an answer or a close, because the child keeps
    // waiting after the parent turn ends or fails (#2523).
    private static ToolInteractionRequest PromptOf(ToolInteractionRequestDispatch dispatch)
        => dispatch.SourceChildRunId is null
            ? dispatch.Request
            : dispatch.Request with { Lifetime = ToolInteractionLifetime.UntilClosed };

    private ToolApprovalRequested CreateApprovalRequest(ToolInteractionRequestDispatch dispatch)
        => _toolWorkflow.CreateApprovalRequest(dispatch, _sessionId, _input.Authority.Context, _input.Authority.Source,
            CurrentTurnAudience(), NowMs());

    private void ApplyToolApprovalRequested(ToolApprovalRequested evt)
    {
        var pending = _toolWorkflow.ApplyApprovalRequested(
            evt,
            recovered: _phase.Current == SessionPhase.Recovering);

        // Replay re-applies requests that a later record may answer, cancel, or lose. RecoveryCompleted logs the
        // approvals that stay open.
        if (_phase.Current != SessionPhase.Recovering)
            LogToolApprovalRequested(pending.AuthorizationAttemptId.Value, evt.CallId, evt.ToolName, recovered: false);

        if (evt.SourceChildRunId is null && pending.TurnContext is { } turnContext)
            _input.SetTurnContext(turnContext);
        else if (pending.TurnContextRestoreFailure is { } restoreFailure)
            _log.Error(
                "Approval request {CallId} could not restore turn context: {Reason}",
                evt.CallId,
                restoreFailure);
    }

    private void LogToolApprovalRequested(string authorizationAttemptId, string callId, string toolName, bool recovered)
        => _log.Info(
            "Tool approval requested authorizationAttemptId={AuthorizationAttemptId} " +
            "sessionId={SessionId} callId={CallId} toolName={ToolName} recovered={Recovered}",
            authorizationAttemptId,
            _sessionId.Value,
            callId,
            toolName,
            recovered);

    private void ClearApprovalTurnState()
    {
        _toolWorkflow.ClearApprovalTurn();
        _input.ClearTurnContext();
    }

    // A parked run keeps the answer in its run record. A parent answer changes only the workflow.
    private void ApplyToolApprovalResolved(ToolApprovalResolved evt)
    {
        _state = _state.Apply(evt);
        _toolWorkflow.ApplyResolvedResponse(evt);
    }

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

    private void EmitApprovalNotice(string text)
        => EmitOutput(new TextOutput(text) { SessionId = _sessionId }, OutputFilter.Text);

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
            case ApprovalNoticeKind.Withdrawn:
                EmitApprovalNotice(ChildApprovalPrompt.WithdrawnText(rejection.Withdrawal
                    ?? throw new InvalidOperationException("A withdrawn approval rejection has no reason.")));
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
            case ApprovalNackReasons.UseButtons:
                var openCount = _toolWorkflow.AnswerablePrompts(msg.SenderId).Count;
                _log.Warning("Ignoring text tool interaction response from {SenderId}: {OpenCount} prompts are open",
                    msg.SenderId, openCount);
                EmitApprovalNotice(ChildApprovalPrompt.UseButtonsText(openCount));
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
        // A child answer has no live wait and never belongs to the parent turn (design D3).
        if (_toolWorkflow.IsChildApproval(msg.CallId))
        {
            await HandleToolInteractionResponseWhenIdle(msg);
            return;
        }
        var response = _toolWorkflow.PrepareLiveResponse(msg, _state.History);
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
            var resolution = _toolWorkflow.CreateResolution(accepted, _sessionId, NowMs());
            Persist(resolution, evt =>
            {
                _toolWorkflow.ApplyResolvedResponse(evt, accepted);
                TryReplyAck();
            });
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
            ApplyToolApprovalResolved(evt);
            if (evt.SourceChildRunId is { } answeredRun)
            {
                TryReplyAck();
                IReadOnlyList<AgentEvent.ChildApprovalResolved> closed =
                    [_toolWorkflow.CreateChildResolution(evt, _state.ChildRuns[answeredRun], _sessionId, _timeProvider.GetUtcNow())];
                EmitClosedPrompts(closed);
                ChildApprovalsClosed(closed);
                TryResumeParkedRun(answeredRun);
                return;
            }
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
                    _input.RestoreTurnAuthority(dispatch.TurnContext, _trustContextDeriver);
                    UpdateCrashContext();
                    TransitionTo(SessionPhase.Processing);
                    DispatchToolBatch(dispatch.Calls, new HashSet<string>(), repeatNudge: null, approvalPlan: dispatch.ApprovalPlan,
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

        var next = _input.AdvanceAfterTurn(RestartDrainRequested, _toolWorkflow.IsChildConsentPaused);
        if (next.Action == InputProgressAction.Restart)
        {
            RecordRestartReminderEligibility();
            ClearBufferedMessagesForRestartDrain();
            TransitionTo(SessionPhase.Passivating);
            return;
        }

        if (next.Batch is { DispatchesCommand: true } command)
        {
            DispatchBufferedCommand(command);
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

    // A buffered slash command never joins a turn (#2591). The session goes to Ready, and the command runs on the
    // live command path. A rejected command then starts the next waiting input (RejectSlashCommand).
    private void DispatchBufferedCommand(BufferedInputBatch batch)
    {
        if (_phase.Current != SessionPhase.Ready)
        {
            ClearApprovalTurnState();
            TransitionTo(SessionPhase.Ready);
        }

        SettleStopNote(batch);
        TurnLog().Info("turn_buffer_command input={InputId}", batch.Messages[0].AdmittedInputId);
        // Admission already sent the acknowledgement. Keep this decision through any input-close callback.
        ContinueIncomingUserMessage(batch.Messages[0], replyAck: false);
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

    // A tool batch clears the selected inputs. A background job result turn that ends after a tool batch still writes
    // the close, because the close carries the job ID. The journal then closes the job, as TurnRecorded does.
    private void CloseActiveInputs(Action onClosed)
    {
        if (_input.SelectedCount == 0 && _input.SelectedInputs.BackgroundJobId is null)
        {
            onClosed();
            return;
        }

        var closed = new InputClosed
        {
            SessionId = _sessionId,
            InputIds = _input.SelectedInputs.Ids,
            SourceBackgroundJobId = _input.SelectedInputs.BackgroundJobId,
            ClosedAtMs = NowMs()
        };
        _ = _state.Apply(closed);
        Persist(closed, evt =>
        {
            ApplyInputClosed(evt);
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

        EmitFailedTurnOutputs(errorMessage, cause, category);

        DrainBufferedMessagesOrBecomeReady();
    }

    private void EmitFailedTurnOutputs(string errorMessage, Exception cause, ErrorCategory category)
    {
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
            WithoutChildRunFiles(result.Receipt));
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

    // A child result file is the channel of a notice, not work of the user. Listing it in the working context would put
    // the path of a stale result into every later prompt.
    private ToolInvocationReceipt? WithoutChildRunFiles(ToolInvocationReceipt? receipt)
        => receipt is ToolInvocationReceipt.Succeeded success
           && success.FileActivity.Any(activity => _sessionStorage.IsChildRunFile(activity.CanonicalPath))
            ? new ToolInvocationReceipt.Succeeded(
                [.. success.FileActivity.Where(activity => !_sessionStorage.IsChildRunFile(activity.CanonicalPath))],
                success.DeclaredProjectDirectory)
            : receipt;

    private void TryCompleteStreamedToolBatch()
    {
        if (_toolWorkflow.CompleteIfReady(
                _state,
                _turnState) is not BatchCompletion.Ready completion)
            return;

        _state = completion.State;
        // Tool-batch end: a ready approval notice is admitted first. It waits for its own turn and never joins this one.
        AdmitConsentNoticeThen(inOpenTurn: true, () => ContinueAfterToolBatch(completion));
    }

    private void ContinueAfterToolBatch(BatchCompletion.Ready completion)
    {
        // A drain request arrived during the tool batch. The turn stops at this batch boundary and starts no model
        // call, so that a long tool loop cannot hold the drain until the coordinator deadline (#2601).
        if (RestartDrainRequested)
        {
            _toolWorkflow.ClearNormalBatch();
            _execution.ClearToolTracking();
            TurnLog().Info("turn_restart_stop_at_tool_batch iteration={Iteration}", _turnState.ToolIterationCount);
            StopTurnForRestart();
            return;
        }

        var advance = _input.AdvanceAfterToolBatch(completion, ShouldCompact(), _toolWorkflow.IsChildConsentPaused);
        if (advance.Batch is { } batch)
        {
            TurnLog().Info("turn_mid_loop_buffer_drain count={BufferCount} iteration={Iteration}",
                batch.Messages.Count, _turnState.ToolIterationCount);
            ApplyBufferedBatch(batch);
        }

        // A person's input waits behind this automation turn. The turn ends quietly, with no further model call, and
        // the person's input then starts its own turn (SessionInputFlow.AdvanceAfterToolBatch).
        if (advance.Next == ToolBatchNextAction.EndTurn)
        {
            TurnLog().Warning("turn_person_input_end_at_tool_batch iteration={Iteration} reminder={ReminderId} job={BackgroundJobId}",
                _turnState.ToolIterationCount, _input.Authority.Source?.ReminderId?.Value, _input.Authority.Source?.BackgroundJobId?.Value);
            // A compaction that is due at this batch commits before the person's first model call, so that call cannot
            // overflow. The compaction end then drains with no tool-loop resume.
            EndAutomationTurnForPerson(() =>
            {
                if (!ShouldCompact())
                {
                    DrainBufferedMessagesOrBecomeReady();
                    return;
                }

                _log.Info("Compaction threshold reached at a person stop ({InputTokens} tokens >= {Threshold} limit), starting compaction",
                    _lastInputTokenCount, _model.CompactionTokenLimit(_config.Tuning.CompactionThreshold));
                Self.Tell(new CompactionTriggered(_lastInputTokenCount));
                TransitionTo(SessionPhase.Compacting);
            });
            return;
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

    private void RequestRestartDrain(PrepareForDaemonRestart request)
    {
        var action = _execution.RequestRestart(
            Sender,
            request.Reason,
            _phase.Current,
            _input.HasBufferedContinuation,
            _toolWorkflow.CanParkForRestart());
        if (action == RestartAction.EnterPassivation)
        {
            // A ready session has no turn to resume. A child run can still owe it a result, and only the restart
            // reminder wakes the session after the restart so that the recovery can report that child.
            _execution.MarkRestartReminderEligible(0, UnreportedRuns().Count(), false, 0, _timeProvider.GetUtcNow());
            TransitionTo(SessionPhase.Passivating);
        }
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

    // The child wait ended with a live child. The session acknowledges the drain, PostStop stops the child, and its run
    // stays Accepted, so recovery records it as lost (owner_restarted).
    private void HandleRestartChildWaitExpired(RestartChildWaitExpired _)
    {
        _execution.EndChildWait();
        if (!_execution.HasLiveChildren)
            return;
        _log.Warning("Restart drain child_wait_ended live_children={LiveChildren}", _execution.LiveChildCount);
        if (_phase.Current == SessionPhase.Passivating)
            FinalizePassivation();
    }

    private void HandleRestartModelGraceExpired(RestartModelGraceExpired expired)
    {
        if (_execution.PlanRestartModelStop(expired, _phase.Current) is not { } callId)
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
            CloseActiveInputs(StopTurnForRestart);
        else
            StopTurnForRestart();
    }

    // The drain stopped the turn before its end. The turn resumes only when a reminder replays its own pending inputs.
    private void StopTurnForRestart() => KeepStoppedTurnThen(stopsTurn: true, pendingTurnInputs =>
    {
        EmitRestartStoppedTurnNotice(RecordRestartReminderEligibility(), pendingTurnInputs);
        ClearBufferedMessagesForRestartDrain();
        TransitionTo(SessionPhase.Passivating);
    });

    // A tool batch or a text close clears the selected inputs, so the count is read before the eligibility record
    // (#2601). After a tool batch, an eligible turn gets one continuation input in the journal before the drain goes on
    // (#2632). The input has no text and carries the recorded authority of the turn, never a wider one.
    private void KeepStoppedTurnThen(bool stopsTurn, Action<int> stop)
    {
        var selected = _input.SelectedCount;
        if (!stopsTurn || _input.Authority.Context is not { } context
            || !SessionExecutionLifetime.ContinuesStoppedTurn(
                _input.EmittedText,
                _state.GetExecutablePendingInputs().Count,
                _input.StartsWithReplay,
                HasLiveBackgroundJobs,
                ParkedToolBatchHistory.FindRedrivableAssistantMessage(_state.History, null) is not null,
                context.ChannelType is not null))
        {
            stop(selected);
            return;
        }

        var continuation = new InputAdmitted
        {
            SessionId = _sessionId,
            InputId = InputId.New(),
            UserMessage = new SerializableChatMessage { Role = Protocol.ChatRole.User },
            // As normal admission does (SessionInputFlow.PrepareAdmission): persist a concrete array.
            TurnContext = context.ToRecord() with { AdoptedSpeakerIds = context.AdoptedSpeakerIds.ToArray() },
            ContinuesStoppedTurn = true,
            AdmittedAtMs = NowMs()
        };
        _continuationWrite = (continuation, stop);
        Persist(continuation, admitted =>
        {
            _continuationWrite = null;
            _state = _state.Apply(admitted);
            stop(1);
        });
    }

    private Action<int>? TakeContinuationWrite(object @event)
    {
        if (_continuationWrite is not { } write || !ReferenceEquals(write.Event, @event))
            return null;

        _continuationWrite = null;
        return write.Stop;
    }

    private void EmitRestartStoppedTurnNotice(ReminderEligibility eligibility, int pendingTurnInputCount)
    {
        if (SessionExecutionLifetime.RestartStoppedTurnNotice(
                eligibility, pendingTurnInputCount, _execution.RestartDrainReason) is not { } notice)
            return;

        _log.Info("Restart drain stopped the turn of session {SessionId}, and no restart reminder resumes that turn.",
            _sessionId.Value);
        EmitOutput(new TextOutput(notice) { SessionId = _sessionId }, OutputFilter.Text);
    }

    private ReminderEligibility RecordRestartReminderEligibility()
    {
        var eligibility = _execution.MarkRestartReminderEligible(
            _state.GetExecutablePendingInputs().Count,
            UnreportedRuns().Count(),
            _input.EmittedText,
            _input.SelectedCount,
            _timeProvider.GetUtcNow());
        if (eligibility == ReminderEligibility.EmittedText)
        {
            _log.Warning(
                "Restart drain cannot resume session {SessionId} because the interrupted turn emitted text.",
                _sessionId.Value);
        }

        return eligibility;
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
        // The parked approval is durable, but a child run can still owe its result, and an input can wait behind the
        // approval. Only the restart reminder wakes the session after the restart.
        RecordRestartReminderEligibility();
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
