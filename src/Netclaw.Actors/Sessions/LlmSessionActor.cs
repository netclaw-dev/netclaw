using System.Collections.Immutable;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Akka.Persistence;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Text;
using Netclaw.Configuration;
using Netclaw.Actors.Tools;
using Netclaw.Tools;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

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
    private readonly IToolExecutor? _toolExecutor;
    private readonly IToolAuditLogger? _auditLogger;
    private readonly IMemoryExtractor _memoryExtractor;
    private readonly IMemoryRecallCoordinator _memoryRecallCoordinator;
    private readonly IMemoryCheckpointSink _memoryCheckpointSink;
    private readonly MemoryProposalGate _memoryProposalGate = new();
    private readonly TimeProvider _timeProvider;
    private readonly string? _sessionsBasePath;
    private readonly string? _sessionLogsBasePath;
    private readonly ISessionLifecycleObserver? _lifecycleObserver;
    private readonly Memory.SQLiteMemoryStore? _memoryStore;
    private readonly IChatClientProvider _clientProvider;
    private readonly ILoggingAdapter _log;

    // Transient state (not persisted)
    private readonly List<SendUserMessage> _buffer = new();
    private readonly Dictionary<IActorRef, OutputFilter> _subscribers = new();
    private readonly List<AITool> _availableTools = new();
    private MessageSource? _currentTurnSource;
    private readonly ToolRegistry? _fullRegistry;
    private readonly ToolAccessPolicy? _toolAccessPolicy;
    private readonly TrustContextDeriver? _trustContextDeriver;
    private int _baseToolCount; // count of always-loaded tools; dynamic tools appended after this
    private readonly DiscoveredToolCache _discoveredToolCache = new();

    // Last observed input token count from LLM response (for compaction trigger)
    private long _lastInputTokenCount;

    // When compaction triggers mid-tool-loop, the turn is still in-progress.
    // After compaction completes, we need to fire a follow-up LLM call to
    // continue the turn instead of transitioning to Ready. See #424.
    private bool _resumeToolLoopAfterCompaction;

    // Per-turn transient counters (tool budget, duplicate detection, empty-response retries)
    private readonly TurnStateTracker _turnState = new();

    private const string ToolBudgetExhaustedMessage =
        "I used all available tool calls for this turn and couldn't produce a final summary. "
        + "You can ask me to summarize what was done, or rephrase your request.";

    // Delivery retry handler (eligibility tracking, retry counting, nudge builders)
    private readonly DeliveryRetryHandler _deliveryRetry = new();

    // Child actor for per-session log file (created when session logs directory is configured)
    private IActorRef? _logActor;

    // Child actor for per-session memory curation (evaluate-before-write pipeline)
    private IActorRef? _curationActor;

    // Child actor for session-level memory observation (distills transcript on idle)
    private IActorRef? _observerActor;

    // Processing watchdog (timer management for stuck operations)
    private readonly ProcessingWatchdog _watchdog = new();

    // Actor-owned CTS for the active LLM call. Cancelled by the watchdog on timeout
    // or when a response/failure arrives. The session-level watchdog (FirstTokenTimeout /
    // StreamIdleTimeout) is the authoritative timeout — this CTS just propagates
    // cancellation to the HTTP layer so timed-out connections are released.
    private CancellationTokenSource? _activeLlmCts;

    // Correlation ID for the active LLM call. Incremented in FireLlmCall.
    // Stale LlmResponseReceived/LlmCallFailed/LlmResponseDeltaReceived messages
    // from cancelled calls are ignored when their CallId doesn't match.
    private long _activeCallId;

    // Two-phase timeout: tracks whether we've received any streaming delta this turn
    private bool _firstDeltaReceived;

    // Per-turn diagnostic correlation (ephemeral)
    private string? _activeTurnId;
    private string? _activeMessageId;
    private Channels.ChannelType? _activeChannelType;
    private AutomaticRecallResult? _activeRecall;
    private EffectiveTrustContext? _currentTrustContext;

    // Startup context layers: injected on first LLM call, re-injected after compaction
    private bool _startupContextInjected;

    // Guards against infinite compaction loops: if a post-compaction buffer drain
    // overflows again, fail the turn. Reset at the start of each new user turn.
    private int _compactionOverflowRetryCount;

    // Per-turn retry counter for transient streaming failures (5xx, 429)
    private int _streamingRetryAttempt;
    private static readonly object StreamingRetryTimerKey = new();

    // Skill registry for slash-command dispatch
    private readonly Skills.SkillRegistry? _skillRegistry;

    // Memory recall state (transient — reset at turn boundaries and compaction)
    private readonly SessionRecallManager _recallManager = new();

    private readonly Telemetry.ISessionMetrics? _sessionMetrics;

    private bool _restartDrainRequested;
    private IActorRef? _restartDrainReplyTo;
    private string? _pendingRestartNotice;
    private string? _turnRestartNotice;

    // Persistent state (immutable — replaced on each event)
    private SessionState _state = SessionState.Empty;

    // Explicit state machine phase (metadata + validation layer over Become())
    private SessionPhase _currentPhase = SessionPhase.Recovering;

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
        _skillRegistry = tools?.SkillRegistry;
        _toolExecutor = tools?.ToolExecutor;
        _auditLogger = tools?.AuditLogger;
        _toolAccessPolicy = tools?.AccessPolicy;
        _memoryExtractor = memory?.MemoryExtractor ?? NullMemoryExtractor.Instance;
        _memoryRecallCoordinator = memory?.RecallCoordinator ?? NullMemoryRecallCoordinator.Instance;
        _memoryCheckpointSink = memory?.CheckpointSink ?? NullMemoryCheckpointSink.Instance;
        _memoryStore = memory?.MemoryStore;
        _timeProvider = services.TimeProvider;
        _sessionsBasePath = services.Paths?.SessionsDirectory;
        _sessionLogsBasePath = services.Paths?.SessionsDirectory;
        _trustContextDeriver = tools?.TrustDeriver;
        PersistenceId = $"session-{entityId}";

        // Enrich logger with session context — all log messages automatically include SessionId
        _log = Context.GetLogger().WithContext("SessionId", _sessionId.Value);

        // Load all non-MCP tools for initial LLM calls.
        // MCP tools are loaded dynamically via search_tools and can be retained for a
        // small number of future turns (configurable lease) to reduce rediscovery churn.
        _fullRegistry = tools?.ToolRegistry;
        if (_fullRegistry is not null)
        {
            _availableTools.AddRange(_fullRegistry.GetAlwaysLoadedTools());
        }
        _baseToolCount = _availableTools.Count;

        // ── Recovery handlers ──
        // Backward compat: old journals contain SystemPromptSet events.
        // Ignore the content — we always read fresh from disk in RecoveryCompleted.
#pragma warning disable CS0618 // Obsolete type retained for journal deserialization
        Recover<SystemPromptSet>(_ => { });
#pragma warning restore CS0618
        Recover<TurnRecorded>(evt => _state = _state.Apply(evt));
        Recover<SessionTitleSet>(evt => _state = _state.Apply(evt));
        Recover<SessionCompacted>(evt => _state = _state.Apply(evt));
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

            if (_sessionLogsBasePath is not null)
            {
                _logActor = Context.ActorOf(
                    SessionLogActor.CreateProps(_sessionId, _sessionLogsBasePath, _timeProvider),
                    "session-log");
            }

            if (_memoryStore is not null)
            {
                _curationActor = Context.ActorOf(
                    Memory.MemoryCurationActor.CreateProps(_memoryStore, _clientProvider),
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
        if (!IsLegalTransition(_currentPhase, target))
            throw new InvalidOperationException(
                $"Illegal session phase transition: {_currentPhase} → {target}");

        var from = _currentPhase;
        _currentPhase = target;
        _log.Info("session_phase_transition from={From} to={To}", from, target);

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

    private static bool IsLegalTransition(SessionPhase from, SessionPhase to) => from switch
    {
        SessionPhase.Recovering => to == SessionPhase.Ready,
        SessionPhase.Ready => to is SessionPhase.Processing or SessionPhase.Compacting or SessionPhase.Passivating,
        SessionPhase.Processing => to is SessionPhase.Ready or SessionPhase.Compacting,
        SessionPhase.Compacting => to is SessionPhase.Ready or SessionPhase.Processing,
        SessionPhase.Passivating => to is SessionPhase.Ready or SessionPhase.Processing,
        _ => false
    };

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

            _log.Info("Session idle, entering passivation (timeout={Timeout})", _config.IdleTimeout);
            TransitionTo(SessionPhase.Passivating);
        });

        Command<ProcessingWatchdogExpired>(_ => { });
        Command<CompactionWorkCompleted>(_ => { });
        Command<CompactionWorkFailed>(_ => { });
        Command<SpawnChildActorRequest>(msg => Sender.Tell(Context.ActorOf(msg.Props, msg.ActorName)));
        Command<DeliveryFailed>(HandleDeliveryFailedWhenReady);
        Command<PrepareForDaemonRestart>(_ => RequestRestartDrain());

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

        Command<LlmResponseReceived>(msg =>
        {
            if (msg.CallId != _activeCallId) return; // stale response from cancelled call
            _watchdog.Stop(Timers);
            CancelAndDisposeLlmCts();

            var response = msg.Response;
            var lastMessage = response.Messages[^1];

            // Check for tool calls
            var toolCalls = lastMessage.Contents.OfType<FunctionCallContent>().ToList();
            if (toolCalls.Count > 0 && _turnState.ForceNoToolsActive)
            {
                TurnLog().Warning(
                    "turn_force_no_tools_violation toolCallCount={ToolCallCount} budgetUsed={BudgetUsed} max={Max}",
                    toolCalls.Count,
                    _turnState.ToolCallCount,
                    _config.MaxToolCallsPerTurn);
                FailCurrentTurn(
                    ToolBudgetExhaustedMessage,
                    new InvalidOperationException("LLM continued requesting tools after tool execution was disabled for this turn."),
                    ErrorCategory.ProviderFailure);
                return;
            }

            if (toolCalls.Count > 0 && _toolExecutor is not null)
            {
                HandleToolCallResponse(lastMessage, toolCalls, response.Usage);
                return;
            }

            // Guard: empty response (no text, no tool calls) — delegate decision to tracker
            var hasText = lastMessage.Contents.OfType<TextContent>().Any(t => !string.IsNullOrWhiteSpace(t.Text));
            if (!hasText)
            {
                switch (_turnState.EvaluateEmptyResponse())
                {
                    case EmptyResponseAction.Retry retry:
                        _log.Warning("LLM produced empty response — retrying with nudge");
                        _state = _state.AddSystemNudge(retry.NudgeText);
                        FireLlmCall();
                        return;
                    case EmptyResponseAction.Fail fail:
                        _log.Warning("LLM produced empty response — failing turn");
                        FailCurrentTurn(fail.ErrorMessage, fail.Cause, ErrorCategory.ProviderFailure);
                        return;
                }
            }

            // Normal text response — persist turn
            HandleTextResponse(lastMessage, response.Usage, msg.StreamedText, msg.StreamedThinking, msg.RecallResult);
        });

        Command<LlmResponseDeltaReceived>(msg =>
        {
            if (msg.CallId != _activeCallId) return; // stale delta from cancelled call
            _firstDeltaReceived = true;
            _watchdog.Refresh(_config.StreamIdleTimeout, Timers);

            switch (msg.Content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    EmitOutput(new TextDeltaOutput
                    {
                        SessionId = _sessionId,
                        Delta = text.Text
                    }, OutputFilter.TextStreaming);
                    break;

                case TextReasoningContent thinking when !string.IsNullOrEmpty(thinking.Text):
                    EmitOutput(new ThinkingDeltaOutput
                    {
                        SessionId = _sessionId,
                        Delta = thinking.Text
                    }, OutputFilter.Thinking);
                    break;
            }
        });

        Command<ToolExecutionCompleted>(msg =>
        {
            _watchdog.Stop(Timers);

            var emittedRunIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var finding in msg.AcceptedSubAgentFindings)
            {
                if (emittedRunIds.Add(finding.RunId))
                {
                    var runSummary = msg.CompletedSubAgentRuns
                        .FirstOrDefault(x => string.Equals(x.RunId, finding.RunId, StringComparison.Ordinal));

                    EmitOutput(new SubAgentOutput
                    {
                        SessionId = _sessionId,
                        TimestampMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                        AgentName = finding.AgentName,
                        Phase = Netclaw.Actors.SubAgents.SubAgentPhase.Completed,
                        Success = true,
                        Duration = finding.Duration,
                        MemoryDecision = finding.Decision,
                        MemoryDecisionReason = finding.DecisionReason,
                        FindingsCount = runSummary?.FindingsCount ?? 1
                    }, OutputFilter.ToolCalls);
                }

                if (!string.Equals(finding.Decision, "accepted", StringComparison.OrdinalIgnoreCase))
                    continue;

                EnqueueCheckpointFireAndForget(new MemoryCheckpointRequest(
                    SessionId: _sessionId,
                    TurnId: _activeTurnId,
                    TriggerType: Memory.CheckpointTriggerType.SubagentFindings,
                    Priority: 80,
                    Payload: new MemoryCheckpointPayload(
                        SessionId: _sessionId.Value,
                        TriggerType: "subagent-findings",
                        Source: finding.AgentName,
                        Content: finding.Content,
                        UserContent: null,
                        AssistantContent: finding.Content,
                        IsExplicitRequest: false,
                        HasVerifiedToolFinding: false,
                        IsCompactionBoundary: false,
                        HasAcceptedSubAgentFinding: true,
                        Domain: finding.Domain,
                        Boundary: CurrentMemoryBoundary(),
                        Audience: CurrentMemoryAudience(),
                        Sensitivity: finding.Sensitivity,
                        RecallMode: finding.RecallMode,
                        Confidence: finding.Confidence,
                        Title: finding.Title,
                        Kind: finding.Kind,
                        UpdateSemantics: finding.UpdateSemantics,
                        Evidence: finding.Evidence,
                        FreshnessAtMs: finding.FreshnessAtMs)));
            }

            foreach (var run in msg.CompletedSubAgentRuns)
            {
                if (!emittedRunIds.Add(run.RunId))
                    continue;

                EmitOutput(new SubAgentOutput
                {
                    SessionId = _sessionId,
                    TimestampMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    AgentName = run.AgentName,
                    Phase = Netclaw.Actors.SubAgents.SubAgentPhase.Completed,
                    Success = run.Success,
                    Duration = run.Duration,
                    MemoryDecision = run.MemoryDecision,
                    MemoryDecisionReason = run.MemoryDecisionReason,
                    FindingsCount = run.FindingsCount
                }, OutputFilter.ToolCalls);
            }

            foreach (var result in msg.ToolResults)
            {
                _state = _state with { History = _state.History.Add(result) };

                var preview = result.Content is { Length: > 200 }
                    ? result.Content[..200] + "..."
                    : result.Content ?? "(null)";
                _log.Info("Tool [{ToolName}] (call={CallId}) result: {Result}",
                    result.Name ?? "unknown", result.ToolCallId ?? "?", preview);

                EmitOutput(new ToolResultOutput
                {
                    SessionId = _sessionId,
                    CallId = result.ToolCallId ?? string.Empty,
                    ToolName = result.Name ?? "unknown",
                    Result = result.Content ?? string.Empty
                }, OutputFilter.ToolCalls);
            }

            // Dynamic tool loading: if load_tool was called, activate the requested tool.
            // LoadToolTool returns the canonical tool name on success; the actor looks it
            // up in the registry. Error messages won't match any entry, so only valid
            // tool names trigger activation — no string parsing required.
            if (_fullRegistry is not null)
            {
                foreach (var result in msg.ToolResults)
                {
                    if (result.Name is "load_tool" && result.Content is not null)
                    {
                        TryActivateDiscoveredTool(result.Content.Trim());
                    }
                }
            }

            // Emit FileOutput for any file attachments registered by tools
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

            // Record completion and check budget + duplicates
            var budgetStatus = _turnState.RecordToolCompletion(msg.ToolResults.Count, _config.MaxToolCallsPerTurn);

            var dupNudge = _turnState.CheckForDuplicates();
            if (dupNudge is not null)
            {
                TurnLog().Warning(
                    "turn_duplicate_tool_detected tool={ToolName} count={Count} iteration={Iteration}",
                    dupNudge.ToolName, dupNudge.Count, _turnState.ToolIterationCount);
                _state = _state.AddSystemNudge(dupNudge.NudgeText);
            }

            // Mid-loop user message injection: if the user sent messages while tools were
            // running, inject them into the conversation now so the LLM can see corrections
            // like "stop" or "that's already done" before the next iteration.
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

            // Apply budget decision
            switch (budgetStatus)
            {
                case ToolBudgetStatus.Exhausted exhausted:
                    TurnLog().Warning("turn_tool_call_limit_reached callCount={CallCount} max={Max} iteration={Iteration}",
                        _turnState.ToolCallCount, _config.MaxToolCallsPerTurn, _turnState.ToolIterationCount);
                    _state = _state.AddSystemNudge(exhausted.NudgeText);
                    FireLlmCall(forceNoTools: true);
                    return;
                case ToolBudgetStatus.NudgeNeeded nudge:
                    _state = _state.AddSystemNudge(nudge.NudgeText);
                    break;
            }

            // Compaction check before follow-up LLM call (#424)
            if (ShouldCompact())
            {
                _log.Info("Compaction threshold reached during tool loop ({InputTokens} tokens >= {Threshold} limit), starting compaction",
                    _lastInputTokenCount, _model.CompactionTokenLimit(_config.Tuning.CompactionThreshold));
                _resumeToolLoopAfterCompaction = true;
                Self.Tell(new CompactionTriggered { InputTokenCount = _lastInputTokenCount });
                TransitionTo(SessionPhase.Compacting);
                return;
            }

            // Fire follow-up LLM call with tool results in context
            TurnLog().Info("turn_tool_execution_complete iteration={Iteration} callCount={CallCount} max={Max} resultCount={ResultCount}",
                _turnState.ToolIterationCount, _turnState.ToolCallCount, _config.MaxToolCallsPerTurn, msg.ToolResults.Count);
            FireLlmCall();
        });

        Command<ToolExecutionFailed>(msg =>
        {
            _watchdog.Stop(Timers);
            TurnLog().Error(msg.Cause, "turn_tool_execution_failed");

            const string errorMessage = "I encountered an error executing a tool. Please try again.";
            var category = msg.Cause is TimeoutException ? ErrorCategory.Timeout : ErrorCategory.ToolFailure;
            FailCurrentTurn(errorMessage, msg.Cause, category);
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
                Timers.Cancel(StreamingRetryTimerKey);
                Self.Tell(new CompactionTriggered { InputTokenCount = _model.ContextWindowTokens });
                TransitionTo(SessionPhase.Compacting);
                return;
            }

            // Pre-stream transient failure: retry with backoff if no data was streamed yet.
            // IsTransientStreamingError handles ProviderException (which wraps HTTP status codes)
            // while RetryPolicy only provides the attempt limit and backoff delay.
            if (!_firstDeltaReceived && IsTransientStreamingError(msg.Cause))
            {
                var policy = _config.Tuning.StreamingRetryPolicy;
                if (_streamingRetryAttempt < policy.MaxRetries)
                {
                    var delay = policy.GetDelay(_streamingRetryAttempt);
                    _streamingRetryAttempt++;
                    TurnLog().Warning(msg.Cause,
                        "turn_llm_transient_failure — retrying in {DelayMs:F0}ms (attempt {Attempt}/{Max})",
                        delay.TotalMilliseconds, _streamingRetryAttempt, policy.MaxRetries);
                    Timers.StartSingleTimer(
                        StreamingRetryTimerKey,
                        new RetryLlmCallAfterBackoff(_streamingRetryAttempt),
                        delay);
                    return; // Stay in Processing, watchdog is already stopped
                }
            }

            TurnLog().Error(msg.Cause, "turn_llm_call_failed");

            // Evict discovered tools to prevent a poisoned tool set from cascading
            // across turns (e.g., oversized Notion schemas causing repeated 502s).
            _discoveredToolCache.EvictAll(_availableTools, _baseToolCount);
            TurnLog().Info("turn_discovered_tools_evicted — tool list reset to base tools after LLM call failure");

            var errorMessage = ExtractLlmErrorMessage(msg.Cause);
            var category = msg.Cause is TimeoutException ? ErrorCategory.Timeout : ErrorCategory.ProviderFailure;
            FailCurrentTurn(errorMessage, msg.Cause, category);
        });

        Command<RetryLlmCallAfterBackoff>(msg =>
        {
            TurnLog().Info("turn_streaming_retry attempt={Attempt}", msg.Attempt);
            FireLlmCall();
        });

        Command<ProcessingWatchdogExpired>(msg =>
        {
            if (!_watchdog.IsCurrent(msg))
                return;

            var timeout = msg.OperationName switch
            {
                "tool-execution" => _config.ToolExecutionTimeout,
                "llm-call" => _firstDeltaReceived ? _config.StreamIdleTimeout : _config.FirstTokenTimeout,
                _ => _config.TurnLlmTimeout
            };

            var timeoutCause = new TimeoutException(
                $"Session processing operation '{msg.OperationName}' exceeded watchdog timeout of {timeout.TotalSeconds:F0}s");

            _watchdog.Stop(Timers);
            CancelAndDisposeLlmCts();

            _log.Error("Processing watchdog expired for operation {OperationName} (opId={OperationId})",
                msg.OperationName, msg.OperationId);
            var errorMessage = ExtractLlmErrorMessage(timeoutCause);
            FailCurrentTurn(errorMessage, timeoutCause, ErrorCategory.Timeout);
        });

        Command<SpawnChildActorRequest>(msg => Sender.Tell(Context.ActorOf(msg.Props, msg.ActorName)));
        Command<PrepareForDaemonRestart>(_ => RequestRestartDrain());
    }

    private void HandleDistillationResult(SessionDistillationCompleted msg)
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
                TotalTokens = (msg.InputTokens ?? 0) + (msg.OutputTokens ?? 0)
            }, OutputFilter.Usage);
        }

        // Route proposals through the standard gate → curation pipeline
        if (msg.Proposals.Count > 0 && _curationActor is not null)
        {
            // Use session-derived audience when _currentTurnSource is null
            // (distillation fires after idle, turn context is cleared)
            var audience = _currentTurnSource?.Audience
                ?? SecurityPolicyDefaults.ResolveAudienceFromSessionId(_sessionId.Value);
            var gateResult = _memoryProposalGate.Evaluate(
                msg.Proposals,
                _sessionId.ToMemoryDomain(),
                Memory.MemorySensitivity.Normal.ToWireValue(),
                NowMs(),
                boundary: CurrentMemoryBoundary(),
                audience: audience);

            var accepted = gateResult.MemoryOperations;

            _log.Info(
                "session_distillation_gate_result total={Total} accepted={Accepted} rejections={Rejections}",
                gateResult.Summary.Total,
                gateResult.Summary.Accepted,
                gateResult.Summary.RejectionReasons.Count == 0
                    ? "-"
                    : string.Join("|", gateResult.Summary.RejectionReasons.Select(x => $"{x.Key}:{x.Value}")));

            if (accepted.Count > 0)
            {
                _curationActor.Tell(new Memory.EvaluateProposals(accepted, _sessionId.ToMemoryDomain()));
                _sessionMetrics?.RecordMemoriesFormed(accepted.Count);
            }
        }
    }

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

            _log.Info("Buffering user message (compaction in progress)");
            _buffer.Add(cmd);
            TryReplyAck();
        });

        Command<ProcessingWatchdogExpired>(msg =>
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
        });
        Command<SpawnChildActorRequest>(msg => Sender.Tell(Context.ActorOf(msg.Props, msg.ActorName)));
        Command<PrepareForDaemonRestart>(_ => RequestRestartDrain());

        Command<CompactionTriggered>(msg =>
        {
            var timeout = GetCompactionTimeout();
            _watchdog.Start("compaction", timeout, Timers);

            var operationId = _watchdog.CurrentOperationId;
            var stateSnapshot = _state;
            var self = Self;
            var log = _log;
            var compactionClient = _compactionClient;

            var compactionParams = new CompactionParameters(
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
        });

        Command<CompactionWorkCompleted>(msg =>
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
                _lastInputTokenCount = 0; // Reset — next LLM call will provide fresh count
                _startupContextInjected = false; // Re-inject static layers on next LLM call
                _recallManager.ResetForCompaction(); // Force fresh recall + progressive recall reset
                _discoveredToolCache.EvictAll(_availableTools, _baseToolCount); // Reset to base tools — re-discover as needed

                EnqueueCheckpointFireAndForget(new MemoryCheckpointRequest(
                    SessionId: _sessionId,
                    TurnId: _activeTurnId,
                    TriggerType: Memory.CheckpointTriggerType.CompactionBoundary,
                    Priority: 90,
                    Payload: new MemoryCheckpointPayload(
                        SessionId: _sessionId.Value,
                        TriggerType: "compaction-boundary",
                        Source: "compaction",
                        Content: string.IsNullOrWhiteSpace(msg.Summary)
                            ? "Compaction completed"
                            : msg.Summary,
                        UserContent: null,
                        AssistantContent: string.IsNullOrWhiteSpace(msg.Summary) ? null : msg.Summary,
                        IsExplicitRequest: false,
                        HasVerifiedToolFinding: false,
                        IsCompactionBoundary: true,
                        HasAcceptedSubAgentFinding: false,
                        Domain: _sessionId.ToMemoryDomain(),
                        Boundary: CurrentMemoryBoundary(),
                        Audience: CurrentMemoryAudience(),
                        Sensitivity: Memory.MemorySensitivity.Normal.ToWireValue(),
                        RecallMode: Memory.MemoryRecallMode.Auto.ToWireValue(),
                        Confidence: 0.8,
                        Kind: Memory.MemoryKind.Document.ToWireValue(),
                        Title: "compaction-boundary",
                        UpdateSemantics: "append-document")));

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
        });

        Command<CompactionWorkFailed>(msg =>
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
        });

        Command<MemoryExtractionCompleted>(msg =>
        {
            // Persist extracted memories externally (fire-and-forget)
            var self = Self;
            var sessionId = _sessionId.Value;
            _ = PersistMemoriesAsync(_memoryExtractor, sessionId, msg.ExtractedMemories, self);

            DrainBufferOrReady();
        });

        Command<CompactionFailed>(msg =>
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

            // Compaction is best-effort — drain buffer and continue
            DrainBufferOrReady();
        });
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
            _streamingRetryAttempt = 0;
            FireLlmCall();
            TransitionTo(SessionPhase.Processing);
        }
        else
        {
            TransitionTo(SessionPhase.Ready);
        }
    }

    private static readonly TimeSpan PassivationGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly object PassivationTimerKey = new();

    private void Passivating()
    {
        // Disable idle timeout — we're shutting down
        Context.SetReceiveTimeout(null);

        Command<SessionDistillationCompleted>(msg =>
        {
            _log.Info("Passivation distillation complete, stopping");
            Timers.Cancel(PassivationTimerKey);
            HandleDistillationResult(msg);
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
            Timers.Cancel(PassivationTimerKey);
            TransitionTo(SessionPhase.Ready);
            HandleIncomingUserMessage(cmd);
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

        Command<DeliveryFailed>(msg =>
        {
            if (_restartDrainRequested)
            {
                _log.Info("Ignoring delivery feedback while restart passivation is in progress");
                return;
            }

            _log.Info("Aborting passivation due to delivery feedback");
            Timers.Cancel(PassivationTimerKey);
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

    private void CompletePassivation()
    {
        _lifecycleObserver?.OnSessionDeactivated(_sessionId);
        SaveSnapshot(BuildSnapshot());
        _restartDrainReplyTo?.Tell(CommandAck.For(_sessionId));
        _restartDrainReplyTo = null;
        Context.Stop(Self);
    }

    private TimeSpan GetCompactionTimeout()
        => _config.TurnLlmTimeout > _config.SidecarLlmTimeout
            ? _config.TurnLlmTimeout
            : _config.SidecarLlmTimeout;

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
        => _watchdog.IsCurrentOperation("compaction", operationId);


    /// <summary>
    /// Fire a sidecar LLM call to generate a short session title.
    /// Best-effort — failures are silently ignored.
    /// </summary>
    private void MaybeGenerateTitle()
    {
        if (SessionTitleGenerator.ShouldGenerate(_state.TurnCount, _config.Tuning.TitleGenerationInterval))
            _ = SessionTitleGenerator.GenerateAsync(_compactionClient, _state.History, Self, _log, _config.SidecarLlmTimeout);
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

        _ = InvokeMemoryExtractionCoreAsync(client, history, self, timeout);
    }

    private static async Task InvokeMemoryExtractionCoreAsync(
        IChatClient client,
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
            var extractionResponse = await client.GetResponseAsync(extractionMessages,
                cancellationToken: cts.Token);
            var extractedText = extractionResponse.Messages[^1].Text ?? string.Empty;
            self.Tell(new MemoryExtractionCompleted { ExtractedMemories = extractedText });
        }
        catch (Exception ex)
        {
            self.Tell(new CompactionFailed { Cause = ex });
        }
    }

    private static async Task PersistMemoriesAsync(
        IMemoryExtractor extractor, string sessionId, string memories, IActorRef self)
    {
        try
        {
            await extractor.PersistAsync(sessionId, memories);
        }
        catch (Exception ex)
        {
            // Memory persistence is best-effort — log and continue compaction
            Trace.TraceWarning("Memory persistence failed for session {0}: {1}", sessionId, ex.Message);
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

        // Add assistant message (with tool calls) to history
        var assistantMsg = ChatMessageConverter.FromAiMessage(lastMessage);
        _state = _state with { History = _state.History.Add(assistantMsg) };

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
            EmitOutput(new TextOutput
            {
                SessionId = _sessionId,
                Text = preambleText
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
                CallId = tc.CallId,
                ToolName = tc.Name,
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

        // Execute tools async — results come back as ToolExecutionCompleted
        foreach (var tc in toolCalls)
        {
            _log.Info("Invoking tool [{ToolName}] (call={CallId}) args={Args}",
                tc.Name, tc.CallId,
                tc.Arguments is not null ? JsonSerializer.Serialize(tc.Arguments) : "{}");
        }
        var self = Self;
        var executor = _toolExecutor!;
        var sessionId = _sessionId;
        var auditLogger = _auditLogger;
        var tp = _timeProvider;
        var sessionDir = GetSessionDirectory();
        var maxInlineToolResultChars = _config.Tuning.MaxInlineToolResultChars;
        var toolExecutionTimeout = _config.ToolExecutionTimeout;

        _watchdog.Start("tool-execution", toolExecutionTimeout, Timers);

        // Capture subscriber snapshot for subagent activity notifications.
        // These are emitted directly from the tool execution thread via Tell(),
        // which is thread-safe. The snapshot ensures we don't read _subscribers
        // from a non-actor thread.
        var subscriberSnapshot = _subscribers.ToList();
        var logActor = _logActor;
        Action<SubAgentOutput> emitSubAgentOutput = output =>
        {
            foreach (var (subscriber, filter) in subscriberSnapshot)
            {
                if (filter.HasFlag(OutputFilter.ToolCalls))
                    subscriber.Tell(output);
            }
            logActor?.Tell(output);
        };

        // Marshal child-actor spawning back onto the session actor thread.
        Func<object, string, CancellationToken, Task<object>> spawnChildActor = async (props, name, ct) =>
            await self.Ask<IActorRef>(
                new SpawnChildActorRequest
                {
                    Props = (Props)props,
                    ActorName = name
                },
                timeout: toolExecutionTimeout,
                cancellationToken: ct);

        _ = SessionToolExecutionPipeline.ExecuteToolsAsync(executor, toolCalls, sessionId, _currentTurnSource, auditLogger, tp, sessionDir, maxInlineToolResultChars, toolExecutionTimeout, self, emitSubAgentOutput, spawnChildActor);
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
            RecordedAtMs = NowMs()
        };

        Persist(turnEvent, evt =>
        {
            _state = _state with
            {
                History = _state.History.Add(evt.AssistantReply),
                TurnCount = _state.TurnCount + 1
            };

            EmitResponseOutputs(lastMessage, usage, includeText: true, includeThinking: true);
            MaybeSnapshot();
            MaybeGenerateTitle();
            _activeRecall = recallResult;

            EnqueueCheckpointFireAndForget(new MemoryCheckpointRequest(
                SessionId: _sessionId,
                TurnId: _activeTurnId,
                TriggerType: Memory.CheckpointTriggerType.TurnComplete,
                Priority: 40,
                    Payload: new MemoryCheckpointPayload(
                        SessionId: _sessionId.Value,
                        TriggerType: Memory.CheckpointTriggerType.TurnComplete.ToWireValue(),
                        Source: "session",
                        Content: $"User: {evt.UserMessage.Content}\nAssistant: {evt.AssistantReply.Content}",
                        UserContent: evt.UserMessage.Content,
                        AssistantContent: evt.AssistantReply.Content,
                        IsExplicitRequest: false,
                    HasVerifiedToolFinding: false,
                    IsCompactionBoundary: false,
                    HasAcceptedSubAgentFinding: false,
                    Domain: _sessionId.ToMemoryDomain(),
                    Boundary: CurrentMemoryBoundary(),
                    Audience: CurrentMemoryAudience(),
                    Sensitivity: Memory.MemorySensitivity.Normal.ToWireValue(),
                    RecallMode: Memory.MemoryRecallMode.Auto.ToWireValue(),
                    Confidence: 0.7,
                    Kind: Memory.MemoryKind.Document.ToWireValue(),
                    Title: "turn-completion",
                    UpdateSemantics: "append-document")));

            _deliveryRetry.MarkEligible(_state.TurnCount);

            // Check if compaction should trigger
            if (ShouldCompact())
            {
                _log.Info("Compaction threshold reached ({InputTokens} tokens >= {Threshold} limit), starting compaction",
                    _lastInputTokenCount, _model.CompactionTokenLimit(_config.Tuning.CompactionThreshold));
                Self.Tell(new CompactionTriggered { InputTokenCount = _lastInputTokenCount });
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
            _streamingRetryAttempt = 0;
            FireLlmCall();
            // Already in Processing — no transition needed, just fired a new LLM call
            return;
        }

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
        _deliveryRetry.Clear();
        _currentTurnSource = cmd.Source;
        _currentTrustContext = _trustContextDeriver?.Derive(cmd.Source);
        BindTurnTelemetry(cmd.Source);
        TurnLog().Info(
            "turn_received channel={ChannelType} sender={SenderId} hasMedia={HasMedia} textChars={TextLength}",
            cmd.Source?.ChannelType.ToWireValue() ?? "unknown",
            cmd.Source?.SenderId ?? "unknown",
            cmd.MediaReferences.Count > 0,
            cmd.Content?.Length ?? 0);
        _logActor?.Tell(cmd);
        _observerActor?.Tell(cmd);

        _turnState.ResetForNewTurn();
        _discoveredToolCache.PrepareForNewTurn(
            _availableTools, _baseToolCount,
            _config.Tuning.DiscoveredToolRetentionTurns,
            _config.Tuning.DiscoveredToolMaxCount,
            _fullRegistry);

        var mediaRefs = cmd.MediaReferences;
        if (mediaRefs.Count > 0 && !_model.InputModalities.HasFlag(Configuration.ModelModality.Image))
        {
            var imageRefs = mediaRefs.Where(r => r.Modality == (int)MediaModality.Image).ToList();
            if (imageRefs.Count > 0)
            {
                _log.Info("Stripping {Count} image reference(s) — model does not support vision", imageRefs.Count);
                mediaRefs = mediaRefs.Where(r => r.Modality != (int)MediaModality.Image).ToList();

                EmitOutput(new TextOutput
                {
                    SessionId = _sessionId,
                    Text = "[Images removed — the current model does not support vision input]"
                }, OutputFilter.Text);
            }
        }

        if (string.IsNullOrWhiteSpace(cmd.Content) && mediaRefs.Count == 0
            && cmd.MediaReferences.Count > 0)
        {
            _log.Info("Skipping LLM call — message contained only unsupported media");
            EmitOutput(new TextOutput
            {
                SessionId = _sessionId,
                Text = "Your message contained only images, but the current model doesn't support vision. Please send a text message instead."
            }, OutputFilter.Text);
            EmitOutput(new TurnCompleted
            {
                SessionId = _sessionId,
                TurnNumber = _state.TurnCount,
                Outcome = TurnOutcome.Skipped
            });
            TryReplyAck();
            return;
        }

        var userContent = cmd.Content ?? string.Empty;
        if (TryHandleSlashCommand(userContent, mediaRefs))
            return;

        _state = _state.AddUserMessage(userContent, mediaRefs.Count > 0 ? mediaRefs : null);
        TryReplyAck();
        _recallManager.ResetForNewTurn();
        _streamingRetryAttempt = 0;
        _compactionOverflowRetryCount = 0;
        FireInitialTurnLlmCall(userContent);
        TransitionTo(SessionPhase.Processing);
    }

    private bool ShouldCompact()
    {
        var limit = _model.CompactionTokenLimit(_config.Tuning.CompactionThreshold);
        return limit > 0
            && _lastInputTokenCount >= limit;
    }

    private void CommandSubscriptionMessages()
    {
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

        Command<MemoryObservationCompleted>(msg =>
        {
            TurnLog().Info("memory_observation_sidecar_completed proposalCount={ProposalCount}", msg.Proposals.Count);
            var gateResult = _memoryProposalGate.Evaluate(
                msg.Proposals,
                _sessionId.ToMemoryDomain(),
                Memory.MemorySensitivity.Normal.ToWireValue(),
                NowMs(),
                boundary: CurrentMemoryBoundary(),
                audience: _currentTurnSource?.Audience ?? TrustAudience.Public);
            var accepted = gateResult.MemoryOperations;

            TurnLog().Info(
                "memory_observation_gate_summary total={Total} accepted={Accepted} identityUpdates={IdentityUpdates} rejections={Rejections}",
                gateResult.Summary.Total,
                gateResult.Summary.Accepted,
                gateResult.Summary.IdentityUpdates,
                gateResult.Summary.RejectionReasons.Count == 0
                    ? "-"
                    : string.Join("|", gateResult.Summary.RejectionReasons.Select(x => $"{x.Key}:{x.Value}")));

            if (gateResult.IdentityUpdates.Count > 0)
            {
                TurnLog().Info(
                    "memory_observation_identity_updates count={Count} titles={Titles}",
                    gateResult.IdentityUpdates.Count,
                    string.Join("|", gateResult.IdentityUpdates.Select(x => x.Title)));
            }
            if (accepted.Count == 0)
            {
                TurnLog().Info("memory_observation_gate_result accepted=0 rejectedOrIgnored={RejectedCount}", msg.Proposals.Count);
                return;
            }

            TurnLog().Info("memory_observation_gate_result accepted={AcceptedCount} rejectedOrIgnored={RejectedCount}", accepted.Count, Math.Max(0, msg.Proposals.Count - accepted.Count));
            TurnLog().Info(
                "memory_observation_accept_details items={Items}",
                string.Join(" | ", accepted.Select(x =>
                    $"title={x.Title};anchor={x.AnchorCanonicalName};class={x.MemoryClass};aliases={(x.AliasesJson ?? "-")};facets={(x.FacetsJson ?? "-")};slots={(x.SlotsJson ?? "-")}")));

            // Send accepted proposals to the curation actor for evaluate-before-write.
            // If the curation actor is not available (no SQLiteMemoryStore configured),
            // fall back to the checkpoint queue for the worker service to process.
            if (_curationActor is not null)
            {
                _curationActor.Tell(new Memory.EvaluateProposals(accepted, _sessionId.ToMemoryDomain()));
            }
            else
            {
                EnqueueCheckpointFireAndForget(new MemoryCheckpointRequest(
                    SessionId: _sessionId,
                    TurnId: _activeTurnId,
                    TriggerType: Memory.CheckpointTriggerType.ObservedMemoryProposals,
                    Priority: 60,
                    Payload: new ObservedMemoryCheckpointPayload(
                        _sessionId.Value,
                        Memory.CheckpointTriggerType.ObservedMemoryProposals.ToWireValue(),
                        _sessionId.ToMemoryDomain(),
                        Memory.MemorySensitivity.Normal.ToWireValue(),
                        accepted)));
            }
        });

        Command<MemoryObservationFailed>(msg =>
        {
            TurnLog().Warning("memory_observation_sidecar_failed reason={Reason}", msg.Reason);
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

        Command<WarmSession>(cmd =>
        {
            _pendingRestartNotice = cmd.RestartNotice;
            Sender.Tell(CommandAck.For(_sessionId));
        });

        Command<JoinSession>(cmd =>
        {
            // Detect re-join: same subscriber already registered with same filter.
            var isReJoin = _subscribers.TryGetValue(cmd.Subscriber, out var existingFilter)
                           && existingFilter == cmd.Filter;

            if (!isReJoin)
            {
                _subscribers[cmd.Subscriber] = cmd.Filter;
                Context.WatchWith(cmd.Subscriber,
                    new LeaveSession { SessionId = _sessionId, Subscriber = cmd.Subscriber });

                _log.Info("{Subscriber} joined (filter={Filter})", cmd.Subscriber, cmd.Filter);
            }

            var joined = new SessionJoined
            {
                SessionId = _sessionId,
                Title = _state.Title,
                TurnCount = _state.TurnCount,
                RecentMessages = ExtractRecentMessages(_state.History)
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

        base.PreRestart(reason, message);
    }

    protected override void PostStop()
    {
        CancelAndDisposeLlmCts();
        base.PostStop();
    }

    private void CancelAndDisposeLlmCts()
    {
        _activeLlmCts?.Cancel();
        _activeLlmCts?.Dispose();
        _activeLlmCts = null;
    }

    // ── Helpers ──

    /// <summary>
    /// Extracts the best user-facing error message from an LLM call failure.
    /// Checks for ProviderException (user-safe message), context overflow,
    /// timeouts, and falls back to a generic message.
    /// </summary>
    private string ExtractLlmErrorMessage(Exception? cause)
    {
        if (cause is null)
            return "I encountered an error processing your message. Please try again.";

        // ProviderException carries a pre-formatted user-safe message
        var providerEx = FindException<Configuration.ProviderException>(cause);
        if (providerEx is not null)
            return providerEx.UserMessage;

        if (IsContextOverflowError(cause))
            return $"Context window exceeded after compaction — the session has too many tools or a large system prompt for the {_model.ModelId} context window ({_model.ContextWindowTokens} tokens). Try reducing tools or increasing the model's context window.";

        if (cause is TimeoutException)
            return _firstDeltaReceived
                ? "The LLM response stream stopped unexpectedly. Please try again."
                : "The LLM provider did not respond in time. The model may be overloaded or the context too large. Please try again.";

        return "I encountered an error processing your message. Please try again.";
    }

    /// <summary>
    /// Walks the exception chain (including inner exceptions) to find an exception
    /// of the specified type.
    /// </summary>
    private static T? FindException<T>(Exception? ex) where T : Exception
    {
        while (ex is not null)
        {
            if (ex is T match)
                return match;
            ex = ex.InnerException;
        }
        return null;
    }

    /// <summary>
    /// Detect context-length overflow errors from LLM providers.
    /// Uses two signals: (1) ProviderException with HTTP 400 + overflow keywords,
    /// (2) fallback keyword scan of the full exception chain for providers that
    /// don't use ProviderException.
    /// </summary>
    internal static bool IsContextOverflowError(Exception? ex)
    {
        if (ex is null) return false;

        // Preferred path: ProviderException carries structured status code
        var providerEx = FindException<Configuration.ProviderException>(ex);
        if (providerEx is { StatusCode: 400 } && ContainsOverflowKeyword(providerEx.Message))
            return true;

        // Fallback: walk the full exception chain for keyword matches
        var current = ex;
        while (current is not null)
        {
            if (ContainsOverflowKeyword(current.Message))
                return true;
            current = current.InnerException;
        }

        return false;
    }

    // This is inherently brittle — there is no standard error format for context
    // overflow across LLM providers. Each returns different messages. We cast a wide
    // net with keyword matching and rely on the ContextOverflowDetectionTests to
    // verify coverage across known provider formats.
    private static bool ContainsOverflowKeyword(string message) =>
        message.Contains("context length", StringComparison.OrdinalIgnoreCase)
        || message.Contains("context_length", StringComparison.OrdinalIgnoreCase)
        || message.Contains("maximum context", StringComparison.OrdinalIgnoreCase)
        || message.Contains("exceeds", StringComparison.OrdinalIgnoreCase)
            && message.Contains("context", StringComparison.OrdinalIgnoreCase)
        || message.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase)
        || message.Contains("token", StringComparison.OrdinalIgnoreCase)
            && message.Contains("exceed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Detect transient streaming errors that are safe to retry when no data
    /// has been streamed yet (5xx server errors, 429 rate limits, network failures).
    /// </summary>
    internal static bool IsTransientStreamingError(Exception? ex)
    {
        if (ex is null) return false;
        var providerEx = FindException<Configuration.ProviderException>(ex);
        if (providerEx?.StatusCode is >= 500) return true;
        if (providerEx?.StatusCode is 429) return true;
        // Only retry network-level failures (no response from provider).
        // TimeoutException is NOT retried here — the ProcessingWatchdog is the
        // authoritative timeout handler and covers that case.
        return ex is HttpRequestException { StatusCode: null };
    }

    /// <summary>
    /// Extracts the last N user/assistant text messages for session resume display.
    /// Skips system prompts, tool messages, and assistant messages that are tool-call-only
    /// (no visible text). Truncates long content to keep the DTO payload reasonable.
    /// </summary>
    private static IReadOnlyList<ChatMessageDto>? ExtractRecentMessages(
        ImmutableList<SerializableChatMessage> history, int maxMessages = 20)
    {
        if (history.Count == 0)
            return null;

        const int maxContentLength = 2000;

        var candidates = new List<ChatMessageDto>();
        for (var i = 0; i < history.Count; i++)
        {
            var msg = history[i];

            // Only include user and assistant messages
            if (msg.Role is not (Protocol.ChatRole.User or Protocol.ChatRole.Assistant))
                continue;

            // Skip assistant messages that are tool-call-only (no visible text)
            if (msg.Role == Protocol.ChatRole.Assistant
                && string.IsNullOrWhiteSpace(msg.Content)
                && msg.ToolCalls.Count > 0)
                continue;

            // Skip system nudges injected as user messages
            if (SessionState.IsSystemNudge(msg))
                continue;

            var content = msg.Content;
            if (content.Length > maxContentLength)
                content = string.Concat(content.AsSpan(0, maxContentLength - 3), "...");

            candidates.Add(new ChatMessageDto
            {
                Role = msg.Role == Protocol.ChatRole.User ? "user" : "assistant",
                Content = content
            });
        }

        if (candidates.Count == 0)
            return null;

        // Take the last N messages
        if (candidates.Count > maxMessages)
            candidates = candidates.GetRange(candidates.Count - maxMessages, maxMessages);

        return candidates;
    }


    private string GetSessionDirectory() =>
        _sessionsBasePath is not null
            ? SessionDirectoryHelper.GetSessionDirectory(_sessionId, _sessionsBasePath)
            : SessionDirectoryHelper.GetSessionDirectory(_sessionId);

    private long NowMs() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private void SetSystemPrompt()
    {
        var content = _promptProvider.GetSystemPrompt();
        if (string.IsNullOrWhiteSpace(content))
        {
            // Clear stale prompt from snapshot recovery rather than silently keeping it
            if (_state.History.Count > 0 && _state.History[0].Role == Protocol.ChatRole.System)
            {
                _state = _state with { History = _state.History.RemoveAt(0) };
                _log.Warning("Identity files missing — cleared stale system prompt from snapshot");
            }
            else
            {
                _log.Info("No system prompt layers available");
            }
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

        _log.Info("System prompt set ({PromptLength} chars)", content.Length);
    }

    private void FireLlmCall(string? recallQuery = null, bool forceNoTools = false)
    {
        _firstDeltaReceived = false;
        CancelAndDisposeLlmCts();
        _activeLlmCts = new CancellationTokenSource();
        _activeCallId++;

        _turnState.ForceNoToolsActive = forceNoTools;

        var sessionDir = GetSessionDirectory();
        var messages = ChatMessageConverter.ToAiMessages(_state.History, sessionDir);

        // Recall: only resolve on turn-start calls, reuse cache for tool-loop follow-ups
        if (_recallManager.TurnRecallCache is null)
        {
            var recallSw = Stopwatch.StartNew();
            var resolved = _recallManager.ResolveForTurn(recallQuery, _state, _sessionId, _currentTurnSource, _memoryRecallCoordinator);
            recallSw.Stop();

            resolved = _recallManager.ApplyProgressiveRecall(resolved, _log);

            // Persist recalled memories into session history so they survive
            // across turns. Without this, recalled memories are transient system
            // messages that vanish after one turn, and the exclusion set prevents
            // re-injection — making the memory invisible for the rest of the session.
            if (resolved.Items.Count > 0)
            {
                var recallContent = SessionRecallManager.FormatForHistory(resolved);
                _state = _state.AddSystemNudge(recallContent);
                _observerActor?.Tell(new ObserverSystemContext("recalled-memory", recallContent));
            }

            var recallIds = resolved.Items.Count == 0
                ? "-"
                : string.Join(",", resolved.Items.Select(i => i.Id));
            TurnLog().Info(
                "turn_memory_recall degraded={Degraded} stage={Stage} durationMs={DurationMs} itemCount={ItemCount} itemIds={ItemIds}",
                resolved.Degraded,
                resolved.DegradeStage ?? "-",
                recallSw.ElapsedMilliseconds,
                resolved.Items.Count,
                recallIds);

            if (resolved.Items.Count > 0)
                _sessionMetrics?.RecordMemoriesRecalled(resolved.Items.Count);
        }

        _activeRecall = _recallManager.TurnRecallCache;
        SessionRecallManager.InjectIntoMessages(messages, _activeRecall!);

        // Inject dynamic context layers (e.g. tool index, skill index) as transient system messages.
        // These are NOT persisted — rebuilt on every call so rehydrated sessions stay fresh.
        InjectDynamicContextLayers(messages);
        var self = Self;
        var client = _chatClient;
        var timeout = _config.FirstTokenTimeout;

        var exposedTools = ResolveExposedToolsForCurrentTurn();
        ChatOptions? options = null;
        if (!forceNoTools && exposedTools.Count > 0)
        {
            options = new ChatOptions
            {
                Tools = exposedTools.ToList()
            };
        }

        _watchdog.Start("llm-call", timeout, Timers);

        TurnLog().Info("turn_llm_call_start messages={MessageCount} toolsEnabled={ToolsEnabled} forceNoTools={ForceNoTools} callId={CallId}",
            messages.Count,
            options?.Tools?.Count > 0,
            forceNoTools,
            _activeCallId);

        _ = SessionLlmInvoker.InvokeAsync(client, messages, options, self, _activeCallId, _activeLlmCts!.Token);
    }


    private string CurrentMemoryAudience()
        => (_currentTurnSource?.Audience ?? TrustAudience.Public).ToWireValue();

    private string CurrentMemoryBoundary()
        => _currentTurnSource?.Boundary
           ?? SecurityPolicyDefaults.ResolveBoundaryFromSessionId(_sessionId.Value, _currentTurnSource?.Audience ?? TrustAudience.Public);

    private IReadOnlyList<AITool> ResolveExposedToolsForCurrentTurn()
    {
        if (_toolAccessPolicy is null || _fullRegistry is null || _availableTools.Count == 0)
            return _availableTools;

        return _toolAccessPolicy.FilterExposedTools(_availableTools, _fullRegistry, _currentTrustContext);
    }


    /// <summary>
    /// Handles slash-command dispatch. If the user message starts with / and matches
    /// a registered skill, injects the skill content as a transient system message
    /// and passes the remainder as user content. Returns true if handled.
    /// </summary>
    private bool TryHandleSlashCommand(string userContent, List<SerializableMediaReference> mediaRefs)
    {
        if (_skillRegistry is null || string.IsNullOrWhiteSpace(userContent) || userContent[0] != '/')
            return false;

        if (_skillRegistry.TryResolveSlashCommand(userContent, out var skill, out var remainder))
        {
            // Load the skill content and inject as transient system message
            string skillBody;
            try
            {
                var content = File.ReadAllText(skill!.FilePath);
                skillBody = Skills.SkillScanner.ExtractBody(content);
            }
            catch (IOException ex)
            {
                _log.Warning("Failed to read skill file for slash command /{SkillName}: {Error}",
                    skill!.Name, ex.Message);
                EmitOutput(new TextOutput
                {
                    SessionId = _sessionId,
                    Text = $"Failed to load skill /{skill.Name}: {ex.Message}\n\nThe skill file may be missing or corrupted."
                }, OutputFilter.Text);
                EmitOutput(new TurnCompleted { SessionId = _sessionId, TurnNumber = _state.TurnCount, Outcome = TurnOutcome.Skipped });
                TryReplyAck();
                return true; // Handled — do NOT fall through to LLM
            }

            _sessionMetrics?.RecordSkillsLoaded(1);

            // Inject skill as system context, use remainder as user message
            var effectiveUserContent = string.IsNullOrWhiteSpace(remainder)
                ? $"The user invoked /{skill.Name}. Follow the skill instructions."
                : remainder;

            _state = _state.AddUserMessage(effectiveUserContent, mediaRefs.Count > 0 ? mediaRefs : null);
            TryReplyAck();
            _recallManager.ResetForNewTurn();

            // Inject skill content as a one-time system message for this turn
            _slashCommandSkillContent = skillBody;
            FireInitialTurnLlmCall(effectiveUserContent);
            _slashCommandSkillContent = null;

            TransitionTo(SessionPhase.Processing);
            return true;
        }

        // Unrecognized slash command — send deterministic error
        var commands = _skillRegistry.GetAvailableSlashCommands();
        var errorMsg = $"Unknown command: {userContent.Split(' ')[0]}\n\nAvailable commands:\n";
        foreach (var (cmd, hint) in commands)
        {
            errorMsg += hint is not null ? $"  {cmd} {hint}\n" : $"  {cmd}\n";
        }

        EmitOutput(new TextOutput { SessionId = _sessionId, Text = errorMsg.TrimEnd() }, OutputFilter.Text);
        EmitOutput(new TurnCompleted { SessionId = _sessionId, TurnNumber = _state.TurnCount, Outcome = TurnOutcome.Skipped });
        TryReplyAck();
        return true;
    }

    // Transient: skill body injected by slash-command dispatch for the current turn
    private string? _slashCommandSkillContent;
    private string? _sessionPromptOverlay;

    /// <summary>
    /// Inject dynamic context layers as system messages after the persisted system prompt
    /// but before user messages. Static (<see cref="ContextLayerTiming.OnceAtStart"/>) layers
    /// are injected on the first call and again after compaction resets
    /// <see cref="_startupContextInjected"/>. Per-turn layers are always injected.
    /// </summary>
    private void InjectDynamicContextLayers(List<AiChatMessage> messages)
    {
        var parts = new List<string>();
        foreach (var layer in _contextLayers)
        {
            if (_startupContextInjected && layer.Timing == ContextLayerTiming.OnceAtStart)
                continue;

            var content = layer.GetContextLayer();
            if (!string.IsNullOrWhiteSpace(content))
                parts.Add(content.Trim());
        }

        // Slash-command skill injection: if a slash command was invoked this turn,
        // inject the skill body as a system context part
        if (_slashCommandSkillContent is not null)
            parts.Add(_slashCommandSkillContent);

        if (_sessionPromptOverlay is not null)
            parts.Add(_sessionPromptOverlay);

        if (_turnRestartNotice is not null)
            parts.Add(_turnRestartNotice);

        // Session identity — allows the agent to reference its own session and media directory
        var sessionBlock = $"[session]\nid: {_sessionId.Value}";
        if (_sessionsBasePath is not null)
        {
            var sessionDir = SessionDirectoryHelper.GetSessionDirectory(_sessionId, _sessionsBasePath);
            sessionBlock += $"\nmedia_dir: {Path.Combine(sessionDir, "media")}";
        }
        parts.Add(sessionBlock);

        _startupContextInjected = true;

        var contextMessage = new AiChatMessage(
            Microsoft.Extensions.AI.ChatRole.System,
            string.Join("\n\n", parts));

        // Insert after the last system message (the persisted prompt), before user messages
        var insertIndex = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == Microsoft.Extensions.AI.ChatRole.System)
                insertIndex = i + 1;
            else
                break;
        }

        messages.Insert(insertIndex, contextMessage);
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
        _discoveredToolCache.Remember(toolName, tool,
            _config.Tuning.DiscoveredToolRetentionTurns,
            _config.Tuning.DiscoveredToolMaxCount);
        AddAvailableToolIfMissing(toolName, tool.ToAITool());
        return true;
    }

    private void AddAvailableToolIfMissing(string toolName, AITool aiTool)
    {
        if (_availableTools.Any(existing =>
            existing is AIFunction ef && aiTool is AIFunction nf && ef.Name == nf.Name))
            return;

        _availableTools.Add(aiTool);
        _log.Info("Dynamically loaded tool '{ToolName}' into session", toolName);
    }

    private SessionSnapshot BuildSnapshot()
    {
        var snapshot = _state.ToSnapshot();
        snapshot.EligibleDeliveryTurnNumber = _deliveryRetry.EligibleTurnNumber;
        return snapshot;
    }

    private void MaybeSnapshot()
    {
        if (_config.Tuning.SnapshotInterval > 0 && LastSequenceNr % _config.Tuning.SnapshotInterval == 0)
        {
            SaveSnapshot(BuildSnapshot());
        }
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
                    EmitOutput(new ThinkingOutput
                    {
                        SessionId = _sessionId,
                        Text = thinking.Text ?? string.Empty
                    }, OutputFilter.Thinking);
                    break;

                case FunctionCallContent toolCall:
                    EmitOutput(new ToolCallOutput
                    {
                        SessionId = _sessionId,
                        CallId = toolCall.CallId,
                        ToolName = toolCall.Name,
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
                EmitOutput(new TextOutput
                {
                    SessionId = _sessionId,
                    Text = fullText
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
            TurnNumber = _state.TurnCount
        });
    }

    private void EmitUsageOutput(UsageDetails usage)
    {
        _sessionMetrics?.RecordTokenUsage(usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0);

        var contextWindow = _model.ContextWindowTokens;
        double? usagePercent = usage.InputTokenCount.HasValue && contextWindow > 0
            ? (double)usage.InputTokenCount.Value / contextWindow
            : null;

        EmitOutput(new UsageOutput
        {
            SessionId = _sessionId,
            InputTokens = usage.InputTokenCount,
            OutputTokens = usage.OutputTokenCount,
            TotalTokens = usage.TotalTokenCount,
            CachedInputTokens = usage.CachedInputTokenCount,
            ReasoningTokens = usage.ReasoningTokenCount,
            ContextWindowTokens = contextWindow,
            UsagePercent = usagePercent
        }, OutputFilter.Usage);
    }

    private void FailCurrentTurn(string errorMessage, Exception cause, ErrorCategory category = ErrorCategory.Unknown)
    {
        _deliveryRetry.Clear();
        Timers.Cancel(StreamingRetryTimerKey);
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
            TurnNumber = _state.TurnCount,
            Outcome = TurnOutcome.Failed
        });

        DrainBufferedMessagesOrBecomeReady();
    }

    private void EmitOutput(SessionOutput output, OutputFilter requiredFlag = OutputFilter.None)
    {
        foreach (var (subscriber, filter) in _subscribers)
        {
            if (requiredFlag == OutputFilter.None || filter.HasFlag(requiredFlag))
            {
                subscriber.Tell(output);
            }
        }

        _logActor?.Tell(output);
        _observerActor?.Tell(output);
    }

    private void BindTurnTelemetry(MessageSource? source)
    {
        var sourceMessageId = source?.MessageId;
        _activeMessageId = sourceMessageId;
        _activeTurnId = source?.TurnId
            ?? sourceMessageId
            ?? Guid.NewGuid().ToString("N")[..8];
        _activeChannelType = source?.ChannelType;
    }

    private ILoggingAdapter TurnLog()
    {
        var log = _log;

        if (!string.IsNullOrWhiteSpace(_activeTurnId))
            log = log.WithContext("TurnId", _activeTurnId);

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

        if (_currentPhase == SessionPhase.Ready)
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
            EmitOutput(new SessionTitleOutput
            {
                SessionId = _sessionId,
                Title = title
            });
        });
    }
}
