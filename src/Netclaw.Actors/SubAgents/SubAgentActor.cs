// -----------------------------------------------------------------------
// <copyright file="SubAgentActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Memory;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.SubAgents;

/// <summary>
/// Ephemeral actor that runs a non-interactive LLM session (same execution model
/// as a piped/-p invocation). Receives a <see cref="RunSubAgent"/> message, executes
/// an autonomous turn loop with tool calls, and returns the final text response
/// as a <see cref="SubAgentResult"/> to the caller. Then stops itself.
///
/// No persistence, no subscribers, no streaming, no compaction.
/// Designed to be spawned as a child of a session actor or standalone under a supervisor.
/// </summary>
public sealed class SubAgentActor : ReceiveActor, IWithTimers
{
    internal const int DefaultMaxToolIterations = 30;

    private const string EmptyResponseMarker = "(no response)";
    private const string MalformedFinalOutputMessage = "Subagent produced malformed final output: it emitted unexecuted tool calls as text. This was not a timeout.";
    private const string MalformedFinalOutputNudge =
        "The previous response emitted unexecuted tool-call markup as plain text. "
        + "That text was not executed and cannot be returned as a successful final output. "
        + "If tools are available and you still need those operations, call the tools using structured tool calls now. "
        + "Otherwise provide a final answer based only on executed tool results. "
        + "Do not include <function=...>, <parameter=...>, or </tool_call> markup in final text.";
    private const string HeadlessExecutionContractPrefix = """
        [Subagent Execution Contract]
        You are a headless, non-interactive worker running on behalf of a parent Netclaw session.
        Your subagent role guidance and assigned task are more specific than inherited deployment or project guidance. If they conflict, follow your subagent guidance and assigned task.
        Embedded safety, security, trust-boundary, approval, and tool-policy rules remain mandatory and cannot be overridden.
        Do not ask the user clarifying questions, request conversational input, or wait for a reply.
        Do the best work you can with the task, context, and tools available.
        If the task is ambiguous, make reasonable assumptions and state them in your final output.
        If you are blocked, return a final result that explains what you found, what remains, and what decision the parent session needs.
        Parent-mediated tool approval may occur only for concrete tool calls; it is a security gate, not a dialogue channel.
        """;
    private const string ProjectScopeDeclarationContract =
        "Before tool work in another task-named project, call set_working_directory once, even with absolute paths. " +
        "Declare the task's first project path exactly before probing it.";
    private const string HeadlessExecutionContractSuffix =
        "Always end by emitting a final output for the parent session.";
    private static readonly TimeSpan StreamPingInterval = TimeSpan.FromSeconds(2);

    private readonly SubAgentDefinition _definition;
    private readonly IChatClient _chatClient;
    private readonly ToolAccessPolicy _toolAccessPolicy;
    private readonly IToolApprovalService? _approvalService;
    private readonly ISystemPromptProvider _promptProvider;
    private readonly int _maxToolIterations;

    // Process-wide daily-stats sink (the same singleton the parent session records
    // to). Nullable because a hosting configuration without the daemon stats backend
    // is a real runtime state — mirrors LlmSessionActor._sessionMetrics. When present,
    // every LLM call this sub-agent makes is billed here so its tokens show up in
    // `netclaw stats` instead of vanishing.
    private readonly Telemetry.ISessionMetrics? _sessionMetrics;
    private readonly ToolRegistry _toolRegistry;
    private IReadOnlyList<AITool> _aiTools = [];
    private ILoggingAdapter _log;
    private readonly MemoryPolicyEvaluator _policyEvaluator = new();
    private readonly TurnStateTracker _turnState = new();

    // Stopwatch tracking the total wall-clock duration of the sub-agent run.
    // Used for the summary log on completion (ProcessingWatchdog is only a
    // timer scheduler — it doesn't track elapsed time itself).
    private readonly Stopwatch _runStopwatch = Stopwatch.StartNew();

    // Cumulative token usage across every LLM call this sub-agent makes. Summed for
    // the completion summary log; per-call usage is also recorded to _sessionMetrics
    // as each call returns (see RecordUsage).
    private long _runInputTokens;
    private long _runOutputTokens;

    // Conversation state (not persisted — ephemeral)
    private readonly List<AiChatMessage> _history = [];
    private readonly SessionScratchCorrectionState _sessionScratchCorrections = new();
    private long _llmCallId;
    private IActorRef _replyTo = ActorRefs.Nobody;
    private CancellationTokenSource? _executionCts;

    // Threaded into approval-bridge waits so a slow human approver does not
    // let the internal inactivity watchdog cancel a legitimate await on user
    // input. The watchdog itself never touches this CTS — it only cancels
    // _executionCts. Other terminal paths (Complete, PostStop, SubAgentCancelled)
    // cancel both for symmetric cleanup, but those paths are already heading
    // for actor stop, so the bridge wait is being torn down anyway.
    private CancellationTokenSource? _externalCts;

    // Mailbox-thread only. Tracks tool batches that can enter parent approval
    // waits and the concrete number of waits currently in-flight. Waiting for
    // human approval is legitimate work, so the watchdog re-arms instead of
    // cancelling while this state says a wait is active.
    private ToolExecutionWatchdogState _toolExecutionWatchdogState = ToolExecutionWatchdogState.None;
    private int _pendingApprovalWaits;

    private IParentApprovalBridge? _approvalBridge;
    private ChannelWriter<ToolActivityUpdate>? _activitySink;

    // Parent session id (scopeId with the "/subagent/..." suffix stripped) and the full
    // sub-session scope id. Carried on the sub-agent's ChatOptions so its LLM-pipeline lines
    // group under the parent in OTEL (SessionId) while the file-logger partitions them into the
    // sub-agent's own session.log (SubSessionId) — matching the enriched logger's own context.
    private string? _parentSessionId;
    private string? _subSessionId;
    private ChildFileActivityTracker _fileActivity = new(WorkingContext.Empty);
    private string? _projectInstructions;

    // Default wait-for-first-delta budget when the spawn message carries none
    // (direct/test callers). Mirrors SessionConfig.PrefillTimeout so an unset
    // prefill never collapses to the tighter inter-delta budget — that collapse
    // would silently reintroduce the cold-prefill timeout this watchdog exists
    // to prevent.
    private static readonly TimeSpan DefaultPrefillBudget = TimeSpan.FromSeconds(1800);

    // Two-phase inactivity watchdog, shared with the main session path
    // (LlmSessionActor). The watchdog starts each LLM call on the generous
    // _prefillBudget (content-free keepalives refresh it so a slow cold prefill
    // is not mistaken for a hang) and promotes to the tighter _interDeltaBudget
    // on the first substantive delta. _anyContentStreamed tracks whether the
    // current call has produced real output yet. _noProgressBudget is the
    // keepalive-immune ceiling reset only by real tokens — it bounds a backend
    // that heartbeats forever without producing output (null = unbounded).
    private readonly ProcessingWatchdog _watchdog = new();
    private TimeSpan _interDeltaBudget;
    private TimeSpan _prefillBudget;
    private TimeSpan? _noProgressBudget;
    private bool _anyContentStreamed;

    public ITimerScheduler Timers { get; set; } = null!;
    private CancellationTokenRegistration _externalCancellationRegistration;
    private ToolExecutionContext? _toolExecutionContext;
    private ToolExecutionContext ToolExecutionContext
        => _toolExecutionContext
           ?? throw new InvalidOperationException("Sub-agent tool context is unavailable before a run is admitted.");
    private bool _malformedFinalOutputRepairAttempted;
    private SubAgentOutcomeReason? _forcedFinalOutcomeReason;

    public SubAgentActor(
        SubAgentDefinition definition,
        IChatClient chatClient,
        ToolAccessPolicy toolAccessPolicy,
        IToolApprovalService? approvalService = null,
        int maxToolIterations = DefaultMaxToolIterations,
        Telemetry.ISessionMetrics? sessionMetrics = null)
        : this(
            definition,
            chatClient,
            toolAccessPolicy,
            NullSystemPromptProvider.Instance,
            approvalService,
            maxToolIterations,
            sessionMetrics)
    {
    }

    private SubAgentActor(
        SubAgentDefinition definition,
        IChatClient chatClient,
        ToolAccessPolicy toolAccessPolicy,
        ISystemPromptProvider promptProvider,
        IToolApprovalService? approvalService,
        int maxToolIterations,
        Telemetry.ISessionMetrics? sessionMetrics)
    {
        if (maxToolIterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxToolIterations), maxToolIterations,
                "Sub-agent tool iteration budget must be greater than zero.");

        // A sub-agent must run under a fully-wired access policy — never a
        // degraded default that drops the deny-list / protected-path checks.
        // Callers (SubAgentSpawner) inject the session's real policy; the
        // non-nullable parameter enforces it.
        _definition = definition;
        _chatClient = chatClient;
        _sessionMetrics = sessionMetrics;
        _toolAccessPolicy = toolAccessPolicy;
        _promptProvider = promptProvider;
        _approvalService = approvalService;
        _maxToolIterations = maxToolIterations;
        _projectInstructions = definition.ProjectInstructions;
        _log = Context.GetLogger();

        // Build a private ToolRegistry for this subagent's tool subset
        _toolRegistry = new ToolRegistry();
        foreach (var tool in definition.Tools)
        {
            if (!SubAgentToolPolicy.IsAllowedForSubAgent(tool.Name))
                continue;

            _toolRegistry.Register(tool);
        }

        Become(Idle);
    }

    public static Props CreateProps(
        SubAgentDefinition definition,
        IChatClient chatClient,
        ToolAccessPolicy toolAccessPolicy,
        IToolApprovalService? approvalService = null,
        int maxToolIterations = DefaultMaxToolIterations,
        Telemetry.ISessionMetrics? sessionMetrics = null)
    {
        return Props.Create(() => new SubAgentActor(
            definition,
            chatClient,
            toolAccessPolicy,
            approvalService,
            maxToolIterations,
            sessionMetrics));
    }

    internal static Props CreatePropsWithProjectInstructionProvider(
        SubAgentDefinition definition,
        IChatClient chatClient,
        ToolAccessPolicy toolAccessPolicy,
        ISystemPromptProvider promptProvider,
        IToolApprovalService? approvalService = null,
        int maxToolIterations = DefaultMaxToolIterations,
        Telemetry.ISessionMetrics? sessionMetrics = null)
    {
        ArgumentNullException.ThrowIfNull(promptProvider);
        return Props.CreateBy(new SubAgentActorProducer(
            definition,
            chatClient,
            toolAccessPolicy,
            promptProvider,
            approvalService,
            maxToolIterations,
            sessionMetrics));
    }

    private sealed class SubAgentActorProducer(
        SubAgentDefinition definition,
        IChatClient chatClient,
        ToolAccessPolicy toolAccessPolicy,
        ISystemPromptProvider promptProvider,
        IToolApprovalService? approvalService,
        int maxToolIterations,
        Telemetry.ISessionMetrics? sessionMetrics) : IIndirectActorProducer
    {
        public Type ActorType => typeof(SubAgentActor);

        public ActorBase Produce()
            => new SubAgentActor(
                definition,
                chatClient,
                toolAccessPolicy,
                promptProvider,
                approvalService,
                maxToolIterations,
                sessionMetrics);

        public void Release(ActorBase actor)
        {
        }
    }

    /// <summary>
    /// Final cleanup if the actor is stopped externally (parent supervision,
    /// <c>PoisonPill</c>) without going through <see cref="Complete"/>. Cancels
    /// both CTSes so any in-flight approval wait running on the thread pool
    /// unblocks promptly, then replies once to any outstanding ask. <see cref="Complete"/>
    /// already nulls the CTSes, so the null-conditional access is intentional.
    /// </summary>
    protected override void PostStop()
    {
        _executionCts?.Cancel();
        _externalCts?.Cancel();
        if (!_completed)
        {
            _completed = true;
            _replyTo.Tell(new SubAgentResult
            {
                Completion = new ChildRunCompletion.Failed(SubAgentOutcomeReason.ActorStopped),
                Output = "Subagent stopped before completion.",
                AgentName = _definition.Name,
                Findings = [],
                FindingsCount = 0
            });
        }
        _executionCts?.Dispose();
        _externalCts?.Dispose();
        _externalCancellationRegistration.Dispose();
        base.PostStop();
    }

    private void Idle()
    {
        Receive<RunSubAgent>(msg =>
        {
            _replyTo = Sender;

            _approvalBridge = msg.Scope.Authority.InteractiveApproval is InteractiveApprovalCapability.Available available
                ? available.Bridge
                : null;
            var scopeId = msg.Scope.ScopeId.Value;
            var subAgentAudience = msg.Scope.Authority.Audience;
            _toolExecutionContext = new ToolExecutionContext(
                msg.Scope.Authority,
                ToolExecutionTimeout.Default);
            _fileActivity = new ChildFileActivityTracker(msg.Scope.InitialWorkingSnapshot.WorkingContext);
            _aiTools = ResolveExposedAiTools();
            _executionCts = new CancellationTokenSource();
            _externalCts = new CancellationTokenSource();
            var self = Self; // Capture before callback — Self requires active actor context
            _externalCancellationRegistration = msg.Cancellation.Register(() => self.Tell(SubAgentCancelled.Instance));

            // Enrich the logger so every sub-agent log line correlates back to the
            // parent session (SessionId) and to this specific sub-agent run
            // (SubSessionId), and is plainly attributable to the sub-agent. scopeId is
            // "{parentSessionId}/subagent/{name}/{runId}"; NormalizeSessionId strips the
            // "/subagent/..." suffix to recover the parent. SessionId matches the key the
            // session/channel actors already tag their loggers with, so sub-agent and
            // parent logs share one filterable attribute (so OTEL groups them under the parent);
            // SubSessionId isolates a single run and is what the file-logger partitions on, so the
            // sub-agent's lines land in its OWN session.log rather than the parent's.
            var parentSessionId = SubAgentSessionScope.NormalizeSessionId(scopeId);
            _parentSessionId = parentSessionId;
            _subSessionId = scopeId;
            var enrichedLog = Context.GetLogger();
            if (!string.IsNullOrWhiteSpace(parentSessionId))
                enrichedLog = enrichedLog.WithContext(NetclawLogProperties.SessionId, parentSessionId);
            if (!string.IsNullOrWhiteSpace(scopeId))
                enrichedLog = enrichedLog.WithContext(NetclawLogProperties.SubSessionId, scopeId);
            _log = enrichedLog;

            // The run is bounded by a two-phase inactivity watchdog re-armed on
            // every progress event (LLM response, tool batch, streaming delta,
            // keepalive) plus a keepalive-immune no-progress deadline. A sub-agent
            // making real progress is never killed; a stalled one (no bytes) or a
            // wedged one (keepalives but no tokens) is. FireLlmCall arms both. An
            // unset prefill defaults to the session budget rather than collapsing
            // to the inter-delta budget; an unset no-progress deadline is
            // unbounded (only direct/test callers leave it unset).
            _activitySink = msg.ActivitySink;
            _interDeltaBudget = msg.Timeout;
            _prefillBudget = msg.PrefillTimeout > TimeSpan.Zero ? msg.PrefillTimeout : DefaultPrefillBudget;
            _noProgressBudget = msg.NoProgressTimeout > TimeSpan.Zero ? msg.NoProgressTimeout : null;

            // Build initial conversation: system prompt (from file, verbatim) + task as user message.
            // If the caller supplied runtime context, prefix it onto the user message so the
            // system prompt stays reproducible across invocations.
            _history.Add(new AiChatMessage(
                Microsoft.Extensions.AI.ChatRole.System,
                BuildSystemPrompt(
                    _definition,
                    _projectInstructions,
                    CanDeclareProjectScope())));
            _history.Add(new AiChatMessage(Microsoft.Extensions.AI.ChatRole.User,
                BuildUserMessage(
                    msg.RuntimeContext,
                    BuildModelContext(
                        msg.Scope.InitialWorkingSnapshot,
                        subAgentAudience,
                        ToolExecutionContext.SessionDirectory),
                    msg.Task)));

            _log.Info("SubAgent [{AgentName}] starting (tools={ToolCount}, prefill={Prefill}, interDelta={InterDelta}, noProgress={NoProgress})",
                _definition.Name, _aiTools.Count, _prefillBudget, _interDeltaBudget,
                _noProgressBudget?.ToString() ?? "unbounded");

            FireLlmCall();
            Become(Processing);
        });
    }

    private void Processing()
    {
        Receive<LlmResponseReceived>(msg =>
        {
            // The streaming call finished; re-baseline to the inter-delta budget for
            // the synchronous processing that follows (tool dispatch or completion).
            RestartWatchdog(_interDeltaBudget);
            var response = msg.Response;

            // Record this call's token usage before branching so EVERY call is billed —
            // tool-call turns, retries, the forced-no-tools final turn, and repair turns
            // all flow through here exactly once. Mirrors the main session, which records
            // its own per-call usage; without this the sub-agent's tokens never reach the
            // daily-stats pipeline and `netclaw stats` under-counts by the whole sub-run.
            RecordUsage(response.Usage);

            var lastMessage = response.Messages[^1];
            var analysis = LlmResponseClassifier.Analyze(lastMessage);

            if (analysis.Kind == LlmResponseKind.ToolCalls && _turnState.ForceNoToolsActive)
            {
                _log.Warning(
                    "SubAgent [{AgentName}] requested {ToolCallCount} tool(s) after tool execution was disabled (budgetUsed={BudgetUsed}, max={Max})",
                    _definition.Name,
                    analysis.ToolCalls.Count,
                    _turnState.ToolCallCount,
                    _maxToolIterations);
                Complete(
                    false,
                    "Subagent exceeded its tool iteration budget and continued requesting tools after tools were disabled.",
                    SubAgentRunOutcome.Failed,
                    SubAgentOutcomeReason.ToolIterationBudgetExceededAfterDisable);
                return;
            }

            _log.Info(
                $"SubAgent [{_definition.Name}] LLM response callId={msg.CallId} kind={analysis.Kind} "
                + $"text={analysis.TextChars}ch thinking={analysis.ThinkingChars}ch "
                + $"toolCalls={analysis.ToolCalls.Count} finishReason={response.FinishReason?.ToString() ?? "null"} "
                + $"streamUpdates={msg.StreamUpdateCount} emptyUpdates={msg.EmptyStreamUpdateCount} "
                + $"streamText={msg.StreamTextDeltaCount}/{msg.StreamTextChars}ch "
                + $"streamThinking={msg.StreamThinkingDeltaCount}/{msg.StreamThinkingChars}ch "
                + $"streamToolCalls={msg.StreamToolCallDeltaCount} "
                + $"textPreview={PreviewForLog(ExtractText(lastMessage), 300)} "
                + $"toolCallPreview={FormatToolCallPreview(analysis.ToolCalls)}");

            var toolCalls = analysis.ToolCalls;
            if (toolCalls.Count > 0)
            {
                HandleToolCalls(lastMessage, toolCalls);
                return;
            }

            if (analysis.Kind is LlmResponseKind.ThinkingOnly or LlmResponseKind.Empty)
            {
                var truncated = response.FinishReason == ChatFinishReason.Length;
                switch (_turnState.EvaluateEmptyResponse(analysis.Kind, truncated))
                {
                    case EmptyResponseAction.Retry retry:
                        _log.Warning(
                            "SubAgent [{AgentName}] produced {Kind} response ({ThinkingChars} reasoning chars, truncated={Truncated}) — retrying with nudge",
                            _definition.Name,
                            analysis.Kind,
                            analysis.ThinkingChars,
                            truncated);
                        AddSystemNudge(retry.NudgeText);
                        FireLlmCall();
                        return;
                    case EmptyResponseAction.Fail fail:
                        _log.Warning(
                            "SubAgent [{AgentName}] produced repeated {Kind} responses — reporting failure",
                            _definition.Name,
                            analysis.Kind);
                        Complete(false, fail.ErrorMessage, SubAgentRunOutcome.Failed, SubAgentOutcomeReason.EmptyFinalResponse);
                        return;
                }
            }

            // Final text response — we're done.
            var text = ExtractText(lastMessage);
            if (string.IsNullOrWhiteSpace(text) || text == EmptyResponseMarker)
            {
                // LLM returned neither tool calls nor usable text. Surface as failure
                // so the parent session sees a real error instead of fabricating
                // "subagent still working" from an empty tool result.
                _log.Warning(
                    "SubAgent [{AgentName}] LLM returned empty text after non-empty analysis — reporting as failure",
                    _definition.Name);
                Complete(false, "Subagent returned an empty final response.", SubAgentRunOutcome.Failed, SubAgentOutcomeReason.EmptyFinalResponse);
                return;
            }

            if (ContainsUnexecutedToolCallMarkup(text))
            {
                if (!_malformedFinalOutputRepairAttempted)
                {
                    _malformedFinalOutputRepairAttempted = true;
                    _log.Warning(
                        "SubAgent [{AgentName}] emitted unexecuted tool-call markup as final text; retrying with repair nudge",
                        _definition.Name);
                    AddSystemNudge(MalformedFinalOutputNudge);
                    FireLlmCall(forceNoTools: _turnState.ForceNoToolsActive);
                    return;
                }

                _log.Warning(
                    "SubAgent [{AgentName}] repeatedly emitted unexecuted tool-call markup as final text; reporting failure",
                    _definition.Name);
                Complete(false, MalformedFinalOutputMessage, SubAgentRunOutcome.Failed, SubAgentOutcomeReason.MalformedFinalOutput);
                return;
            }

            Complete(
                true,
                text,
                _forcedFinalOutcomeReason.HasValue ? SubAgentRunOutcome.Partial : SubAgentRunOutcome.Completed,
                _forcedFinalOutcomeReason);
        });

        Receive<ToolExecutionCompleted>(msg =>
        {
            _toolExecutionWatchdogState = ToolExecutionWatchdogState.None;
            RecordProgress("processing tool results");

            // Append tool results as MEAI messages and log each result
            foreach (var result in msg.ToolResults)
            {
                _history.Add(ChatMessageConverter.ToAiMessage(result));
                if (result.ToolCallId is { } callId)
                    _fileActivity.Complete(callId.Value, result.Content);
                TryApplyProjectDirectory(result);
                var preview = result.Content is { Length: > 200 }
                    ? result.Content[..200] + "..."
                    : result.Content ?? "(null)";
                _log.Info("SubAgent [{AgentName}] tool [{ToolName}] result: {Result}",
                    _definition.Name, result.Name ?? "unknown", SecretOutputRedactor.Redact(preview));
            }

            foreach (var change in msg.ScratchCorrectionChanges)
                _sessionScratchCorrections.Apply(change);

            AddModelInputMediaNudge(msg.ModelInputMediaReferences);

            var budgetStatus = _turnState.RecordToolCompletion(msg.ToolResults.Count, _maxToolIterations);
            var dupNudge = _turnState.CheckForDuplicates();
            if (dupNudge is not null)
            {
                _log.Warning(
                    "SubAgent [{AgentName}] duplicate tool detected tool={ToolName} count={Count} iteration={Iteration}",
                    _definition.Name,
                    dupNudge.ToolName,
                    dupNudge.Count,
                    _turnState.ToolIterationCount);
                AddSystemNudge(dupNudge.NudgeText);
            }

            switch (budgetStatus)
            {
                case ToolBudgetStatus.Exhausted exhausted:
                    _log.Warning("SubAgent [{AgentName}] hit tool iteration limit ({Count}), forcing text response",
                        _definition.Name, _turnState.ToolIterationCount);
                    _forcedFinalOutcomeReason = SubAgentOutcomeReason.ToolIterationBudgetExhausted;
                    AddSystemNudge(exhausted.NudgeText);
                    FireLlmCall(forceNoTools: true);
                    return;
                case ToolBudgetStatus.NudgeNeeded nudge:
                    _log.Info(
                        "SubAgent [{AgentName}] nearing tool iteration limit ({Used}/{Max}, remaining={Remaining})",
                        _definition.Name,
                        _turnState.ToolIterationCount,
                        _maxToolIterations,
                        nudge.Remaining);
                    AddSystemNudge(nudge.NudgeText);
                    break;
            }

            _log.Debug("SubAgent [{AgentName}] tool iteration {Count}, continuing",
                _definition.Name, _turnState.ToolIterationCount);
            FireLlmCall();
        });

        Receive<ToolExecutionFailed>(msg =>
        {
            _toolExecutionWatchdogState = ToolExecutionWatchdogState.None;
            _log.Error(msg.Cause, "SubAgent [{AgentName}] tool execution failed", _definition.Name);
            Complete(false, $"Tool execution failed: {msg.Cause.Message}", SubAgentRunOutcome.Failed, SubAgentOutcomeReason.ToolExecutionFailed);
        });

        Receive<LlmCallFailed>(msg =>
        {
            _log.Error(msg.Cause, "SubAgent [{AgentName}] LLM call failed callId={CallId}", _definition.Name, msg.CallId);
            Complete(false, $"LLM call failed: {msg.Cause.Message}", SubAgentRunOutcome.Failed, SubAgentOutcomeReason.LlmCallFailed);
        });

        Receive<SubAgentCancelled>(_ =>
        {
            _executionCts?.Cancel();
            _externalCts?.Cancel();
            _log.Warning("SubAgent [{AgentName}] cancelled by parent", _definition.Name);
            Complete(false, "Subagent cancelled by parent", SubAgentRunOutcome.Failed, SubAgentOutcomeReason.CancelledByParent);
        });

        Receive<ProcessingWatchdogExpired>(msg =>
        {
            // Ignore a stale expiry from a watchdog operation that has since been
            // re-armed. Only Start bumps the operation id; a keepalive/promote
            // reschedules the same id, and Stop clears the operation name.
            if (!_watchdog.IsCurrent(msg))
                return;

            if (msg.NoProgress)
            {
                // The keepalive-immune deadline fired: the backend streamed
                // heartbeats (or nothing) for the full no-progress budget without a
                // single substantive token. Unlike a liveness tick this gets no
                // approval suppression or grace — it is only armed during an active
                // LLM stream and is never refreshed by keepalives, so reaching it
                // means a genuine wedge.
                var noProgress = _noProgressBudget?.TotalSeconds ?? 0;
                _executionCts?.Cancel();
                _watchdog.Stop(Timers);
                _log.Warning(
                    "SubAgent [{AgentName}] timed out: no substantive output for {Budget:F0}s after {Iterations} tool iterations",
                    _definition.Name, noProgress, _turnState.ToolIterationCount);
                Complete(false, $"Subagent timed out: no substantive output for {noProgress:F0}s.", SubAgentRunOutcome.Failed, SubAgentOutcomeReason.NoSubstantiveOutputTimeout);
                return;
            }

            if (_toolExecutionWatchdogState == ToolExecutionWatchdogState.WaitingForParentApproval)
            {
                _log.Debug(
                    "SubAgent [{AgentName}] watchdog tick suppressed while waiting on parent approval ({WaitCount} pending)",
                    _definition.Name,
                    _pendingApprovalWaits);
                return;
            }

            if (_toolExecutionWatchdogState == ToolExecutionWatchdogState.RunningApprovalCapableTools)
            {
                _log.Debug(
                    "SubAgent [{AgentName}] watchdog tick granted one-cycle grace while tool execution resolves whether parent approval is needed",
                    _definition.Name);
                _toolExecutionWatchdogState = ToolExecutionWatchdogState.None;
                RestartWatchdog(_interDeltaBudget);
                return;
            }

            // Report the budget that was actually in force: the prefill budget
            // while still waiting for the first token, else the inter-delta budget.
            var activeBudget = _anyContentStreamed ? _interDeltaBudget : _prefillBudget;
            _executionCts?.Cancel();
            _watchdog.Stop(Timers);
            _log.Warning(
                "SubAgent [{AgentName}] timed out: no activity for {Budget}s ({Phase}) after {Iterations} tool iterations",
                _definition.Name, activeBudget.TotalSeconds,
                _anyContentStreamed ? "inter-delta" : "prefill", _turnState.ToolIterationCount);
            Complete(false, $"Subagent timed out: no activity for {activeBudget.TotalSeconds:F0}s.", SubAgentRunOutcome.Failed, SubAgentOutcomeReason.NoActivityTimeout);
        });

        Receive<SubAgentApprovalWaitStarted>(_ =>
        {
            // Stop the watchdog outright on first wait — re-armed by
            // SubAgentApprovalWaitCompleted when the last wait clears. The
            // ProcessingWatchdogExpired handler's WaitingForParentApproval state
            // check is belt-and-braces for a stale timer fire that arrived ahead
            // of this message in the mailbox.
            if (_pendingApprovalWaits == 0)
                _watchdog.Stop(Timers);

            _toolExecutionWatchdogState = ToolExecutionWatchdogState.WaitingForParentApproval;
            _pendingApprovalWaits++;
            EmitActivity("awaiting human approval");
        });

        Receive<SubAgentApprovalWaitCompleted>(_ =>
        {
            // Started/Completed are sent as a try/finally pair from
            // ExecuteToolsAsync; FIFO mailbox delivery from a single sender
            // guarantees Started lands first. An underflow means the pairing
            // invariant is broken (orphan Tell, double Completed) — log loudly
            // rather than silently clamping, since hiding the imbalance would
            // re-introduce the watchdog-vs-approval bug this PR fixes.
            if (_pendingApprovalWaits <= 0)
            {
                _log.Warning(
                    "SubAgent [{AgentName}] received ApprovalWaitCompleted with no pending wait (counter={Counter}); ignoring",
                    _definition.Name, _pendingApprovalWaits);
                return;
            }

            _pendingApprovalWaits--;
            _toolExecutionWatchdogState = _pendingApprovalWaits == 0
                ? ToolExecutionWatchdogState.RunningApprovalCapableTools
                : ToolExecutionWatchdogState.WaitingForParentApproval;

            // Re-baseline only when the last approval clears, so concurrent
            // approval waits don't each rearm the same single-key timer
            // (Akka's StartSingleTimer cancels-and-reschedules each call).
            if (_pendingApprovalWaits == 0)
                RecordProgress("approval resolved");
        });

        // Liveness ping from a streaming LLM call — progress, even before the
        // full response message arrives. Keepalives (content-free heartbeats)
        // refresh whichever budget is in force; the first substantive delta
        // promotes from the generous prefill budget to the tighter inter-delta
        // budget. This is the two-phase behavior shared with the main session.
        Receive<SubAgentStreamPing>(msg =>
        {
            _log.Debug(
                $"SubAgent [{_definition.Name}] LLM stream progress callId={msg.CallId} "
                + $"updates={msg.UpdateCount} emptyUpdates={msg.EmptyUpdateCount} "
                + $"text={msg.TextDeltaCount}/{msg.TextChars}ch "
                + $"thinking={msg.ThinkingDeltaCount}/{msg.ThinkingChars}ch "
                + $"toolCalls={msg.ToolCallDeltaCount} finishReason={msg.FinishReason ?? "null"} "
                + $"substantive={msg.IsSubstantive}");

            // Same two-phase policy as the main session: keepalives refresh the
            // current budget, the first substantive delta promotes off prefill.
            _anyContentStreamed = _watchdog.OnStreamProgress(
                msg.IsSubstantive,
                _anyContentStreamed,
                _prefillBudget,
                _interDeltaBudget,
                Timers);

            // log: false — the SubAgentStreamPing handler above already logs this
            // liveness at Debug; an Info line per streamed delta would flood Seq.
            EmitActivity("the model is responding", log: false);
        });
    }

    // Bill one LLM call's token usage to the shared daily-stats sink and accumulate
    // the run totals for the completion summary log. We record at the source (here in
    // the child) rather than propagating totals up to the parent: the parent's
    // ISessionMetrics is the SAME process-wide singleton, so re-recording there would
    // double-count, and folding sub-agent tokens into the parent's UsageOutput would
    // corrupt its context-window percentage (the sub-agent has its own context window).
    private void RecordUsage(UsageDetails? usage)
    {
        if (usage is null)
            return;

        var input = usage.InputTokenCount ?? 0;
        var output = usage.OutputTokenCount ?? 0;
        _runInputTokens += input;
        _runOutputTokens += output;
        _sessionMetrics?.RecordTokenUsage(input, output);
    }

    private void HandleToolCalls(AiChatMessage assistantMessage, List<FunctionCallContent> toolCalls)
    {
        _turnState.ResetEmptyResponseGuards();

        // Add assistant message (with tool calls) to history
        _history.Add(assistantMessage);

        foreach (var toolCall in toolCalls)
        {
            var argsJson = toolCall.Arguments is not null
                ? JsonSerializer.Serialize(toolCall.Arguments)
                : null;
            _turnState.TrackToolCall(toolCall.Name, argsJson);
            if (toolCall.CallId is { Length: > 0 } callId
                && WorkingContextUpdater.TryExtractFilePath(argsJson, out var path))
                _fileActivity.Begin(callId, toolCall.Name, path);

            // Log tool START event so tool execution spans are visible in Seq
            // (previously only tool results were logged, making it impossible to
            // correlate tool start with tool end when tools take a long time).
            _log.Info("SubAgent [{AgentName}] tool start callId={ToolCallId} name={ToolName}",
                _definition.Name, toolCall.CallId ?? "unknown", toolCall.Name);
        }

        var toolNames = string.Join(", ", toolCalls.Select(tc => tc.Name));
        RecordProgress($"running tools: {toolNames}");
        _log.Info("SubAgent [{AgentName}] calling tools: [{ToolNames}]",
            _definition.Name, toolNames);

        var self = Self;
        var executor = new DispatchingToolExecutor(_toolRegistry, _toolAccessPolicy, _approvalService);
        var setWorkingDirectoryTool =
            _toolRegistry.GetByName(SetWorkingDirectoryTool.ToolName) as SetWorkingDirectoryTool;
        Func<string, ToolInvocationContext, bool>? canDeclareWorkingDirectory =
            setWorkingDirectoryTool is null ? null : setWorkingDirectoryTool.CanDeclare;
        _toolExecutionWatchdogState = _approvalBridge is null
            ? ToolExecutionWatchdogState.None
            : ToolExecutionWatchdogState.RunningApprovalCapableTools;

        _ = ExecuteToolsAsync(
            executor,
            toolCalls,
            ToolExecutionContext,
            _executionCts?.Token ?? CancellationToken.None,
            _externalCts?.Token ?? CancellationToken.None,
            self,
            _approvalBridge,
            _sessionScratchCorrections.Snapshot(),
            canDeclareWorkingDirectory);
    }

    private void FireLlmCall(bool forceNoTools = false)
    {
        _turnState.ForceNoToolsActive = forceNoTools;
        // Each LLM call starts fresh on the generous prefill budget; the watchdog
        // promotes to the inter-delta budget on the first substantive delta. The
        // no-progress deadline is armed alongside it and only real tokens reset it.
        _anyContentStreamed = false;
        StartLlmCallWatchdog();
        EmitActivity("calling the model");
        var self = Self;
        var client = _chatClient;
        var messages = new List<AiChatMessage>(_history);
        var callId = ++_llmCallId;

        // Carry the parent session id (when known) so the chat-client decorators group this
        // sub-agent's LLM-pipeline lines under the spawning session in OTEL, plus the sub-session
        // id so the file-logger partitions them into the sub-agent's OWN session.log. Direct/test
        // callers leave both unset.
        ChatOptions? options = _parentSessionId is { Length: > 0 } sid
            ? new SessionScopedChatOptions { SessionId = sid, SubSessionId = _subSessionId }
            : null;
        if (!forceNoTools && _aiTools.Count > 0)
        {
            options ??= new ChatOptions();
            options.Tools = [.. _aiTools];
        }

        _log.Info(
            "SubAgent [{AgentName}] LLM call start callId={CallId} iteration={Iteration} messages={MessageCount} toolsEnabled={ToolsEnabled} forceNoTools={ForceNoTools}",
            _definition.Name,
            callId,
            _turnState.ToolIterationCount,
            messages.Count,
            options?.Tools?.Count > 0,
            forceNoTools);
        _ = InvokeLlmAsync(client, messages, options, self, callId, _executionCts?.Token ?? CancellationToken.None);
    }

    private IReadOnlyList<AITool> ResolveExposedAiTools()
    {
        var tools = _toolRegistry.GetAllRegistrations().Select(r => r.Tool);
        return _toolAccessPolicy
            .FilterDiscoverableTools(tools, ToolExecutionContext.Invocation)
            .Select(tool => tool.ToAITool())
            .ToList();
    }

    private bool CanDeclareProjectScope() =>
        _aiTools.Any(tool => string.Equals(
            tool.Name,
            SetWorkingDirectoryTool.ToolName,
            StringComparison.Ordinal));

    private bool _completed;

    private void Complete(
        bool success,
        string output,
        SubAgentRunOutcome? outcome = null,
        SubAgentOutcomeReason? outcomeReason = null)
    {
        // Idempotent guard: a stale ProcessingWatchdogExpired or other handler can
        // land after Complete has already run (Tell from a thread-pool finally
        // races mailbox-thread Stop). The second call would send a duplicate
        // SubAgentResult and log a misleading "completed" line. The first
        // caller wins; subsequent calls are no-ops.
        if (_completed) return;
        _completed = true;
        _sessionScratchCorrections.Clear();

        _toolExecutionWatchdogState = ToolExecutionWatchdogState.None;
        _pendingApprovalWaits = 0;
        _executionCts?.Cancel();
        _externalCts?.Cancel();
        _watchdog.Stop(Timers);
        _externalCancellationRegistration.Dispose();
        _executionCts?.Dispose();
        _externalCts?.Dispose();
        _executionCts = null;
        _externalCts = null;
        _pendingApprovalWaits = 0;

        var resolvedOutcome = outcome ?? (success ? SubAgentRunOutcome.Completed : SubAgentRunOutcome.Failed);

        _log.Info("SubAgent [{AgentName}] completed (success={Success}, outcome={Outcome}, reason={Reason}, output={OutputLength} chars, iterations={Iterations})",
            _definition.Name, success, resolvedOutcome, outcomeReason?.Value ?? "-", output.Length, _turnState.ToolIterationCount);

        // Log cumulative stats for observability — total LLM calls, tool usage, tokens.
        // This gives operators a single summary line for sub-agent cost/duration analysis.
        // (success is already on the "completed" line above; omitted here to stay within
        // ILoggingAdapter's 6-argument ceiling.)
        _log.Info(
            "SubAgent [{AgentName}] summary: totalToolCalls={TotalToolCalls}, "
            + "iterations={Iterations}, inputTokens={InputTokens}, outputTokens={OutputTokens}, "
            + "duration={Duration}s",
            _definition.Name, _turnState.ToolCallCount,
            _turnState.ToolIterationCount,
            _runInputTokens, _runOutputTokens,
            _runStopwatch.Elapsed.TotalSeconds);

        var findings = success && _definition.EmitStructuredFindings
            ? BuildFindings(output, ToolExecutionContext.SessionId, resolvedOutcome, outcomeReason)
            : [];

        var workingContextResult = BuildWorkingContextResult(success);
        _replyTo.Tell(new SubAgentResult
        {
            Completion = ChildRunCompletion.FromReportedOutcome(resolvedOutcome, outcomeReason, workingContextResult),
            Output = output,
            AgentName = _definition.Name,
            Findings = findings,
            FindingsCount = findings.Count,
        });

        Context.Stop(Self);
    }

    /// <summary>
    /// Arm the watchdog for a fresh LLM streaming call: the generous prefill
    /// liveness budget plus the keepalive-immune no-progress deadline that bounds
    /// a backend streaming heartbeats without producing real tokens.
    /// </summary>
    private void StartLlmCallWatchdog()
        => _watchdog.Start(ProcessingWatchdog.LlmCall, _prefillBudget, Timers, _noProgressBudget);

    /// <summary>
    /// (Re)start the liveness watchdog on the given budget without a no-progress
    /// deadline — used to re-baseline between streaming calls (tool dispatch,
    /// post-approval). Clears any stale no-progress deadline from the prior call.
    /// </summary>
    private void RestartWatchdog(TimeSpan budget)
        => _watchdog.Start(ProcessingWatchdog.LlmCall, budget, Timers);

    /// <summary>
    /// Emit a liveness/progress item to the spawning tool's stream (if any) and log
    /// the phase so it is visible in Seq even on non-streaming spawn paths (where
    /// <see cref="_activitySink"/> is null), correlated by SessionId/SubSessionId.
    /// The per-delta streaming ping passes <paramref name="log"/> = false because the
    /// <see cref="SubAgentStreamPing"/> handler already logs that liveness at Debug —
    /// logging it here too would emit one Info line per streamed delta.
    /// </summary>
    private void EmitActivity(string phase, bool log = true)
    {
        if (log)
            _log.Info("SubAgent [{AgentName}] {Phase}", _definition.Name, phase);

        _activitySink?.TryWrite(new ToolActivityUpdate(phase));
    }

    /// <summary>
    /// Record forward progress in the tool loop / post-approval: re-baseline the
    /// inter-delta watchdog and emit activity. Uses <see cref="RestartWatchdog"/>
    /// (Start, not Refresh) so it still arms after an approval wait stopped the
    /// watchdog entirely.
    /// </summary>
    private void RecordProgress(string phase)
    {
        RestartWatchdog(_interDeltaBudget);
        EmitActivity(phase);
    }

    private List<SubAgentFinding> BuildFindings(
        string output,
        string? sessionId,
        SubAgentRunOutcome outcome,
        SubAgentOutcomeReason? outcomeReason)
    {
        var content = output?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
            return [];

        if (content.Length < 30)
            return [];

        var confidence = outcome == SubAgentRunOutcome.Partial ? 0.6 : 0.65;
        var policy = _policyEvaluator.EvaluateWrite(
            sensitivity: SubAgentFindingSensitivity.Normal.ToWireValue(),
            recallMode: SubAgentFindingRecallMode.Auto.ToWireValue(),
            confidence,
            isExplicitRequest: false);

        if (!policy.Allowed)
            return [];

        var evidence = new List<string> { $"subagent_outcome:{outcome.ToString().ToLowerInvariant()}" };
        if (outcomeReason is { } reason)
            evidence.Add($"subagent_outcome_reason:{reason.Value}");

        return
        [
            new SubAgentFinding
            {
                Shape = SubAgentFindingShape.Conclusion,
                Title = $"subagent:{_definition.Name}",
                Content = content,
                Kind = "record",
                Sensitivity = SubAgentFindingSensitivity.Normal,
                RecallMode = SubAgentFindingRecallMode.Searchable,
                UpdateSemantics = "append-document",
                Confidence = confidence,
                Durability = SubAgentFindingDurability.Durable,
                Reusability = SubAgentFindingReusability.Reusable,
                Evidence = evidence
            }
        ];
    }

    private void AddSystemNudge(string nudge)
    {
        _history.Add(new AiChatMessage(Microsoft.Extensions.AI.ChatRole.User, $"[system: {nudge}]"));
    }

    private void AddModelInputMediaNudge(IReadOnlyList<SerializableMediaReference> mediaReferences)
    {
        if (mediaReferences.Count == 0)
            return;

        var itemText = mediaReferences.Count == 1 ? "file" : "files";
        var message = new SerializableChatMessage
        {
            Role = Protocol.ChatRole.User,
            Content = $"[system: A tool loaded {mediaReferences.Count} media {itemText} for model-visible inspection. " +
                      "Use the attached media along with the tool result to complete the delegated task.]",
            MediaReferences = mediaReferences
        };
        _history.Add(ChatMessageConverter.ToAiMessage(message, ToolExecutionContext.SessionDirectory));
    }

    /// <summary>
    /// Compose the initial user message for the subagent. When <paramref name="runtimeContext"/>
    /// is present, prefixes a <c>Context:</c> block ahead of the <c>Task:</c> block so the
    /// parent-supplied background stays visually separated from the agent's task.
    /// When runtime context is null or whitespace, returns the raw task string for backward
    /// compatibility with the pre-Context protocol.
    /// </summary>
    private static string BuildUserMessage(string? runtimeContext, string workingContext, string task)
    {
        var contextParts = new[] { runtimeContext?.Trim(), workingContext.Trim() }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        var combinedContext = string.Join("\n\n", contextParts);
        if (combinedContext.Length == 0)
            return task;

        return $"Context:\n{combinedContext}\n\nTask:\n{task}";
    }

    private static string BuildModelContext(
        WorkingContextSnapshot snapshot,
        TrustAudience audience,
        string? sessionDirectory)
    {
        if (audience == TrustAudience.Public)
            return string.Empty;

        var workingContext = snapshot.ToContextBlock();
        var sessionContext = string.IsNullOrWhiteSpace(sessionDirectory)
                             || sessionDirectory.Any(char.IsControl)
            ? string.Empty
            : $"[session]\nsession_dir: {sessionDirectory}\n"
              + ToolChoiceGuidance.DirectorySelectionOrder + "\n"
              + ToolChoiceGuidance.ShellCompositionOrder;

        return string.Join(
            "\n\n",
            new[] { workingContext, sessionContext }
                .Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private WorkingContextDelta? BuildWorkingContextResult(bool success)
    {
        if (!success)
            return null;

        return _fileActivity.BuildResult();
    }

    private void TryApplyProjectDirectory(SerializableChatMessage result)
    {
        if (result.Name is not SetWorkingDirectoryTool.ToolName
            || result.Content is null
            || _toolRegistry.GetByName(SetWorkingDirectoryTool.ToolName) is not SetWorkingDirectoryTool tool)
        {
            return;
        }

        var projectDirectory = result.Content.Trim();
        var validatedWorkingContext = WorkingContext.Empty.WithProjectDirectory(projectDirectory);
        if (!string.Equals(
                validatedWorkingContext.ProjectDirectory,
                projectDirectory,
                StringComparison.Ordinal)
            || !Path.IsPathRooted(projectDirectory)
            || !tool.CanDeclare(projectDirectory, ToolExecutionContext.Invocation))
        {
            return;
        }

        var nextScope = ToolExecutionContext.RunScope with
        {
            ProjectDirectory = projectDirectory
        };
        _toolExecutionContext = new ToolExecutionContext(
            nextScope,
            ToolExecutionContext.ExecutionTimeout);
        _fileActivity.SetProjectDirectory(projectDirectory);
        _projectInstructions = _promptProvider.GetProjectInstructions(
            nextScope.Audience,
            projectDirectory);

        var prompt = BuildSystemPrompt(
            _definition,
            _projectInstructions,
            CanDeclareProjectScope());
        var systemMessage = new AiChatMessage(Microsoft.Extensions.AI.ChatRole.System, prompt);
        if (_history.Count > 0 && _history[0].Role == Microsoft.Extensions.AI.ChatRole.System)
            _history[0] = systemMessage;
        else
            _history.Insert(0, systemMessage);

        _log.Info("SubAgent [{AgentName}] project directory set to {ProjectDir}",
            _definition.Name,
            projectDirectory);
    }

    private static string ExtractText(AiChatMessage message)
    {
        var sb = new StringBuilder();
        foreach (var content in message.Contents)
        {
            if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(text.Text);
            }
        }

        return sb.Length > 0 ? sb.ToString() : EmptyResponseMarker;
    }

    // ── Static async helpers (same pattern as LlmSessionActor) ──

    internal static Task InvokeLlmAsync(
        IChatClient client,
        List<AiChatMessage> messages,
        ChatOptions? options,
        IActorRef self,
        CancellationToken ct)
        => InvokeLlmAsync(client, messages, options, self, callId: 0, ct);

    internal static async Task InvokeLlmAsync(
        IChatClient client,
        List<AiChatMessage> messages,
        ChatOptions? options,
        IActorRef self,
        long callId,
        CancellationToken ct)
    {
        try
        {
            // Use streaming to match the main session path. The non-streaming
            // GetResponseAsync path drops reasoning content for some providers
            // (e.g., Qwen emits <think> blocks that surface as TextReasoningContent
            // only in streaming mode), leaving the assistant message with no
            // TextContent and causing the subagent to report empty "(no response)".
            // The loop, counting, and response assembly are shared with the main
            // session path via StreamingResponseReader.
            var pingThrottle = Stopwatch.StartNew();

            var result = await StreamingResponseReader.ReadAsync(
                client,
                messages,
                options,
                (_, cls, diag) =>
                {
                    // Always surface the first substantive delta promptly so the
                    // watchdog can promote off the generous prefill budget;
                    // otherwise throttle pings. Keepalives are forwarded too (the
                    // actor refreshes whichever budget is in force) so a healthy
                    // slow prefill is never mistaken for a hang.
                    if (cls.IsFirstSubstantive || pingThrottle.Elapsed >= StreamPingInterval)
                    {
                        pingThrottle.Restart();
                        self.Tell(new SubAgentStreamPing(
                            callId,
                            diag.UpdateCount,
                            diag.EmptyUpdateCount,
                            diag.TextDeltaCount,
                            diag.TextChars,
                            diag.ThinkingDeltaCount,
                            diag.ThinkingChars,
                            diag.ToolCallDeltaCount,
                            diag.FinishReason,
                            IsSubstantive: cls.HasSubstantiveContent));
                    }
                },
                ct);

            self.Tell(new LlmResponseReceived
            {
                Response = result.Response,
                CallId = callId,
                StreamUpdateCount = result.Diagnostics.UpdateCount,
                EmptyStreamUpdateCount = result.Diagnostics.EmptyUpdateCount,
                StreamTextDeltaCount = result.Diagnostics.TextDeltaCount,
                StreamTextChars = result.Diagnostics.TextChars,
                StreamThinkingDeltaCount = result.Diagnostics.ThinkingDeltaCount,
                StreamThinkingChars = result.Diagnostics.ThinkingChars,
                StreamToolCallDeltaCount = result.Diagnostics.ToolCallDeltaCount
            });
        }
        catch (Exception ex)
        {
            self.Tell(new LlmCallFailed(ex) { CallId = callId });
        }
    }

    private static string FormatToolCallPreview(IEnumerable<FunctionCallContent> toolCalls)
    {
        var summaries = toolCalls.Select(tc =>
            $"{tc.Name}#{tc.CallId}({PreviewArguments(tc.Arguments)})");
        return string.Join("; ", summaries);
    }

    private static string PreviewArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return "no-args";

        var rendered = string.Join(", ", arguments.Select(kvp =>
            $"{kvp.Key}={PreviewForLog(kvp.Value?.ToString() ?? "null", 160)}"));
        return PreviewForLog(rendered, 500);
    }

    private static string PreviewForLog(string? text, int maxChars)
        => SecretOutputRedactor.Redact(PreviewText(text, maxChars));

    private static string PreviewText(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var normalized = text.ReplaceLineEndings("\\n");
        return normalized.Length <= maxChars ? normalized : normalized[..maxChars] + "...";
    }

    internal static bool ContainsUnexecutedToolCallMarkup(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var outsideQuotedExamples = StripFencedAndQuotedBlocks(text);
        var lines = outsideQuotedExamples
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("<tool_call", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("</tool_call>", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (line.StartsWith("<function=", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("<parameter=", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("</function>", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("</parameter>", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripFencedAndQuotedBlocks(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inFence = false;

        foreach (var rawLine in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = rawLine.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence || trimmed.StartsWith('>'))
                continue;

            builder.AppendLine(rawLine);
        }

        return builder.ToString();
    }

    private static async Task ExecuteToolsAsync(
        IToolExecutor executor,
        List<FunctionCallContent> toolCalls,
        ToolExecutionContext executionContext,
        CancellationToken ct,
        CancellationToken externalCt,
        IActorRef self,
        IParentApprovalBridge? approvalBridge,
        SessionScratchCorrectionDispatch scratchCorrections,
        Func<string, ToolInvocationContext, bool>? canDeclareWorkingDirectory)
    {
        try
        {
            var modelInputBudget = new ModelInputBatchBudget(ChannelAttachmentPolicy.DefaultMaxFileBytes);
            var tasks = toolCalls.Select(async tc =>
            {
                // Same execution-preflight seam as the main pipeline: validate +
                // extract in one step (the sub-agent previously skipped extraction
                // entirely, silently dropping timeout hints). meta.Background and
                // meta.Rationale are intentionally not consumed here — sub-agents
                // have no background-job manager or audit logger; only the timeout
                // hint is materialized into the immutable per-tool context.
                var interpretation = executor.InterpretToolCall(tc);
                var toolContext = CreatePerToolExecutionContext(executionContext, interpretation.Meta);
                if (interpretation.Rejection is { } rejection)
                    return BuildToolResult(tc, rejection.Message, toolContext, modelInputBudget);

                var meta = interpretation.Meta;
                var cleanedTc = interpretation.Cleaned;
                var scratchCall = SessionToolExecutionPipeline.BuildSessionScratchCallSemantics(
                    cleanedTc,
                    meta,
                    toolContext.ExecutionTimeout.Value,
                    executor is ISessionScratchRetryAwareExecutor scratchAwareExecutor
                        ? scratchAwareExecutor.Shell
                        : ApprovalShell.Bash);
                SessionScratchCorrectionKey? consumedScratchKey = null;
                if (scratchCall is { } call
                    && scratchCorrections.TryConsume(call, out var correctionKey)
                    && executor is ISessionScratchRetryAwareExecutor retryAwareExecutor)
                {
                    consumedScratchKey = correctionKey;
                    retryAwareExecutor.MarkSessionScratchRetry(
                        toolContext,
                        new ToolAgentCorrection.SessionScratchSuggested(
                            correctionKey.SessionDirectory,
                            correctionKey.TemporaryRoot,
                            correctionKey.Call.Shell));
                }
                try
                {
                    var result = await executor.ExecuteAsync(tc, toolContext, ct);
                    return BuildToolResult(
                        cleanedTc,
                        result,
                        toolContext,
                        modelInputBudget,
                        consumedScratchKey is { } directConsumed
                            ? new SessionScratchCorrectionChange.Consume(directConsumed)
                            : null);
                }
                catch (ToolApprovalRequiredException approvalEx)
                {
                    var ctx = approvalEx.ApprovalContext;
                    if (approvalBridge is not null
                        && ctx.AgentCorrection is
                        ToolAgentCorrection.SessionScratchSuggested scratchCorrection
                        && scratchCall is { } correctedCall)
                    {
                        var correctionText = SessionToolExecutionPipeline.BuildSessionScratchCorrection(
                            scratchCorrection.SessionDirectory);
                        var newCorrectionKey = new SessionScratchCorrectionKey(
                            correctedCall,
                            scratchCorrection.TemporaryRoot,
                            scratchCorrection.SessionDirectory);
                        return BuildToolResult(
                            cleanedTc,
                            correctionText,
                            toolContext,
                            modelInputBudget,
                            new SessionScratchCorrectionChange.Arm(newCorrectionKey));
                    }

                    var projectScopeCorrection =
                        SessionToolExecutionPipeline.BuildProjectScopeDeclarationCorrection(
                            ctx,
                            canDeclareWorkingDirectory is not null,
                            toolContext.Invocation,
                            canDeclareWorkingDirectory);
                    if (!string.IsNullOrEmpty(projectScopeCorrection))
                    {
                        return BuildToolResult(
                            cleanedTc,
                            projectScopeCorrection,
                            toolContext,
                            modelInputBudget);
                    }

                    if (approvalBridge is null)
                    {
                        throw new ParentApprovalUnavailableException(
                            $"Tool '{tc.Name}' requires interactive approval, but no parent approval bridge is available.");
                    }

                    var bridgeCandidates = ctx.Candidates is { Count: > 0 } c
                        ? c.Select(x => new ParentApprovalCandidate(x.Verb, x.Directory)
                        {
                            Shell = x.Shell,
                            VerbTokens = x.VerbTokens,
                        }).ToList()
                        : (IReadOnlyList<ParentApprovalCandidate>)[];
                    var bridgeOptions = ctx.Options
                        .Select(o => new ParentApprovalOption(o.Key.Value, o.Label))
                        .ToList();
                    // Signal the actor that an approval wait is starting BEFORE
                    // the await so the inactivity watchdog cannot cancel the
                    // wait. The bridge call uses externalCt — only explicit
                    // external cancellation (parent passivation, daemon
                    // restart, user cancel) aborts the wait; the internal
                    // watchdog cannot.
                    self.Tell(SubAgentApprovalWaitStarted.Instance);

                    ParentApprovalDecision decision;
                    try
                    {
                        decision = await approvalBridge.RequestApprovalAsync(
                            new ToolCallId(tc.CallId),
                            ctx.ToolName,
                            ctx.DisplayText,
                            ctx.Patterns,
                            ctx.CandidateVerbs,
                            bridgeCandidates,
                            ctx.Cwd,
                            bridgeOptions,
                            ctx.IsMessy,
                            externalCt);
                    }
                    finally
                    {
                        self.Tell(SubAgentApprovalWaitCompleted.Instance);
                    }

                    if (decision.IsApprovalGrant())
                    {
                        // The immediate retry needs a transient grant even for session/always
                        // approvals because the sub-agent's scope ID differs from the parent
                        // session's scope. Keep that retry-local so approve-once cannot bleed
                        // across parallel tool calls or later iterations.
                        var retryContext = CreatePerToolExecutionContext(executionContext, meta);
                        retryContext.Approval.SeedOneTimeApproval(tc.Name, OneTimeApprovalKeys.Create(ctx));
                        var result = await executor.ExecuteAsync(tc, retryContext, ct);
                        return BuildToolResult(
                            cleanedTc,
                            result,
                            retryContext,
                            modelInputBudget,
                            consumedScratchKey is { } approvedConsumed
                                ? new SessionScratchCorrectionChange.Consume(approvedConsumed)
                                : null);
                    }

                    var reason = decision == ParentApprovalDecision.TimedOut
                        ? "Tool access denied: approval_timed_out"
                        : "Tool access denied: approval_denied_by_user";
                    if (decision == ParentApprovalDecision.Denied
                        && consumedScratchKey is { } deniedScratchRetry)
                    {
                        reason = $"{reason}\n{SessionToolExecutionPipeline.BuildSessionScratchDenialHint(deniedScratchRetry.SessionDirectory)}";
                    }

                    return BuildToolResult(
                        tc,
                        reason,
                        toolContext,
                        modelInputBudget,
                        consumedScratchKey is { } deniedConsumed
                            ? new SessionScratchCorrectionChange.Consume(deniedConsumed)
                            : null);
                }
                catch (ParentApprovalUnavailableException)
                {
                    throw;
                }
                catch (OperationCanceledException) when (externalCt.IsCancellationRequested)
                {
                    // External cancellation is not a tool error — let it propagate
                    // so the outer try routes it via ToolExecutionFailed instead of
                    // synthesising a fake tool result that the LLM would see as
                    // success-with-error and continue from. SubAgentCancelled is
                    // also enqueued via msg.Cancellation.Register, so Complete
                    // typically wins the race; this guard covers the case where
                    // it doesn't.
                    throw;
                }
                catch (Exception ex)
                {
                    return BuildToolResult(
                        tc,
                        $"Error: {ex.Message}",
                        toolContext,
                        modelInputBudget,
                        consumedScratchKey is { } failedConsumed
                            ? new SessionScratchCorrectionChange.Consume(failedConsumed)
                            : null);
                }
            });

            var results = await Task.WhenAll(tasks);
            self.Tell(new ToolExecutionCompleted
            {
                ToolResults = [.. results.Select(r => r.Message)],
                ModelInputMediaReferences = [.. results.SelectMany(r => r.ModelInputMediaReferences)],
                ScratchCorrectionChanges = [.. results.Where(r => r.ScratchCorrectionChange is not null).Select(r => r.ScratchCorrectionChange!)]
            });
        }
        catch (Exception ex)
        {
            self.Tell(new ToolExecutionFailed { Cause = ex });
        }
    }

    private static ToolExecutionContext CreatePerToolExecutionContext(
        ToolExecutionContext source,
        ToolCallMeta? meta)
    {
        var timeout = meta?.TimeoutHintSeconds is { } seconds
            ? new ToolExecutionTimeout(TimeSpan.FromSeconds(seconds))
            : source.ExecutionTimeout;
        return new ToolExecutionContext(source.RunScope, timeout, source.Outputs.Fork());
    }

    private static SubAgentToolCallResult BuildToolResult(
        FunctionCallContent toolCall,
        string resultText,
        ToolExecutionContext toolContext,
        ModelInputBatchBudget modelInputBudget,
        SessionScratchCorrectionChange? scratchCorrectionChange = null)
    {
        var materialization = MaterializeModelInputFiles(toolContext, modelInputBudget);
        if (materialization.RequestedCount > materialization.MediaReferences.Count)
        {
            resultText = SessionToolExecutionPipeline.AppendModelInputHandoffWarning(
                resultText,
                materialization.RequestedCount - materialization.MediaReferences.Count);
        }

        return new SubAgentToolCallResult(
            new SerializableChatMessage
            {
                Role = Protocol.ChatRole.Tool,
                Content = resultText,
                ToolCallId = new ToolCallId(toolCall.CallId),
                Name = toolCall.Name
            },
            materialization.MediaReferences,
            scratchCorrectionChange);
    }

    private static ModelInputMaterializationResult MaterializeModelInputFiles(
        ToolExecutionContext toolContext,
        ModelInputBatchBudget modelInputBudget)
    {
        if (toolContext.Outputs.ModelInputFiles.Count == 0)
            return new ModelInputMaterializationResult([], 0);

        if (string.IsNullOrWhiteSpace(toolContext.SessionDirectory))
            return new ModelInputMaterializationResult([], toolContext.Outputs.ModelInputFiles.Count);

        return SessionToolExecutionPipeline.MaterializeModelInputFiles(
            toolContext,
            toolContext.SessionDirectory,
            NoLogger.Instance,
            modelInputBudget);
    }

    private sealed record SubAgentToolCallResult(
        SerializableChatMessage Message,
        IReadOnlyList<SerializableMediaReference> ModelInputMediaReferences,
        SessionScratchCorrectionChange? ScratchCorrectionChange);

    private static string BuildSystemPrompt(
        SubAgentDefinition definition,
        string? projectInstructions,
        bool canDeclareProjectScope)
    {
        // Assemble the identity stack that sub-agents inherit from the parent session:
        // 1. Embedded operating core + deployment AGENTS.md mission playbook
        // 2. Project instructions (workspace/domain context from the parent's working directory)
        var basePrompt = SystemPromptAssembler.Assemble(
            agents: definition.OperatingRules,
            projectInstructions: projectInstructions);

        // Append the sub-agent's own system prompt (markdown body + optional skill overlay)
        var rolePrompt = string.IsNullOrWhiteSpace(basePrompt)
            ? definition.SystemPrompt
            : string.Concat(basePrompt.TrimEnd(), "\n\n", definition.SystemPrompt);

        // Append the headless execution and precedence contract — always at the bottom,
        // after the specialized role prompt it protects from inherited mission conflicts.
        var projectScopeContract = canDeclareProjectScope
            ? string.Concat(ProjectScopeDeclarationContract, "\n")
            : string.Empty;
        return string.Concat(
            rolePrompt.TrimEnd(),
            "\n\n",
            HeadlessExecutionContractPrefix,
            projectScopeContract,
            HeadlessExecutionContractSuffix);
    }

    private sealed class ChildFileActivityTracker
    {
        private readonly HashSet<string> _readFiles = new(StringComparer.Ordinal);
        private readonly HashSet<string> _confirmedChangedFiles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingFileActivity> _pending = new(StringComparer.Ordinal);
        private WorkingContext _workingContext;

        public ChildFileActivityTracker(WorkingContext parentSnapshot)
        {
            _workingContext = parentSnapshot;
        }

        public void Begin(string callId, string toolName, string path)
        {
            var kind = toolName switch
            {
                "file_read" => FileActivityKind.Read,
                "file_write" or "file_edit" => FileActivityKind.Changed,
                _ => FileActivityKind.None
            };
            if (kind == FileActivityKind.None)
                return;

            var resolvedPath = Path.IsPathRooted(path) || string.IsNullOrWhiteSpace(_workingContext.ProjectDirectory)
                ? path
                : Path.GetFullPath(path, _workingContext.ProjectDirectory);
            _pending[callId] = new PendingFileActivity(resolvedPath, kind);
        }

        public void Complete(string callId, string? result)
        {
            if (!_pending.Remove(callId, out var activity) || !Succeeded(activity.Kind, result))
                return;

            _workingContext = _workingContext.AddRecentFile(activity.Path);
            if (activity.Kind == FileActivityKind.Read)
                _readFiles.Add(activity.Path);
            else
                _confirmedChangedFiles.Add(activity.Path);
        }

        public void SetProjectDirectory(string projectDirectory)
            => _workingContext = _workingContext.WithProjectDirectory(projectDirectory);

        public WorkingContextDelta BuildResult() => new()
        {
            ProjectDirectory = _workingContext.ProjectDirectory,
            ReadFiles = _readFiles.Order(StringComparer.Ordinal).ToArray(),
            ConfirmedChangedFiles = _confirmedChangedFiles.Order(StringComparer.Ordinal).ToArray(),
            ObservedChangedFiles = []
        };

        private static bool Succeeded(FileActivityKind kind, string? result)
        {
            if (string.IsNullOrWhiteSpace(result))
                return false;

            return kind == FileActivityKind.Changed
                ? result.StartsWith("Successfully ", StringComparison.Ordinal)
                : !result.StartsWith("Error:", StringComparison.Ordinal)
                  && !result.StartsWith("Tool access denied:", StringComparison.Ordinal)
                  && !result.Contains("requires approval", StringComparison.OrdinalIgnoreCase);
        }

        private sealed record PendingFileActivity(string Path, FileActivityKind Kind);

        private enum FileActivityKind
        {
            None,
            Read,
            Changed
        }
    }

    private sealed class SubAgentCancelled
    {
        public static readonly SubAgentCancelled Instance = new();
        private SubAgentCancelled() { }
    }

    private enum ToolExecutionWatchdogState
    {
        None,
        RunningApprovalCapableTools,
        WaitingForParentApproval
    }

    /// <summary>
    /// Sent by <see cref="ExecuteToolsAsync"/> immediately before awaiting the
    /// parent approval bridge. Increments the in-flight approval-wait counter
    /// so the <see cref="ProcessingWatchdogExpired"/> handler ignores stale timer
    /// ticks while a human is deciding.
    /// </summary>
    private sealed class SubAgentApprovalWaitStarted
    {
        public static readonly SubAgentApprovalWaitStarted Instance = new();
        private SubAgentApprovalWaitStarted() { }
    }

    /// <summary>
    /// Sent by <see cref="ExecuteToolsAsync"/> in a <c>finally</c> after the
    /// approval bridge call returns or throws. Decrements the counter and
    /// re-baselines the inactivity watchdog so post-approval inactivity is
    /// still governed.
    /// </summary>
    private sealed class SubAgentApprovalWaitCompleted
    {
        public static readonly SubAgentApprovalWaitCompleted Instance = new();
        private SubAgentApprovalWaitCompleted() { }
    }

    /// <summary>
    /// Throttled self-message: the streaming LLM call is still alive.
    /// <see cref="IsSubstantive"/> is false for content-free keepalives, so the
    /// watchdog refreshes the current budget on them but only promotes off the
    /// prefill budget on real output.
    /// </summary>
    private sealed record SubAgentStreamPing(
        long CallId,
        int UpdateCount,
        int EmptyUpdateCount,
        int TextDeltaCount,
        int TextChars,
        int ThinkingDeltaCount,
        int ThinkingChars,
        int ToolCallDeltaCount,
        string? FinishReason,
        bool IsSubstantive);

    // ── Reuse LlmSessionActor's internal message types ──
    // These are internal to Netclaw.Actors so accessible here.

    // LlmResponseReceived, LlmCallFailed, ToolExecutionCompleted, ToolExecutionFailed
    // are defined in Sessions/LlmMessages.cs
}
