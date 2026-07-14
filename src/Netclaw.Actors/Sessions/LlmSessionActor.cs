// -----------------------------------------------------------------------
// <copyright file="LlmSessionActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Persistence;
using Netclaw.Actors.Hosting;
using Microsoft.Extensions.AI;
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
/// Conversation state is held in an immutable <see cref="SessionState"/> record.
/// The actor owns only transient concerns: subscribers, message buffer, and behavior.
///
/// Uses three command behaviors:
/// - Ready: accepts user messages and fires async LLM call
/// - Processing: buffers incoming messages while LLM call is in flight
/// - Compacting: runs tiered context compaction when usage exceeds threshold
/// </summary>
public sealed class LlmSessionActor : ReceivePersistentActor, IWithTimers
{
    private readonly SessionId _sessionId;
    private readonly IChatClient _chatClient;
    private readonly IChatClient _compactionClient;
    private readonly ModelCapabilities _model;
    private readonly SessionConfig _config;
    private readonly ISystemPromptProvider _promptProvider;
    private readonly IReadOnlyList<IContextLayerProvider> _contextLayers;
    private readonly IWorkingContextSnapshotProvider _workingContextSnapshots;
    private readonly IToolExecutor? _toolExecutor;
    private readonly SessionToolExecutionPipeline? _toolExecutionPipeline;
    private readonly Tools.ToolRegistry? _toolRegistry;
    private readonly IToolApprovalService? _approvalService;
    private readonly ApprovalChannel _approvalChannel = new();
    private readonly IMemoryExtractor _memoryExtractor;
    private readonly IMemoryRecallCoordinator _memoryRecallCoordinator;
    private readonly IMemoryCheckpointSink _memoryCheckpointSink;
    private readonly MemoryProposalGate _memoryProposalGate = new();
    private readonly MemoryConfig _memoryConfig;
    private readonly TimeProvider _timeProvider;
    private readonly string _sessionsBasePath;
    private readonly ISessionLifecycleObserver? _lifecycleObserver;
    private readonly Memory.SQLiteMemoryStore? _memoryStore;
    private readonly IChatClientProvider _clientProvider;
    private readonly ILoggingAdapter _log;

    // Transient state (not persisted)
    private readonly List<SendUserMessage> _buffer = [];
    // In-flight reminder/background-job dedup (transient; rebuilt from journal on recovery).
    private readonly InFlightTurnDedup _inFlightDedup = new();
    private readonly SessionSubscriberManager _subscribers = new();
    private readonly Dictionary<string, PendingToolInteraction> _pendingToolInteractions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedToolApproval> _resolvedToolApprovals = new(StringComparer.Ordinal);
    // Live-only coordination for the currently executing streamed tool batch.
    // Durable recovery derives unanswered calls from _state.History.
    private readonly ActiveToolBatchTracker _activeToolBatch = new();
    // Media loaded by tools for model-visible inspection during a streamed tool
    // batch; drained into a system nudge when the batch completes.
    private readonly ModelInputMediaBuffer _mediaBuffer = new();
    private MessageSource? _currentTurnSource;
    private TurnContext? _currentTurnContext;
    private bool _processingStateActive;
    private ApprovalTurnState _approvalTurnState = ApprovalTurnState.None;
    private readonly ToolRegistry? _fullRegistry;
    private readonly ToolAccessPolicy? _toolAccessPolicy;
    private readonly TrustContextDeriver? _trustContextDeriver;
    // Owns the exposed tool list (base + discovered) and lease-based eviction.
    private readonly DiscoveredToolCache _discoveredToolCache = new();

    // Last observed input token count from LLM response (for compaction trigger)
    private long _lastInputTokenCount;

    // When compaction triggers mid-tool-loop, the turn is still in-progress.
    // After compaction completes, we need to fire a follow-up LLM call to
    // continue the turn instead of transitioning to Ready. See #424.
    private bool _resumeToolLoopAfterCompaction;

    // A tool-approval feedback command that arrived while the session was
    // Compacting.
    // Re-driving a tool batch mid-compaction is unsafe — compaction rewrites
    // _state.History — so the response is buffered here and replayed via
    // Self.Tell once compaction finishes and the phase transition has run.
    private IWithSessionId? _deferredApprovalResponse;

    // Per-turn transient counters (tool budget, duplicate detection, empty-response retries)
    private readonly TurnStateTracker _turnState = new();

    private const string ToolBudgetExhaustedMessage =
        "I used all available tool iterations for this turn and couldn't produce a final summary. "
        + "You can ask me to summarize what was done, or rephrase your request.";

    // Delivery retry handler (eligibility tracking, retry counting, nudge builders)
    private readonly DeliveryRetryHandler _deliveryRetry = new();

    // Reference to the singleton SessionLogDispatcher; resolved lazily on
    // recovery completion. The dispatcher owns one SessionLogActor child per
    // session id and is the single writer per session.log file. Audit messages
    // (SendUserMessage, SessionOutput) are forwarded through it.
    private IActorRef? _logActor;

    // Child actor for per-session memory curation (evaluate-before-write pipeline)
    private IActorRef? _curationActor;

    // Child actor for session-level memory observation (distills transcript on idle)
    private IActorRef? _observerActor;

    // Processing watchdog (timer management for stuck operations)
    private readonly ProcessingWatchdog _watchdog = new();

    // Actor-owned CTS for the active LLM call. Cancelled by the watchdog on timeout
    // or when a response/failure arrives. The session-level watchdog (FirstTokenTimeout)
    // is the authoritative timeout — this CTS just propagates cancellation to the
    // HTTP layer so timed-out connections are released.
    private CancellationTokenSource? _activeLlmCts;

    // Actor-owned CTS for active tool execution. Cancels direct approval waits
    // and tool calls when the session stops, restarts, or fails the turn.
    private CancellationTokenSource? _activeToolExecutionCts;

    // Correlation ID for the active LLM call. Incremented in FireLlmCall.
    // Stale LlmResponseReceived/LlmCallFailed/LlmResponseDeltaReceived messages
    // from cancelled calls are ignored when their CallId doesn't match.
    private long _activeCallId;

    // Correlates asynchronous working-context inspection with the call that
    // requested it. Every call advances the generation so a late snapshot can
    // never enter a newer turn or tool-loop continuation.
    private long _workingContextGeneration;

    // Tracks whether any content was streamed this call — selects the watchdog timeout
    // (the generous prefill budget before the first token vs the tighter inter-delta
    // budget after).
    private bool _anyContentStreamed;

    // Per-turn diagnostic correlation (ephemeral)
    private Protocol.TurnId? _activeTurnId;
    private string? _activeMessageId;
    private Channels.ChannelType? _activeChannelType;
    private AutomaticRecallResult? _activeRecall;
    private EffectiveTrustContext? _currentTrustContext;

    // Startup context layers: injected on first LLM call, re-injected after compaction
    private bool _startupContextInjected;

    // Guards against infinite compaction loops: if a post-compaction buffer drain
    // overflows again, fail the turn. Reset at the start of each new user turn.
    private int _compactionOverflowRetryCount;

    // Skill registry for slash-command dispatch
    private readonly Skills.SkillRegistry? _skillRegistry;
    private readonly SubAgentDefinitionRegistry? _subAgentRegistry;
    private readonly SubAgentSpawner? _subAgentSpawner;
    private readonly FileSubAgentDefinitionLoader? _subAgentLoader;

    // Memory recall state (transient — reset at turn boundaries and compaction)
    private readonly SessionRecallManager _recallManager = new();

    private readonly Telemetry.ISessionMetrics? _sessionMetrics;

    private bool _restartDrainRequested;
    private bool _passivationCompleted;
    private bool _passivationFinalStopScheduled;

    // Reap-on-passivation handshake: while a KillJobsForSession ask is in
    // flight, the final snapshot is deferred so it captures the reaped marks.
    // _jobReapEpoch is bumped per reap request so a late reply from a
    // superseded passivation (aborted, then re-entered) cannot resolve a newer
    // handshake — see JobReapResolved.
    private bool _jobReapPending;
    private bool _passivationDeferredForReap;
    private long _jobReapEpoch;
    private IActorRef? _restartDrainReplyTo;
    private string? _pendingRestartNotice;
    private string? _turnRestartNotice;

    // Persistent state (immutable — replaced on each event)
    private SessionState _state = SessionState.Empty;

    // Explicit state machine phase (metadata + validation layer over Become())
    private readonly SessionPhaseMachine _phase = new();

    public override string PersistenceId { get; }
    public ITimerScheduler Timers { get; set; } = null!;

    public LlmSessionActor(
        string entityId,
        ModelCapabilities modelCapabilities,
        SessionConfig config,
        SessionServices services,
        SessionToolServices? tools = null,
        SessionMemoryServices? memory = null,
        SessionObservability? observability = null)
    {
        _sessionId = new SessionId(entityId);
        _sessionMetrics = observability?.Metrics;
        _lifecycleObserver = observability?.LifecycleObserver;
        _clientProvider = services.ClientProvider;
        _chatClient = services.ClientProvider.GetClient(ModelRole.Main);
        _compactionClient = modelCapabilities.CompactionModelId is not null
            ? services.ClientProvider.GetClient(ModelRole.Compaction)
            : _chatClient;
        _model = modelCapabilities;
        _config = config;
        _promptProvider = services.PromptProvider;
        _contextLayers = services.ContextLayers;
        _workingContextSnapshots = services.WorkingContextSnapshots;
        _skillRegistry = tools?.SkillRegistry;
        _subAgentRegistry = tools?.SubAgentRegistry;
        _subAgentSpawner = tools?.SubAgentSpawner;
        _subAgentLoader = tools?.SubAgentLoader;
        _toolExecutor = tools?.ToolExecutor;
        _toolRegistry = tools?.ToolRegistry;
        _toolAccessPolicy = tools?.AccessPolicy;
        _approvalService = tools?.ApprovalService;
        _memoryExtractor = memory?.MemoryExtractor ?? NullMemoryExtractor.Instance;
        _memoryRecallCoordinator = memory?.RecallCoordinator ?? NullMemoryRecallCoordinator.Instance;
        _memoryCheckpointSink = memory?.CheckpointSink ?? NullMemoryCheckpointSink.Instance;
        _memoryStore = memory?.MemoryStore;
        _memoryConfig = memory?.MemoryConfig ?? new MemoryConfig();
        _timeProvider = services.TimeProvider;
        _sessionsBasePath = services.Paths.SessionsDirectory;
        _trustContextDeriver = tools?.TrustDeriver;
        PersistenceId = $"session-{entityId}";

        // Enrich logger with session context — all log messages automatically include SessionId
        _log = Context.GetLogger().WithContext(NetclawLogProperties.SessionId, _sessionId.Value);
        _toolExecutionPipeline = tools is null
            ? null
            : new SessionToolExecutionPipeline(
                tools.ToolExecutor,
                tools.AuditLogger,
                services.TimeProvider,
                NoLogger.Instance);

        // Load all non-MCP tools for initial LLM calls.
        // MCP tools are loaded dynamically via search_tools and can be retained for a
        // small number of future turns (configurable lease) to reduce rediscovery churn.
        _fullRegistry = tools?.ToolRegistry;
        if (_fullRegistry is not null)
        {
            _discoveredToolCache.SeedBaseTools(_fullRegistry.GetAlwaysLoadedTools());
        }

        // ── Recovery handlers ──
        Recover<TurnRecorded>(evt =>
        {
            ApplyTurnRecorded(evt);
            _pendingToolInteractions.Clear();
            _resolvedToolApprovals.Clear();
            ClearApprovalTurnState();
            ClearActiveToolBatchTracking();
        });
        Recover<SessionTitleSet>(evt => _state = _state.Apply(evt));
        Recover<SessionBackgroundJobsReaped>(evt => _state = _state.Apply(evt));
        Recover<SessionCompacted>(evt =>
        {
            _state = _state.Apply(evt);
            _pendingToolInteractions.Clear();
            _resolvedToolApprovals.Clear();
            ClearApprovalTurnState();
            ClearActiveToolBatchTracking();
        });
        Recover<ToolBatchStarted>(ApplyToolBatchStarted);
        Recover<ToolCallRecorded>(ApplyToolCallRecorded);
        Recover<ToolApprovalRequested>(ApplyToolApprovalRequested);
        Recover<ToolApprovalResolved>(ApplyToolApprovalResolved);
        Recover<ToolBatchAbandoned>(ApplyToolBatchAbandoned);
        Recover<SnapshotOffer>(offer =>
        {
            if (offer.Snapshot is SessionSnapshot snapshot)
            {
                _state = SessionState.FromSnapshot(snapshot);
                if (snapshot.EligibleDeliveryTurnNumber is { } eligibleTurn)
                    _deliveryRetry.MarkEligible(eligibleTurn);

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

            // Resolve once at recovery time. ActorRegistry returns Nobody if
            // the dispatcher hasn't been registered (unit-test scenarios that
            // skip the hosting wiring) — leave _logActor null in that case.
            var registry = ActorRegistry.For(Context.System);
            if (registry.TryGet<SessionLogDispatcherActorKey>(out var dispatcher))
                _logActor = dispatcher;

            if (_memoryStore is not null)
            {
                _curationActor = Context.ActorOf(
                    Memory.MemoryCurationActor.CreateProps(_memoryStore, _sessionId, _clientProvider),
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

        EmitProcessingStateForPhase(target);

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

    private void EmitProcessingStateForPhase(SessionPhase phase)
    {
        var isProcessing = phase is SessionPhase.Processing or SessionPhase.Compacting;
        if (_processingStateActive == isProcessing)
            return;

        _processingStateActive = isProcessing;
        EmitOutput(new ProcessingStateOutput(isProcessing)
        {
            SessionId = _sessionId
        }, OutputFilter.ProcessingState);
    }

    // ── Command behaviors ──

    private void Ready()
    {
        CommandSubscriptionMessages();
        CommandSessionContextMessages();
        CommandSnapshotMessages();

        // Passivation: stop after idle timeout, snapshot first for fast recovery
        if (_config.IdleTimeout > TimeSpan.Zero)
        {
            Context.SetReceiveTimeout(_config.IdleTimeout);
        }

        Command<ReceiveTimeout>(_ =>
        {
            if (_subscribers.Count > 0)
            {
                _log.Info(
                    "Session idle but {SubscriberCount} subscriber(s) active; deferring passivation",
                    _subscribers.Count);
                return;
            }

            // Pending tool approvals do NOT defer passivation: approval state is
            // journaled (ToolApprovalRequested/Resolved) and an approval response
            // rehydrates the session and re-drives the parked batch, the same
            // path that already covers daemon restarts. Keeping the actor in
            // memory while a human decides buys nothing but resident memory.
            if (_pendingToolInteractions.Count > 0)
            {
                _log.Info(
                    "Session idle with {PendingApprovalCount} journaled approval(s) outstanding; passivating — an approval response will rehydrate and resume",
                    _pendingToolInteractions.Count);
            }

            if (_resolvedToolApprovals.Count > 0)
            {
                _log.Info(
                    "Session idle with {ResolvedApprovalCount} resolved approval(s) but no completed tool result; abandoning parked tool batch before passivation",
                    _resolvedToolApprovals.Count);
                var abandoned = BuildToolBatchAbandonedEvent(
                    "Tool call was not completed — the session became idle before the approved action completed.");
                Persist(abandoned, evt =>
                {
                    ApplyToolBatchAbandoned(evt);
                    TransitionTo(SessionPhase.Passivating);
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
        CommandJobReapResolved();
        Command<SpawnChildActorRequest>(msg => Sender.Tell(Context.ActorOf(msg.Props, msg.ActorName)));
        Command<DeliveryFailed>(HandleDeliveryFailedWhenReady);
        Command<PrepareForDaemonRestart>(_ => RequestRestartDrain());

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
        CommandSubscriptionMessages();
        CommandSessionContextMessages();
        CommandSnapshotMessages();

        Command<SendUserMessage>(cmd =>
        {
            if (_restartDrainRequested)
            {
                _log.Info("Rejecting new user message while restart drain is pending");
                TryReplyNack(SessionIngressGate.RestartInProgressMessage);
                return;
            }

            var reminderId = cmd.Source?.ReminderId;
            if (IsReminderDedupHit(reminderId, includeBuffered: true))
            {
                TurnLog().Info(
                    "reminder_mode_b_dedup_hit reminder={ReminderId} phase=processing",
                    reminderId);
                TryReplyAck();
                return;
            }

            _deliveryRetry.Clear();
            _log.Info("Buffering user message (LLM call in progress)");
            _buffer.Add(cmd);
            TryReplyAck();
        });

        Command<DeliveryFailed>(msg =>
        {
            if (_restartDrainRequested)
            {
                _log.Info("Ignoring delivery feedback while restart drain is pending");
                return;
            }

            if (!DeliveryRetryHandler.IsRetryable(msg))
            {
                _log.Warning(
                    "Non-retryable delivery feedback while processing channel={Channel} turn={Turn} kind={FailureKind}; injecting context",
                    msg.ChannelType,
                    msg.TurnNumber,
                    msg.FailureKind);
                _state = _state.AddSystemNudge(DeliveryRetryHandler.BuildNudge(msg));
                return;
            }

            _log.Warning(
                "Ignoring retryable delivery feedback while processing channel={Channel} turn={Turn}",
                msg.ChannelType,
                msg.TurnNumber);
        });

        Command<CompactionWorkCompleted>(_ => { });
        Command<CompactionWorkFailed>(_ => { });

        Command<LlmResponseReceived>(HandleLlmResponseReceived);

        Command<LlmResponseDeltaReceived>(HandleLlmResponseDeltaReceived);

        Command<ToolExecutionSingleCompleted>(msg =>
        {
            var result = msg.Result;
            Persist(new ToolCallRecorded
            {
                SessionId = _sessionId,
                ToolResult = result.Message,
                RecordedAtMs = NowMs()
            }, evt =>
            {
                ApplyToolCallRecorded(evt);
                ProcessToolCallResult(result);
                TryCompleteStreamedToolBatch();
            });
        });

        Command<ToolExecutionBatchCompleted>(_ =>
        {
            _watchdog.Stop(Timers);
            CancelAndDisposeToolExecutionCts();
            _activeToolBatch.MarkExecutionTaskCompleted();
            TryCompleteStreamedToolBatch();
        });

        Command<ToolExecutionCompleted>(HandleToolExecutionCompleted);

        Command<ToolExecutionFailed>(msg =>
        {
            _watchdog.Stop(Timers);
            CancelAndDisposeToolExecutionCts();
            _mediaBuffer.Clear();
            TurnLog().Error(msg.Cause, "turn_tool_execution_failed");

            const string errorMessage = "I encountered an error executing a tool. Please try again.";
            var category = msg.Cause is TimeoutException ? ErrorCategory.Timeout : ErrorCategory.ToolFailure;
            FailCurrentTurn(errorMessage, msg.Cause, category);
        });

        Command<ToolInteractionRequest>(msg => HandleToolInteractionRequestDispatch(
            new ToolInteractionRequestDispatch(msg, PersistApprovalState: true)));
        Command<ToolInteractionRequestDispatch>(HandleToolInteractionRequestDispatch);

        CommandAsync<ToolInteractionResponse>(HandleProcessingApprovalResponseAsync);

        CommandAsync<ToolInteractionTextResponse>(async msg =>
        {
            if (_restartDrainRequested)
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
            if (msg.CallId != _activeCallId) return; // stale failure from cancelled call
            _watchdog.Stop(Timers);
            CancelAndDisposeLlmCts();

            // Context overflow: roll back the failed turn, buffer the user message,
            // compact the history, and let the normal buffer drain re-deliver it.
            if (IsContextOverflowError(msg.Cause))
            {
                if (_compactionOverflowRetryCount > 0)
                {
                    _compactionOverflowRetryCount = 0;
                    TurnLog().Error(msg.Cause, "turn_context_overflow_after_compaction — failing turn");
                    FailCurrentTurn(
                        "Context window exceeded even after compaction. The conversation may be too large.",
                        msg.Cause, ErrorCategory.ProviderFailure);
                    return;
                }

                _compactionOverflowRetryCount++;
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

            // Evict discovered tools to prevent a poisoned tool set from cascading
            // across turns (e.g., oversized Notion schemas causing repeated 502s).
            _discoveredToolCache.EvictAll();
            TurnLog().Info("turn_discovered_tools_evicted — tool list reset to base tools after LLM call failure");

            var errorMessage = ExtractLlmErrorMessage(msg.Cause);
            var category = msg.Cause is TimeoutException ? ErrorCategory.Timeout : ErrorCategory.ProviderFailure;
            FailCurrentTurn(errorMessage, msg.Cause, category);
        });

        Command<ProcessingWatchdogExpired>(msg =>
        {
            if (!_watchdog.IsCurrent(msg))
                return;

            TimeoutException timeoutCause;
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
                    ProcessingWatchdog.LlmCall => _anyContentStreamed ? _config.FirstTokenTimeout : _config.PrefillTimeout,
                    _ => _config.TurnLlmTimeout
                };
                timeoutCause = new TimeoutException(
                    $"Session processing operation '{msg.OperationName}' exceeded watchdog timeout of {timeout.TotalSeconds:F0}s");
            }

            _watchdog.Stop(Timers);
            CancelAndDisposeLlmCts();

            _log.Error("Processing watchdog expired for operation {OperationName} (opId={OperationId}, noProgress={NoProgress})",
                msg.OperationName, msg.OperationId, msg.NoProgress);
            var errorMessage = ExtractLlmErrorMessage(timeoutCause);
            FailCurrentTurn(errorMessage, timeoutCause, ErrorCategory.Timeout);
        });

        Command<SpawnChildActorRequest>(msg => Sender.Tell(Context.ActorOf(msg.Props, msg.ActorName)));
        Command<RoutedSkillExecutionCompleted>(HandleRoutedSkillExecutionCompleted);
        Command<RoutedSkillExecutionFailed>(msg =>
            FailCurrentTurn(
                $"Skill '/{msg.SkillName}' routed to subagent '{msg.SubagentName}' failed: {msg.ErrorMessage}",
                new InvalidOperationException(msg.ErrorMessage),
                ErrorCategory.ToolFailure));
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
        Command<PrepareForDaemonRestart>(_ => RequestRestartDrain());
        CommandDistillationAckNoOp();
        CommandJobReapResolved();
    }

    private void HandleLlmResponseReceived(LlmResponseReceived msg)
    {
        if (msg.CallId != _activeCallId)
            return;

        _watchdog.Stop(Timers);
        CancelAndDisposeLlmCts();

        var response = msg.Response;
        var lastMessage = response.Messages[^1];
        var analysis = LlmResponseClassifier.Analyze(lastMessage);

        _log.Debug(
            "LLM response content breakdown: text={TextChars}ch thinking={ThinkingChars}ch toolCalls={ToolCallCount} finishReason={FinishReason}",
            analysis.TextChars,
            analysis.ThinkingChars,
            analysis.ToolCalls.Count,
            response.FinishReason?.ToString() ?? "null");

        if (analysis.Kind == LlmResponseKind.ToolCalls && _turnState.ForceNoToolsActive)
        {
            TurnLog().Warning(
                "turn_force_no_tools_violation toolCallCount={ToolCallCount} budgetUsed={BudgetUsed} max={Max}",
                analysis.ToolCalls.Count,
                _turnState.ToolCallCount,
                _config.MaxToolIterationsPerTurn);
            FailCurrentTurn(
                ToolBudgetExhaustedMessage,
                new InvalidOperationException("LLM continued requesting tools after tool execution was disabled for this turn."),
                ErrorCategory.ProviderFailure);
            return;
        }

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
                    _log.Warning("LLM produced {Kind} response — failing turn", analysis.Kind);
                    FailCurrentTurn(fail.ErrorMessage, fail.Cause, ErrorCategory.ProviderFailure);
                    return;
            }
        }

        HandleTextResponse(lastMessage, response.Usage, msg.StreamedText, msg.StreamedThinking, msg.RecallResult);
    }

    private void HandleLlmResponseDeltaReceived(LlmResponseDeltaReceived msg)
    {
        if (msg.CallId != _activeCallId)
            return;

        // Two-phase watchdog (shared with the sub-agent path): keep the generous
        // prefill budget until the first substantive delta, then promote to the
        // tighter inter-delta budget. Content-free keepalives refresh but never
        // promote, so a slow cold prefill emitting prompt_progress heartbeats is
        // not killed early.
        _anyContentStreamed = _watchdog.OnStreamProgress(
            msg.Substantive,
            _anyContentStreamed,
            _config.PrefillTimeout,
            _config.FirstTokenTimeout,
            Timers);

        switch (msg.Content)
        {
            case TextContent text when !string.IsNullOrEmpty(text.Text):
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

    private void HandleToolExecutionCompleted(ToolExecutionCompleted msg)
    {
        _watchdog.Stop(Timers);
        CancelAndDisposeToolExecutionCts();

        foreach (var startedJob in msg.StartedBackgroundJobs)
            TrackStartedBackgroundJob(startedJob);

        var emittedRunIds = new HashSet<SubAgentRunId>();
        foreach (var finding in msg.AcceptedSubAgentFindings)
        {
            if (emittedRunIds.Add(finding.RunId))
            {
                var runSummary = msg.CompletedSubAgentRuns
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

            EnqueueCheckpointFireAndForget(new MemoryCheckpointRequest(
                SessionId: _sessionId,
                TurnId: _activeTurnId,
                TriggerType: Memory.CheckpointTriggerType.SubagentFindings,
                Priority: 80,
                Payload: SessionMemoryCheckpointFactory.ForSubAgentFinding(
                    _sessionId,
                    CurrentMemoryBoundary(),
                    CurrentMemoryAudience(),
                    finding)));
        }

        foreach (var run in msg.CompletedSubAgentRuns)
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

        foreach (var result in msg.ToolResults)
        {
            _state = _state with { History = _state.History.Add(result) };

            if (result.ToolCallId is not { } toolCallId)
                throw new InvalidOperationException(
                    $"Tool-result message for tool '{result.Name ?? "unknown"}' has no ToolCallId.");

            var preview = result.Content is { Length: > 200 }
                ? result.Content[..200] + "..."
                : result.Content ?? "(null)";
            _log.Info("Tool [{ToolName}] (call={CallId}) result: {Result}",
                result.Name ?? "unknown", toolCallId.Value, preview);

            EmitOutput(new ToolResultOutput
            {
                SessionId = _sessionId,
                CallId = toolCallId,
                ToolName = new ToolName(result.Name ?? "unknown"),
                Result = result.Content ?? string.Empty
            }, OutputFilter.ToolCalls);
        }

        // Processes all results, including failed tool calls. RecentFiles tracks
        // interaction intent, not successful reads only.
        var updatedContext = WorkingContextUpdater.UpdateFromToolResults(
            _state.WorkingContext,
            _state.History,
            msg.ToolResults,
            _log);
        if (!ReferenceEquals(updatedContext, _state.WorkingContext))
            _state = _state with { WorkingContext = updatedContext };

        if (_fullRegistry is not null)
        {
            foreach (var result in msg.ToolResults)
            {
                if (result.Name is "load_tool" && result.Content is not null)
                    TryActivateDiscoveredTool(result.Content.Trim());
            }
        }

        foreach (var result in msg.ToolResults)
        {
            if (result.Name is not "set_working_directory" || result.Content is null)
                continue;

            var projectDir = result.Content.Trim();
            if (!Path.IsPathRooted(projectDir))
                continue;

            var next = _state.WorkingContext.WithProjectDirectory(projectDir);
            if (ReferenceEquals(next, _state.WorkingContext))
                continue;

            _state = _state with { WorkingContext = next };
            SetSystemPrompt();
            _log.Info("Project directory set to {ProjectDir}", projectDir);
        }

        foreach (var file in msg.FileAttachments)
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

        AddModelInputMediaNudge(msg.ModelInputMediaReferences);

        var budgetStatus = _turnState.RecordToolCompletion(msg.ToolResults.Count, _config.MaxToolIterationsPerTurn);
        var dupNudge = _turnState.CheckForDuplicates();
        if (dupNudge is not null)
        {
            TurnLog().Warning(
                "turn_duplicate_tool_detected tool={ToolName} count={Count} iteration={Iteration}",
                dupNudge.ToolName, dupNudge.Count, _turnState.ToolIterationCount);
            _state = _state.AddSystemNudge(dupNudge.NudgeText);
        }

        if (_buffer.Count > 0)
        {
            TurnLog().Info("turn_mid_loop_buffer_drain count={BufferCount} iteration={Iteration}",
                _buffer.Count, _turnState.ToolIterationCount);
            foreach (var buffered in _buffer)
            {
                var refs = buffered.MediaReferences.Count > 0 ? buffered.MediaReferences : null;
                _state = _state.AddUserMessage(buffered.Content, refs);
            }
            _buffer.Clear();
        }

        switch (budgetStatus)
        {
            case ToolBudgetStatus.Exhausted exhausted:
                TurnLog().Warning("turn_tool_call_limit_reached callCount={CallCount} max={Max} iteration={Iteration}",
                    _turnState.ToolCallCount, _config.MaxToolIterationsPerTurn, _turnState.ToolIterationCount);
                _state = _state.AddSystemNudge(exhausted.NudgeText);
                FireLlmCall(forceNoTools: true);
                return;
            case ToolBudgetStatus.NudgeNeeded nudge:
                _state = _state.AddSystemNudge(nudge.NudgeText);
                break;
        }

        if (ShouldCompact())
        {
            _log.Info("Compaction threshold reached during tool loop ({InputTokens} tokens >= {Threshold} limit), starting compaction",
                _lastInputTokenCount, _model.CompactionTokenLimit(_config.Tuning.CompactionThreshold));
            _resumeToolLoopAfterCompaction = true;
            Self.Tell(new CompactionTriggered(_lastInputTokenCount));
            TransitionTo(SessionPhase.Compacting);
            return;
        }

        _pendingToolInteractions.Clear();
        _resolvedToolApprovals.Clear();
        TurnLog().Info("turn_tool_execution_complete iteration={Iteration} callCount={CallCount} max={Max} resultCount={ResultCount}",
            _turnState.ToolIterationCount, _turnState.ToolCallCount, _config.MaxToolIterationsPerTurn, msg.ToolResults.Count);
        MarkApprovalRunningAfterRedrive();
        FireLlmCall();
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
            if (ShouldSkipMemoryCurationForThirdPartyAdoptedContext(_currentTurnContext, _currentTurnSource))
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
        CommandSubscriptionMessages();
        CommandSessionContextMessages();
        CommandSnapshotMessages();

        // Buffer user messages during compaction (same as Processing)
        Command<SendUserMessage>(cmd =>
        {
            if (_restartDrainRequested)
            {
                _log.Info("Rejecting new user message while restart drain is pending during compaction");
                TryReplyNack(SessionIngressGate.RestartInProgressMessage);
                return;
            }

            var reminderId = cmd.Source?.ReminderId;
            if (IsReminderDedupHit(reminderId, includeBuffered: true))
            {
                TurnLog().Info(
                    "reminder_mode_b_dedup_hit reminder={ReminderId} phase=compacting",
                    reminderId);
                TryReplyAck();
                return;
            }

            _log.Info("Buffering user message (compaction in progress)");
            _buffer.Add(cmd);
            TryReplyAck();
        });

        // Buffer an approval response during compaction — compaction rewrites
        // history, so the parked tool batch cannot be re-driven mid-flight.
        // DrainBufferOrReady replays the buffered response after compaction.
        Command<ToolInteractionResponse>(msg =>
        {
            _log.Info("Buffering tool interaction response (compaction in progress)");
            _deferredApprovalResponse = msg;
            TryReplyAck();
        });

        Command<ToolInteractionTextResponse>(msg =>
        {
            _log.Info("Buffering text tool interaction response (compaction in progress)");
            _deferredApprovalResponse = msg;
            TryReplyAck();
        });

        Command<ProcessingWatchdogExpired>(HandleCompactionWatchdogExpired);
        Command<SpawnChildActorRequest>(msg => Sender.Tell(Context.ActorOf(msg.Props, msg.ActorName)));
        Command<PrepareForDaemonRestart>(_ => RequestRestartDrain());

        Command<CompactionTriggered>(HandleCompactionTriggered);

        Command<CompactionWorkCompleted>(HandleCompactionWorkCompleted);

        Command<CompactionWorkFailed>(HandleCompactionWorkFailed);

        Command<MemoryExtractionCompleted>(msg =>
        {
            // Persist extracted memories externally (fire-and-forget)
            var self = Self;
            _ = PersistMemoriesAsync(_memoryExtractor, _sessionId, msg.ExtractedMemories, self);

            DrainBufferOrReady();
        });

        Command<CompactionFailed>(HandleLegacyCompactionFailed);

        CommandDistillationAckNoOp();
        CommandJobReapResolved();
    }

    private void HandleCompactionWatchdogExpired(ProcessingWatchdogExpired msg)
    {
        if (!_watchdog.IsCurrent(msg))
            return;

        _log.Error("Compaction watchdog expired for operation {OperationName} (opId={OperationId})",
            msg.OperationName, msg.OperationId);

        _watchdog.Stop(Timers);

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
        _watchdog.Start(ProcessingWatchdog.Compaction, timeout, Timers);

        var operationId = _watchdog.CurrentOperationId;
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

        _watchdog.Stop(Timers);

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
            _startupContextInjected = false;
            _recallManager.ResetForCompaction();
            _discoveredToolCache.EvictAll();

            EnqueueCheckpointFireAndForget(new MemoryCheckpointRequest(
                SessionId: _sessionId,
                TurnId: _activeTurnId,
                TriggerType: Memory.CheckpointTriggerType.CompactionBoundary,
                Priority: 90,
                Payload: SessionMemoryCheckpointFactory.ForCompactionBoundary(
                    _sessionId,
                    CurrentMemoryBoundary(),
                    CurrentMemoryAudience(),
                    msg.Summary)));

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

            if (_memoryExtractor is NullMemoryExtractor)
                DrainBufferOrReady();
            else
                InvokeMemoryExtractionAsync();
        });
    }

    private void HandleCompactionWorkFailed(CompactionWorkFailed msg)
    {
        if (!IsCurrentCompactionOperation(msg.OperationId))
            return;

        _watchdog.Stop(Timers);
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

    private void HandleLegacyCompactionFailed(CompactionFailed msg)
    {
        _log.Warning(msg.Cause, "Compaction failed");

        EmitOutput(new ErrorOutput
        {
            SessionId = _sessionId,
            Message = "Context compaction encountered an error. The session will continue.",
            Category = ErrorCategory.Unknown,
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

            _buffer.Insert(0, new SendUserMessage
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
        if (_restartDrainRequested)
        {
            _deferredApprovalResponse = null;
            ClearBufferedMessagesForRestartDrain();
            _resumeToolLoopAfterCompaction = false;
            TransitionTo(SessionPhase.Ready);
            TransitionTo(SessionPhase.Passivating);
            return;
        }

        // Mid-tool-loop compaction (#424): the turn is still in-progress,
        // so we must fire a follow-up LLM call even if the buffer is empty.
        var resumeToolLoop = _resumeToolLoopAfterCompaction;
        _resumeToolLoopAfterCompaction = false;

        if (resumeToolLoop)
            _log.Info("Post-compaction: resuming tool loop with follow-up LLM call");

        var hadBufferedMessages = _buffer.Count > 0;
        if (hadBufferedMessages)
        {
            _log.Info("Post-compaction: draining {BufferCount} buffered message(s)", _buffer.Count);
            foreach (var buffered in _buffer)
            {
                var refs = buffered.MediaReferences.Count > 0 ? buffered.MediaReferences : null;
                _state = _state.AddUserMessage(buffered.Content, refs);
            }
            _buffer.Clear();
        }

        if (resumeToolLoop || hadBufferedMessages)
        {
            FireLlmCall();
            TransitionTo(SessionPhase.Processing);
        }
        else
        {
            ClearApprovalTurnState();
            TransitionTo(SessionPhase.Ready);
        }

        // Replay an approval response that arrived during compaction. History
        // is now rebuilt and the phase transition has run, so the parked tool
        // batch can be re-driven by whichever phase the session landed in.
        if (_deferredApprovalResponse is { } deferred)
        {
            _deferredApprovalResponse = null;
            _log.Info("Post-compaction: replaying buffered tool interaction response");
            Self.Tell(deferred);
        }
    }

    private static readonly TimeSpan PassivationGracePeriod = TimeSpan.FromSeconds(5);

    // Bounds the reap-on-passivation handshake; the Ask always resolves within
    // this window (ack or piped failure), so passivation can never wedge on it.
    private static readonly TimeSpan JobReapAckTimeout = TimeSpan.FromSeconds(5);
    private static readonly object PassivationTimerKey = new();

    // After distillation + snapshot complete we wait this long for a racing
    // user-initiated message to abort the stop. Closes the race where a
    // SendUserMessage forwarded by the parent arrives in the child's mailbox
    // microseconds after Context.Stop(Self) would have been dispatched.
    // 100 ms is geological time for an in-proc Akka hop, and idle-driven
    // passivation does not care about an extra 100 ms.
    private static readonly TimeSpan PassivationFinalStopDelay = TimeSpan.FromMilliseconds(100);
    private static readonly object PassivationFinalStopTimerKey = new();

    private void Passivating()
    {
        // Disable idle timeout — we're shutting down
        Context.SetReceiveTimeout(null);

        // Reap-on-passivation: a background job is session-scoped — when the
        // conversation goes idle its processes must not linger. Kills are
        // requested up front (parallel with distillation) and the final
        // snapshot is gated on the ack so it captures the reaped marks.
        _jobReapPending = false;
        _passivationDeferredForReap = false;
        if (!_state.ActiveBackgroundJobs.IsEmpty)
        {
            var jobRegistry = ActorRegistry.For(Context.System);
            if (jobRegistry.TryGet<BackgroundJobManagerActorKey>(out var jobManager))
            {
                _jobReapPending = true;
                var reapEpoch = ++_jobReapEpoch;
                jobManager.Ask<SessionJobsReaped>(
                        new KillJobsForSession(_sessionId), JobReapAckTimeout)
                    .PipeTo(Self,
                        success: ack => new JobReapResolved(reapEpoch, ack.ReapedCount, null),
                        failure: ex => new JobReapResolved(reapEpoch, 0, ex));
            }
            else
            {
                _log.Error(
                    "Session has {JobCount} active background job(s) but no background job manager is registered — processes cannot be reaped",
                    _state.ActiveBackgroundJobs.Count);
            }
        }

        // The reap reply is handled by the same epoch-correlated handler used in
        // every other phase (CommandJobReapResolved) — registered for ALL phases
        // so a reply can never dead-letter no matter where the session is when it
        // lands. Here in Passivating the handler also releases the deferred
        // CompletePassivation via FinishJobReap.
        CommandJobReapResolved();

        Command<SessionDistillationCompleted>(msg =>
        {
            _log.Info("Passivation distillation complete, finalizing");
            HandleDistillationResult(msg, stopAfterAcceptedProposalPersistence: true);
        });

        Command<AcceptedDistillationProposalsRecorded>(_ =>
        {
            _log.Info("Passivation distillation dedup persistence complete, stopping");
            Timers.Cancel(PassivationTimerKey);
            CompletePassivation();
        });

        CommandSubscriptionMessages();
        CommandSessionContextMessages();
        CommandSnapshotMessages();
        Command<PrepareForDaemonRestart>(_ => RequestRestartDrain());

        Command<SendUserMessage>(cmd =>
        {
            if (_restartDrainRequested)
            {
                _log.Info("Rejecting new user message while restart passivation is in progress");
                TryReplyNack(SessionIngressGate.RestartInProgressMessage);
                return;
            }

            _log.Info("Aborting passivation due to new user message");
            AbortPassivationTimers();
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
            if (_restartDrainRequested)
            {
                _log.Info("Rejecting tool interaction response while restart passivation is in progress");
                TryReplyNack(SessionIngressGate.RestartInProgressMessage);
                return;
            }

            _log.Info("Aborting passivation due to tool interaction response");
            AbortPassivationTimers();
            TransitionTo(SessionPhase.Ready);
            await HandleToolInteractionResponseWhenIdle(msg);
        });

        CommandAsync<ToolInteractionTextResponse>(async msg =>
        {
            if (_restartDrainRequested)
            {
                _log.Info("Rejecting text tool interaction response while restart passivation is in progress");
                TryReplyNack(SessionIngressGate.RestartInProgressMessage);
                return;
            }

            _log.Info("Aborting passivation due to text tool interaction response");
            AbortPassivationTimers();
            TransitionTo(SessionPhase.Ready);
            await HandleToolInteractionTextResponseWhenIdle(msg);
        });

        // Ignore stale processing/compaction messages
        Command<ProcessingWatchdogExpired>(_ => { });
        Command<CompactionWorkCompleted>(_ => { });
        Command<CompactionWorkFailed>(_ => { });
        Command<SpawnChildActorRequest>(msg => Sender.Tell(Context.ActorOf(msg.Props, msg.ActorName)));

        // Timeout — stop even if distillation didn't finish
        Command<PassivationTimeout>(_ =>
        {
            _log.Warning("Passivation grace period expired, stopping without distillation");
            CompletePassivation();
        });

        // No racing message arrived during the post-snapshot grace window — commit the stop.
        Command<PassivationFinalStop>(_ =>
        {
            _log.Info("Passivation grace window elapsed, finalizing stop");
            FinalizePassivation();
        });

        Command<DeliveryFailed>(msg =>
        {
            if (_restartDrainRequested)
            {
                _log.Info("Ignoring delivery feedback while restart passivation is in progress");
                return;
            }

            _log.Info("Aborting passivation due to delivery feedback");
            AbortPassivationTimers();
            TransitionTo(SessionPhase.Ready);
            HandleDeliveryFailedWhenReady(msg);
        });

        // Request final distillation from observer, or stop immediately
        if (_observerActor is not null)
        {
            _observerActor.Tell(new DistillMemories(), Self);
            Timers.StartSingleTimer(PassivationTimerKey, new PassivationTimeout(), PassivationGracePeriod);
        }
        else
        {
            CompletePassivation();
        }
    }

    // Enters the post-distillation grace window: persist the final snapshot
    // and arm a short timer. Any user-initiated message arriving in the
    // mailbox during this window cancels the timer (via AbortPassivationTimers)
    // and resurrects the actor in Ready — avoiding a full stop + respawn +
    // recovery cycle. If nothing arrives, the timer fires PassivationFinalStop
    // and we proceed to FinalizePassivation.
    private void CompletePassivation()
    {
        if (_passivationFinalStopScheduled)
            return;

        // The final snapshot must capture the reaped job marks — defer until
        // the reap ask resolves (it always does: success or piped failure
        // within JobReapAckTimeout).
        if (_jobReapPending)
        {
            _passivationDeferredForReap = true;
            return;
        }

        _passivationFinalStopScheduled = true;
        SaveSnapshotIfSafe();
        Timers.StartSingleTimer(
            PassivationFinalStopTimerKey,
            new PassivationFinalStop(),
            PassivationFinalStopDelay);
    }

    // Marks tracked jobs reaped and releases a passivation that was waiting on
    // the reap handshake. Runs for both the ack and the loud-failure path —
    // the manager's definitions are the authoritative status either way.
    // The reap is journaled (not just folded into the passivation snapshot) so
    // the marks survive recovery even when that snapshot is skipped because an
    // approval batch is parked (SaveSnapshotIfSafe). Otherwise a crash in that
    // window would rehydrate the killed jobs as "running" in the context block.
    private void FinishJobReap()
    {
        Persist(new SessionBackgroundJobsReaped { SessionId = _sessionId, ReapedAtMs = NowMs() }, evt =>
        {
            _state = _state.Apply(evt);
            _jobReapPending = false;
            if (_passivationDeferredForReap)
            {
                _passivationDeferredForReap = false;
                CompletePassivation();
            }
        });
    }

    // Actual termination after the grace window expires. The observer
    // notification is deferred to this point so it never fires for an
    // aborted passivation.
    private void FinalizePassivation()
    {
        if (_passivationCompleted)
            return;

        _passivationCompleted = true;
        _lifecycleObserver?.OnSessionDeactivated(_sessionId);
        _restartDrainReplyTo?.Tell(CommandAck.For(_sessionId));
        _restartDrainReplyTo = null;
        Context.Stop(Self);
    }

    private void AbortPassivationTimers()
    {
        Timers.Cancel(PassivationTimerKey);
        Timers.Cancel(PassivationFinalStopTimerKey);
        _passivationFinalStopScheduled = false;
    }

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

    // Single handler for the reap Ask reply, registered in EVERY non-terminal
    // phase (Ready/Processing/Compacting/Passivating) so the reply can never
    // dead-letter regardless of which phase the session is in when it lands —
    // passivation may have been aborted back to Ready, moved on to
    // Processing/Compacting, or still be in Passivating. Centralizing here means
    // a future phase cannot silently drop the reply by forgetting a bespoke
    // registration. Epoch-correlated so a late reply from a superseded reap
    // request cannot resolve a newer handshake.
    private void CommandJobReapResolved()
    {
        Command<JobReapResolved>(HandleJobReapResolved);
    }

    private void HandleJobReapResolved(JobReapResolved msg)
    {
        if (msg.Epoch != _jobReapEpoch)
        {
            _log.Debug(
                "Ignoring superseded background-job reap reply (epoch {Stale}, current {Current})",
                msg.Epoch, _jobReapEpoch);
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

        FinishJobReap();
    }

    private void CommandSessionContextMessages()
    {
        Command<SetSessionPromptOverlay>(msg =>
        {
            _sessionPromptOverlay = string.IsNullOrWhiteSpace(msg.PromptOverlay)
                ? null
                : msg.PromptOverlay.Trim();
        });
    }

    private bool IsCurrentCompactionOperation(long operationId)
        => _watchdog.IsCurrentOperation(ProcessingWatchdog.Compaction, operationId);


    /// <summary>
    /// Fire a sidecar LLM call to generate a short session title.
    /// Best-effort — failures are silently ignored.
    /// </summary>
    private void MaybeGenerateTitle()
    {
        if (SessionTitleGenerator.ShouldGenerate(_state.TurnCount, _config.Tuning.TitleGenerationInterval))
            _ = SessionTitleGenerator.GenerateAsync(_compactionClient, _sessionId, _state.History, Self, _log, _config.SidecarLlmTimeout);
    }


    /// <summary>
    /// Fire an async memory extraction LLM call with a 30-second timeout.
    /// Results come back as <see cref="MemoryExtractionCompleted"/> or
    /// <see cref="CompactionFailed"/> if the call fails or times out.
    /// </summary>
    private void InvokeMemoryExtractionAsync()
    {
        var history = _state.History;
        var self = Self;
        var client = _compactionClient;
        var timeout = _config.SidecarLlmTimeout;
        var sessionId = _sessionId;

        _ = InvokeMemoryExtractionCoreAsync(client, sessionId, history, self, timeout);
    }

    internal static async Task InvokeMemoryExtractionCoreAsync(
        IChatClient client,
        SessionId sessionId,
        IReadOnlyList<SerializableChatMessage> history,
        IActorRef self,
        TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var extractionMessages = new List<AiChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.System,
                    CompactionPromptBuilder.BuildMemoryExtractionSystemPrompt()),
                new(Microsoft.Extensions.AI.ChatRole.User,
                    CompactionPromptBuilder.BuildMemoryExtractionUserPrompt(history))
            };
            // Carry the session id so memory-extraction chat-client diagnostics route to the
            // session's session.log and correlate in Seq/OTLP (replaces the deleted AsyncLocal).
            var options = new SessionScopedChatOptions { SessionId = sessionId.Value };
            var extractionResult = await StreamingResponseReader.ReadAsync(
                client, extractionMessages, options, cts.Token);
            var extractedText = extractionResult.Response.Text ?? string.Empty;
            self.Tell(new MemoryExtractionCompleted { ExtractedMemories = extractedText });
        }
        catch (Exception ex)
        {
            self.Tell(new CompactionFailed { Cause = ex });
        }
    }

    private static async Task PersistMemoriesAsync(
        IMemoryExtractor extractor, SessionId sessionId, string memories, IActorRef self)
    {
        try
        {
            await extractor.PersistAsync(sessionId, memories);
        }
        catch (Exception ex)
        {
            // Memory persistence is best-effort — log and continue compaction
            Trace.TraceWarning("Memory persistence failed for session {0}: {1}", sessionId.Value, ex.Message);
        }
    }

    private void HandleToolCallResponse(
        AiChatMessage lastMessage,
        List<FunctionCallContent> toolCalls,
        UsageDetails? usage)
    {
        // Model produced tool calls — reset empty-response guards so they can
        // fire again if the model stalls later in the chain.
        _turnState.ResetEmptyResponseGuards();

        // Normalize the LLM-facing alias back to the canonical name BEFORE
        // anything reads tc.Name. The LLM sees and emits sanitized names
        // for MCP tools (e.g. `notion__notion-search`); every internal
        // consumer downstream — audit log, ToolCallOutput rendered to the
        // user, duplicate-call fingerprint, persisted assistant message,
        // approval gate — keys on canonical (e.g. `notion/notion-search`).
        // Doing the conversion here keeps each consumer free of name-form
        // awareness; the outbound boundary (SessionMessageAssembler) does
        // the reverse mapping when re-serializing history to the wire.
        if (_toolRegistry is not null)
        {
            CanonicalizeToolCallNames(lastMessage, toolCalls, _toolRegistry);
        }

        // Persist tool calls exactly as the executor will interpret them (schema-aware
        // meta extraction), so recorded history matches what actually runs — a near-miss
        // meta key is stripped + captured in MetaJson, not left raw with an empty meta.
        var assistantMsg = ChatMessageConverter.FromAiMessage(
            lastMessage,
            interpretToolCall: _toolExecutor is { } toolExec
                ? tc =>
                {
                    var (meta, cleaned) = toolExec.PrepareToolCall(tc);
                    return (meta, cleaned.Arguments);
                }
        : null);
        var userMsg = _state.FindLastUserMessage() ?? new SerializableChatMessage
        {
            Role = Protocol.ChatRole.User,
            Content = string.Empty
        };

        Persist(new ToolBatchStarted
        {
            SessionId = _sessionId,
            UserMessage = userMsg,
            AssistantMessage = assistantMsg,
            StartedAtMs = NowMs()
        }, evt =>
        {
            ApplyToolBatchStarted(evt);
            EmitAndDispatchToolBatch(lastMessage, toolCalls, usage);
        });
    }

    /// <summary>
    /// Rewrite every <see cref="FunctionCallContent"/> in
    /// <paramref name="lastMessage"/> and <paramref name="toolCalls"/> to use
    /// the canonical tool name. The original list is mutated in-place
    /// because every other consumer in this turn reads from these
    /// references — including the persisted assistant message that gets
    /// reconstructed by <see cref="ChatMessageConverter.FromAiMessage"/>.
    /// Tool calls whose names don't resolve to a registered tool pass
    /// through unchanged (the executor will reject them downstream).
    /// </summary>
    private static void CanonicalizeToolCallNames(
        AiChatMessage lastMessage,
        List<FunctionCallContent> toolCalls,
        Tools.ToolRegistry registry)
    {
        for (var i = 0; i < toolCalls.Count; i++)
        {
            var tc = toolCalls[i];
            var canonical = registry.ToCanonicalName(tc.Name);
            if (string.Equals(canonical, tc.Name, StringComparison.Ordinal))
                continue;

            toolCalls[i] = new FunctionCallContent(tc.CallId, canonical, tc.Arguments);
        }

        for (var i = 0; i < lastMessage.Contents.Count; i++)
        {
            if (lastMessage.Contents[i] is not FunctionCallContent fc)
                continue;
            var canonical = registry.ToCanonicalName(fc.Name);
            if (string.Equals(canonical, fc.Name, StringComparison.Ordinal))
                continue;

            lastMessage.Contents[i] = new FunctionCallContent(fc.CallId, canonical, fc.Arguments);
        }
    }

    private void EmitAndDispatchToolBatch(
        AiChatMessage lastMessage,
        List<FunctionCallContent> toolCalls,
        UsageDetails? usage)
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

        // Emit tool call outputs to subscribers and track for duplicate detection
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

            // Duplicate tool call detection: hash tool name + args
            _turnState.TrackToolCall(tc.Name, argsJson);
        }

        // Emit usage if present (intermediate turn) and track for compaction
        if (usage is not null)
        {
            EmitUsageOutput(usage);

            if (usage.InputTokenCount is > 0)
            {
                _lastInputTokenCount = usage.InputTokenCount.Value;
            }
        }

        DispatchToolBatch(toolCalls);
    }

    /// <summary>
    /// Dispatches a tool batch to <see cref="SessionToolExecutionPipeline"/>.
    /// Factored out of <see cref="HandleToolCallResponse"/> so the post-approval
    /// re-drive path (<see cref="RedriveToolBatchForApproval"/>) runs the exact
    /// same dispatch logic — there is no divergent second copy.
    /// </summary>
    /// <param name="oneTimeApprovalPreSeed">
    /// Optional map of <c>callId → approved patterns</c>. For each entry, the
    /// pipeline pre-seeds the one-time approval bypass on that call's execution
    /// context before the first attempt, so an <c>ApprovedOnce</c> re-drive
    /// skips the approval gate for exactly that call without emitting a second
    /// approval prompt. Scoped per call id — never widens scope to other calls.
    /// </param>
    private void DispatchToolBatch(
        List<FunctionCallContent> toolCalls,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? oneTimeApprovalPreSeed = null,
        IReadOnlyDictionary<string, ApprovalDecision>? decisionOverride = null)
    {
        _activeToolBatch.Start(toolCalls);

        // Execute tools async — results come back as ToolExecutionCompleted
        TurnLog().Info("turn_tool_call_batch count={Count} tools={Tools}",
            toolCalls.Count,
            string.Join(",", toolCalls.Select(tc => tc.Name)));
        foreach (var tc in toolCalls)
        {
            _log.Info("Invoking tool [{ToolName}] (call={CallId}) args={Args}",
                tc.Name, tc.CallId,
                tc.Arguments is not null ? JsonSerializer.Serialize(tc.Arguments) : "{}");
        }
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
        Func<object, string, CancellationToken, Task<object>> spawnChildActor = async (props, name, ct) =>
            await self.Ask<IActorRef>(
                new SpawnChildActorRequest((Props)props, name),
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
        var setWorkingDirectoryAvailable = IsSetWorkingDirectoryAvailable();

        CancelAndDisposeToolExecutionCts();
        _activeToolExecutionCts = new CancellationTokenSource();
        var toolExecutionCt = _activeToolExecutionCts.Token;
        var turnContext = _currentTurnContext
            ?? throw new InvalidOperationException("Tool batch dispatch requires admitted turn authority.");
        var runEnvironment = new SessionToolRunEnvironment
        {
            SessionDirectory = sessionDir,
            InlineOutputBudget = new InlineOutputBudget(_config.Tuning.MaxInlineToolResultChars),
            ModelInputModalities = _model.InputModalities,
            SpawnChildActor = spawnChildActor,
            ProjectDirectory = _state.WorkingContext.ProjectDirectory,
            RecentFiles = _state.WorkingContext.RecentFiles
        };
        var pipeline = _toolExecutionPipeline
            ?? throw new InvalidOperationException("Tool batch dispatch requires tool execution infrastructure.");
        var batch = new SessionToolBatch(turnContext, runEnvironment)
        {
            ToolCalls = toolCalls,
            DefaultTimeout = new ToolExecutionTimeout(toolExecutionTimeout),
            ReplyTo = self,
            EmitSubAgentOutput = emitSubAgentOutput,
            ApprovalRequests = new ToolApprovalRequests(
                _approvalChannel,
                request => self.Tell(request),
                new ToolExecutionTimeout(Timeout.InfiniteTimeSpan)),
            BackgroundJobs = backgroundJobs,
            SetWorkingDirectoryAvailable = setWorkingDirectoryAvailable,
            StreamResults = true,
            OneTimeApprovalPreSeed = oneTimeApprovalPreSeed
                ?? new Dictionary<string, IReadOnlyList<string>>(),
            DecisionOverrides = decisionOverride
                ?? new Dictionary<string, ApprovalDecision>(),
            CancellationToken = toolExecutionCt
        };

        _ = pipeline.ExecuteAsync(batch);
    }

    private void HandleTextResponse(
        AiChatMessage lastMessage,
        UsageDetails? usage,
        bool streamedText,
        bool streamedThinking,
        AutomaticRecallResult? recallResult)
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
            SourceReminderId = _currentTurnSource?.ReminderId,
            SourceBackgroundJobId = _currentTurnSource?.BackgroundJobId
        };

        Persist(turnEvent, evt =>
        {
            _inFlightDedup.CompleteReminder(evt.SourceReminderId);
            _inFlightDedup.CompleteBackgroundJob(evt.SourceBackgroundJobId);

            var processed = _state.ProcessedReminderIds;
            if (evt.SourceReminderId is { } reminderId && !string.IsNullOrEmpty(reminderId.Value))
            {
                processed = processed.Add(reminderId);
            }

            _state = (_state with
            {
                History = _state.History.Add(evt.AssistantReply),
                TurnCount = _state.TurnCount + 1,
                ProcessedReminderIds = processed
            }).CompleteTurnBackgroundJobBookkeeping(evt.SourceBackgroundJobId);

            EmitResponseOutputs(lastMessage, usage, includeText: true, includeThinking: true);
            MaybeSnapshot();
            MaybeGenerateTitle();
            _activeRecall = recallResult;

            EnqueueCheckpointFireAndForget(new MemoryCheckpointRequest(
                SessionId: _sessionId,
                TurnId: _activeTurnId,
                TriggerType: Memory.CheckpointTriggerType.TurnComplete,
                Priority: 40,
                Payload: SessionMemoryCheckpointFactory.ForTurnComplete(
                    _sessionId,
                    evt,
                    CurrentMemoryBoundary(),
                    CurrentMemoryAudience())));

            _deliveryRetry.MarkEligible(new TurnNumber(_state.TurnCount));

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
        if (_restartDrainRequested)
        {
            ClearBufferedMessagesForRestartDrain();
            TransitionTo(SessionPhase.Ready);
            TransitionTo(SessionPhase.Passivating);
            return;
        }

        if (_buffer.Count > 0)
        {
            TurnLog().Info("turn_buffer_drain count={BufferCount}", _buffer.Count);
            foreach (var buffered in _buffer)
            {
                var refs = buffered.MediaReferences.Count > 0 ? buffered.MediaReferences : null;
                _state = _state.AddUserMessage(buffered.Content, refs);
            }

            _buffer.Clear();
            _recallManager.ResetForNewTurn(); // New user input — resolve recall fresh
            FireLlmCall();
            // Already in Processing — no transition needed, just fired a new LLM call
            return;
        }

        ClearApprovalTurnState();
        TransitionTo(SessionPhase.Ready);
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

        if (!DeliveryRetryHandler.IsRetryable(msg))
        {
            _log.Warning(
                "Non-retryable delivery failure channel={Channel} turn={Turn} kind={FailureKind}; injecting context for next turn",
                msg.ChannelType,
                msg.TurnNumber,
                msg.FailureKind);
            _state = _state.AddSystemNudge(DeliveryRetryHandler.BuildNudge(msg));
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
        _recallManager.ResetForNewTurn(); // Delivery feedback changed context — resolve recall fresh
        FireLlmCall();
        TransitionTo(SessionPhase.Processing);
    }

    private void HandleIncomingUserMessage(SendUserMessage cmd)
    {
        var reminderId = cmd.Source?.ReminderId;
        if (IsReminderDedupHit(reminderId, includeBuffered: true))
        {
            TurnLog().Info(
                "reminder_mode_b_dedup_hit reminder={ReminderId}",
                reminderId);
            TryReplyAck();
            return;
        }

        var bgJobId = cmd.Source?.BackgroundJobId;
        if (IsBackgroundJobDedupHit(bgJobId, includeBuffered: true))
        {
            TurnLog().Info(
                "background_job_dedup_hit job={BackgroundJobId}",
                bgJobId);
            TryReplyAck();
            return;
        }

        _inFlightDedup.ReserveReminder(reminderId);
        _inFlightDedup.ReserveBackgroundJob(bgJobId);

        // A new inbound message while a tool batch is still parked on an
        // approval gate means the user abandoned that approval. This is only
        // reachable on a cold-recovered session — a live session parked on
        // approval is in Processing, not Ready — so a non-empty
        // _pendingToolInteractions here is the cold-recovery signal. Close the
        // orphaned tool_use blocks before the new turn's LLM call: an assistant
        // tool_use with no matching tool_result is rejected by the provider
        // API, which would otherwise wedge every subsequent turn.
        if (_pendingToolInteractions.Count > 0)
        {
            if (_approvalTurnState is WaitingApprovalTurn waiting)
                _approvalTurnState = new AbandoningApprovalTurn(waiting.Context, "superseded_by_new_message");
            var abandoned = BuildToolBatchAbandonedEvent();
            Persist(abandoned, evt =>
            {
                ApplyToolBatchAbandoned(evt);
                ContinueIncomingUserMessage(cmd);
            });
            return;
        }

        if (_resolvedToolApprovals.Count > 0)
        {
            var abandoned = BuildResolvedToolBatchInterruptedByRestartEvent();
            Persist(abandoned, evt =>
            {
                ApplyToolBatchAbandoned(evt);
                ContinueIncomingUserMessage(cmd);
            });
            return;
        }

        if (HasInterruptedToolBatchAfterRecovery())
        {
            var abandoned = BuildInterruptedToolBatchAfterRecoveryEvent();
            Persist(abandoned, evt =>
            {
                ApplyToolBatchAbandoned(evt);
                ContinueIncomingUserMessage(cmd);
            });
            return;
        }

        ContinueIncomingUserMessage(cmd);
    }

    private void ContinueIncomingUserMessage(SendUserMessage cmd)
    {
        _deliveryRetry.Clear();
        _currentTurnSource = cmd.Source;
        BindTurnTelemetry(cmd.Source);
        _currentTurnContext = TurnContext.FromMessageSource(
            _sessionId,
            _activeTurnId ?? new Protocol.TurnId(IdGen.ShortId()),
            cmd.Source);
        _approvalTurnState = new RunningApprovalTurn(_currentTurnContext);
        _currentTrustContext = _trustContextDeriver?.DeriveFromTurnContext(_currentTurnContext);
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

        _turnState.ResetForNewTurn();
        _discoveredToolCache.PrepareForNewTurn(
            _config.Tuning.DiscoveredToolRetentionTurns,
            _config.Tuning.DiscoveredToolMaxCount,
            _fullRegistry);

        // Strict modality consumer contract: the session actor trusts ingress
        // to have routed attachments through its own capability gate. If an
        // unsupported modality still reaches here, the originating channel
        // skipped the contract in netclaw-input-adapters and that's a bug
        // the operator needs to see — surface it loudly and continue.
        if (mediaRefs.Count > 0 && !_model.InputModalities.HasFlag(Configuration.ModelModality.Image))
        {
            var offendingRefs = mediaRefs.Where(r => r.Modality == (int)MediaModality.Image).ToList();
            if (offendingRefs.Count > 0)
            {
                var offendingDesc = string.Join(",",
                    offendingRefs.Select(r => $"{r.RelativePath}:modality={r.Modality}"));
                _log.Error(
                    "ingress_bug model={ModelId} modalities={Modalities} offending={Offending}",
                    _model.ModelId, _model.InputModalities, offendingDesc);

                mediaRefs = [.. mediaRefs.Where(r => r.Modality != (int)MediaModality.Image)];

                const string ingressBugNotice =
                    "[system] An attachment was received but could not be delivered to the model due to an ingress bug. " +
                    "Please retry, or notify the operator if this persists.";
                userContent = string.IsNullOrEmpty(userContent)
                    ? ingressBugNotice
                    : userContent + "\n\n" + ingressBugNotice;
            }
        }

        if (TryHandleSlashCommand(executableUserContent, mediaRefs))
            return;

        _state = _state.AddUserMessage(userContent, mediaRefs.Count > 0 ? mediaRefs : null);
        TryReplyAck();
        _recallManager.ResetForNewTurn();
        _compactionOverflowRetryCount = 0;
        FireInitialTurnLlmCall(executableUserContent);
        TransitionTo(SessionPhase.Processing);
    }

    private bool IsReminderDedupHit(ReminderId? reminderId, bool includeBuffered)
    {
        if (reminderId is not { } id || string.IsNullOrEmpty(id.Value))
            return false;

        if (_state.ProcessedReminderIds.Contains(id))
            return true;

        if (_inFlightDedup.IsReminderInFlight(id))
            return true;

        if (!includeBuffered)
            return false;

        return _buffer.Any(buffered => buffered.Source?.ReminderId == id);
    }

    private bool IsBackgroundJobDedupHit(BackgroundJobId? bgJobId, bool includeBuffered)
    {
        if (bgJobId is not { } id || string.IsNullOrEmpty(id.Value))
            return false;

        if (_state.ProcessedBackgroundJobIds.Contains(id))
            return true;

        if (_inFlightDedup.IsBackgroundJobInFlight(id))
            return true;

        if (!includeBuffered)
            return false;

        return _buffer.Any(buffered => buffered.Source?.BackgroundJobId == id);
    }

    private bool ShouldCompact()
    {
        var limit = _model.CompactionTokenLimit(_config.Tuning.CompactionThreshold);
        return limit > 0
            && _lastInputTokenCount >= limit;
    }

    private void CommandSubscriptionMessages()
    {
        Command<WorkingContextSnapshotReady>(HandleWorkingContextSnapshotReady);
        Command<WorkingContextSnapshotCancelled>(msg =>
        {
            if (msg.Generation == _workingContextGeneration)
                TurnLog().Debug("working_context_inspection_cancelled generation={Generation}", msg.Generation);
        });
        Command<WorkingContextSnapshotFailed>(HandleWorkingContextSnapshotFailed);

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
            FailCurrentTurn(
                $"Skill '/{msg.SkillName}' routed to subagent '{msg.SubagentName}' failed: {msg.ErrorMessage}",
                new InvalidOperationException(msg.ErrorMessage),
                ErrorCategory.ToolFailure));
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
                Context.WatchWith(cmd.Subscriber,
                    new LeaveSession(cmd.Subscriber) { SessionId = _sessionId });

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
        foreach (var buffered in _buffer)
        {
            Self.Tell(buffered);
        }
        _buffer.Clear();
        CancelAndDisposeLlmCts();
        CancelAndDisposeToolExecutionCts();

        base.PreRestart(reason, message);
    }

    protected override void PostStop()
    {
        CancelAndDisposeLlmCts();
        CancelAndDisposeToolExecutionCts();

        // Safety net for non-graceful stop paths (shutdown timeout, OOM, etc.).
        if (!_passivationCompleted)
            _lifecycleObserver?.OnSessionDeactivated(_sessionId);

        base.PostStop();
    }

    private void CancelAndDisposeLlmCts()
    {
        _activeLlmCts?.Cancel();
        _activeLlmCts?.Dispose();
        _activeLlmCts = null;
    }

    private void CancelAndDisposeToolExecutionCts()
    {
        _activeToolExecutionCts?.Cancel();
        _activeToolExecutionCts?.Dispose();
        _activeToolExecutionCts = null;
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

    private string GetSessionDirectory() =>
        SessionDirectoryHelper.GetSessionDirectory(_sessionId, _sessionsBasePath);

    private long NowMs() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private void SetSystemPrompt()
    {
        var audience = CurrentTurnAudience();
        var content = _promptProvider.GetSystemPrompt(audience, _state.WorkingContext.ProjectDirectory);
        if (string.IsNullOrWhiteSpace(content))
        {
            // Retain the last-known prompt from recovery — deleting it strips the agent
            // of all identity and project context, which is worse than a stale prompt.
            if (_state.History.Count > 0 && _state.History[0].Role == Protocol.ChatRole.System)
            {
                _log.Warning("Identity files missing — retaining last-known system prompt from recovery");
            }
            else
            {
                _log.Info("No system prompt layers available");
            }
            return;
        }

        // Idempotent on content: if the freshly-rebuilt prompt is
        // byte-identical to history[0], skip the replacement. The
        // immutable-list SetItem allocates a new spine even when the value
        // is unchanged, and the rebuild itself drops the persisted prompt
        // from llama.cpp's KV cache from token 0 because the underlying
        // prompt bytes change identity. PR #1171 fixed the volatile-tail
        // merge; this fix closes the residual cache-bust path where
        // SetSystemPrompt fires unconditionally on every channel-driven
        // turn even when on-disk identity files are unchanged.
        if (_state.History.Count > 0
            && _state.History[0].Role == Protocol.ChatRole.System
            && string.Equals(_state.History[0].Content, content, StringComparison.Ordinal))
        {
            return;
        }

        var systemMsg = new SerializableChatMessage
        {
            Role = Protocol.ChatRole.System,
            Content = content
        };

        // Replace or insert at position 0 — never persisted, always read fresh from disk
        _state = _state.History.Count > 0 && _state.History[0].Role == Protocol.ChatRole.System
            ? _state with { History = _state.History.SetItem(0, systemMsg) }
            : _state with { History = _state.History.Insert(0, systemMsg) };

        // Hash is included so a future cache-drift investigation can confirm
        // at a glance whether two "System prompt set" lines carry the same
        // content or genuinely different content.
        _log.Info("System prompt set ({PromptLength} chars, hash={ContentHash})",
            content.Length,
            ShortContentHash(content));
    }

    private static string ShortContentHash(string content)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }

    private void FireLlmCall(string? recallQuery = null, bool forceNoTools = false)
    {
        _anyContentStreamed = false;
        CancelAndDisposeLlmCts();
        _activeLlmCts = new CancellationTokenSource();
        _activeCallId++;
        var workingContextGeneration = ++_workingContextGeneration;

        _turnState.ForceNoToolsActive = forceNoTools;

        // Recall: only resolve on turn-start calls, reuse cache for tool-loop follow-ups
        if (_recallManager.TurnRecallCache is null)
        {
            var recallSw = Stopwatch.StartNew();
            var resolved = _recallManager.ResolveForTurn(
                recallQuery,
                _state,
                _sessionId,
                _currentTurnSource,
                _memoryRecallCoordinator,
                _memoryConfig,
                turnContext: _currentTurnContext);
            recallSw.Stop();

            resolved = _recallManager.ApplyProgressiveRecall(resolved, _log);

            // Observer audit: track which memories were recalled this turn
            // independently of how they end up on the wire. Downstream
            // distillation prompts grep for "[recalled-memory]" entries.
            if (resolved.Items.Count > 0)
            {
                var recallContent = SessionRecallManager.FormatForHistory(resolved);
                _observerActor?.Tell(new ObserverSystemContext("recalled-memory", recallContent));
            }

            var recallIds = resolved.Items.Count == 0
                ? "-"
                : string.Join(",", resolved.Items.Select(i => i.Id.Value));
            TurnLog().Info(
                "turn_memory_recall degraded={Degraded} stage={Stage} durationMs={DurationMs} itemCount={ItemCount} itemIds={ItemIds}",
                resolved.Degraded,
                resolved.DegradeStage ?? "-",
                recallSw.ElapsedMilliseconds,
                resolved.Items.Count,
                recallIds);

            if (resolved.Items.Count > 0)
                _sessionMetrics?.RecordMemoriesRecalled(resolved.Items.Count);

            // Insert the FULL volatile context block (recall + current
            // time + working context + skill hint + slash command body +
            // session prompt overlay + turn restart notice + active
            // background jobs) into History via AddVolatileContextNudge.
            // Gated on the same first-call-of-turn sentinel as recall
            // resolution, so tool-loop iterations don't re-nudge. The block
            // is placed BEFORE the real user message (not after) so the user
            // message stays at the tail of the user-portion — a trailing
            // volatile User-role message is read by strict ChatML templates
            // (Qwen3) as a fresh user turn and causes the tool-loop
            // acknowledgement spin (see AddVolatileContextNudge). By living
            // in History (rather than being wrapped at assemble-time), the
            // volatile bytes let every byte-prefix caching provider
            // (llama.cpp, vLLM, OpenAI, Ollama, ...) extend the cache prefix
            // through this content on every subsequent turn instead of
            // re-tokenizing it from scratch.
            _activeRecall = _recallManager.TurnRecallCache;
            _ = CreateWorkingContextContinuationAsync(
                    workingContextGeneration,
                    forceNoTools,
                    _turnRestartNotice,
                    _state.WorkingContext,
                    CurrentTurnAudience(),
                    _activeLlmCts.Token)
                .PipeTo(Self);
            return;
        }

        ContinueFireLlmCall(forceNoTools);
    }

    private void ContinueFireLlmCall(bool forceNoTools)
    {
        _activeRecall = _recallManager.TurnRecallCache;

        // Build the full message list via the cache-stable assembler.
        // Static content (persisted prompt, OnceAtStart layers, [session],
        // [attachments]) sits at the head so the prompt prefix stays
        // byte-stable across turns. Volatile per-turn content (memory
        // recall, current time, working context, etc.) was already
        // persisted into History above via AddSystemNudge — the assembler
        // just emits the static head + history verbatim. See
        // SessionMessageAssembler for the full assembly contract. Mark
        // startup injection complete after the first call to preserve the
        // existing OnceAtStart semantics.
        var skillHint = BuildSkillHint();
        var messages = SessionMessageAssembler.Assemble(new ContextAssemblyInput(
            State: _state,
            ContextLayers: _contextLayers,
            StartupContextInjected: _startupContextInjected,
            SlashCommandSkillContent: _slashCommandSkillContent,
            SessionPromptOverlay: _sessionPromptOverlay,
            TurnRestartNotice: _turnRestartNotice,
            SessionId: _sessionId,
            SessionsBasePath: _sessionsBasePath,
            FileReadGranted: HasFileReadGranted(),
            ActiveRecall: _activeRecall,
            WorkingContextBlock: string.Empty,
            Audience: CurrentTurnAudience(),
            SkillHint: skillHint,
            // Canonical names live in history (post-PR follow-up); the
            // LLM provider wants the sanitized alias back on the wire.
            ToolNameToLlmFacing: _toolRegistry is null ? null : _toolRegistry.ToLlmFacingName));
        _startupContextInjected = true;

        var self = Self;
        var client = _chatClient;

        var exposedTools = ResolveExposedToolsForCurrentTurn();
        // Always carry the session id so the session-agnostic chat-client decorators
        // (logging/retry/routing) can correlate LLM diagnostics — including provider
        // failover/outage — back to this session in Seq. Tools are attached only when
        // the turn exposes them; an empty Tools list is wire-equivalent to no options.
        var options = new SessionScopedChatOptions { SessionId = _sessionId.Value };
        if (!forceNoTools && exposedTools.Count > 0)
            options.Tools = [.. exposedTools];

        _watchdog.Start(ProcessingWatchdog.LlmCall, _config.PrefillTimeout, Timers, _config.NoProgressTimeout);

        TurnLog().Info("turn_llm_call_start messages={MessageCount} toolsEnabled={ToolsEnabled} forceNoTools={ForceNoTools} callId={CallId}",
            messages.Count,
            options.Tools?.Count > 0,
            forceNoTools,
            _activeCallId);

        _ = SessionLlmInvoker.InvokeAsync(client, messages, options, self, _activeCallId, _sessionId, _activeLlmCts!.Token);
    }

    private async Task<INoSerializationVerificationNeeded> CreateWorkingContextContinuationAsync(
        long generation,
        bool forceNoTools,
        string? turnRestartNotice,
        WorkingContext workingContext,
        TrustAudience audience,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _workingContextSnapshots.CreateAsync(
                workingContext,
                audience,
                cancellationToken).ConfigureAwait(false);
            return new WorkingContextSnapshotReady(generation, forceNoTools, turnRestartNotice, snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new WorkingContextSnapshotCancelled(generation);
        }
        catch (Exception ex)
        {
            return new WorkingContextSnapshotFailed(
                generation,
                forceNoTools,
                turnRestartNotice,
                workingContext,
                ex);
        }
    }

    private void HandleWorkingContextSnapshotFailed(WorkingContextSnapshotFailed message)
    {
        if (!ShouldApplyWorkingContextSnapshot(
                message.Generation,
                _workingContextGeneration,
                _activeLlmCts is not null))
            return;

        TurnLog().Warning(
            message.Cause,
            "working_context_inspection_failed generation={Generation}",
            message.Generation);
        HandleWorkingContextSnapshotReady(new WorkingContextSnapshotReady(
            message.Generation,
            message.ForceNoTools,
            message.TurnRestartNotice,
            new WorkingContextSnapshot
            {
                WorkingContext = message.WorkingContext,
                Git = new GitWorkingContextInspection.Unavailable("working context inspection failed")
            }));
    }

    private void HandleWorkingContextSnapshotReady(WorkingContextSnapshotReady message)
    {
        if (!ShouldApplyWorkingContextSnapshot(
                message.Generation,
                _workingContextGeneration,
                _activeLlmCts is not null))
        {
            TurnLog().Debug(
                "working_context_inspection_stale generation={Generation} activeGeneration={ActiveGeneration}",
                message.Generation,
                _workingContextGeneration);
            return;
        }

        var volatileBlock = SessionMessageAssembler.BuildVolatileContextBlock(new ContextAssemblyInput(
            State: _state,
            ContextLayers: _contextLayers,
            StartupContextInjected: _startupContextInjected,
            SlashCommandSkillContent: _slashCommandSkillContent,
            SessionPromptOverlay: _sessionPromptOverlay,
            TurnRestartNotice: message.TurnRestartNotice,
            SessionId: _sessionId,
            SessionsBasePath: _sessionsBasePath,
            FileReadGranted: HasFileReadGranted(),
            ActiveRecall: _activeRecall,
            WorkingContextBlock: message.Snapshot.ToContextBlock(),
            Audience: CurrentTurnAudience(),
            SkillHint: BuildSkillHint()));
        if (!string.IsNullOrEmpty(volatileBlock))
            _state = _state.AddVolatileContextNudge(volatileBlock);

        ContinueFireLlmCall(message.ForceNoTools);
    }

    internal static bool ShouldApplyWorkingContextSnapshot(
        long snapshotGeneration,
        long activeGeneration,
        bool hasActiveLlmCall) => hasActiveLlmCall && snapshotGeneration == activeGeneration;


    private TrustAudience CurrentTurnAudience()
        => _currentTurnContext?.Audience
           ?? _currentTurnSource?.Audience
           ?? SecurityPolicyDefaults.ResolveAudienceFromSessionId(_sessionId.Value);

    private string CurrentMemoryAudience()
        => (_currentTurnContext?.Audience ?? _currentTurnSource?.Audience ?? TrustAudience.Public).ToWireValue();

    private string CurrentMemoryBoundary()
        => _currentTurnContext?.Boundary.Value
           ?? _currentTurnSource?.Boundary.Value
           ?? SecurityPolicyDefaults.ResolveBoundaryFromSessionId(_sessionId.Value, CurrentTurnAudience()).Value;

    private IReadOnlyList<AITool> ResolveExposedToolsForCurrentTurn()
    {
        var availableTools = _discoveredToolCache.AvailableTools;
        if (_toolAccessPolicy is null || _fullRegistry is null || availableTools.Count == 0)
            return availableTools;

        return _toolAccessPolicy.FilterExposedTools(availableTools, _fullRegistry, _currentTrustContext);
    }

    /// <summary>
    /// Returns true when <c>set_working_directory</c> is exposed to the
    /// current turn's audience profile. Used by the deny-path hint logic in
    /// <see cref="SessionToolExecutionPipeline"/>: the hint points the agent
    /// at the tool, so emitting it on a Public-audience turn that cannot call
    /// the tool would be misleading.
    /// </summary>
    private bool IsSetWorkingDirectoryAvailable()
    {
        if (_toolAccessPolicy is null || _fullRegistry is null)
            return false;

        var registration = _fullRegistry.GetRegistrationByToolName(SetWorkingDirectoryTool.ToolName);
        return registration is not null
               && _toolAccessPolicy.IsToolExposed(registration, _currentTrustContext);
    }


    private static readonly string SkillHintText =
        "[skill-hint] Before answering any technical or knowledge question, scan [available-skills] for a relevant skill and call skill_load(name=\"...\"). " +
        "Skills contain user-specific preferences and project conventions that override your general knowledge — even if you already know a topic, the user may have standards defined in a skill. " +
        "When in doubt, load — a redundant load is cheap, a missed skill is not.";

    /// <summary>
    /// Returns a generic per-turn skill reminder when skills are available,
    /// or null when the skill subsystem is inactive.
    /// </summary>
    private string? BuildSkillHint()
    {
        if (_skillRegistry is null || _skillRegistry.GetAll().Count == 0)
            return null;

        return SkillHintText;
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
            {
                EmitOutput(new TextOutput(decision.ErrorMessage!)
                {
                    SessionId = _sessionId
                }, OutputFilter.Text);
                EmitOutput(new TurnCompleted
                {
                    SessionId = _sessionId,
                    TurnNumber = new TurnNumber(_state.TurnCount),
                    Outcome = TurnOutcome.Skipped,
                    SourceReminderId = _currentTurnSource?.ReminderId
                });
                TryReplyAck();
                return true;
            }

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

        EmitOutput(new TextOutput(errorMsg.TrimEnd()) { SessionId = _sessionId }, OutputFilter.Text);
        EmitOutput(new TurnCompleted
        {
            SessionId = _sessionId,
            TurnNumber = new TurnNumber(_state.TurnCount),
            Outcome = TurnOutcome.Skipped,
            SourceReminderId = _currentTurnSource?.ReminderId
        });
        TryReplyAck();
        return true;
    }

    private bool HandleInlineSlashCommand(SkillEntry skill, string remainder, IReadOnlyList<SerializableMediaReference> mediaRefs)
    {
        string skillBody;
        try
        {
            var content = File.ReadAllText(skill.FilePath);
            skillBody = Skills.SkillScanner.ExtractBody(content);
        }
        catch (IOException ex)
        {
            _log.Warning("Failed to read skill file for slash command /{SkillName}: {Error}",
                skill.Name, ex.Message);
            EmitOutput(new TextOutput($"Failed to load skill /{skill.Name}: {ex.Message}\n\nThe skill file may be missing or corrupted.")
            {
                SessionId = _sessionId
            }, OutputFilter.Text);
            EmitOutput(new TurnCompleted
            {
                SessionId = _sessionId,
                TurnNumber = new TurnNumber(_state.TurnCount),
                Outcome = TurnOutcome.Skipped,
                SourceReminderId = _currentTurnSource?.ReminderId
            });
            TryReplyAck();
            return true;
        }

        _sessionMetrics?.RecordSkillLoaded(skill.Name, SkillLoadMethod.SlashCommand);

        var effectiveUserContent = string.IsNullOrWhiteSpace(remainder)
            ? $"The user invoked /{skill.Name}. Follow the skill instructions."
            : remainder;

        _state = _state.AddUserMessage(effectiveUserContent, mediaRefs.Count > 0 ? mediaRefs : null);
        TryReplyAck();
        _recallManager.ResetForNewTurn();

        _slashCommandSkillContent = skillBody;
        FireInitialTurnLlmCall(effectiveUserContent);
        _slashCommandSkillContent = null;

        TransitionTo(SessionPhase.Processing);
        return true;
    }

    private bool TryHandleRoutedSlashCommand(SkillEntry skill, string remainder, IReadOnlyList<SerializableMediaReference> mediaRefs, string routedSubagent)
    {
        if (_subAgentRegistry is null || _subAgentSpawner is null)
        {
            EmitOutput(new TextOutput($"Skill '/{skill.Name}' routes to subagent '{routedSubagent}', but subagent routing is not available in this runtime.")
            {
                SessionId = _sessionId
            }, OutputFilter.Text);
            EmitOutput(new TurnCompleted
            {
                SessionId = _sessionId,
                TurnNumber = new TurnNumber(_state.TurnCount),
                Outcome = TurnOutcome.Skipped,
                SourceReminderId = _currentTurnSource?.ReminderId
            });
            TryReplyAck();
            return true;
        }

        _subAgentLoader?.SyncInto(_subAgentRegistry);

        var profile = _subAgentRegistry.TryGetByName(routedSubagent);
        if (profile is null)
        {
            EmitOutput(new TextOutput(SkillActivationRouter.UnknownTargetError(skill.Name, routedSubagent))
            {
                SessionId = _sessionId
            }, OutputFilter.Text);
            EmitOutput(new TurnCompleted
            {
                SessionId = _sessionId,
                TurnNumber = new TurnNumber(_state.TurnCount),
                Outcome = TurnOutcome.Skipped,
                SourceReminderId = _currentTurnSource?.ReminderId
            });
            TryReplyAck();
            return true;
        }

        if (profile.Visibility != SubAgentVisibility.UserFacing)
        {
            EmitOutput(new TextOutput(SkillActivationRouter.InternalTargetError(skill.Name, routedSubagent))
            {
                SessionId = _sessionId
            }, OutputFilter.Text);
            EmitOutput(new TurnCompleted
            {
                SessionId = _sessionId,
                TurnNumber = new TurnNumber(_state.TurnCount),
                Outcome = TurnOutcome.Skipped,
                SourceReminderId = _currentTurnSource?.ReminderId
            });
            TryReplyAck();
            return true;
        }

        string skillBody;
        try
        {
            var content = File.ReadAllText(skill.FilePath);
            skillBody = Skills.SkillScanner.ExtractBody(content);
        }
        catch (IOException ex)
        {
            _log.Warning("Failed to read skill file for routed slash command /{SkillName}: {Error}",
                skill.Name, ex.Message);
            EmitOutput(new TextOutput($"Failed to load skill /{skill.Name}: {ex.Message}\n\nThe skill file may be missing or corrupted.")
            {
                SessionId = _sessionId
            }, OutputFilter.Text);
            EmitOutput(new TurnCompleted
            {
                SessionId = _sessionId,
                TurnNumber = new TurnNumber(_state.TurnCount),
                Outcome = TurnOutcome.Skipped,
                SourceReminderId = _currentTurnSource?.ReminderId
            });
            TryReplyAck();
            return true;
        }

        _sessionMetrics?.RecordSkillLoaded(skill.Name, SkillLoadMethod.SlashCommand);

        var effectiveTask = string.IsNullOrWhiteSpace(remainder)
            ? $"The user invoked /{skill.Name}. Execute the routed skill instructions and return a concise result."
            : remainder;

        _state = _state.AddUserMessage(effectiveTask, mediaRefs.Count > 0 ? mediaRefs : null);
        TryReplyAck();
        _recallManager.ResetForNewTurn();

        _ = ExecuteRoutedSkillAsync(Self, skill, profile, effectiveTask, skillBody);
        TransitionTo(SessionPhase.Processing);
        return true;
    }

    private async Task ExecuteRoutedSkillAsync(
        IActorRef self,
        SkillEntry skill,
        SubAgentProfile profile,
        string task,
        string skillBody)
    {
        try
        {
            Func<object, string, CancellationToken, Task<object>> spawnChildActor = async (props, name, ct) =>
                await self.Ask<IActorRef>(
                    new SpawnChildActorRequest((Props)props, name),
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
                Session = new ToolSessionScope.Bound(_sessionId.Value, GetSessionDirectory()),
                // No active turn context/source carries no trust context — fall closed.
                Audience = _currentTurnContext?.Audience ?? _currentTurnSource?.Audience ?? TrustAudience.Public,
                InlineOutputBudget = new InlineOutputBudget(_config.Tuning.MaxInlineToolResultChars),
                Boundary = _currentTurnContext?.Boundary ?? _currentTurnSource?.Boundary,
                ChannelType = _currentTurnContext?.ChannelType?.ToWireValue()
                              ?? (_currentTurnSource is null ? null : _currentTurnSource.ChannelType.ToWireValue()),
                ProjectDirectory = _state.WorkingContext.ProjectDirectory,
                RecentFiles = _state.WorkingContext.RecentFiles,
                SupportsInteractiveApproval = false,
                SpawnChildActor = spawnChildActor,
            }, new ToolExecutionTimeout(_config.ToolExecutionTimeout), outputs);

            var result = await _subAgentSpawner!.SpawnAsync(
                profile,
                task,
                runtimeContext: null,
                context.Invocation,
                CancellationToken.None,
                systemPromptOverlay: skillBody);

            self.Tell(new RoutedSkillExecutionCompleted(skill.Name, profile.Name, result));
        }
        catch (Exception ex)
        {
            self.Tell(new RoutedSkillExecutionFailed(skill.Name, profile.Name, ex.Message));
        }
    }

    private void HandleRoutedSkillExecutionCompleted(RoutedSkillExecutionCompleted msg)
    {
        if (!msg.Result.Success)
        {
            FailCurrentTurn(
                $"Skill '/{msg.SkillName}' routed to subagent '{msg.SubagentName}' failed: {msg.Result.Output}",
                new InvalidOperationException(msg.Result.Output),
                ErrorCategory.ToolFailure);
            return;
        }

        MergeSuccessfulSubAgentWorkingContext(msg.Result.Completion);

        var userMsg = _state.FindLastUserMessage();
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
                Content = msg.Result.Output
            },
            RecordedAtMs = NowMs(),
            SourceReminderId = _currentTurnSource?.ReminderId,
            SourceBackgroundJobId = _currentTurnSource?.BackgroundJobId
        };

        Persist(turnEvent, evt =>
        {
            _inFlightDedup.CompleteReminder(evt.SourceReminderId);
            _inFlightDedup.CompleteBackgroundJob(evt.SourceBackgroundJobId);

            var processed = _state.ProcessedReminderIds;
            if (evt.SourceReminderId is { } reminderId && !string.IsNullOrEmpty(reminderId.Value))
            {
                processed = processed.Add(reminderId);
            }

            _state = (_state with
            {
                History = _state.History.Add(evt.AssistantReply),
                TurnCount = _state.TurnCount + 1,
                ProcessedReminderIds = processed
            }).CompleteTurnBackgroundJobBookkeeping(evt.SourceBackgroundJobId);

            EmitOutput(new TextOutput(msg.Result.Output)
            {
                SessionId = _sessionId
            }, OutputFilter.Text);

            EmitOutput(new TurnCompleted
            {
                SessionId = _sessionId,
                TurnNumber = new TurnNumber(_state.TurnCount),
                Outcome = TurnOutcome.Completed,
                SourceReminderId = _currentTurnSource?.ReminderId
            });

            MaybeSnapshot();
            MaybeGenerateTitle();
            DrainBufferedMessagesOrBecomeReady();
        });
    }

    private void MergeSuccessfulSubAgentWorkingContext(ChildRunCompletion completion)
    {
        var updated = MergeSuccessfulSubAgentWorkingContext(_state.WorkingContext, completion);
        if (!ReferenceEquals(updated, _state.WorkingContext))
            _state = _state with { WorkingContext = updated };
    }

    internal static WorkingContext MergeSuccessfulSubAgentWorkingContext(
        WorkingContext current,
        ChildRunCompletion completion) => completion switch
        {
            ChildRunCompletion.Completed completed => MergeConfirmedChanges(current, completed.WorkingContext),
            ChildRunCompletion.Partial partial => MergeConfirmedChanges(current, partial.WorkingContext),
            ChildRunCompletion.Failed or ChildRunCompletion.Cancelled => current,
            _ => throw new ArgumentOutOfRangeException(nameof(completion))
        };

    private static WorkingContext MergeConfirmedChanges(WorkingContext current, WorkingContextDelta child)
    {
        var updated = current;
        foreach (var path in child.ConfirmedChangedFiles)
            updated = updated.AddRecentFile(path);

        return updated;
    }

    // Transient: skill body injected by slash-command dispatch for the current turn
    private string? _slashCommandSkillContent;
    private string? _sessionPromptOverlay;

    private bool HasFileReadGranted()
    {
        foreach (var tool in _discoveredToolCache.AvailableTools)
        {
            if (tool is AIFunction fn && string.Equals(fn.Name, FileReadTool.ToolName, StringComparison.Ordinal))
                return true;
        }
        return false;
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
    /// Attempt to activate a single discovered tool by name.
    /// Checks registry, access policy, and adds to the available tools cache.
    /// </summary>
    private bool TryActivateDiscoveredTool(string toolName)
    {
        if (_fullRegistry is null) return false;

        var registration = _fullRegistry.GetRegistrationByToolName(toolName);
        if (registration is null) return false;

        if (_toolAccessPolicy is not null && !_toolAccessPolicy.IsToolExposed(registration, _currentTrustContext))
            return false;

        var tool = registration.Tool;
        // Cache and log under the canonical name regardless of which form
        // the LLM sent (load_tool / search_tools now emit the LLM-facing
        // alias for MCP, but legacy strings may still arrive). Cache key
        // consistency makes per-tool lease accounting unambiguous.
        var canonicalName = tool.Name;
        _discoveredToolCache.Remember(canonicalName, tool,
            _config.Tuning.DiscoveredToolRetentionTurns,
            _config.Tuning.DiscoveredToolMaxCount);
        if (_discoveredToolCache.AddIfMissing(tool.ToAITool()))
            _log.Info("Dynamically loaded tool '{ToolName}' into session", canonicalName);
        return true;
    }

    private SessionSnapshot BuildSnapshot()
    {
        return _state.ToSnapshot() with
        {
            EligibleDeliveryTurnNumber = _deliveryRetry.EligibleTurnNumber
        };
    }

    private void ApplyTurnRecorded(TurnRecorded evt)
    {
        var lastUser = _state.FindLastUserMessage();
        if (lastUser == evt.UserMessage)
        {
            _state = (_state with
            {
                History = _state.History.Add(evt.AssistantReply),
                TurnCount = _state.TurnCount + 1
            }).CompleteTurnBackgroundJobBookkeeping(evt.SourceBackgroundJobId);
            return;
        }

        _state = _state.Apply(evt);
    }

    private void ApplyToolBatchStarted(ToolBatchStarted evt)
    {
        ApplyToolBatchHistory(evt);
        RestoreActiveToolBatchFrom(evt);
    }

    private void ApplyToolBatchHistory(ToolBatchStarted evt)
    {
        if (_state.FindLastUserMessage() != evt.UserMessage)
            _state = _state with { History = _state.History.Add(evt.UserMessage) };

        if (!_state.History.Contains(evt.AssistantMessage))
            _state = _state with { History = _state.History.Add(evt.AssistantMessage) };
    }

    private void RestoreActiveToolBatchFrom(ToolBatchStarted evt)
    {
        _activeToolBatch.Start(
            evt.AssistantMessage,
            ParkedToolBatchHistory.FindToolResultsFor(_state.History, evt.AssistantMessage));
    }

    private void ApplyToolCallRecorded(ToolCallRecorded evt)
    {
        var alreadyRecorded = false;
        if (evt.ToolResult.ToolCallId is { } toolCallId)
        {
            _activeToolBatch.RecordCompleted(toolCallId.Value);

            if (ParkedToolBatchHistory.HasToolResult(_state.History, toolCallId.Value))
                alreadyRecorded = true;
        }

        if (!alreadyRecorded)
        {
            _state = _state with { History = _state.History.Add(evt.ToolResult) };
            _mediaBuffer.Add(evt.ToolResult.MediaReferences);

            if (_activeToolBatch.HasAllResults)
            {
                AddModelInputMediaNudge(_mediaBuffer.DrainSnapshot());
            }
        }
    }

    private void HandleToolInteractionRequestDispatch(ToolInteractionRequestDispatch dispatch)
    {
        var msg = dispatch.Request;
        var evt = new ToolApprovalRequested
        {
            SessionId = _sessionId,
            CallId = msg.CallId.Value,
            ToolName = msg.ToolName.Value,
            Patterns = msg.Patterns,
            CandidateVerbs = msg.CandidateVerbs,
            Audience = _currentTurnContext?.Audience ?? CurrentTurnAudience(),
            Boundary = _currentTurnContext?.Boundary ?? _currentTurnSource?.Boundary,
            ChannelType = _currentTurnContext?.ChannelType?.ToWireValue() ?? _currentTurnSource?.ChannelType.ToWireValue(),
            SupportsInteractiveApproval = _currentTurnContext?.SupportsInteractiveApproval
                                          ?? _currentTurnSource?.ChannelType.SupportsInteractiveApproval(),
            RequesterSenderId = msg.RequesterSenderId,
            RequesterPrincipal = msg.RequesterPrincipal,
            HasThirdPartyAdoptedContext = msg.HasThirdPartyAdoptedContext,
            AdoptedSpeakerIds = msg.AdoptedSpeakerIds,
            Cwd = msg.Cwd,
            OptionKeys = msg.Options.Select(o => o.Key.Value).ToArray(),
            Candidates = msg.Candidates,
            TurnContext = _currentTurnContext?.ToRecord(),
            RequestedAtMs = NowMs()
        };

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
        });
    }

    private void ApplyToolApprovalRequested(ToolApprovalRequested evt)
        => ApplyToolApprovalRequested(evt, persistApprovalState: true);

    private void ApplyToolApprovalRequested(ToolApprovalRequested evt, bool persistApprovalState)
    {
        var turnContext = ToolApprovalTurnContext.Restore(evt, out var restoreFailure);
        _pendingToolInteractions[evt.CallId] = new PendingToolInteraction(
            evt.CallId,
            evt.ToolName,
            evt.Patterns,
            evt.CandidateVerbs,
            evt.Audience,
            evt.Boundary,
            evt.ChannelType,
            evt.SupportsInteractiveApproval,
            evt.RequesterSenderId?.Value,
            evt.RequesterPrincipal,
            evt.HasThirdPartyAdoptedContext,
            evt.AdoptedSpeakerIds,
            evt.Cwd,
            evt.RequestedAtMs,
            persistApprovalState,
            turnContext,
            restoreFailure,
            evt.OptionKeys,
            evt.Candidates);
        _resolvedToolApprovals.Remove(evt.CallId);

        if (persistApprovalState && turnContext is not null)
            RecordWaitingApprovalState(turnContext, evt.CallId, recovered: _phase.Current == SessionPhase.Recovering);
        else if (persistApprovalState && restoreFailure is not null)
            _log.Warning(
                "Approval request {CallId} could not restore turn context: {Reason}",
                evt.CallId,
                restoreFailure);
    }

    private void RecordWaitingApprovalState(TurnContext context, string callId, bool recovered)
    {
        var pendingCallIds = _approvalTurnState is WaitingApprovalTurn waiting
            ? new HashSet<string>(waiting.PendingCallIds, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        pendingCallIds.Add(callId);

        _currentTurnContext = context;
        _approvalTurnState = new WaitingApprovalTurn(context, pendingCallIds, recovered);
    }

    private void MarkApprovalRedrive(PendingToolInteraction pending, string callId)
    {
        if (pending.TurnContext is null)
            return;

        _currentTurnContext = pending.TurnContext;
        _approvalTurnState = new RedrivingApprovalTurn(pending.TurnContext, callId);
    }

    private void MarkApprovalRunningAfterRedrive()
    {
        if (_approvalTurnState is RedrivingApprovalTurn redriving)
            _approvalTurnState = new RunningApprovalTurn(redriving.Context);
    }

    private void ClearApprovalTurnState()
    {
        _approvalTurnState = ApprovalTurnState.None;
        _currentTurnContext = null;
    }

    private void ApplyToolApprovalResolved(ToolApprovalResolved evt)
    {
        var decision = Enum.TryParse<ApprovalDecision>(evt.Decision, ignoreCase: true, out var parsed)
            ? parsed
            : ApprovalDecision.Denied;

        if (_pendingToolInteractions.Remove(evt.CallId, out var pending))
        {
            _resolvedToolApprovals[evt.CallId] = new ResolvedToolApproval(pending, decision);

            if (_pendingToolInteractions.Count == 0 && pending.TurnContext is not null)
                _approvalTurnState = new RunningApprovalTurn(pending.TurnContext);
        }
    }

    private void ApplyToolBatchAbandoned(ToolBatchAbandoned evt)
    {
        foreach (var result in evt.ToolResults)
            ApplyToolCallRecorded(new ToolCallRecorded
            {
                SessionId = evt.SessionId,
                ToolResult = result,
                RecordedAtMs = evt.AbandonedAtMs
            });
        _pendingToolInteractions.Clear();
        _resolvedToolApprovals.Clear();
        ClearApprovalTurnState();
        ClearActiveToolBatchTracking();
    }

    private void ClearActiveToolBatchTracking()
    {
        _activeToolBatch.Clear();
        _mediaBuffer.Clear();
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
        if (_pendingToolInteractions.Count > 0
            || _resolvedToolApprovals.Count > 0
            || ParkedToolBatchHistory.FindRedrivableAssistantMessage(_state.History, null) is not null)
        {
            _log.Info("Skipping snapshot while approval-paused tool batch is still unresolved");
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
            SourceReminderId = _currentTurnSource?.ReminderId
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

    /// <summary>
    /// Maps a <see cref="ToolInteractionResponse"/> option key to an
    /// <see cref="ApprovalDecision"/>. Shared by the live-<c>Processing</c>
    /// handler and the idle re-drive handler so the two paths never diverge.
    /// Any unrecognized key falls closed to <see cref="ApprovalDecision.Denied"/>.
    /// </summary>
    private static ApprovalDecision MapApprovalDecision(string selectedKey) => selectedKey switch
    {
        ApprovalOptionKeys.ApproveOnce => ApprovalDecision.ApprovedOnce,
        ApprovalOptionKeys.ApproveSession => ApprovalDecision.ApprovedSession,
        ApprovalOptionKeys.ApproveAlways => ApprovalDecision.ApprovedAlways,
        ApprovalOptionKeys.ApproveEverywhere => ApprovalDecision.ApprovedEverywhere,
        ApprovalOptionKeys.Deny => ApprovalDecision.Denied,
        _ => ApprovalDecision.Denied
    };

    private bool HasApprovalHistory
        => _resolvedToolApprovals.Count > 0
        || ParkedToolBatchHistory.FindRedrivableAssistantMessage(_state.History, null) is not null;

    /// <summary>
    /// Emits the channel-visible "approval prompt expired" notice. Used when a
    /// tool interaction response cannot be honored — fail loud instead of
    /// silently dropping the click (constitution: no silent fallbacks).
    /// </summary>
    private void EmitExpiredPromptNotice()
        => EmitOutput(new TextOutput(
            "That approval prompt has expired — the session moved on or restarted. "
            + "Please re-issue the request and I'll ask again if approval is needed.")
        {
            SessionId = _sessionId
        }, OutputFilter.Text);

    /// <summary>
    /// Classifies why a tool-interaction response could not be matched to a
    /// pending interaction. Intent is operational triage: the same expired
    /// notice ships to the channel either way, but the log line tells the
    /// operator whether the click was redundant (already resolved or completed)
    /// or genuinely unknown (silent-drop suspect).
    /// </summary>
    private string ClassifyUnknownApprovalCall(string callId)
    {
        if (_resolvedToolApprovals.ContainsKey(callId))
            return "already_resolved";

        if (ParkedToolBatchHistory.HasToolResult(_state.History, callId))
            return "already_completed";

        if (ParkedToolBatchHistory.FindRedrivableAssistantMessage(_state.History, callId) is not null)
            return "orphaned_assistant_tool_use";

        return "unknown";
    }

    /// <summary>
    /// Shared authorization and grant-persistence for a tool-approval response.
    /// Used by both the live-<c>Processing</c> handler and the idle re-drive
    /// handler so the requester check and grant-scope rules stay in lockstep.
    /// Returns the decision, or null when the responder is not the requesting
    /// user — a rejection notice has already been emitted in that case. On a
    /// non-null return means authorization succeeded; the caller is
    /// responsible for journaling <see cref="ToolApprovalResolved"/>.
    /// </summary>
    private async Task<(ApprovalDecision? Decision, string? NackReason)> AuthorizeApprovalResponseAsync(
        PendingToolInteraction pending,
        ToolInteractionResponse msg,
        bool persistApprovalGrant)
    {
        if (!TryGetApprovalAuthority(pending, out var requesterPrincipal, out var requesterSenderId, out var authorityFailure))
        {
            _log.Warning(
                "Ignoring tool interaction response for call {CallId}: approval authority context is not recoverable ({Reason})",
                msg.CallId,
                authorityFailure);
            EmitExpiredPromptNotice();
            return (null, ApprovalNackReasons.PromptExpired);
        }

        // Only the user who triggered the request may approve it — verified
        // automation requests can be approved by any channel member.
        if (!ApprovalButtonValueCodec.CanApprove(
                requesterPrincipal, requesterSenderId, msg.SenderId.Value))
        {
            _log.Warning(
                "Ignoring tool interaction response for call {CallId} from sender {SenderId}; expected {ExpectedSenderId}",
                msg.CallId, msg.SenderId, requesterSenderId);
            EmitWrongRequesterApprovalNotice();
            return (null, ApprovalNackReasons.WrongRequester);
        }

        // Legacy journal entries created before option persistence landed have
        // an empty OptionKeys list. Skip this check for that concrete recovery
        // path only; live prompts always persist their offered option keys.
        if (pending.OptionKeys.Count > 0
            && !pending.OptionKeys.Any(key => string.Equals(key, msg.SelectedKey.Value, StringComparison.Ordinal)))
        {
            _log.Warning(
                "Ignoring unavailable approval option {SelectedKey} for call {CallId}; offered options were [{OptionKeys}]",
                msg.SelectedKey,
                msg.CallId,
                string.Join(", ", pending.OptionKeys));
            EmitUnavailableApprovalOptionNotice();
            return (null, ApprovalNackReasons.OptionUnavailable);
        }

        var decision = MapApprovalDecision(msg.SelectedKey.Value);
        _log.Info("Approval response for {CallId}: {Decision}", msg.CallId, decision);

        if (persistApprovalGrant)
            await PersistApprovalGrantIfNeededAsync(pending, decision, CancellationToken.None);

        return (decision, null);
    }

    private async Task PersistApprovalGrantIfNeededAsync(
        PendingToolInteraction pending,
        ApprovalDecision decision,
        CancellationToken ct)
    {
        // Persistent scopes write a durable grant so future invocations — and
        // the re-driven call itself — pass the gate without another prompt.
        if (decision is ApprovalDecision.ApprovedSession
                or ApprovalDecision.ApprovedAlways
                or ApprovalDecision.ApprovedEverywhere
            && _approvalService is not null)
        {
            await PersistApprovalCandidatesAsync(pending, decision, ct);
        }
    }

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

    private bool TryResolveTextApprovalResponse(
        ToolInteractionTextResponse msg,
        out ToolInteractionResponse? structured,
        out string? nackReason)
    {
        structured = null;
        nackReason = null;

        var pending = ResolveLatestPendingApprovalForSender(msg.SenderId);
        if (pending is null)
        {
            if (_pendingToolInteractions.Count == 0)
            {
                if (HasApprovalHistory)
                {
                    _log.Warning("Ignoring text tool interaction response with no pending approvals for sender {SenderId}", msg.SenderId);
                    EmitExpiredPromptNotice();
                    nackReason = ApprovalNackReasons.PromptExpired;
                }
                else
                {
                    // Session has never had an approval request. The channel cold path
                    // matched the text as approval-like, but this is almost certainly
                    // ordinary conversation (e.g., "yes", "a", "1"). Don't emit a
                    // user-visible notice and don't consume — the channel should
                    // fall through to normal LLM ingress. See #1164.
                    nackReason = ApprovalNackReasons.NoHistory;
                }
            }
            else
            {
                _log.Warning("Ignoring text tool interaction response from unauthorized sender {SenderId}", msg.SenderId);
                EmitWrongRequesterApprovalNotice();
                nackReason = ApprovalNackReasons.WrongRequester;
            }

            return false;
        }

        var optionKeys = pending.OptionKeys.Count > 0
            ? pending.OptionKeys
            :
            [
                ApprovalOptionKeys.ApproveOnce,
                ApprovalOptionKeys.ApproveSession,
                ApprovalOptionKeys.ApproveAlways,
                ApprovalOptionKeys.Deny
            ];

        var options = optionKeys
            .Select(key => new ToolInteractionOption(new ApprovalOptionKey(key), ApprovalOptionKeys.LabelFor(key)))
            .ToArray();

        if (!ToolInteractionResponseParser.TryParseApprovalResponse(msg.Text, options, out var selectedKey)
            || selectedKey is null)
        {
            _log.Warning(
                "Ignoring unparseable text tool interaction response '{Text}' for call {CallId}; offered options were [{OptionKeys}]",
                msg.Text,
                pending.CallId,
                string.Join(", ", pending.OptionKeys));
            EmitUnavailableApprovalOptionNotice();
            nackReason = ApprovalNackReasons.OptionUnavailable;
            return false;
        }

        structured = new ToolInteractionResponse
        {
            SessionId = msg.SessionId,
            CallId = new ToolCallId(pending.CallId),
            SelectedKey = new ApprovalOptionKey(selectedKey),
            SenderId = msg.SenderId
        };
        return true;
    }

    private PendingToolInteraction? ResolveLatestPendingApprovalForSender(SenderId senderId)
        => _pendingToolInteractions.Values
            .Where(pending => CanApprovePending(pending, senderId))
            .OrderBy(pending => pending.RequestedAtMs)
            .LastOrDefault();

    private static bool CanApprovePending(PendingToolInteraction pending, SenderId senderId)
        => TryGetApprovalAuthority(pending, out var principal, out var requesterSenderId, out _)
           && ApprovalButtonValueCodec.CanApprove(principal, requesterSenderId, senderId.Value);

    private static bool TryGetApprovalAuthority(
        PendingToolInteraction pending,
        out PrincipalClassification? requesterPrincipal,
        out string? requesterSenderId,
        out string? failure)
    {
        if (pending.TurnContext is { } context)
        {
            requesterPrincipal = context.RequesterPrincipal;
            if (!context.HasApprovalRequester)
            {
                requesterSenderId = null;
                failure = "turn context has no requester sender for a non-automation principal";
                return false;
            }

            requesterSenderId = context.RequesterSenderId is { } senderId ? senderId.Value : null;
            failure = null;
            return true;
        }

        requesterPrincipal = pending.RequesterPrincipal;
        requesterSenderId = pending.RequesterSenderId;
        if (pending.TurnContextRestoreFailure is not null)
        {
            failure = pending.TurnContextRestoreFailure;
            return false;
        }

        if (requesterPrincipal is not PrincipalClassification.VerifiedAutomation
            && string.IsNullOrWhiteSpace(requesterSenderId))
        {
            failure = "legacy approval state has no requester sender for a non-automation principal";
            return false;
        }

        failure = null;
        return true;
    }

    private async Task HandleProcessingApprovalResponseAsync(ToolInteractionResponse msg)
    {
        if (!_pendingToolInteractions.TryGetValue(msg.CallId.Value, out var pending))
        {
            _log.Warning("Ignoring tool interaction response for unknown call {CallId}", msg.CallId);
            TryReplyNack(ApprovalNackReasons.PromptExpired);
            return;
        }

        ClaimedApprovalWait? claimedWait = null;
        try
        {
            var authorization = await AuthorizeApprovalResponseAsync(
                pending,
                msg,
                persistApprovalGrant: false);
            if (authorization.Decision is not { } decision)
            {
                TryReplyNack(authorization.NackReason ?? ApprovalNackReasons.WrongRequester);
                return;
            }

            // Claim the live wait before writing any broader grant. A response
            // can be authorized and still be stale; only a claimed wait proves
            // the prompt still corresponds to a blocked tool call.
            if (!_approvalChannel.TryClaim(msg.CallId, out var approvalWait))
            {
                _log.Warning(
                    "Ignoring tool interaction response for call {CallId}: prompt no longer has a live approval wait",
                    msg.CallId);
                EmitExpiredPromptNotice();
                TryReplyNack(ApprovalNackReasons.PromptExpired);
                return;
            }
            claimedWait = approvalWait;

            if (!pending.PersistApprovalState)
                _pendingToolInteractions.Remove(msg.CallId.Value);

            await PersistApprovalGrantIfNeededAsync(pending, decision, CancellationToken.None);

            if (!pending.PersistApprovalState)
            {
                // Live-only prompts should release the blocked child task, not
                // journal ToolApprovalResolved. After restart the child actor is
                // gone, so a durable redrive would be misleading.
                approvalWait.Complete(decision);
                TryReplyAck();
                return;
            }

            PersistApprovalResolved(msg, decision, () =>
            {
                approvalWait.Complete(decision);
                TryReplyAck();
            });
        }
        catch (Exception ex)
        {
            claimedWait?.Complete(ApprovalDecision.Denied);
            FailCurrentTurn("I couldn't persist that approval decision. Please try again.", ex, ErrorCategory.ToolFailure);
            TryReplyNack(ApprovalNackReasons.PersistFailed);
        }
    }

    private void PersistApprovalResolved(
        ToolInteractionResponse msg,
        ApprovalDecision decision,
        Action afterPersist)
    {
        Persist(new ToolApprovalResolved
        {
            SessionId = _sessionId,
            CallId = msg.CallId.Value,
            Decision = decision.ToString(),
            ResolvedAtMs = NowMs()
        }, evt =>
        {
            ApplyToolApprovalResolved(evt);
            afterPersist();
        });
    }

    /// <summary>
    /// Handles a <see cref="ToolInteractionResponse"/> that arrives when the
    /// session is NOT actively processing the parked tool batch — i.e. after
    /// idle passivation, cold recovery, or an aborted passivation. There is no
    /// live tool-loop task or <see cref="ApprovalChannel"/> TCS to complete, so
    /// the parked batch must be re-driven from the persisted assistant message.
    /// </summary>
    private async Task HandleToolInteractionResponseWhenIdle(ToolInteractionResponse msg)
    {
        var callId = msg.CallId.Value;

        if (!_pendingToolInteractions.TryGetValue(callId, out var pending))
        {
            // No persisted pending record — there is no turn context and no Patterns to
            // pre-seed an ApprovedOnce re-drive. Whether or not the history
            // tail still carries the tool_use block, treat the prompt as
            // expired and fail loud with a channel-visible message rather
            // than silently dropping the click.
            //
            // The classification helps triage "where did my click go" reports:
            // already_resolved means the approval landed but the click is a
            // duplicate (e.g., user double-clicked, or text + button both
            // arrived); no_history means the CallId was never observed by this
            // session — the most common silent-drop scenario when a binding
            // misroutes; orphaned_history means a tool_use exists with no
            // pending or resolved record, suggesting the batch was abandoned
            // (e.g., user sent a new message instead of approving).
            var classification = ClassifyUnknownApprovalCall(callId);
            _log.Warning(
                "Tool interaction response for unknown/expired call {CallId} ({Classification}); pending={PendingCount} resolved={ResolvedCount}",
                msg.CallId, classification, _pendingToolInteractions.Count, _resolvedToolApprovals.Count);
            EmitExpiredPromptNotice();
            TryReplyNack(ApprovalNackReasons.PromptExpired);
            return;
        }

        ApprovalDecision decision;
        try
        {
            var authorization = await AuthorizeApprovalResponseAsync(
                pending,
                msg,
                persistApprovalGrant: true);
            if (authorization.Decision is not { } authorizedDecision)
            {
                TryReplyNack(authorization.NackReason ?? ApprovalNackReasons.WrongRequester);
                return;
            }
            decision = authorizedDecision;
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

        PersistApprovalResolved(msg, decision, () =>
        {
            var outcome = TryRedriveToolBatchAfterApproval(callId);
            if (outcome == ApprovalRedriveOutcome.Failed)
            {
                TryReplyNack(ApprovalNackReasons.PromptExpired);
                return;
            }

            TryReplyAck();
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

    private ApprovalRedriveOutcome TryRedriveToolBatchAfterApproval(string callId)
    {
        var assistantMsg = ParkedToolBatchHistory.FindRedrivableAssistantMessage(_state.History, callId);
        if (assistantMsg is null)
        {
            _log.Warning(
                "Cannot re-drive tool batch for call {CallId}: no unanswered assistant tool batch in history",
                callId);
            EmitExpiredPromptNotice();
            return ApprovalRedriveOutcome.Failed;
        }

        if (assistantMsg.ToolCalls.Any(tc => _pendingToolInteractions.ContainsKey(tc.CallId.Value)))
        {
            _log.Info(
                "Deferring parked tool batch re-drive for call {CallId}: sibling approval(s) still pending",
                callId);
            return ApprovalRedriveOutcome.Deferred;
        }

        if (!_resolvedToolApprovals.TryGetValue(callId, out var resolved))
        {
            _log.Warning(
                "Cannot re-drive tool batch for call {CallId}: approval decision was not recoverable",
                callId);
            EmitExpiredPromptNotice();
            return ApprovalRedriveOutcome.Failed;
        }

        var redrivePlan = BuildApprovalRedrivePlan(assistantMsg);
        if (RedriveToolBatchForApproval(callId, resolved.Pending, redrivePlan))
            return ApprovalRedriveOutcome.Started;

        var abandoned = BuildToolBatchAbandonedEvent(
            "Tool call was not completed — approval state could not be recovered safely.");
        Persist(abandoned, ApplyToolBatchAbandoned);
        return ApprovalRedriveOutcome.Failed;
    }

    private bool AbandonResolvedToolBatchAfterRecovery()
    {
        if (_resolvedToolApprovals.Count == 0)
            return false;

        if (_pendingToolInteractions.Count > 0)
            return false;

        _log.Info(
            "Abandoning recovered parked tool batch with {ResolvedApprovalCount} resolved approval(s) after restart",
            _resolvedToolApprovals.Count);
        var abandoned = BuildResolvedToolBatchInterruptedByRestartEvent();
        Persist(abandoned, ApplyToolBatchAbandoned);

        return true;
    }

    private bool AbandonInterruptedToolBatchAfterRecovery()
    {
        if (!HasInterruptedToolBatchAfterRecovery())
            return false;

        _log.Info("Abandoning recovered interrupted tool batch with no recoverable approval state");
        Persist(BuildInterruptedToolBatchAfterRecoveryEvent(), ApplyToolBatchAbandoned);

        return true;
    }

    private bool HasInterruptedToolBatchAfterRecovery()
        => _pendingToolInteractions.Count == 0
        && _resolvedToolApprovals.Count == 0
        && ParkedToolBatchHistory.FindRedrivableAssistantMessage(_state.History, null) is not null;

    private ToolBatchAbandoned BuildResolvedToolBatchInterruptedByRestartEvent()
        => BuildToolBatchAbandonedEvent(
            "Tool call was not completed — the session restarted after approval before the action completed.");

    private ToolBatchAbandoned BuildInterruptedToolBatchAfterRecoveryEvent()
        => BuildToolBatchAbandonedEvent(
            "Tool call was not completed — the session restarted before the action completed.");

    private ApprovalRedrivePlan BuildApprovalRedrivePlan(SerializableChatMessage assistantMessage)
    {
        // Approval redrive is only for a live actor processing a fresh click
        // after the original tool-loop task is gone. If replay shows an approval
        // was already resolved before restart but no tool result was recorded,
        // we abandon the parked batch instead of replaying side effects.
        var preSeed = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var decisionOverride = new Dictionary<string, ApprovalDecision>(StringComparer.Ordinal);
        foreach (var call in assistantMessage.ToolCalls)
        {
            if (!_resolvedToolApprovals.TryGetValue(call.CallId.Value, out var resolved))
                continue;

            if (resolved.Decision == ApprovalDecision.ApprovedOnce)
            {
                // ApprovedOnce has no persisted grant. Pre-seed only this call
                // so the re-drive skips the gate once without broadening approval.
                preSeed[call.CallId.Value] = resolved.Pending.Patterns;
            }

            if (resolved.Decision is ApprovalDecision.Denied or ApprovalDecision.TimedOut)
            {
                // Denials and timeouts still need a tool_result so provider
                // history stays well-formed, but the reconstructed dispatch
                // must not execute the tool or ask for approval again.
                decisionOverride[call.CallId.Value] = resolved.Decision;
            }
        }

        return new ApprovalRedrivePlan(
            preSeed.Count == 0 ? null : preSeed,
            decisionOverride.Count == 0 ? null : decisionOverride);
    }

    /// <summary>
    /// Re-drives the parked tool batch after an approval decision was applied
    /// while the session was idle. Reconstructs the batch from the last
    /// assistant message in history whose tool calls have no later tool result,
    /// transitions to <see cref="SessionPhase.Processing"/>, and dispatches it
    /// under the parked turn's persisted trust context. After cold recovery
    /// <see cref="_currentTurnSource"/> is null, so the persisted trust fields
    /// are what keep the re-driven call faithful to the original turn.
    /// </summary>
    private bool RedriveToolBatchForApproval(
        string callId,
        PendingToolInteraction pending,
        ApprovalRedrivePlan redrivePlan)
    {
        var assistantMsg = ParkedToolBatchHistory.FindRedrivableAssistantMessage(_state.History, callId);
        if (assistantMsg is null)
        {
            _log.Warning(
                "Cannot re-drive tool batch for call {CallId}: no unanswered assistant tool batch in history",
                callId);
            EmitExpiredPromptNotice();
            return false;
        }

        // Rebuild the FunctionCallContent batch from the persisted assistant
        // message — tool arguments are durably stored in SerializableToolCall.
        // reinjectMeta re-applies the persisted per-call hints (timeout/background/
        // rationale) that were stripped into MetaJson at persistence, so a re-driven
        // call honors them instead of falling back to defaults.
        var aiMessage = ChatMessageConverter.ToAiMessage(assistantMsg, reinjectMeta: true);
        var toolCalls = aiMessage.Contents
            .OfType<FunctionCallContent>()
            .Where(tc => !ParkedToolBatchHistory.HasToolResult(_state.History, tc.CallId)
                && (tc.CallId == callId || !_pendingToolInteractions.ContainsKey(tc.CallId)))
            .ToList();
        if (toolCalls.Count == 0)
        {
            _log.Warning(
                "Cannot re-drive tool batch for call {CallId}: assistant message has no tool calls",
                callId);
            EmitExpiredPromptNotice();
            return false;
        }

        _log.Info(
            "Re-driving parked tool batch ({Count} call(s)) after approval response for {CallId}",
            toolCalls.Count, callId);

        if (pending.TurnContext is not { } turnContext)
        {
            _log.Warning(
                "Cannot re-drive tool batch for call {CallId}: turn context is not recoverable ({Reason})",
                callId,
                pending.TurnContextRestoreFailure ?? "missing turn context");
            EmitExpiredPromptNotice();
            return false;
        }

        _currentTurnContext = turnContext;
        _currentTrustContext = _trustContextDeriver?.DeriveFromTurnContext(turnContext);
        BindTurnTelemetry(turnContext);
        MarkApprovalRedrive(pending, callId);

        TransitionTo(SessionPhase.Processing);
        DispatchToolBatch(
            toolCalls,
            oneTimeApprovalPreSeed: redrivePlan.OneTimeApprovalPreSeed,
            decisionOverride: redrivePlan.DecisionOverride);
        return true;
    }

    /// <summary>
    /// Builds the durable event for a parked tool batch the user abandoned by
    /// sending a new message instead of answering its approval prompt. Carries a synthetic
    /// <see cref="Protocol.ChatRole.Tool"/> result for every unanswered tool
    /// call in the tail assistant message so history stays well-formed — an
    /// assistant tool_use with no matching tool_result is rejected by the
    /// provider API — then clears <see cref="_pendingToolInteractions"/>.
    /// </summary>
    private ToolBatchAbandoned BuildToolBatchAbandonedEvent()
        => BuildToolBatchAbandonedEvent(
            "Tool call was not completed — the request was "
            + "superseded by a new message before approval was given.");

    private ToolBatchAbandoned BuildToolBatchAbandonedEvent(string resultContent)
    {
        var assistantMsg = ParkedToolBatchHistory.FindRedrivableAssistantMessage(_state.History, callId: null);
        IReadOnlyList<SerializableChatMessage> results = assistantMsg is null
            ? []
            : ParkedToolBatchHistory.BuildSyntheticAbandonResults(_state.History, assistantMsg, resultContent);
        if (assistantMsg is not null)
        {
            _log.Info(
                "Abandoned parked tool batch ({Count} call(s)) superseded by a new user message",
                assistantMsg.ToolCalls.Count);
        }

        return new ToolBatchAbandoned
        {
            SessionId = _sessionId,
            ToolResults = results,
            AbandonedAtMs = NowMs()
        };
    }

    private void FailCurrentTurn(string errorMessage, Exception cause, ErrorCategory category = ErrorCategory.Unknown)
    {
        _inFlightDedup.CompleteReminder(_currentTurnSource?.ReminderId);
        _inFlightDedup.CompleteBackgroundJob(_currentTurnSource?.BackgroundJobId);
        CancelAndDisposeLlmCts();
        CancelAndDisposeToolExecutionCts();
        _deliveryRetry.Clear();
        _pendingToolInteractions.Clear();
        _resolvedToolApprovals.Clear();
        ClearApprovalTurnState();
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
            SourceReminderId = _currentTurnSource?.ReminderId
        });

        DrainBufferedMessagesOrBecomeReady();
    }

    private void EmitOutput(SessionOutput output, OutputFilter requiredFlag = OutputFilter.None)
    {
        _subscribers.Emit(output, requiredFlag);
        _logActor?.Tell(output);
        _observerActor?.Tell(output);
    }

    private async Task PersistApprovalCandidatesAsync(
        PendingToolInteraction pending,
        ApprovalDecision decision,
        CancellationToken ct)
    {
        if (_approvalService is null)
            return;

        var persistent = decision is ApprovalDecision.ApprovedAlways
            or ApprovalDecision.ApprovedEverywhere;
        var globalWildcard = decision == ApprovalDecision.ApprovedEverywhere;
        var audience = pending.TurnContext?.Audience ?? pending.Audience;

        // Prefer per-clause Candidates so we can use each clause's extracted
        // path argument as the directory half. Fall back to the verb-only
        // CandidateVerbs list for older callers (or non-shell tools whose
        // matcher doesn't populate Candidates).
        if (pending.Candidates.Count == 0)
        {
            var fallbackCwd = globalWildcard ? null : pending.Cwd;
            await _approvalService.RecordApprovalAsync(
                (ToolApprovalSessionId)_sessionId.Value,
                audience,
                new ToolName(pending.ToolName),
                pending.CandidateVerbs,
                persistent,
                fallbackCwd,
                ct);
            return;
        }

        // Group candidates by their effective directory so we make one
        // RecordApprovalAsync call per (audience, tool, directory) bucket
        // rather than one per verb. Side-effect-only clauses are dropped
        // before grouping — they're authorized for the current call by
        // the decision but persistence is suppressed.
        //
        // Bucket key is string.Empty for the null-directory (global wildcard)
        // bucket; mapped back to null when calling the persistence layer
        // below. The session-scratch dead-on-arrival guard is applied inside
        // BuildApprovalBuckets for persistent scope only — session-scope
        // entries are matched verb-only at lookup time so threading cwd
        // through here just feeds the filter that drops standalone verbs
        // with no path arg (curl, gh, git status).
        var sessionDirectory = GetSessionDirectory();

        var grouping = ApprovalBucketBuilder.Build(
            pending.Candidates,
            persistent,
            globalWildcard,
            pending.Cwd,
            sessionDirectory);

        foreach (var (key, verbs) in grouping)
        {
            if (verbs.Count == 0)
                continue;

            // Re-derive null vs concrete directory: the dictionary key was
            // string.Empty for null to satisfy the comparer; map back here.
            var directory = string.IsNullOrEmpty(key) ? null : key;

            await _approvalService.RecordApprovalAsync(
                (ToolApprovalSessionId)_sessionId.Value,
                audience,
                new ToolName(pending.ToolName),
                verbs,
                persistent,
                directory,
                ct);
        }
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

    private void ProcessToolCallResult(Pipelines.ToolCallResult result)
    {
        TrackStartedBackgroundJob(result.StartedBackgroundJob);

        var emittedRunIds = new HashSet<SubAgentRunId>();
        foreach (var finding in result.AcceptedSubAgentFindings)
        {
            if (emittedRunIds.Add(finding.RunId))
            {
                var runSummary = result.CompletedSubAgentRuns
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

            EnqueueCheckpointFireAndForget(new MemoryCheckpointRequest(
                SessionId: _sessionId,
                TurnId: _activeTurnId,
                TriggerType: Memory.CheckpointTriggerType.SubagentFindings,
                Priority: 80,
                Payload: SessionMemoryCheckpointFactory.ForSubAgentFinding(
                    _sessionId,
                    CurrentMemoryBoundary(),
                    CurrentMemoryAudience(),
                    finding)));
        }

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
        if (toolMessage.ToolCallId is not { } toolCallId)
            throw new InvalidOperationException(
                $"Tool-result message for tool '{toolMessage.Name ?? "unknown"}' has no ToolCallId.");

        var preview = toolMessage.Content is { Length: > 200 }
            ? toolMessage.Content[..200] + "..."
            : toolMessage.Content ?? "(null)";
        _log.Info("Tool [{ToolName}] (call={CallId}) result: {Result}",
            toolMessage.Name ?? "unknown", toolCallId.Value, preview);

        EmitOutput(new ToolResultOutput
        {
            SessionId = _sessionId,
            CallId = toolCallId,
            ToolName = new ToolName(toolMessage.Name ?? "unknown"),
            Result = toolMessage.Content ?? string.Empty
        }, OutputFilter.ToolCalls);

        var updatedContext = WorkingContextUpdater.UpdateFromToolResults(
            _state.WorkingContext,
            _state.History,
            [toolMessage],
            _log);
        if (!ReferenceEquals(updatedContext, _state.WorkingContext))
            _state = _state with { WorkingContext = updatedContext };

        if (toolMessage.Name is "load_tool" && toolMessage.Content is not null)
            TryActivateDiscoveredTool(toolMessage.Content.Trim());

        if (toolMessage.Name is "set_working_directory" && toolMessage.Content is not null)
        {
            var projectDir = toolMessage.Content.Trim();
            if (Path.IsPathRooted(projectDir))
            {
                var next = _state.WorkingContext.WithProjectDirectory(projectDir);
                if (!ReferenceEquals(next, _state.WorkingContext))
                {
                    _state = _state with { WorkingContext = next };
                    SetSystemPrompt();
                    _log.Info("Project directory set to {ProjectDir}", projectDir);
                }
            }
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

    private void AddModelInputMediaNudge(IReadOnlyList<SerializableMediaReference> mediaReferences)
    {
        if (mediaReferences.Count == 0)
            return;

        var itemText = mediaReferences.Count == 1 ? "file" : "files";
        _state = _state.AddSystemNudge(
            $"A tool loaded {mediaReferences.Count} media {itemText} for model-visible inspection. " +
            "Use the attached media along with the tool result to answer the user.",
            mediaReferences);
    }

    private void TryCompleteStreamedToolBatch()
    {
        if (!_activeToolBatch.CanComplete)
            return;

        CompleteToolBatch(_activeToolBatch.CompletedCount);
    }

    private void CompleteToolBatch(int resultCount)
    {
        AddModelInputMediaNudge(_mediaBuffer.DrainSnapshot());

        var budgetStatus = _turnState.RecordToolCompletion(resultCount, _config.MaxToolIterationsPerTurn);

        var dupNudge = _turnState.CheckForDuplicates();
        if (dupNudge is not null)
        {
            TurnLog().Warning(
                "turn_duplicate_tool_detected tool={ToolName} count={Count} iteration={Iteration}",
                dupNudge.ToolName, dupNudge.Count, _turnState.ToolIterationCount);
            _state = _state.AddSystemNudge(dupNudge.NudgeText);
        }

        if (_buffer.Count > 0)
        {
            TurnLog().Info("turn_mid_loop_buffer_drain count={BufferCount} iteration={Iteration}",
                _buffer.Count, _turnState.ToolIterationCount);
            foreach (var buffered in _buffer)
            {
                var refs = buffered.MediaReferences.Count > 0 ? buffered.MediaReferences : null;
                _state = _state.AddUserMessage(buffered.Content, refs);
            }
            _buffer.Clear();
        }

        switch (budgetStatus)
        {
            case ToolBudgetStatus.Exhausted exhausted:
                TurnLog().Warning("turn_tool_call_limit_reached callCount={CallCount} max={Max} iteration={Iteration}",
                    _turnState.ToolCallCount, _config.MaxToolIterationsPerTurn, _turnState.ToolIterationCount);
                _state = _state.AddSystemNudge(exhausted.NudgeText);
                FireLlmCall(forceNoTools: true);
                return;
            case ToolBudgetStatus.NudgeNeeded nudge:
                _state = _state.AddSystemNudge(nudge.NudgeText);
                break;
        }

        if (ShouldCompact())
        {
            _log.Info("Compaction threshold reached during tool loop ({InputTokens} tokens >= {Threshold} limit), starting compaction",
                _lastInputTokenCount, _model.CompactionTokenLimit(_config.Tuning.CompactionThreshold));
            _resumeToolLoopAfterCompaction = true;
            Self.Tell(new CompactionTriggered(_lastInputTokenCount));
            TransitionTo(SessionPhase.Compacting);
            return;
        }

        _pendingToolInteractions.Clear();
        _resolvedToolApprovals.Clear();
        ClearActiveToolBatchTracking();
        TurnLog().Info("turn_tool_execution_complete iteration={Iteration} callCount={CallCount} max={Max} resultCount={ResultCount}",
            _turnState.ToolIterationCount, _turnState.ToolCallCount, _config.MaxToolIterationsPerTurn, resultCount);
        MarkApprovalRunningAfterRedrive();
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
        SubAgentResult Result) : INoSerializationVerificationNeeded;

    private sealed record RoutedSkillExecutionFailed(
        string SkillName,
        string SubagentName,
        string ErrorMessage) : INoSerializationVerificationNeeded;

    private sealed record RoutedSkillSubAgentActivity(
        long TimestampMs,
        AgentName AgentName,
        SubAgentPhase Phase,
        int ToolCount,
        bool? Success,
        TimeSpan? Duration,
        int FindingsCount) : INoSerializationVerificationNeeded;

    private enum ApprovalRedriveOutcome
    {
        Started,
        Deferred,
        Failed
    }

    private void BindTurnTelemetry(MessageSource? source)
    {
        var sourceMessageId = source?.MessageId;
        _activeMessageId = sourceMessageId;
        _activeTurnId = source?.TurnId
            ?? new Protocol.TurnId(sourceMessageId ?? IdGen.ShortId());
        _activeChannelType = source?.ChannelType;

        CrashContextSnapshot.Update(
            _sessionId.Value,
            _activeTurnId?.Value,
            _activeMessageId,
            _activeChannelType?.ToWireValue(),
            _timeProvider.GetUtcNow());
    }

    private void BindTurnTelemetry(TurnContext context)
    {
        _activeMessageId = null;
        _activeTurnId = context.TurnId;
        _activeChannelType = context.ChannelType;

        CrashContextSnapshot.Update(
            _sessionId.Value,
            _activeTurnId?.Value,
            _activeMessageId,
            _activeChannelType?.ToWireValue(),
            _timeProvider.GetUtcNow());
    }

    private ILoggingAdapter TurnLog()
    {
        var log = _log;

        if (_activeTurnId is { Value: { Length: > 0 } turnIdValue })
            log = log.WithContext("TurnId", turnIdValue);

        if (!string.IsNullOrWhiteSpace(_activeMessageId))
            log = log.WithContext("MessageId", _activeMessageId);

        if (_activeChannelType is { } act)
            log = log.WithContext("ChannelType", act.ToWireValue());

        return log;
    }

    private void RequestRestartDrain()
    {
        _restartDrainRequested = true;
        _restartDrainReplyTo = Sender;

        if (_phase.Current == SessionPhase.Ready)
            TransitionTo(SessionPhase.Passivating);
    }

    private void ClearBufferedMessagesForRestartDrain()
    {
        if (_buffer.Count == 0)
            return;

        _log.Warning(
            "Dropping {BufferCount} buffered message(s) because coordinated restart drain is completing.",
            _buffer.Count);
        _buffer.Clear();
    }

    private void FireInitialTurnLlmCall(string? recallQuery)
    {
        _turnRestartNotice = _pendingRestartNotice;
        _pendingRestartNotice = null;

        try
        {
            FireLlmCall(recallQuery);
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
