using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Akka.Persistence;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Skills;
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
    private readonly SessionConfig _config;
    private readonly ISystemPromptProvider _promptProvider;
    private readonly IReadOnlyList<IContextLayerProvider> _contextLayers;
    private readonly IToolExecutor? _toolExecutor;
    private readonly IToolAuditLogger? _auditLogger;
    private readonly IMemoryExtractor _memoryExtractor;
    private readonly IMemoryRecallCoordinator _memoryRecallCoordinator;
    private readonly IMemoryCheckpointSink _memoryCheckpointSink;
    private readonly SidecarMemoryObserver _sidecarMemoryObserver = new();
    private readonly MemoryProposalGate _memoryProposalGate = new();
    private readonly TimeProvider _timeProvider;
    private readonly string? _sessionsBasePath;
    private readonly string? _sessionLogsBasePath;
    private readonly ISessionLifecycleObserver? _lifecycleObserver;
    private readonly ILoggingAdapter _log;

    // Transient state (not persisted)
    private readonly List<SendUserMessage> _buffer = new();
    private readonly Dictionary<IActorRef, OutputFilter> _subscribers = new();
    private readonly List<AITool> _availableTools = new();
    private readonly ToolRegistry? _fullRegistry;
    private int _baseToolCount; // count of always-loaded tools; dynamic tools appended after this
    private readonly List<string> _discoveredToolOrder = new();
    private readonly Dictionary<string, int> _discoveredToolLeases = new(StringComparer.Ordinal);

    // Last observed input token count from LLM response (for compaction trigger)
    private long _lastInputTokenCount;

    // Tool call counter (reset per turn, incremented by the number of tool calls in each batch)
    private int _toolCallCount;

    // Tool iteration counter (reset per turn, incremented once per ToolExecutionCompleted batch — for logging)
    private int _toolIterationCount;

    // Whether the budget-awareness nudge has been injected for this turn
    private bool _budgetNudgeSent;

    private const int MaxPreToolEmptyResponseRetries = 2;
    private const int MaxDeliveryFailureRetries = 2;
    private const string EmptyResponseFallbackMessage = "I didn't manage to produce a reply. Please try rephrasing or sending your request again.";
    private const string ToolBudgetExhaustedMessage =
        "I used all available tool calls for this turn and couldn't produce a final summary. "
        + "You can ask me to summarize what was done, or rephrase your request.";

    // Whether we already sent a post-tool empty-response nudge in this tool chain
    private bool _postToolNudgeSent;

    // Number of consecutive empty responses observed before any tool work happened
    private int _preToolEmptyResponseCount;

    // Whether the current LLM call intentionally disabled tool use after a circuit breaker fired.
    private bool _forceNoToolsActive;

    // Duplicate tool call detection: tracks hash(toolName:argsJson) within a turn
    private readonly Dictionary<string, int> _toolCallHashes = new(StringComparer.Ordinal);
    private bool _duplicateNudgeSent;

    // Delivery retry state. A completed turn remains eligible until a new user turn starts.
    private int? _deliveryRetryEligibleTurnNumber;
    private int _deliveryRetryCount;
    private bool _deliveryRetryChainActive;

    // Child actor for per-session log file (created when session logs directory is configured)
    private IActorRef? _logActor;

    // Processing watchdog state (non-persistent)
    private static readonly object ProcessingWatchdogTimerKey = new();
    private long _processingOperationId;
    private string? _processingOperationName;

    // Per-turn diagnostic correlation (ephemeral)
    private string? _activeTurnId;
    private string? _activeMessageId;
    private Channels.ChannelType? _activeChannelType;
    private AutomaticRecallResult? _activeRecall;

    // Startup context layers: injected on first LLM call, re-injected after compaction
    private bool _startupContextInjected;

    // Skill auto-load state (transient — cleared on compaction, empty on recovery)
    private readonly HashSet<string> _autoLoadedSkills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _autoLoadedSkillContent = new(StringComparer.OrdinalIgnoreCase);
    private readonly SkillRegistry? _skillRegistry;
    private readonly Telemetry.ISessionMetrics? _sessionMetrics;

    // Persistent state (immutable — replaced on each event)
    private SessionState _state = SessionState.Empty;

    // Track whether system prompt was recovered from journal
    private bool _systemPromptRecovered;

    public override string PersistenceId { get; }
    public ITimerScheduler Timers { get; set; } = null!;

    public LlmSessionActor(
        string entityId,
        IChatClientProvider clientProvider,
        SessionConfig config,
        ISystemPromptProvider promptProvider,
        IToolExecutor? toolExecutor = null,
        IToolAuditLogger? auditLogger = null,
        ToolRegistry? toolRegistry = null,
        IMemoryExtractor? memoryExtractor = null,
        IMemoryRecallCoordinator? memoryRecallCoordinator = null,
        IMemoryCheckpointSink? memoryCheckpointSink = null,
        TimeProvider? timeProvider = null,
        IReadOnlyList<IContextLayerProvider>? contextLayers = null,
        NetclawPaths? paths = null,
        SkillRegistry? skillRegistry = null,
        Telemetry.ISessionMetrics? sessionMetrics = null,
        ISessionLifecycleObserver? lifecycleObserver = null)
    {
        _sessionId = new SessionId(entityId);
        _skillRegistry = skillRegistry;
        _sessionMetrics = sessionMetrics;
        _lifecycleObserver = lifecycleObserver;
        _chatClient = clientProvider.GetClient(ModelRole.Main);
        _compactionClient = config.CompactionModelId is not null
            ? clientProvider.GetClient(ModelRole.Compaction)
            : _chatClient;
        _config = config;
        _promptProvider = promptProvider;
        _contextLayers = contextLayers ?? [];
        _toolExecutor = toolExecutor;
        _auditLogger = auditLogger;
        _memoryExtractor = memoryExtractor ?? NullMemoryExtractor.Instance;
        _memoryRecallCoordinator = memoryRecallCoordinator ?? NullMemoryRecallCoordinator.Instance;
        _memoryCheckpointSink = memoryCheckpointSink ?? NullMemoryCheckpointSink.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sessionsBasePath = paths?.SessionsDirectory;
        _sessionLogsBasePath = paths?.SessionsDirectory;
        PersistenceId = $"session-{entityId}";

        // Enrich logger with session context — all log messages automatically include SessionId
        _log = Context.GetLogger().WithContext("SessionId", _sessionId.Value);

        // Load all non-MCP tools for initial LLM calls.
        // MCP tools are loaded dynamically via search_tools and can be retained for a
        // small number of future turns (configurable lease) to reduce rediscovery churn.
        _fullRegistry = toolRegistry;
        if (toolRegistry is not null)
        {
            _availableTools.AddRange(toolRegistry.GetAlwaysLoadedTools());
        }
        _baseToolCount = _availableTools.Count;

        // ── Recovery handlers ──
        Recover<SystemPromptSet>(evt =>
        {
            _state = _state.Apply(evt);
            _systemPromptRecovered = true;
        });
        Recover<TurnRecorded>(evt => _state = _state.Apply(evt));
        Recover<SessionTitleSet>(evt => _state = _state.Apply(evt));
        Recover<SessionCompacted>(evt => _state = _state.Apply(evt));
        Recover<SnapshotOffer>(offer =>
        {
            if (offer.Snapshot is SessionSnapshot snapshot)
            {
                _state = SessionState.FromSnapshot(snapshot);
                _systemPromptRecovered = _state.History.Count > 0
                    && _state.History[0].Role == Protocol.ChatRole.System;
                _log.Info("Recovered from snapshot (turns={TurnCount})", _state.TurnCount);
            }
        });
        Recover<RecoveryCompleted>(_ =>
        {
            _log.Info("Recovery complete (turns={TurnCount}, history={HistoryCount})",
                _state.TurnCount, _state.History.Count);

            if (!_systemPromptRecovered)
            {
                SetSystemPrompt();
            }

            Become(Ready);

            if (_sessionLogsBasePath is not null)
            {
                _logActor = Context.ActorOf(
                    SessionLogActor.CreateProps(_sessionId, _sessionLogsBasePath, _timeProvider),
                    "session-log");
            }
        });
    }

    // ── Command behaviors ──

    private void Ready()
    {
        CommandSubscriptionMessages();
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

            _log.Info("Session idle, passivating (timeout={Timeout})", _config.IdleTimeout);
            _lifecycleObserver?.OnSessionDeactivated(_sessionId);
            SaveSnapshot(_state.ToSnapshot());
            Context.Stop(Self);
        });

        Command<ProcessingWatchdogExpired>(_ => { });
        Command<CompactionWorkCompleted>(_ => { });
        Command<CompactionWorkFailed>(_ => { });
        Command<SpawnChildActorRequest>(msg => Sender.Tell(Context.ActorOf(msg.Props, msg.ActorName)));
        Command<DeliveryFailed>(HandleDeliveryFailedWhenReady);

        Command<SendUserMessage>(cmd =>
        {
            ClearDeliveryRetryState();
            BindTurnTelemetry(cmd.Source);
            TurnLog().Info(
                "turn_received channel={ChannelType} sender={SenderId} hasMedia={HasMedia} textChars={TextLength}",
                cmd.Source?.ChannelType.ToWireValue() ?? "unknown",
                cmd.Source?.SenderId ?? "unknown",
                cmd.MediaReferences.Count > 0,
                cmd.Content?.Length ?? 0);
            _logActor?.Tell(cmd);

            _toolCallCount = 0;
            _toolIterationCount = 0;
            _budgetNudgeSent = false;
            _postToolNudgeSent = false;
            _preToolEmptyResponseCount = 0;
            _forceNoToolsActive = false;
            _toolCallHashes.Clear();
            _duplicateNudgeSent = false;
            PrepareDiscoveredToolsForNewTurn();

            // Modality gate: strip unsupported media references
            var mediaRefs = cmd.MediaReferences;
            if (mediaRefs.Count > 0 && !_config.InputModalities.HasFlag(Configuration.ModelModality.Image))
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

            // If ALL content is images (no text) and model doesn't support vision, skip entirely
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
                    TurnNumber = _state.TurnCount
                });
                TryReplyAck();
                return;
            }

            var userContent = cmd.Content ?? string.Empty;
            _state = _state.AddUserMessage(userContent, mediaRefs.Count > 0 ? mediaRefs : null);
            TryReplyAck();
            FireLlmCall(userContent);
            Become(Processing);
        });
    }

    private void Processing()
    {
        // Disable idle timeout while processing — re-enabled in Become(Ready)
        Context.SetReceiveTimeout(null);
        CommandSubscriptionMessages();
        CommandSnapshotMessages();

        Command<SendUserMessage>(cmd =>
        {
            ClearDeliveryRetryState();
            _log.Info("Buffering user message (LLM call in progress)");
            _buffer.Add(cmd);
            TryReplyAck();
        });

        Command<DeliveryFailed>(msg =>
        {
            if (!IsRetryableDeliveryFailure(msg))
            {
                _log.Warning(
                    "Non-retryable delivery feedback while processing channel={Channel} turn={Turn} kind={FailureKind}; injecting context",
                    msg.ChannelType,
                    msg.TurnNumber,
                    msg.FailureKind);
                _state = _state.AddSystemNudge(BuildDeliveryFailureNudge(msg));
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
            StopProcessingWatchdog();

            var response = msg.Response;
            var lastMessage = response.Messages[^1];

            // Check for tool calls
            var toolCalls = lastMessage.Contents.OfType<FunctionCallContent>().ToList();
            if (toolCalls.Count > 0 && _forceNoToolsActive)
            {
                TurnLog().Warning(
                    "turn_force_no_tools_violation toolCallCount={ToolCallCount} budgetUsed={BudgetUsed} max={Max}",
                    toolCalls.Count,
                    _toolCallCount,
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

            // Guard: if the LLM produced no text and no tool calls, it likely tried to
            // call an MCP tool that isn't in ChatOptions.Tools yet. Add a nudge and retry.
            var hasText = lastMessage.Contents.OfType<TextContent>().Any(t => !string.IsNullOrWhiteSpace(t.Text));
            if (!hasText && _toolIterationCount == 0)
            {
                _preToolEmptyResponseCount++;
                if (_preToolEmptyResponseCount > MaxPreToolEmptyResponseRetries)
                {
                    _log.Warning(
                        "LLM produced empty response after {RetryCount} pre-tool retries; failing turn",
                        _preToolEmptyResponseCount - 1);
                    FailCurrentTurn(
                        EmptyResponseFallbackMessage,
                        new InvalidOperationException("LLM produced repeated empty responses before any tool execution."),
                        ErrorCategory.ProviderFailure);
                    return;
                }

                _log.Warning("LLM produced empty response (no text, no tool calls) — retrying with nudge");
                _state = _state.AddSystemNudge(
                    "Your previous response was empty. If you need MCP capabilities, call search_tools(\"servers\") to pick a server "
                    + "(for example browser, memory, or email), then call search_tools(\"<intent>\", server: \"<server_name>\") to load tools. "
                    + "MCP tools are not directly callable until loaded via search_tools.");
                FireLlmCall();
                return;
            }

            // Guard: if the LLM did tool work but produced an empty final response, nudge it
            // to continue working or answer the user.
            if (!hasText && _toolIterationCount > 0 && !_postToolNudgeSent)
            {
                _log.Debug("LLM produced empty response after {ToolIterations} tool iteration(s) — nudging",
                    _toolIterationCount);
                _postToolNudgeSent = true;
                _state = _state.AddSystemNudge(
                    "You received tool results but did not respond. "
                    + "Continue working or answer the user's question.");
                FireLlmCall();
                return;
            }

            if (!hasText && _toolIterationCount > 0 && _postToolNudgeSent)
            {
                _log.Warning(
                    "LLM produced empty response after tool work and post-tool nudge; failing turn after {ToolIterations} tool iteration(s)",
                    _toolIterationCount);
                FailCurrentTurn(
                    EmptyResponseFallbackMessage,
                    new InvalidOperationException("LLM produced an empty response after tool execution and follow-up nudge."),
                    ErrorCategory.ProviderFailure);
                return;
            }

            // Normal text response — persist turn
            HandleTextResponse(lastMessage, response.Usage, msg.StreamedText, msg.StreamedThinking, msg.RecallResult);
        });

        Command<LlmResponseDeltaReceived>(msg =>
        {
            RefreshProcessingWatchdogIfActive();

            switch (msg.Content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    EmitOutput(new TextDeltaOutput
                    {
                        SessionId = _sessionId,
                        Delta = text.Text
                    }, OutputFilter.Text);
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
            StopProcessingWatchdog();

            var hasVerifiedToolFinding = msg.ToolResults.Any(r =>
                r.Name is "web_search" or "webfetch");
            if (hasVerifiedToolFinding)
            {
                var summarized = string.Join("\n", msg.ToolResults
                    .Where(r => r.Name is "web_search" or "webfetch")
                    .Take(2)
                    .Select(r => $"[{r.Name}] {r.Content}"));

                EnqueueCheckpointFireAndForget(new MemoryCheckpointRequest(
                    SessionId: _sessionId,
                    TurnId: _activeTurnId,
                    TriggerType: Memory.CheckpointTriggerType.VerifiedToolFinding,
                    Priority: 60,
                    Payload: new MemoryCheckpointPayload(
                        SessionId: _sessionId.Value,
                        TriggerType: "verified-tool-finding",
                        Source: "tool",
                        Content: summarized,
                        UserContent: null,
                        AssistantContent: null,
                        IsExplicitRequest: false,
                        HasVerifiedToolFinding: true,
                        IsCompactionBoundary: false,
                        HasAcceptedSubAgentFinding: false,
                        Domain: _sessionId.ToMemoryDomain(),
                        Sensitivity: Memory.MemorySensitivity.Normal.ToWireValue(),
                        RecallMode: Memory.MemoryRecallMode.Auto.ToWireValue(),
                        Confidence: 0.85,
                        Kind: Memory.MemoryKind.Record.ToWireValue(),
                        Title: "verified-tool-finding",
                        UpdateSemantics: Memory.MemoryUpdateSemantics.ImmutableRecord.ToWireValue())));
            }

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

            // Add tool results to history and log each result
            foreach (var result in msg.ToolResults)
            {
                _state = _state with { History = _state.History.Add(result) };

                var preview = result.Content is { Length: > 200 }
                    ? result.Content[..200] + "..."
                    : result.Content ?? "(null)";
                _log.Info("Tool [{ToolName}] (call={CallId}) result: {Result}",
                    result.Name ?? "unknown", result.ToolCallId ?? "?", preview);
            }

            // Dynamic tool loading: if search_tools was called, load discovered tools
            // into the available tools list so they can be called in subsequent turns
            if (_fullRegistry is not null)
            {
                foreach (var result in msg.ToolResults)
                {
                    if (result.Name is "search_tools" && result.Content is not null)
                    {
                        LoadDiscoveredTools(result.Content);
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

            _toolCallCount += msg.ToolResults.Count;
            _toolIterationCount++;

            // Duplicate tool call detection: warn model if identical tool+args calls repeat.
            // Hashes are tracked in HandleToolCallResponse where args are available.
            const int duplicateToolThreshold = 3;
            if (!_duplicateNudgeSent)
            {
                foreach (var (callHash, dupCount) in _toolCallHashes)
                {
                    if (dupCount >= duplicateToolThreshold)
                    {
                        // Extract tool name from the hash key (format: "toolName:{args}")
                        var toolName = callHash.AsSpan(0, callHash.IndexOf(':', StringComparison.Ordinal)).ToString();
                        TurnLog().Warning(
                            "turn_duplicate_tool_detected tool={ToolName} count={Count} iteration={Iteration}",
                            toolName, dupCount, _toolIterationCount);
                        _state = _state.AddSystemNudge(
                            $"You have called the tool '{toolName}' with the same arguments {dupCount} times this turn. "
                            + "This strongly indicates you are repeating work you already completed. "
                            + "Review your prior tool results — the information you need is already in the conversation. "
                            + "If the task is complete, produce your final response to the user.");
                        _duplicateNudgeSent = true;
                        break;
                    }
                }
            }

            // Mid-loop user message injection: if the user sent messages while tools were
            // running, inject them into the conversation now so the LLM can see corrections
            // like "stop" or "that's already done" before the next iteration.
            if (_buffer.Count > 0)
            {
                TurnLog().Info("turn_mid_loop_buffer_drain count={BufferCount} iteration={Iteration}",
                    _buffer.Count, _toolIterationCount);
                foreach (var buffered in _buffer)
                {
                    var refs = buffered.MediaReferences.Count > 0 ? buffered.MediaReferences : null;
                    _state = _state.AddUserMessage(buffered.Content, refs);
                }
                _buffer.Clear();
            }

            // Safety circuit breaker: force text response when tool call budget exhausted
            if (_toolCallCount >= _config.MaxToolCallsPerTurn)
            {
                TurnLog().Warning("turn_tool_call_limit_reached callCount={CallCount} max={Max} iteration={Iteration}",
                    _toolCallCount, _config.MaxToolCallsPerTurn, _toolIterationCount);
                _state = _state.AddSystemNudge(
                    "You have reached the tool call limit for this turn. "
                    + "Do NOT request any more tools. "
                    + "Summarize the work you completed and answer the user's question "
                    + "based on the information you have gathered so far. "
                    + "If you could not complete the task, explain what you found and what remains.");
                FireLlmCall(forceNoTools: true);
                return;
            }

            // Budget-awareness nudge: warn the model when approaching the limit
            var budgetThreshold = (int)(_config.MaxToolCallsPerTurn * 0.75);
            if (_toolCallCount >= budgetThreshold && !_budgetNudgeSent)
            {
                var remaining = _config.MaxToolCallsPerTurn - _toolCallCount;
                _state = _state.AddSystemNudge(
                    $"You have used {_toolCallCount} of {_config.MaxToolCallsPerTurn} tool calls for this turn. "
                    + $"You have approximately {remaining} tool calls remaining. "
                    + "Start wrapping up your tool usage and prepare to answer the user's question.");
                _budgetNudgeSent = true;
            }

            // Fire follow-up LLM call with tool results in context
            TurnLog().Info("turn_tool_execution_complete iteration={Iteration} callCount={CallCount} max={Max} resultCount={ResultCount}",
                _toolIterationCount, _toolCallCount, _config.MaxToolCallsPerTurn, msg.ToolResults.Count);
            FireLlmCall();
        });

        Command<ToolExecutionFailed>(msg =>
        {
            StopProcessingWatchdog();
            TurnLog().Error(msg.Cause, "turn_tool_execution_failed");

            const string errorMessage = "I encountered an error executing a tool. Please try again.";
            var category = msg.Cause is TimeoutException ? ErrorCategory.Timeout : ErrorCategory.ToolFailure;
            FailCurrentTurn(errorMessage, msg.Cause, category);
        });

        Command<LlmCallFailed>(msg =>
        {
            StopProcessingWatchdog();

            // Context overflow: compact and recover instead of failing the turn
            if (IsContextOverflowError(msg.Cause))
            {
                TurnLog().Warning(msg.Cause, "turn_context_overflow — triggering emergency compaction");

                EmitOutput(new ErrorOutput
                {
                    SessionId = _sessionId,
                    Message = "Context window exceeded — compacting session history. Please resend your last message.",
                    Category = ErrorCategory.ProviderFailure,
                    CorrelationId = Guid.NewGuid(),
                    Cause = msg.Cause
                });

                // Use the configured context window as the token count estimate since
                // the provider rejected the request without returning usage stats.
                Self.Tell(new CompactionTriggered { InputTokenCount = _config.ContextWindowTokens });
                Become(Compacting);
                return;
            }

            TurnLog().Error(msg.Cause, "turn_llm_call_failed");

            var errorMessage = ExtractLlmErrorMessage(msg.Cause);
            var category = msg.Cause is TimeoutException ? ErrorCategory.Timeout : ErrorCategory.ProviderFailure;
            FailCurrentTurn(errorMessage, msg.Cause, category);
        });

        Command<ProcessingWatchdogExpired>(msg =>
        {
            if (!IsCurrentWatchdog(msg))
                return;

            _log.Error("Processing watchdog expired for operation {OperationName} (opId={OperationId})",
                msg.OperationName, msg.OperationId);

            var timeout = msg.OperationName is "tool-execution"
                ? TimeSpan.FromSeconds(Math.Max(1, _config.ToolExecutionTimeoutSeconds))
                : TimeSpan.FromSeconds(Math.Max(1, _config.TurnLlmTimeoutSeconds));

            var timeoutCause = new TimeoutException(
                $"Session processing operation '{msg.OperationName}' exceeded watchdog timeout of {timeout.TotalSeconds:F0}s");

            StopProcessingWatchdog();
            FailCurrentTurn("I encountered a timeout while processing your message. Please try again.", timeoutCause, ErrorCategory.Timeout);
        });

        Command<SpawnChildActorRequest>(msg => Sender.Tell(Context.ActorOf(msg.Props, msg.ActorName)));
    }

    private void Compacting()
    {
        // Disable idle timeout while compacting — re-enabled in Become(Ready)
        Context.SetReceiveTimeout(null);
        CommandSubscriptionMessages();
        CommandSnapshotMessages();

        // Buffer user messages during compaction (same as Processing)
        Command<SendUserMessage>(cmd =>
        {
            _log.Info("Buffering user message (compaction in progress)");
            _buffer.Add(cmd);
            TryReplyAck();
        });

        Command<ProcessingWatchdogExpired>(msg =>
        {
            if (!IsCurrentWatchdog(msg))
                return;

            _log.Error("Compaction watchdog expired for operation {OperationName} (opId={OperationId})",
                msg.OperationName, msg.OperationId);

            StopProcessingWatchdog();

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

        Command<CompactionTriggered>(msg =>
        {
            var timeout = GetCompactionTimeout();
            StartProcessingWatchdog("compaction", timeout);

            var operationId = _processingOperationId;
            var stateSnapshot = _state;
            var self = Self;
            var log = _log;
            var compactionClient = _compactionClient;

            _ = ExecuteCompactionAsync(
                stateSnapshot,
                msg.InputTokenCount,
                _config.KeepRecentToolResults,
                _config.KeepRecentMessages,
                _config.ContextWindowTokens,
                TimeSpan.FromSeconds(Math.Max(1, _config.SidecarLlmTimeoutSeconds)),
                timeout,
                compactionClient,
                self,
                log,
                operationId);
        });

        Command<CompactionWorkCompleted>(msg =>
        {
            if (!IsCurrentCompactionOperation(msg.OperationId))
                return;

            StopProcessingWatchdog();

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
                // NOTE: do NOT clear _autoLoadedSkills or _autoLoadedSkillContent here.
                // Skills loaded during this session are still relevant after compaction.
                // They will be re-injected on the next LLM call from the cache.

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
                        Sensitivity: Memory.MemorySensitivity.Normal.ToWireValue(),
                        RecallMode: Memory.MemoryRecallMode.Auto.ToWireValue(),
                        Confidence: 0.8,
                        Kind: Memory.MemoryKind.Document.ToWireValue(),
                        Title: "compaction-boundary",
                        UpdateSemantics: "append-document")));

                SaveSnapshot(_state.ToSnapshot());

                EmitOutput(new CompactionOutput
                {
                    SessionId = _sessionId,
                    MessagesBefore = msg.MessagesBefore,
                    MessagesAfter = _state.History.Count,
                    ToolResultsCleared = msg.ClearedCount > 0,
                    Summarized = !string.IsNullOrWhiteSpace(msg.Summary),
                    ContextWindowTokens = _config.ContextWindowTokens,
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

            StopProcessingWatchdog();

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

    private void DrainBufferOrReady()
    {
        if (_buffer.Count > 0)
        {
            _log.Info("Post-compaction: draining {BufferCount} buffered message(s)", _buffer.Count);
            foreach (var buffered in _buffer)
            {
                var refs = buffered.MediaReferences.Count > 0 ? buffered.MediaReferences : null;
                _state = _state.AddUserMessage(buffered.Content, refs);
            }
            _buffer.Clear();
            FireLlmCall();
            Become(Processing);
        }
        else
        {
            Become(Ready);
        }
    }

    private TimeSpan GetCompactionTimeout()
        => TimeSpan.FromSeconds(Math.Max(1,
            Math.Max(_config.TurnLlmTimeoutSeconds, _config.SidecarLlmTimeoutSeconds)));

    private bool IsCurrentCompactionOperation(long operationId)
        => _processingOperationName == "compaction" && _processingOperationId == operationId;

    private static async Task ExecuteCompactionAsync(
        SessionState stateSnapshot,
        long preCompactionInputTokens,
        int keepRecentToolResults,
        int keepRecentMessages,
        int contextWindowTokens,
        TimeSpan sidecarTimeout,
        TimeSpan compactionTimeout,
        IChatClient client,
        IActorRef self,
        ILoggingAdapter log,
        long operationId)
    {
        try
        {
            using var cts = new CancellationTokenSource(compactionTimeout);

            var messagesBefore = stateSnapshot.History.Count;
            var (compactionState, clearedCount) = stateSnapshot.ClearOldToolResults(keepRecentToolResults);
            var history = compactionState.History;

            if (clearedCount > 0)
            {
                log.Info("Phase 1: Cleared {ClearedCount} old tool result(s)", clearedCount);
            }

            var systemOffset = history.Count > 0 && history[0].Role == Protocol.ChatRole.System ? 1 : 0;
            var effectiveKeepCount = keepRecentMessages;
            List<SerializableChatMessage> compactedMessages;
            int discardStartIndex;

            while (true)
            {
                cts.Token.ThrowIfCancellationRequested();

                var reducer = new ExtractiveSessionReducer(effectiveKeepCount);
                var meaiMessages = ChatMessageConverter.ToAiMessages(history);
                var reduced = await reducer.ReduceAsync(meaiMessages, cts.Token);

                var reducedList = reduced as IList<Microsoft.Extensions.AI.ChatMessage> ?? reduced.ToList();
                var keptNonSystemCount = reducedList.Count(m => m.Role != Microsoft.Extensions.AI.ChatRole.System);

                var startIndex = history.Count - keptNonSystemCount;
                discardStartIndex = startIndex;
                compactedMessages = [];
                for (var i = Math.Max(systemOffset, startIndex); i < history.Count; i++)
                {
                    compactedMessages.Add(history[i]);
                }

                var estimatedTokens = EstimateTokens(compactedMessages, systemOffset > 0 ? history[0] : null);
                var budgetHalf = contextWindowTokens / 2;

                if (estimatedTokens <= budgetHalf || effectiveKeepCount <= 2)
                    break;

                var newKeepCount = Math.Max(2, effectiveKeepCount / 2);
                log.Info("Adaptive compaction: estimated {EstimatedTokens} tokens > {Budget} budget half, reducing keep count {OldKeep} -> {NewKeep}",
                    estimatedTokens, budgetHalf, effectiveKeepCount, newKeepCount);
                effectiveKeepCount = newKeepCount;
            }

            log.Info("Phase 2: Extractive reduction (history={HistoryCount} -> {KeptCount} messages, keepCount={KeepCount})",
                history.Count, compactedMessages.Count + systemOffset, effectiveKeepCount);

            var observationText = await GenerateObservationsAsync(
                client,
                history,
                systemOffset,
                discardStartIndex,
                sidecarTimeout,
                log,
                cts.Token);

            if (!string.IsNullOrWhiteSpace(observationText))
            {
                var observationMsg = new SerializableChatMessage
                {
                    Role = Protocol.ChatRole.User,
                    Content = ObservationPromptBuilder.WrapObservations(observationText)
                };
                compactedMessages.Insert(0, observationMsg);
                log.Info("Observer: generated {ObsLength} chars of observations from {DiscardedCount} discarded messages",
                    observationText.Length, Math.Max(0, discardStartIndex - systemOffset));
            }

            self.Tell(new CompactionWorkCompleted
            {
                OperationId = operationId,
                Summary = observationText ?? string.Empty,
                CompactedMessages = compactedMessages,
                MessagesBefore = messagesBefore,
                ClearedCount = clearedCount,
                PreCompactionInputTokens = preCompactionInputTokens,
                KeepCountUsed = effectiveKeepCount
            });
        }
        catch (Exception ex)
        {
            self.Tell(new CompactionWorkFailed
            {
                OperationId = operationId,
                Cause = ex
            });
        }
    }

    /// <summary>
    /// Fire a sidecar LLM call to generate a short session title.
    /// Best-effort — failures are silently ignored.
    /// </summary>
    private void MaybeGenerateTitle()
    {
        var interval = _config.TitleGenerationInterval;
        if (interval <= 0)
            return;

        // Generate on turn 1, then refresh every N turns
        var turn = _state.TurnCount;
        if (turn != 1 && turn % interval != 0)
            return;

        var history = _state.History;
        var self = Self;
        var client = _compactionClient;
        var log = _log;
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _config.SidecarLlmTimeoutSeconds));

        _ = GenerateTitleAsync(client, history, self, log, timeout);
    }

    private static async Task GenerateTitleAsync(
        IChatClient client,
        IReadOnlyList<SerializableChatMessage> history,
        IActorRef self,
        ILoggingAdapter log,
        TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var messages = new List<AiChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.User,
                    CompactionPromptBuilder.BuildTitleGenerationPrompt(history))
            };
            var response = await client.GetResponseAsync(messages, cancellationToken: cts.Token);
            var title = response.Messages[^1].Text ?? string.Empty;

            if (string.IsNullOrWhiteSpace(title))
            {
                log.Warning("Sidecar title generation returned null/whitespace text");
                return;
            }

            self.Tell(new TitleGenerationCompleted { Title = title });
        }
        catch (Exception ex)
        {
            // Title generation is best-effort — log and move on
            log.Warning("Sidecar title generation failed: {0}", ex.Message);
        }
    }

    /// <summary>
    /// Observer phase: compress discarded messages into observation notes via LLM call.
    /// Returns null if no messages to compress or if the LLM call fails (graceful degradation).
    /// </summary>
    private static async Task<string?> GenerateObservationsAsync(
        IChatClient client,
        IReadOnlyList<SerializableChatMessage> history,
        int systemOffset,
        int keepStartIndex,
        TimeSpan sidecarTimeout,
        ILoggingAdapter log,
        CancellationToken cancellationToken)
    {
        var discardedMessages = new List<SerializableChatMessage>();
        for (var i = systemOffset; i < keepStartIndex && i < history.Count; i++)
        {
            discardedMessages.Add(history[i]);
        }

        if (discardedMessages.Count == 0)
            return null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(sidecarTimeout);
            var observerMessages = new List<AiChatMessage>
            {
                new(Microsoft.Extensions.AI.ChatRole.System,
                    ObservationPromptBuilder.BuildObservationSystemPrompt()),
                new(Microsoft.Extensions.AI.ChatRole.User,
                    ObservationPromptBuilder.BuildObservationUserPrompt(discardedMessages))
            };

            var response = await client.GetResponseAsync(
                observerMessages, cancellationToken: cts.Token);
            var text = response.Messages[^1].Text;

            if (string.IsNullOrWhiteSpace(text))
            {
                log.Warning("Observer returned empty observation text — falling back to extractive-only");
                return null;
            }

            return text;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Observer LLM call failed — falling back to extractive-only compaction");
            return null;
        }
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
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _config.SidecarLlmTimeoutSeconds));

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
        // Model produced tool calls — reset post-tool nudge so it can fire again
        // if the model stalls later in the chain.
        _postToolNudgeSent = false;
        _preToolEmptyResponseCount = 0;
        _forceNoToolsActive = false;

        // Add assistant message (with tool calls) to history
        var assistantMsg = ChatMessageConverter.FromAiMessage(lastMessage);
        _state = _state with { History = _state.History.Add(assistantMsg) };

        // Surface preamble text immediately before tool execution starts.
        // TextOutput handles the non-streaming (single-delta) path where no
        // TextDeltaOutput was emitted to subscribers.
        // BufferFlush tells streaming adapters to flush their accumulated buffer
        // so the preamble is visible to users before the potentially long tool phase.
        var hasPreambleText = lastMessage.Contents
            .OfType<TextContent>()
            .Any(t => !string.IsNullOrWhiteSpace(t.Text));

        if (hasPreambleText)
        {
            foreach (var content in lastMessage.Contents)
            {
                if (content is TextContent text && !string.IsNullOrWhiteSpace(text.Text))
                {
                    EmitOutput(new TextOutput
                    {
                        SessionId = _sessionId,
                        Text = text.Text
                    }, OutputFilter.Text);
                }
            }
            EmitOutput(new BufferFlush { SessionId = _sessionId });
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
            var hashKey = $"{tc.Name}:{argsJson ?? "{}"}";
            _toolCallHashes.TryGetValue(hashKey, out var dupCount);
            _toolCallHashes[hashKey] = dupCount + 1;
        }

        // Emit usage if present (intermediate turn)
        if (usage is not null)
        {
            EmitUsageOutput(usage);
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
        var maxInlineToolResultChars = _config.MaxInlineToolResultChars;
        var toolExecutionTimeout = TimeSpan.FromSeconds(Math.Max(1, _config.ToolExecutionTimeoutSeconds));

        StartProcessingWatchdog("tool-execution", toolExecutionTimeout);

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

        _ = ExecuteToolsAsync(executor, toolCalls, sessionId, auditLogger, tp, sessionDir, maxInlineToolResultChars, toolExecutionTimeout, self, emitSubAgentOutput, spawnChildActor);
    }

    private void HandleTextResponse(
        AiChatMessage lastMessage,
        UsageDetails? usage,
        bool streamedText,
        bool streamedThinking,
        AutomaticRecallResult? recallResult)
    {
        _toolCallCount = 0; // Reset for potential buffer drain (new logical turn)
        _toolIterationCount = 0;
        _toolCallHashes.Clear();
        _duplicateNudgeSent = false;

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

            if (_config.MemorySidecarsEnabled)
                ObserveTurnForMemory(evt.UserMessage, evt.AssistantReply);

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
                    Sensitivity: Memory.MemorySensitivity.Normal.ToWireValue(),
                    RecallMode: Memory.MemoryRecallMode.Auto.ToWireValue(),
                    Confidence: 0.7,
                    Kind: Memory.MemoryKind.Document.ToWireValue(),
                    Title: "turn-completion",
                    UpdateSemantics: "append-document")));

            MarkTurnEligibleForDeliveryRetry(_state.TurnCount);

            // Check if compaction should trigger
            if (ShouldCompact())
            {
                _log.Info("Compaction threshold reached ({InputTokens} tokens >= {Threshold} limit), starting compaction",
                    _lastInputTokenCount, _config.CompactionTokenLimit);
                Self.Tell(new CompactionTriggered { InputTokenCount = _lastInputTokenCount });
                Become(Compacting);
                return;
            }

            DrainBufferedMessagesOrBecomeReady();
        });
    }

    private void DrainBufferedMessagesOrBecomeReady()
    {
        if (_buffer.Count > 0)
        {
            TurnLog().Info("turn_buffer_drain count={BufferCount}", _buffer.Count);
            foreach (var buffered in _buffer)
            {
                var refs = buffered.MediaReferences.Count > 0 ? buffered.MediaReferences : null;
                _state = _state.AddUserMessage(buffered.Content, refs);
            }

            _buffer.Clear();
            FireLlmCall();
            Become(Processing);
            return;
        }

        Become(Ready);
    }

    private void HandleDeliveryFailedWhenReady(DeliveryFailed msg)
    {
        if (_deliveryRetryEligibleTurnNumber != msg.TurnNumber)
        {
            _log.Warning(
                "Ignoring stale delivery feedback channel={Channel} turn={Turn} eligibleTurn={EligibleTurn}",
                msg.ChannelType,
                msg.TurnNumber,
                _deliveryRetryEligibleTurnNumber?.ToString() ?? "none");
            return;
        }

        if (!IsRetryableDeliveryFailure(msg))
        {
            _log.Warning(
                "Non-retryable delivery failure channel={Channel} turn={Turn} kind={FailureKind}; injecting context for next turn",
                msg.ChannelType,
                msg.TurnNumber,
                msg.FailureKind);
            _state = _state.AddSystemNudge(BuildDeliveryFailureNudge(msg));
            return;
        }

        if (_deliveryRetryCount >= MaxDeliveryFailureRetries)
        {
            _log.Warning(
                "Delivery retry budget exhausted channel={Channel} turn={Turn} kind={FailureKind}",
                msg.ChannelType,
                msg.TurnNumber,
                msg.FailureKind);
            _deliveryRetryEligibleTurnNumber = null;
            _deliveryRetryChainActive = false;
            return;
        }

        _deliveryRetryCount++;
        _deliveryRetryChainActive = true;

        _log.Warning(
            "Retrying after delivery failure channel={Channel} turn={Turn} kind={FailureKind} attempt={Attempt}",
            msg.ChannelType,
            msg.TurnNumber,
            msg.FailureKind,
            _deliveryRetryCount);

        _state = _state.AddSystemNudge(BuildDeliveryFailureNudge(msg));
        FireLlmCall();
        Become(Processing);
    }

    private static bool IsRetryableDeliveryFailure(DeliveryFailed msg)
        => msg.FailureKind is DeliveryFailureKind.ContentRejected
            or DeliveryFailureKind.MessageTooLarge
            or DeliveryFailureKind.UnsupportedContent;

    private static string BuildDeliveryFailureNudge(DeliveryFailed msg)
    {
        var guidance = msg.FailureKind switch
        {
            DeliveryFailureKind.ContentRejected => "Produce a simpler channel-safe response and avoid the content pattern the channel rejected.",
            DeliveryFailureKind.MessageTooLarge => "Produce a shorter response that fits the channel's length limits.",
            DeliveryFailureKind.UnsupportedContent => "Avoid unsupported formatting or content types for this channel.",
            DeliveryFailureKind.TransportFailure => "The channel experienced a transport error. Your response content was likely fine. Acknowledge to the user that delivery failed due to a technical issue and offer to retry.",
            DeliveryFailureKind.PermissionDenied => "The bot lacks permission to post in this channel. Inform the user that a permissions issue prevented delivery.",
            DeliveryFailureKind.Unknown or _ => "An unknown delivery error occurred. Acknowledge the issue to the user."
        };

        return $"Your last response could not be delivered to the user via {msg.ChannelType}. "
            + $"The user did not receive it. Delivery failure kind: {msg.FailureKind}. "
            + $"Channel error: {msg.ErrorMessage}\n{guidance}";
    }

    private void ClearDeliveryRetryState()
    {
        _deliveryRetryEligibleTurnNumber = null;
        _deliveryRetryCount = 0;
        _deliveryRetryChainActive = false;
    }

    private void MarkTurnEligibleForDeliveryRetry(int turnNumber)
    {
        _deliveryRetryEligibleTurnNumber = turnNumber;
        if (!_deliveryRetryChainActive)
            _deliveryRetryCount = 0;
    }

    private bool ShouldCompact()
    {
        return _config.CompactionTokenLimit > 0
            && _lastInputTokenCount >= _config.CompactionTokenLimit;
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
                NowMs());
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
        });

        Command<MemoryObservationFailed>(msg =>
        {
            TurnLog().Warning("memory_observation_sidecar_failed reason={Reason}", msg.Reason);
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

        base.PreRestart(reason, message);
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
            return $"Context window exceeded after compaction — the session has too many tools or a large system prompt for the {_config.ModelId} context window ({_config.ContextWindowTokens} tokens). Try reducing tools or increasing the model's context window.";

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
    /// Naive token estimation: total character count / 4.
    /// Includes the system prompt (if present) plus all compacted messages.
    /// </summary>
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

    private static int EstimateTokens(
        List<SerializableChatMessage> messages,
        SerializableChatMessage? systemPrompt)
    {
        var totalChars = 0;
        if (systemPrompt is not null)
            totalChars += systemPrompt.Content?.Length ?? 0;
        foreach (var msg in messages)
        {
            totalChars += msg.Content?.Length ?? 0;
            foreach (var tc in msg.ToolCalls)
                totalChars += tc.ArgumentsJson?.Length ?? 0;
        }
        return totalChars / 4;
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
            _log.Info("No system prompt layers available");
            return;
        }

        var evt = new SystemPromptSet
        {
            SessionId = _sessionId,
            Content = content,
            SetAtMs = NowMs()
        };

        Persist(evt, e =>
        {
            _state = _state.Apply(e);
            _log.Info("System prompt set ({PromptLength} chars)", content.Length);
        });
    }

    private void FireLlmCall(string? recallQuery = null, bool forceNoTools = false)
    {
        _forceNoToolsActive = forceNoTools;

        var sessionDir = GetSessionDirectory();
        var messages = ChatMessageConverter.ToAiMessages(_state.History, sessionDir);

        var recallSw = Stopwatch.StartNew();
        _activeRecall = ResolveRecallBundle(recallQuery);
        recallSw.Stop();

        var recallIds = _activeRecall.Items.Count == 0
            ? "-"
            : string.Join(",", _activeRecall.Items.Select(i => i.Id));
        TurnLog().Info(
            "turn_memory_recall degraded={Degraded} stage={Stage} durationMs={DurationMs} itemCount={ItemCount} itemIds={ItemIds}",
            _activeRecall.Degraded,
            _activeRecall.DegradeStage ?? "-",
            recallSw.ElapsedMilliseconds,
            _activeRecall.Items.Count,
            recallIds);

        InjectAutomaticRecall(messages, _activeRecall);

        if (_activeRecall.Items.Count > 0)
        {
            _sessionMetrics?.RecordMemoriesRecalled(_activeRecall.Items.Count);
        }

        // Skill auto-load: deterministic keyword matching against enriched skill index.
        // Injects matched skill content as transient system messages before the LLM decides.
        var userMessage = _state.FindLastUserMessage()?.Content;
        if (!string.IsNullOrWhiteSpace(userMessage))
            ResolveAndInjectAutoLoadedSkills(messages, userMessage);

        // Inject dynamic context layers (e.g. tool index) as transient system messages.
        // These are NOT persisted — rebuilt on every call so rehydrated sessions stay fresh.
        InjectDynamicContextLayers(messages);
        var self = Self;
        var client = _chatClient;
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _config.TurnLlmTimeoutSeconds));

        ChatOptions? options = null;
        if (!forceNoTools && _availableTools.Count > 0)
        {
            options = new ChatOptions
            {
                Tools = _availableTools.ToList()
            };
        }

        StartProcessingWatchdog("llm-call", timeout);

        TurnLog().Info("turn_llm_call_start messages={MessageCount} toolsEnabled={ToolsEnabled} forceNoTools={ForceNoTools}",
            messages.Count,
            options?.Tools?.Count > 0,
            forceNoTools);

        _ = InvokeLlmAsync(client, messages, options, self, timeout);
    }

    private AutomaticRecallResult ResolveRecallBundle(string? recallQuery)
    {
        var query = string.IsNullOrWhiteSpace(recallQuery)
            ? _state.FindLastUserMessage()?.Content ?? string.Empty
            : recallQuery;

        if (string.IsNullOrWhiteSpace(query))
            return new AutomaticRecallResult([]);

        var recentUser = _state.History
            .Where(x => x.Role == Protocol.ChatRole.User && !SessionState.IsSystemNudge(x))
            .Select(x => x.Content)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .TakeLast(3)
            .ToArray();

        var request = new AutomaticRecallRequest(
            _sessionId.Value,
            query,
            recentUser,
            3,
            RecentAssistantMessages: _state.History
                .Where(x => x.Role == Protocol.ChatRole.Assistant)
                .Select(x => x.Content)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .TakeLast(3)
                .ToArray(),
            RecentEntities: [],
            HardScopeOverride: _sessionId.ToMemoryDomain(),
            ThreadTitle: _state.Title);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            return _memoryRecallCoordinator.RecallAsync(request, cts.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            return new AutomaticRecallResult([], true, ex.Message, "resolution");
        }
    }

    private static void InjectAutomaticRecall(List<AiChatMessage> messages, AutomaticRecallResult recall)
    {
        if (recall.Degraded)
        {
            var degraded = new AiChatMessage(
                Microsoft.Extensions.AI.ChatRole.System,
                "[memory-recall]\nstatus: degraded\nreason: automatic recall unavailable for this turn");
            var insertAt = messages.FindLastIndex(m => m.Role == Microsoft.Extensions.AI.ChatRole.System);
            messages.Insert(insertAt >= 0 ? insertAt + 1 : 0, degraded);
            return;
        }

        if (recall.Items.Count == 0)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("[memory-recall]");
        sb.AppendLine("status: healthy");
        sb.AppendLine("mode: automatic");
        foreach (var item in recall.Items)
        {
            sb.AppendLine($"- {item.Title} [{item.Id}] domain={item.Domain} sensitivity={item.Sensitivity} score={item.Score:F2}");
            sb.AppendLine($"  {item.Content}");
        }

        var recallMessage = new AiChatMessage(Microsoft.Extensions.AI.ChatRole.System, sb.ToString().TrimEnd());
        var index = messages.FindLastIndex(m => m.Role == Microsoft.Extensions.AI.ChatRole.System);
        messages.Insert(index >= 0 ? index + 1 : 0, recallMessage);
    }

    /// <summary>
    /// Deterministic skill auto-loading: score user message against enriched keywords,
    /// load matching skill content from disk (first match) or cache (subsequent turns),
    /// and inject as transient system messages.
    /// </summary>
    private void ResolveAndInjectAutoLoadedSkills(List<AiChatMessage> messages, string userMessage)
    {
        if (_skillRegistry is null) return;

        // 1. Find NEW matches (excludes already-loaded skills)
        var newMatches = _skillRegistry.MatchByKeywords(userMessage, _autoLoadedSkills);
        foreach (var match in newMatches)
        {
            try
            {
                var raw = File.ReadAllText(match.Skill.FilePath);
                _autoLoadedSkillContent[match.Skill.Name] = Skills.SkillScanner.ExtractBody(raw);
            }
            catch (IOException ex)
            {
                _log.Warning(ex, "Failed to read skill for auto-load: {SkillName}", match.Skill.Name);
                continue;
            }

            _autoLoadedSkills.Add(match.Skill.Name);
        }

        // 2. Inject ALL loaded skills (new + previously cached)
        if (_autoLoadedSkillContent.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var (name, content) in _autoLoadedSkillContent)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine($"[skill-auto-loaded: {name}]");
            sb.AppendLine(content);
        }

        var msg = new AiChatMessage(Microsoft.Extensions.AI.ChatRole.System, sb.ToString().TrimEnd());
        var idx = messages.FindLastIndex(m => m.Role == Microsoft.Extensions.AI.ChatRole.System);
        messages.Insert(idx >= 0 ? idx + 1 : 0, msg);

        if (newMatches.Count > 0)
        {
            var details = string.Join(" | ", newMatches.Select(m =>
                $"{m.Skill.Name}:score={m.Score.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}"
                + $"/threshold={m.Threshold.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}"
                + $" tokens=[{string.Join(",", m.MatchedTokens)}]"
                + $" phrases=[{string.Join(",", m.MatchedPhrases)}]"));

            TurnLog().Info(
                "turn_skill_auto_load new={New} total={Total} skills={Names} tokenHits={TokenHits} phraseHits={PhraseHits} details={Details}",
                newMatches.Count, _autoLoadedSkillContent.Count,
                string.Join(",", newMatches.Select(m => m.Skill.Name)),
                string.Join(",", newMatches.Select(m => m.TokenHits)),
                string.Join(",", newMatches.Select(m => m.PhraseHits)),
                details);

            _sessionMetrics?.RecordSkillsLoaded(newMatches.Count);
        }
    }

    /// <summary>
    /// Inject dynamic context layers as system messages after the persisted system prompt
    /// but before user messages. Static (<see cref="ContextLayerTiming.OnceAtStart"/>) layers
    /// are injected on the first call and again after compaction resets
    /// <see cref="_startupContextInjected"/>. Per-turn layers are always injected.
    /// </summary>
    private void InjectDynamicContextLayers(List<AiChatMessage> messages)
    {
        if (_contextLayers.Count == 0) return;

        var parts = new List<string>();
        foreach (var layer in _contextLayers)
        {
            if (_startupContextInjected && layer.Timing == ContextLayerTiming.OnceAtStart)
                continue;

            var content = layer.GetContextLayer();
            if (!string.IsNullOrWhiteSpace(content))
                parts.Add(content.Trim());
        }

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

    private static async Task InvokeLlmAsync(
        IChatClient client,
        List<AiChatMessage> messages,
        ChatOptions? options,
        IActorRef self,
        TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var response = await InvokeStreamingResponseAsync(client, messages, options, self, cts.Token);
            self.Tell(response);
        }
        catch (OperationCanceledException ex)
        {
            self.Tell(new LlmCallFailed
            {
                Cause = new TimeoutException(
                    $"LLM call exceeded timeout of {timeout.TotalSeconds:F0}s",
                    ex)
            });
        }
        catch (Exception ex)
        {
            self.Tell(new LlmCallFailed { Cause = ex });
        }
    }

    private static async Task<LlmResponseReceived> InvokeStreamingResponseAsync(
        IChatClient client,
        List<AiChatMessage> messages,
        ChatOptions? options,
        IActorRef self,
        CancellationToken cancellationToken)
    {
        var contents = new List<AIContent>();
        var updates = new List<ChatResponseUpdate>();
        var textBuilder = new StringBuilder();
        var thinkingBuilder = new StringBuilder();
        string? pendingTextDelta = null;
        string? pendingThinkingDelta = null;
        var textDeltaCount = 0;
        var thinkingDeltaCount = 0;

        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            updates.Add(update);

            if (update.Contents is not null)
            {
                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text when !string.IsNullOrEmpty(text.Text):
                            textBuilder.Append(text.Text);
                            textDeltaCount++;
                            if (textDeltaCount == 1)
                            {
                                pendingTextDelta = text.Text;
                            }
                            else
                            {
                                if (textDeltaCount == 2 && !string.IsNullOrEmpty(pendingTextDelta))
                                {
                                    self.Tell(new LlmResponseDeltaReceived
                                    {
                                        Content = new TextContent(pendingTextDelta)
                                    });
                                }

                                self.Tell(new LlmResponseDeltaReceived { Content = content });
                            }
                            break;

                        case TextReasoningContent thinking when !string.IsNullOrEmpty(thinking.Text):
                            thinkingBuilder.Append(thinking.Text);
                            thinkingDeltaCount++;
                            if (thinkingDeltaCount == 1)
                            {
                                pendingThinkingDelta = thinking.Text;
                            }
                            else
                            {
                                if (thinkingDeltaCount == 2 && !string.IsNullOrEmpty(pendingThinkingDelta))
                                {
                                    self.Tell(new LlmResponseDeltaReceived
                                    {
                                        Content = new TextReasoningContent(pendingThinkingDelta)
                                    });
                                }

                                self.Tell(new LlmResponseDeltaReceived { Content = content });
                            }
                            break;

                        case FunctionCallContent:
                            contents.Add(content);
                            break;
                    }
                }
            }

        }

        if (thinkingBuilder.Length > 0)
            contents.Add(new TextReasoningContent(thinkingBuilder.ToString()));

        if (textBuilder.Length > 0)
            contents.Add(new TextContent(textBuilder.ToString()));

        var response = updates.Count > 0
            ? updates.ToChatResponse()
            : new ChatResponse(new AiChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, contents));

        if (response.Messages.Count == 0)
            response.Messages.Add(new AiChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, contents));

        return new LlmResponseReceived
        {
            Response = response,
            StreamedText = textDeltaCount > 1,
            StreamedThinking = thinkingDeltaCount > 1,
            RecallResult = null
        };
    }

    private sealed record ToolCallResult(
        SerializableChatMessage Message,
        IReadOnlyList<FileAttachmentInfo> FileAttachments,
        IReadOnlyList<CompletedSubAgentRun> CompletedSubAgentRuns,
        IReadOnlyList<AcceptedSubAgentFinding> AcceptedSubAgentFindings);

    private static async Task ExecuteToolsAsync(
        IToolExecutor executor,
        List<FunctionCallContent> toolCalls,
        SessionId sessionId,
        IToolAuditLogger? auditLogger,
        TimeProvider timeProvider,
        string sessionDir,
        int maxInlineToolResultChars,
        TimeSpan timeout,
        IActorRef self,
        Action<SubAgentOutput> emitSubAgentOutput,
        Func<object, string, CancellationToken, Task<object>> spawnChildActor)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);

            // Execute all tool calls in parallel — each is independent
            var tasks = toolCalls.Select(tc => ExecuteSingleToolAsync(
                executor,
                tc,
                sessionId,
                auditLogger,
                timeProvider,
                sessionDir,
                maxInlineToolResultChars,
                emitSubAgentOutput,
                spawnChildActor,
                cts.Token));
            var results = await Task.WhenAll(tasks);

            var fileAttachments = results.SelectMany(r => r.FileAttachments).ToList();
            self.Tell(new ToolExecutionCompleted
            {
                ToolResults = results.Select(r => r.Message).ToList(),
                FileAttachments = fileAttachments,
                CompletedSubAgentRuns = results
                    .SelectMany(r => r.CompletedSubAgentRuns)
                    .ToList(),
                AcceptedSubAgentFindings = results
                    .SelectMany(r => r.AcceptedSubAgentFindings)
                    .ToList()
            });
        }
        catch (OperationCanceledException ex)
        {
            self.Tell(new ToolExecutionFailed
            {
                Cause = new TimeoutException(
                    $"Tool execution exceeded timeout of {timeout.TotalSeconds:F0}s",
                    ex)
            });
        }
        catch (Exception ex)
        {
            self.Tell(new ToolExecutionFailed { Cause = ex });
        }
    }

    private static async Task<ToolCallResult> ExecuteSingleToolAsync(
        IToolExecutor executor,
        FunctionCallContent tc,
        SessionId sessionId,
        IToolAuditLogger? auditLogger,
        TimeProvider timeProvider,
        string sessionDir,
        int maxInlineToolResultChars,
        Action<SubAgentOutput> emitSubAgentOutput,
        Func<object, string, CancellationToken, Task<object>> spawnChildActor,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string resultText;
        var context = new Netclaw.Tools.ToolExecutionContext(sessionId.Value, sessionDir);
        context.SpawnChildActor = spawnChildActor;
        var completedRuns = new List<CompletedSubAgentRun>();
        var acceptedFindings = new List<AcceptedSubAgentFinding>();
        context.OnSubAgentActivity = info =>
        {
            if (info.IsStarted)
            {
                emitSubAgentOutput(new SubAgentOutput
                {
                    SessionId = sessionId,
                    TimestampMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    AgentName = info.AgentName,
                    Phase = Netclaw.Actors.SubAgents.SubAgentPhase.Started,
                    ToolCount = info.ToolCount,
                    Success = info.Success,
                    Duration = info.Duration
                });
            }

            if (!info.IsStarted)
            {
                string? decision = null;
                string? reason = null;

                if (info.Success && info.Findings.Count == 1)
                {
                    var singleDecision = ReviewSubAgentFinding(info.Findings[0], sessionId.Value);
                    decision = singleDecision.Decision.ToWireValue();
                    reason = singleDecision.Reason;
                }

                completedRuns.Add(new CompletedSubAgentRun
                {
                    RunId = info.RunId,
                    AgentName = info.AgentName,
                    Success = info.Success,
                    Duration = info.Duration,
                    FindingsCount = info.Findings.Count,
                    MemoryDecision = decision,
                    MemoryDecisionReason = reason
                });
            }

            if (!info.IsStarted && info.Success)
            {
                foreach (var finding in info.Findings)
                {
                    var decision = ReviewSubAgentFinding(finding, sessionId.Value);
                    acceptedFindings.Add(new AcceptedSubAgentFinding
                    {
                        RunId = info.RunId,
                        AgentName = info.AgentName,
                        Duration = info.Duration,
                        Shape = finding.Shape.ToWireValue(),
                        Title = finding.Title,
                        Content = finding.Content,
                        Kind = finding.Kind,
                        Domain = finding.Domain,
                        Sensitivity = finding.Sensitivity.ToWireValue(),
                        RecallMode = finding.RecallMode.ToWireValue(),
                        UpdateSemantics = finding.UpdateSemantics,
                        Confidence = finding.Confidence,
                        Durability = finding.Durability.ToWireValue(),
                        Reusability = finding.Reusability.ToWireValue(),
                        Evidence = finding.Evidence,
                        FreshnessAtMs = finding.FreshnessAtMs,
                        Decision = decision.Decision.ToWireValue(),
                        DecisionReason = decision.Reason
                    });
                }
            }
        };
        try
        {
            resultText = await executor.ExecuteAsync(tc, context, ct);
            sw.Stop();

            auditLogger?.Log(new ToolAuditEntry
            {
                SessionId = sessionId.Value,
                ToolName = tc.Name,
                CallId = tc.CallId,
                Timestamp = timeProvider.GetUtcNow(),
                Allowed = true,
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            resultText = $"Error executing tool: {ex.Message}";

            auditLogger?.Log(new ToolAuditEntry
            {
                SessionId = sessionId.Value,
                ToolName = tc.Name,
                CallId = tc.CallId,
                Timestamp = timeProvider.GetUtcNow(),
                Allowed = true,
                Duration = sw.Elapsed
            });
        }

        resultText = ClampToolResult(resultText, maxInlineToolResultChars);

        var message = new SerializableChatMessage
        {
            Role = Protocol.ChatRole.Tool,
            Content = resultText,
            ToolCallId = tc.CallId,
            Name = tc.Name
        };

        return new ToolCallResult(
            message,
            context.FileAttachments,
            completedRuns,
            acceptedFindings);
    }

    internal static SubAgentFindingReviewResult ReviewSubAgentFinding(
        SubAgentFinding finding,
        string sessionId)
    {
        if (string.IsNullOrWhiteSpace(finding.Title))
            return new(SubAgentFindingReviewDecision.Deferred, "missing title");

        if (string.IsNullOrWhiteSpace(finding.Content))
            return new(SubAgentFindingReviewDecision.Rejected, "empty content");

        if (finding.Shape != SubAgentFindingShape.Conclusion)
            return new(SubAgentFindingReviewDecision.Rejected, "unsupported shape");

        if (!Enum.IsDefined(finding.Durability))
            return new(SubAgentFindingReviewDecision.Deferred, "missing durability");

        if (!Enum.IsDefined(finding.Reusability))
            return new(SubAgentFindingReviewDecision.Deferred, "missing reusability");

        if (finding.RecallMode == SubAgentFindingRecallMode.Never)
            return new(SubAgentFindingReviewDecision.Rejected, "recallMode=never");

        if (!string.Equals(finding.Kind, "record", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(finding.Kind, "document", StringComparison.OrdinalIgnoreCase))
            return new(SubAgentFindingReviewDecision.Deferred, "unsupported kind");

        if (finding.Sensitivity == SubAgentFindingSensitivity.Secret
            && finding.RecallMode == SubAgentFindingRecallMode.Auto)
            return new(SubAgentFindingReviewDecision.Rejected, "secret cannot auto-recall");

        var expectedDomain = new SessionId(sessionId).ToMemoryDomain();
        if (!string.Equals(finding.Domain, expectedDomain, StringComparison.OrdinalIgnoreCase))
            return new(SubAgentFindingReviewDecision.Deferred, $"domain mismatch: expected {expectedDomain}");

        if (finding.Durability != SubAgentFindingDurability.Durable)
            return new(SubAgentFindingReviewDecision.Deferred, "insufficient durability");

        if (finding.Reusability != SubAgentFindingReusability.Reusable)
            return new(SubAgentFindingReviewDecision.Deferred, "insufficient reusability");

        if (finding.Confidence < 0.55)
            return new(SubAgentFindingReviewDecision.Deferred, "low confidence");

        return new(SubAgentFindingReviewDecision.Accepted, null);
    }

    private static string ClampToolResult(string resultText, int maxInlineToolResultChars)
    {
        if (maxInlineToolResultChars <= 0 || resultText.Length <= maxInlineToolResultChars)
            return resultText;

        var omittedChars = resultText.Length - maxInlineToolResultChars;
        return resultText[..maxInlineToolResultChars]
               + $"\n[tool result truncated: omitted {omittedChars} chars to protect context window]";
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
    /// Parse tool names from search_tools output and add their AITool definitions
    /// to <see cref="_availableTools"/> so the LLM can call them in subsequent iterations.
    /// </summary>
    private void LoadDiscoveredTools(string searchToolOutput)
    {
        if (_fullRegistry is null) return;

        // Parse tool names from the search output format: "  server/toolname — description"
        foreach (var line in searchToolOutput.Split('\n'))
        {
            var trimmed = line.TrimStart();
            var separatorIndex = trimmed.IndexOf(" — ", StringComparison.Ordinal);
            if (separatorIndex < 0)
                continue;

            var toolName = trimmed[..separatorIndex].Trim();
            if (string.IsNullOrEmpty(toolName))
                continue;

            var tool = _fullRegistry.GetByName(toolName);
            if (tool is null)
                continue;

            RememberDiscoveredTool(toolName, tool);
            AddAvailableToolIfMissing(toolName, tool.ToAITool());
        }
    }

    private void PrepareDiscoveredToolsForNewTurn()
    {
        if (_fullRegistry is null)
            return;

        if (_config.DiscoveredToolRetentionTurns <= 0 || _config.DiscoveredToolMaxCount <= 0)
        {
            _discoveredToolLeases.Clear();
            _discoveredToolOrder.Clear();
            TrimAvailableToolsToBase();
            return;
        }

        if (_discoveredToolLeases.Count == 0)
        {
            TrimAvailableToolsToBase();
            return;
        }

        var expired = _discoveredToolLeases
            .Where(x => x.Value <= 0)
            .Select(x => x.Key)
            .ToList();

        if (expired.Count > 0)
        {
            foreach (var name in expired)
            {
                _discoveredToolLeases.Remove(name);
            }

            _discoveredToolOrder.RemoveAll(name => !_discoveredToolLeases.ContainsKey(name));
        }

        RebuildAvailableToolsFromDiscoveredCache();

        // Lease countdown happens after this turn's tool set is prepared,
        // so a lease value of N keeps tools available for N future turns.
        foreach (var name in _discoveredToolLeases.Keys.ToList())
        {
            _discoveredToolLeases[name]--;
        }
    }

    private void RememberDiscoveredTool(string toolName, INetclawTool tool)
    {
        if (tool is not McpToolAdapter)
            return;

        if (_config.DiscoveredToolRetentionTurns <= 0 || _config.DiscoveredToolMaxCount <= 0)
            return;

        var lease = Math.Max(1, _config.DiscoveredToolRetentionTurns);
        _discoveredToolLeases[toolName] = lease;

        if (!_discoveredToolOrder.Contains(toolName))
        {
            _discoveredToolOrder.Add(toolName);
        }

        while (_discoveredToolOrder.Count > _config.DiscoveredToolMaxCount)
        {
            var evicted = _discoveredToolOrder[0];
            _discoveredToolOrder.RemoveAt(0);
            _discoveredToolLeases.Remove(evicted);
        }
    }

    private void RebuildAvailableToolsFromDiscoveredCache()
    {
        if (_fullRegistry is null)
            return;

        TrimAvailableToolsToBase();

        foreach (var toolName in _discoveredToolOrder)
        {
            if (!_discoveredToolLeases.TryGetValue(toolName, out var lease) || lease <= 0)
                continue;

            var tool = _fullRegistry.GetByName(toolName);
            if (tool is null)
                continue;

            AddAvailableToolIfMissing(toolName, tool.ToAITool());
        }
    }

    private void TrimAvailableToolsToBase()
    {
        if (_availableTools.Count > _baseToolCount)
            _availableTools.RemoveRange(_baseToolCount, _availableTools.Count - _baseToolCount);
    }

    private void AddAvailableToolIfMissing(string toolName, AITool aiTool)
    {
        if (_availableTools.Any(existing =>
            existing is AIFunction ef && aiTool is AIFunction nf && ef.Name == nf.Name))
            return;

        _availableTools.Add(aiTool);
        _log.Info("Dynamically loaded tool '{ToolName}' into session", toolName);
    }

    private void MaybeSnapshot()
    {
        if (_config.SnapshotInterval > 0 && LastSequenceNr % _config.SnapshotInterval == 0)
        {
            SaveSnapshot(_state.ToSnapshot());
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
                case TextContent text when includeText:
                    EmitOutput(new TextOutput
                    {
                        SessionId = _sessionId,
                        Text = text.Text ?? string.Empty
                    }, OutputFilter.Text);
                    break;

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

    private void ObserveTurnForMemory(SerializableChatMessage userMessage, SerializableChatMessage assistantReply)
    {
        var userText = userMessage.Content ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userText))
            return;

        var recentUser = _state.History
            .Where(x => x.Role == Protocol.ChatRole.User && !SessionState.IsSystemNudge(x))
            .Select(x => x.Content)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .TakeLast(3)
            .ToArray();

        var recentAssistant = _state.History
            .Where(x => x.Role == Protocol.ChatRole.Assistant)
            .Select(x => x.Content)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .TakeLast(3)
            .ToArray();

        var strongAssertions = BuildStrongAssertions(userText);
        var request = _sidecarMemoryObserver.BuildRequest(
            _sessionId.Value,
            _activeTurnId ?? $"{_sessionId.Value}:{NowMs()}",
            "turn_completed",
            _sessionId.ToMemoryDomain(),
            Memory.MemorySensitivity.Normal.ToWireValue(),
            userText,
            assistantReply.Content ?? string.Empty,
            strongAssertions,
            [],
            recentUser,
            recentAssistant,
            [],
            false,
            _timeProvider.GetUtcNow());

        var self = Self;
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _config.SidecarLlmTimeoutSeconds));
        _ = ObserveMemoryAsync(_compactionClient, request, self, _log, timeout);
    }

    private static async Task ObserveMemoryAsync(
        IChatClient client,
        MemoryObservationRequest request,
        IActorRef self,
        ILoggingAdapter log,
        TimeSpan timeout)
    {
        var proposals = await SessionSidecarRunner.RunJsonAsync<IReadOnlyList<MemoryProposal>>(
            client,
            MemorySidecarPromptBuilder.BuildMemoryObservationSystemPrompt(),
            MemorySidecarPromptBuilder.BuildMemoryObservationUserPrompt(request),
            timeout,
            message => log.Warning("Memory observation sidecar failed: {0}", message));

        if (proposals is null)
        {
            self.Tell(new MemoryObservationFailed { Reason = "sidecar failed or returned null" });
            return;
        }

        self.Tell(new MemoryObservationCompleted { Proposals = proposals });
    }

    private static IReadOnlyList<string> BuildStrongAssertions(string userText)
    {
        var assertions = new List<string>();
        var text = userText.Trim();
        if (text.StartsWith("I ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("I'm ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("I’m ", StringComparison.OrdinalIgnoreCase))
        {
            assertions.Add(text);
        }
        return assertions;
    }

    private void EmitUsageOutput(UsageDetails usage)
    {
        _sessionMetrics?.RecordTokenUsage(usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0);

        var contextWindow = _config.ContextWindowTokens;
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

    private void StartProcessingWatchdog(string operationName, TimeSpan timeout)
    {
        _processingOperationId++;
        _processingOperationName = operationName;

        Timers.StartSingleTimer(
            ProcessingWatchdogTimerKey,
            new ProcessingWatchdogExpired
            {
                OperationId = _processingOperationId,
                OperationName = operationName
            },
            timeout);
    }

    private void RefreshProcessingWatchdogIfActive()
    {
        if (_processingOperationName is not "llm-call")
            return;

        var timeout = TimeSpan.FromSeconds(Math.Max(1, _config.TurnLlmTimeoutSeconds));
        Timers.StartSingleTimer(
            ProcessingWatchdogTimerKey,
            new ProcessingWatchdogExpired
            {
                OperationId = _processingOperationId,
                OperationName = _processingOperationName
            },
            timeout);
    }

    private bool IsCurrentWatchdog(ProcessingWatchdogExpired msg)
        => msg.OperationId == _processingOperationId
           && string.Equals(msg.OperationName, _processingOperationName, StringComparison.Ordinal);

    private void StopProcessingWatchdog()
    {
        Timers.Cancel(ProcessingWatchdogTimerKey);
        _processingOperationName = null;
    }

    private void FailCurrentTurn(string errorMessage, Exception cause, ErrorCategory category = ErrorCategory.Unknown)
    {
        ClearDeliveryRetryState();
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
            TurnNumber = _state.TurnCount
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

    private void TryReplyAck()
    {
        if (Sender.IsNobody() || Equals(Sender, Context.System.DeadLetters))
            return;

        Sender.Tell(CommandAck.For(_sessionId));
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
