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
    private const string TimeoutTimerKey = "subagent-timeout";
    private const string MalformedFinalOutputMessage = "Subagent produced malformed final output: it emitted unexecuted tool calls as text. This was not a timeout.";
    private const string MalformedFinalOutputNudge =
        "The previous response emitted unexecuted tool-call markup as plain text. "
        + "That text was not executed and cannot be returned as a successful final output. "
        + "If tools are available and you still need those operations, call the tools using structured tool calls now. "
        + "Otherwise provide a final answer based only on executed tool results. "
        + "Do not include <function=...>, <parameter=...>, or </tool_call> markup in final text.";
    private const string HeadlessExecutionContract = """
        [Subagent Execution Contract]
        You are a headless, non-interactive worker running on behalf of a parent Netclaw session.
        Do not ask the user clarifying questions, request conversational input, or wait for a reply.
        Do the best work you can with the task, context, and tools available.
        If the task is ambiguous, make reasonable assumptions and state them in your final output.
        If you are blocked, return a final result that explains what you found, what remains, and what decision the parent session needs.
        Parent-mediated tool approval may occur only for concrete tool calls; it is a security gate, not a dialogue channel.
        Always end by emitting a final output for the parent session.
        """;
    private static readonly TimeSpan StreamPingInterval = TimeSpan.FromSeconds(2);

    private readonly SubAgentDefinition _definition;
    private readonly IChatClient _chatClient;
    private readonly ToolAccessPolicy _toolAccessPolicy;
    private readonly IToolApprovalService? _approvalService;
    private readonly int _maxToolIterations;
    private readonly ToolRegistry _toolRegistry;
    private IReadOnlyList<AITool> _aiTools = [];
    private readonly ILoggingAdapter _log;
    private readonly MemoryPolicyEvaluator _policyEvaluator = new();
    private readonly TurnStateTracker _turnState = new();

    // Conversation state (not persisted — ephemeral)
    private readonly List<AiChatMessage> _history = [];
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
    private TimeSpan _inactivityBudget;

    public ITimerScheduler Timers { get; set; } = null!;
    private CancellationTokenRegistration _externalCancellationRegistration;
    private ToolExecutionContext _toolExecutionContext = ToolExecutionContext.Empty;
    private bool _malformedFinalOutputRepairAttempted;

    public SubAgentActor(
        SubAgentDefinition definition,
        IChatClient chatClient,
        ToolAccessPolicy? toolAccessPolicy = null,
        IToolApprovalService? approvalService = null,
        int maxToolIterations = DefaultMaxToolIterations)
    {
        if (maxToolIterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxToolIterations), maxToolIterations,
                "Sub-agent tool iteration budget must be greater than zero.");

        _definition = definition;
        _chatClient = chatClient;
        _toolAccessPolicy = toolAccessPolicy ?? new ToolAccessPolicy(
            new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed },
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy());
        _approvalService = approvalService;
        _maxToolIterations = maxToolIterations;
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
        ToolAccessPolicy? toolAccessPolicy = null,
        IToolApprovalService? approvalService = null,
        int maxToolIterations = DefaultMaxToolIterations)
    {
        return Props.Create(() => new SubAgentActor(
            definition,
            chatClient,
            toolAccessPolicy,
            approvalService,
            maxToolIterations));
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
                Success = false,
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

            _approvalBridge = msg.ApprovalBridge;

            var scopeId = !string.IsNullOrWhiteSpace(msg.SessionScopeId)
                ? msg.SessionScopeId!
                : $"subagent/{_definition.Name}/{Guid.NewGuid():N}";
            // A sub-agent inherits the spawning session's audience. A spawn with no
            // audience is a programming error — defaulting to Personal would
            // silently grant the sub-agent broader trust than its parent. Fail the
            // run immediately with a result so the caller fails fast, rather than
            // throwing (which crashes the actor and makes the caller wait out the
            // Ask timeout).
            if (msg.Audience is not { } subAgentAudience)
            {
                _log.Error(
                    "SubAgent [{AgentName}] spawn rejected: RunSubAgent carried no trust audience.",
                    _definition.Name);
                Complete(
                    success: false,
                    "Sub-agent spawn failed: no trust audience was provided. A sub-agent "
                    + "must inherit the spawning session's audience.");
                return;
            }

            _toolExecutionContext = new ToolExecutionContext(scopeId, msg.ParentSessionDirectory)
            {
                Audience = subAgentAudience,
                InheritedCwd = msg.ParentCwd,
            };
            _toolExecutionContext.Boundary = msg.Boundary;
            _toolExecutionContext.ChannelType = msg.ChannelType;
            _toolExecutionContext.ModelInputModalities = msg.ModelInputModalities;
            _toolExecutionContext.ProjectDirectory = msg.ParentProjectDirectory;
            _toolExecutionContext.SupportsInteractiveApproval = _approvalBridge is not null;
            _aiTools = ResolveExposedAiTools();
            _executionCts = new CancellationTokenSource();
            _externalCts = new CancellationTokenSource();
            var self = Self; // Capture before callback — Self requires active actor context
            _externalCancellationRegistration = msg.Cancellation.Register(() => self.Tell(SubAgentCancelled.Instance));

            // The run is bounded by an inactivity watchdog re-armed on every
            // progress event (LLM response, tool batch, streaming ping), so a
            // sub-agent making progress is never killed and a stalled one is.
            _activitySink = msg.ActivitySink;
            _inactivityBudget = msg.Timeout;
            ArmInactivityTimer();

            // Build initial conversation: system prompt (from file, verbatim) + task as user message.
            // If the caller supplied runtime context, prefix it onto the user message so the
            // system prompt stays reproducible across invocations.
            _history.Add(new AiChatMessage(Microsoft.Extensions.AI.ChatRole.System, BuildSystemPrompt(_definition)));
            _history.Add(new AiChatMessage(Microsoft.Extensions.AI.ChatRole.User, BuildUserMessage(msg.RuntimeContext, msg.Task)));

            _log.Info("SubAgent [{AgentName}] starting (tools={ToolCount}, timeout={Timeout})",
                _definition.Name, _aiTools.Count, msg.Timeout);

            FireLlmCall();
            Become(Processing);
        });
    }

    private void Processing()
    {
        Receive<LlmResponseReceived>(msg =>
        {
            ArmInactivityTimer();
            var response = msg.Response;
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
                Complete(false, "Subagent exceeded its tool iteration budget and continued requesting tools after tools were disabled.");
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
                switch (_turnState.EvaluateEmptyResponse(analysis.Kind))
                {
                    case EmptyResponseAction.Retry retry:
                        _log.Warning(
                            "SubAgent [{AgentName}] produced {Kind} response ({ThinkingChars} reasoning chars) — retrying with nudge",
                            _definition.Name,
                            analysis.Kind,
                            analysis.ThinkingChars);
                        AddSystemNudge(retry.NudgeText);
                        FireLlmCall();
                        return;
                    case EmptyResponseAction.Fail fail:
                        _log.Warning(
                            "SubAgent [{AgentName}] produced repeated {Kind} responses — reporting failure",
                            _definition.Name,
                            analysis.Kind);
                        Complete(false, fail.ErrorMessage);
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
                Complete(false, "Subagent returned an empty final response.");
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
                Complete(false, MalformedFinalOutputMessage);
                return;
            }

            Complete(true, text);
        });

        Receive<ToolExecutionCompleted>(msg =>
        {
            _toolExecutionWatchdogState = ToolExecutionWatchdogState.None;
            RecordProgress("processing tool results");

            // Append tool results as MEAI messages and log each result
            foreach (var result in msg.ToolResults)
            {
                _history.Add(ChatMessageConverter.ToAiMessage(result));
                var preview = result.Content is { Length: > 200 }
                    ? result.Content[..200] + "..."
                    : result.Content ?? "(null)";
                _log.Info("SubAgent [{AgentName}] tool [{ToolName}] result: {Result}",
                    _definition.Name, result.Name ?? "unknown", SecretOutputRedactor.Redact(preview));
            }

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
            Complete(false, $"Tool execution failed: {msg.Cause.Message}");
        });

        Receive<LlmCallFailed>(msg =>
        {
            _log.Error(msg.Cause, "SubAgent [{AgentName}] LLM call failed callId={CallId}", _definition.Name, msg.CallId);
            Complete(false, $"LLM call failed: {msg.Cause.Message}");
        });

        Receive<SubAgentCancelled>(_ =>
        {
            _executionCts?.Cancel();
            _externalCts?.Cancel();
            _log.Warning("SubAgent [{AgentName}] cancelled by parent", _definition.Name);
            Complete(false, "Subagent cancelled by parent");
        });

        Receive<SubAgentTimeout>(_ =>
        {
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
                ArmInactivityTimer();
                return;
            }

            _executionCts?.Cancel();
            _log.Warning(
                "SubAgent [{AgentName}] timed out: no activity for {Budget}s after {Iterations} tool iterations",
                _definition.Name, _inactivityBudget.TotalSeconds, _turnState.ToolIterationCount);
            Complete(false, $"Subagent timed out: no activity for {_inactivityBudget.TotalSeconds:F0}s.");
        });

        Receive<SubAgentApprovalWaitStarted>(_ =>
        {
            // Cancel the inactivity timer outright on first wait — re-armed
            // by SubAgentApprovalWaitCompleted when the last wait clears. The
            // SubAgentTimeout handler's WaitingForParentApproval state check is
            // belt-and-braces for a stale timer fire that arrived ahead of
            // this message in the mailbox.
            if (_pendingApprovalWaits == 0)
                Timers.Cancel(TimeoutTimerKey);

            _toolExecutionWatchdogState = ToolExecutionWatchdogState.WaitingForParentApproval;
            _pendingApprovalWaits++;
            EmitActivity("awaiting human approval", suspendsInactivityWatchdog: true);
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

        // Throttled liveness ping from a streaming LLM call — progress, even
        // before the full response message arrives.
        Receive<SubAgentStreamPing>(msg =>
        {
            _log.Debug(
                $"SubAgent [{_definition.Name}] LLM stream progress callId={msg.CallId} "
                + $"updates={msg.UpdateCount} emptyUpdates={msg.EmptyUpdateCount} "
                + $"text={msg.TextDeltaCount}/{msg.TextChars}ch "
                + $"thinking={msg.ThinkingDeltaCount}/{msg.ThinkingChars}ch "
                + $"toolCalls={msg.ToolCallDeltaCount} finishReason={msg.FinishReason ?? "null"}");
            RecordProgress("the model is responding");
        });
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
        }

        var toolNames = string.Join(", ", toolCalls.Select(tc => tc.Name));
        RecordProgress($"running tools: {toolNames}");
        _log.Info("SubAgent [{AgentName}] calling tools: [{ToolNames}]",
            _definition.Name, toolNames);

        var self = Self;
        var executor = new DispatchingToolExecutor(_toolRegistry, _toolAccessPolicy, _approvalService);
        _toolExecutionWatchdogState = _approvalBridge is null
            ? ToolExecutionWatchdogState.None
            : ToolExecutionWatchdogState.RunningApprovalCapableTools;

        _ = ExecuteToolsAsync(
            executor,
            toolCalls,
            _toolExecutionContext,
            _executionCts?.Token ?? CancellationToken.None,
            _externalCts?.Token ?? CancellationToken.None,
            self,
            _approvalBridge);
    }

    private void FireLlmCall(bool forceNoTools = false)
    {
        _turnState.ForceNoToolsActive = forceNoTools;
        RecordProgress("calling the model");
        var self = Self;
        var client = _chatClient;
        var messages = new List<AiChatMessage>(_history);
        var callId = ++_llmCallId;

        ChatOptions? options = null;
        if (!forceNoTools && _aiTools.Count > 0)
        {
            options = new ChatOptions
            {
                Tools = [.. _aiTools]
            };
        }

        var sessionId = _toolExecutionContext.SessionId is null ? (SessionId?)null : new SessionId(_toolExecutionContext.SessionId);
        _log.Info(
            "SubAgent [{AgentName}] LLM call start callId={CallId} iteration={Iteration} messages={MessageCount} toolsEnabled={ToolsEnabled} forceNoTools={ForceNoTools}",
            _definition.Name,
            callId,
            _turnState.ToolIterationCount,
            messages.Count,
            options?.Tools?.Count > 0,
            forceNoTools);
        _ = InvokeLlmAsync(client, messages, options, sessionId, self, callId, _executionCts?.Token ?? CancellationToken.None);
    }

    private IReadOnlyList<AITool> ResolveExposedAiTools()
    {
        var tools = _toolRegistry.GetAllRegistrations().Select(r => r.Tool);
        return _toolAccessPolicy
            .FilterDiscoverableTools(tools, _toolExecutionContext)
            .Select(tool => tool.ToAITool())
            .ToList();
    }

    private bool _completed;

    private void Complete(bool success, string output)
    {
        // Idempotent guard: a stale SubAgentTimeout or other handler can land
        // after Complete has already run (Tell from a thread-pool finally races
        // mailbox-thread Stop). The second call would send a duplicate
        // SubAgentResult and log a misleading "completed" line. The first
        // caller wins; subsequent calls are no-ops.
        if (_completed) return;
        _completed = true;

        _toolExecutionWatchdogState = ToolExecutionWatchdogState.None;
        _pendingApprovalWaits = 0;
        _executionCts?.Cancel();
        _externalCts?.Cancel();
        Timers.Cancel(TimeoutTimerKey);
        _externalCancellationRegistration.Dispose();
        _executionCts?.Dispose();
        _externalCts?.Dispose();
        _executionCts = null;
        _externalCts = null;
        _pendingApprovalWaits = 0;

        _log.Info("SubAgent [{AgentName}] completed (success={Success}, output={OutputLength} chars, iterations={Iterations})",
            _definition.Name, success, output.Length, _turnState.ToolIterationCount);

        var findings = success && _definition.EmitStructuredFindings
            ? BuildFindings(output, _toolExecutionContext.SessionId)
            : [];

        _replyTo.Tell(new SubAgentResult
        {
            Success = success,
            Output = output,
            AgentName = _definition.Name,
            Findings = findings,
            FindingsCount = findings.Count
        });

        Context.Stop(Self);
    }

    /// <summary>Re-arm the inactivity watchdog (Akka replaces the same-key timer).</summary>
    private void ArmInactivityTimer()
        => Timers.StartSingleTimer(TimeoutTimerKey, SubAgentTimeout.Instance, _inactivityBudget);

    /// <summary>Emit a liveness/progress item to the spawning tool's stream, if any.</summary>
    private void EmitActivity(string phase, bool suspendsInactivityWatchdog = false)
        => _activitySink?.TryWrite(new ToolActivityUpdate(phase)
        {
            SuspendsInactivityWatchdog = suspendsInactivityWatchdog
        });

    /// <summary>Record forward progress: re-arm the inactivity watchdog and emit activity.</summary>
    private void RecordProgress(string phase)
    {
        ArmInactivityTimer();
        EmitActivity(phase);
    }

    private List<SubAgentFinding> BuildFindings(string output, string? sessionId)
    {
        var content = output?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
            return [];

        if (content.Length < 30)
            return [];

        var normalized = content.Length <= 1800
            ? content
            : content[..1800];

        var confidence = 0.65;
        var policy = _policyEvaluator.EvaluateWrite(
            sensitivity: SubAgentFindingSensitivity.Normal.ToWireValue(),
            recallMode: SubAgentFindingRecallMode.Auto.ToWireValue(),
            confidence,
            isExplicitRequest: false);

        if (!policy.Allowed)
            return [];

        return
        [
            new SubAgentFinding
            {
                Shape = SubAgentFindingShape.Conclusion,
                Title = $"subagent:{_definition.Name}",
                Content = normalized,
                Kind = "record",
                Sensitivity = SubAgentFindingSensitivity.Normal,
                RecallMode = SubAgentFindingRecallMode.Searchable,
                UpdateSemantics = "append-document",
                Confidence = confidence,
                Durability = SubAgentFindingDurability.Durable,
                Reusability = SubAgentFindingReusability.Reusable,
                Evidence = []
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
        _history.Add(ChatMessageConverter.ToAiMessage(message, _toolExecutionContext.SessionDirectory));
    }

    /// <summary>
    /// Compose the initial user message for the subagent. When <paramref name="runtimeContext"/>
    /// is present, prefixes a <c>Context:</c> block ahead of the <c>Task:</c> block so the
    /// parent-supplied background stays visually separated from the agent's task.
    /// When runtime context is null or whitespace, returns the raw task string for backward
    /// compatibility with the pre-Context protocol.
    /// </summary>
    private static string BuildUserMessage(string? runtimeContext, string task)
    {
        if (string.IsNullOrWhiteSpace(runtimeContext))
            return task;

        return $"Context:\n{runtimeContext.Trim()}\n\nTask:\n{task}";
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
        SessionId? sessionId,
        IActorRef self,
        CancellationToken ct)
        => InvokeLlmAsync(client, messages, options, sessionId, self, callId: 0, ct);

    internal static async Task InvokeLlmAsync(
        IChatClient client,
        List<AiChatMessage> messages,
        ChatOptions? options,
        SessionId? sessionId,
        IActorRef self,
        long callId,
        CancellationToken ct)
    {
        try
        {
            // Sub-agents share the parent's diagnostics scope: SessionDiagnosticsContext
            // strips the "/subagent/..." suffix back to the parent id. Null is intentional
            // for sub-agents that run outside any session.
            using var diagnosticsScope = SessionDiagnosticsContext.Push(sessionId?.Value);

            // Use streaming to match the main session path. The non-streaming
            // GetResponseAsync path drops reasoning content for some providers
            // (e.g., Qwen emits <think> blocks that surface as TextReasoningContent
            // only in streaming mode), leaving the assistant message with no
            // TextContent and causing the subagent to report empty "(no response)".
            var updates = new List<ChatResponseUpdate>();
            var pingThrottle = Stopwatch.StartNew();
            int updateCount = 0;
            int emptyUpdateCount = 0;
            int textDeltaCount = 0;
            int textChars = 0;
            int thinkingDeltaCount = 0;
            int thinkingChars = 0;
            int toolCallDeltaCount = 0;
            string? finishReason = null;
            await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct))
            {
                updates.Add(update);
                updateCount++;
                if (update.Contents.Count == 0 && update.FinishReason is null)
                    emptyUpdateCount++;
                if (update.FinishReason is not null)
                    finishReason = update.FinishReason.ToString();

                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case TextContent text:
                            textDeltaCount++;
                            textChars += text.Text?.Length ?? 0;
                            break;
                        case TextReasoningContent reasoning:
                            thinkingDeltaCount++;
                            thinkingChars += reasoning.Text?.Length ?? 0;
                            break;
                        case FunctionCallContent:
                            toolCallDeltaCount++;
                            break;
                    }
                }

                // Content-free keepalives only prove the socket is alive; they
                // do not prove the model is making progress. Re-arm the
                // watchdog only for substantive deltas so provider heartbeat
                // loops cannot keep a sub-agent alive forever.
                if (IsSubstantiveStreamingUpdate(update) && pingThrottle.Elapsed >= StreamPingInterval)
                {
                    pingThrottle.Restart();
                    self.Tell(new SubAgentStreamPing(
                        callId,
                        updateCount,
                        emptyUpdateCount,
                        textDeltaCount,
                        textChars,
                        thinkingDeltaCount,
                        thinkingChars,
                        toolCallDeltaCount,
                        finishReason));
                }
            }

            var response = updates.ToChatResponse();
            if (response.Messages.Count == 0)
            {
                response = new ChatResponse(new AiChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, []));
            }

            self.Tell(new LlmResponseReceived
            {
                Response = response,
                CallId = callId,
                StreamUpdateCount = updateCount,
                EmptyStreamUpdateCount = emptyUpdateCount,
                StreamTextDeltaCount = textDeltaCount,
                StreamTextChars = textChars,
                StreamThinkingDeltaCount = thinkingDeltaCount,
                StreamThinkingChars = thinkingChars,
                StreamToolCallDeltaCount = toolCallDeltaCount
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

    internal static bool IsSubstantiveStreamingUpdate(ChatResponseUpdate update)
    {
        if (update.FinishReason is not null)
            return true;

        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                case FunctionCallContent:
                    return true;
                case UsageContent:
                    break;
                default:
                    return true;
            }
        }

        return false;
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
        IParentApprovalBridge? approvalBridge = null)
    {
        try
        {
            var modelInputBudget = new ModelInputBatchBudget(ChannelAttachmentPolicy.DefaultMaxFileBytes);
            var tasks = toolCalls.Select(async tc =>
            {
                var toolContext = CreatePerToolExecutionContext(executionContext);
                try
                {
                    var result = await executor.ExecuteAsync(tc, toolContext, ct);
                    return BuildToolResult(tc, result, toolContext, modelInputBudget);
                }
                catch (ToolApprovalRequiredException approvalEx)
                    when (approvalBridge is not null)
                {
                    var ctx = approvalEx.ApprovalContext;
                    var bridgeCandidates = ctx.Candidates is { Count: > 0 } c
                        ? c.Select(x => new ParentApprovalCandidate(x.Verb, x.Directory)).ToList()
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

                    if (decision is ParentApprovalDecision.ApprovedOnce
                        or ParentApprovalDecision.ApprovedSession
                        or ParentApprovalDecision.ApprovedAlways
                        or ParentApprovalDecision.ApprovedEverywhere)
                    {
                        // The immediate retry needs a transient grant even for session/always
                        // approvals because the sub-agent's scope ID differs from the parent
                        // session's scope. Keep that retry-local so approve-once cannot bleed
                        // across parallel tool calls or later iterations.
                        var retryContext = CreatePerToolExecutionContext(executionContext);
                        retryContext.OneTimeApprovedToolName = tc.Name;
                        retryContext.SetOneTimeApprovedPatterns(ctx.Patterns);

                        var result = await executor.ExecuteAsync(tc, retryContext, ct);
                        return BuildToolResult(tc, result, retryContext, modelInputBudget);
                    }

                    var reason = decision == ParentApprovalDecision.TimedOut
                        ? "Tool access denied: approval_timed_out"
                        : "Tool access denied: approval_denied_by_user";
                    return BuildToolResult(tc, reason, toolContext, modelInputBudget);
                }
                catch (ToolApprovalRequiredException)
                {
                    // Missing bridge wiring is a security/configuration failure,
                    // not a recoverable tool error for the model to continue past.
                    throw new ParentApprovalUnavailableException(
                        $"Tool '{tc.Name}' requires interactive approval, but no parent approval bridge is available.");
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
                    return BuildToolResult(tc, $"Error: {ex.Message}", toolContext, modelInputBudget);
                }
            });

            var results = await Task.WhenAll(tasks);
            self.Tell(new ToolExecutionCompleted
            {
                ToolResults = [.. results.Select(r => r.Message)],
                ModelInputMediaReferences = [.. results.SelectMany(r => r.ModelInputMediaReferences)]
            });
        }
        catch (Exception ex)
        {
            self.Tell(new ToolExecutionFailed { Cause = ex });
        }
    }

    private static ToolExecutionContext CreatePerToolExecutionContext(ToolExecutionContext source)
        => new(source.SessionId, source.SessionDirectory)
        {
            Audience = source.Audience,
            Boundary = source.Boundary,
            RequestedTimeoutSeconds = source.RequestedTimeoutSeconds,
            ChannelType = source.ChannelType,
            ModelInputModalities = source.ModelInputModalities,
            ProjectDirectory = source.ProjectDirectory,
            InheritedCwd = source.InheritedCwd,
            SupportsInteractiveApproval = source.SupportsInteractiveApproval,
            OnSubAgentActivity = source.OnSubAgentActivity,
            SpawnChildActor = source.SpawnChildActor,
            ApprovalBridge = source.ApprovalBridge
        };

    private static SubAgentToolCallResult BuildToolResult(
        FunctionCallContent toolCall,
        string resultText,
        ToolExecutionContext toolContext,
        ModelInputBatchBudget modelInputBudget)
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
            materialization.MediaReferences);
    }

    private static ModelInputMaterializationResult MaterializeModelInputFiles(
        ToolExecutionContext toolContext,
        ModelInputBatchBudget modelInputBudget)
    {
        if (toolContext.ModelInputFiles.Count == 0)
            return new ModelInputMaterializationResult([], 0);

        if (string.IsNullOrWhiteSpace(toolContext.SessionDirectory))
            return new ModelInputMaterializationResult([], toolContext.ModelInputFiles.Count);

        return SessionToolExecutionPipeline.MaterializeModelInputFiles(
            toolContext,
            toolContext.SessionDirectory,
            logger: null,
            modelInputBudget);
    }

    private sealed record SubAgentToolCallResult(
        SerializableChatMessage Message,
        IReadOnlyList<SerializableMediaReference> ModelInputMediaReferences);

    private static string BuildSystemPrompt(SubAgentDefinition definition)
    {
        var basePrompt = string.IsNullOrWhiteSpace(definition.ProjectInstructions)
            ? definition.SystemPrompt
            : SystemPromptAssembler.Assemble(agents: definition.SystemPrompt, projectInstructions: definition.ProjectInstructions);

        return string.Concat(basePrompt.TrimEnd(), "\n\n", HeadlessExecutionContract);
    }

    /// <summary>Singleton inactivity-watchdog marker message.</summary>
    private sealed class SubAgentTimeout
    {
        public static readonly SubAgentTimeout Instance = new();
        private SubAgentTimeout() { }
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
    /// so <see cref="SubAgentTimeout"/> ignores stale timer ticks
    /// while a human is deciding.
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

    /// <summary>Throttled self-message: the streaming LLM call is still producing output.</summary>
    private sealed record SubAgentStreamPing(
        long CallId,
        int UpdateCount,
        int EmptyUpdateCount,
        int TextDeltaCount,
        int TextChars,
        int ThinkingDeltaCount,
        int ThinkingChars,
        int ToolCallDeltaCount,
        string? FinishReason);

    // ── Reuse LlmSessionActor's internal message types ──
    // These are internal to Netclaw.Actors so accessible here.

    // LlmResponseReceived, LlmCallFailed, ToolExecutionCompleted, ToolExecutionFailed
    // are defined in Sessions/LlmMessages.cs
}
