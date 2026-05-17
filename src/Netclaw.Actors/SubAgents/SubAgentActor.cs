// -----------------------------------------------------------------------
// <copyright file="SubAgentActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Text;
using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
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
    private const int MaxToolIterations = 10;
    private const string EmptyResponseMarker = "(no response)";
    private const string BackstopTimerKey = "subagent-backstop";
    private const string HeartbeatTimerKey = "subagent-heartbeat";
    private static readonly TimeSpan MaxHeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinHeartbeatInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ActivityPingInterval = TimeSpan.FromSeconds(1);

    private readonly SubAgentDefinition _definition;
    private readonly IChatClient _chatClient;
    private readonly ToolAccessPolicy _toolAccessPolicy;
    private readonly IToolApprovalService? _approvalService;
    private readonly ToolRegistry _toolRegistry;
    private readonly IReadOnlyList<AITool> _aiTools;
    private readonly ILoggingAdapter _log;
    private readonly MemoryPolicyEvaluator _policyEvaluator = new();

    // Conversation state (not persisted — ephemeral)
    private readonly List<AiChatMessage> _history = [];
    private int _toolIterationCount;
    private IActorRef _replyTo = ActorRefs.Nobody;
    private CancellationTokenSource? _executionCts;
    private IParentApprovalBridge? _approvalBridge;

    public ITimerScheduler Timers { get; set; } = null!;
    private CancellationTokenRegistration _externalCancellationRegistration;
    private ToolExecutionContext _toolExecutionContext = ToolExecutionContext.Empty;

    // Inactivity watchdog (mirrors LlmSessionActor): resets on streaming/tool
    // activity so a responsive sub-agent is never killed mid-stream. The
    // single-shot backstop timer is the only hard wall-clock cap.
    private readonly ProcessingWatchdog _watchdog = new();
    private SubAgentTimeoutBudget _budget = SubAgentTimeoutBudget.FromLegacyTimeout(TimeSpan.FromSeconds(60));
    private bool _anyContentStreamedThisCall;

    // Set once in Complete — guards the multiple convergent termination paths
    // (inactivity watchdog, absolute backstop, parent cancellation) from
    // double-replying or double-stopping.
    private bool _completed;

    // Heartbeat state — liveness signal to the parent session's watchdog.
    private IActorRef? _heartbeatSink;
    private string _runId = string.Empty;
    private long _parentWatchdogOpId;
    private SubAgentHeartbeatPhase _currentPhase = SubAgentHeartbeatPhase.LlmStreaming;
    private long? _lastInputTokens;
    private long? _lastOutputTokens;

    public SubAgentActor(
        SubAgentDefinition definition,
        IChatClient chatClient,
        ToolAccessPolicy? toolAccessPolicy = null,
        IToolApprovalService? approvalService = null)
    {
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
        _log = Context.GetLogger();

        // Build a private ToolRegistry for this subagent's already-resolved tool subset.
        _toolRegistry = new ToolRegistry();
        foreach (var tool in definition.Tools)
        {
            _toolRegistry.Register(tool);
        }

        _aiTools = _toolRegistry.GetAllTools();

        Become(Idle);
    }

    public static Props CreateProps(
        SubAgentDefinition definition,
        IChatClient chatClient,
        ToolAccessPolicy? toolAccessPolicy = null,
        IToolApprovalService? approvalService = null)
    {
        return Props.Create(() => new SubAgentActor(definition, chatClient, toolAccessPolicy, approvalService));
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
            };
            _toolExecutionContext.Boundary = msg.Boundary;
            _toolExecutionContext.ChannelType = msg.ChannelType;
            _toolExecutionContext.ProjectDirectory = msg.ParentProjectDirectory;
            _toolExecutionContext.SupportsInteractiveApproval = _approvalBridge is not null;
            _executionCts = new CancellationTokenSource();
            var self = Self; // Capture before callback — Self requires active actor context
            _externalCancellationRegistration = msg.Cancellation.Register(() => self.Tell(SubAgentCancelled.Instance));

            _budget = msg.TimeoutBudget ?? SubAgentTimeoutBudget.FromLegacyTimeout(msg.Timeout);
            _heartbeatSink = msg.HeartbeatSink;
            _runId = msg.RunId ?? Guid.NewGuid().ToString("N");
            _parentWatchdogOpId = msg.ParentWatchdogOpId;

            // Absolute wall-clock backstop — armed once for the whole run, never
            // refreshed. The inactivity watchdog (armed per-operation in
            // FireLlmCall / HandleToolCalls) is the primary control; the backstop
            // only bounds a run that keeps producing activity but never finishes.
            Timers.StartSingleTimer(BackstopTimerKey, SubAgentBackstopExpired.Instance, _budget.AbsoluteBackstop);

            // Periodic liveness heartbeat so the parent session can keep its
            // spawn_agent tool-execution watchdog refreshed while this sub-agent
            // is alive. Skipped when there is no parent sink (standalone run).
            if (_heartbeatSink is not null)
            {
                EmitHeartbeat(SubAgentHeartbeatPhase.LlmStreaming);
                Timers.StartPeriodicTimer(
                    HeartbeatTimerKey,
                    SubAgentHeartbeatTick.Instance,
                    ComputeHeartbeatInterval(_budget.ToolExecutionTimeout));
            }

            // Build initial conversation: system prompt (from file, verbatim) + task as user message.
            // If the caller supplied runtime context, prefix it onto the user message so the
            // system prompt stays reproducible across invocations.
            _history.Add(new AiChatMessage(Microsoft.Extensions.AI.ChatRole.System, BuildSystemPrompt(_definition)));
            _history.Add(new AiChatMessage(Microsoft.Extensions.AI.ChatRole.User, BuildUserMessage(msg.RuntimeContext, msg.Task)));

            _log.Info("SubAgent [{AgentName}] starting (tools={ToolCount}, backstop={Backstop})",
                _definition.Name, _aiTools.Count, _budget.AbsoluteBackstop);

            FireLlmCall();
            Become(Processing);
        });
    }

    private void Processing()
    {
        Receive<LlmResponseReceived>(msg =>
        {
            var response = msg.Response;
            if (response.Usage is { } usage)
            {
                _lastInputTokens = usage.InputTokenCount;
                _lastOutputTokens = usage.OutputTokenCount;
            }

            var lastMessage = response.Messages[^1];

            var toolCalls = lastMessage.Contents.OfType<FunctionCallContent>().ToList();
            if (toolCalls.Count > 0)
            {
                HandleToolCalls(lastMessage, toolCalls);
                return;
            }

            // Final text response — we're done
            var text = ExtractText(lastMessage);
            if (string.IsNullOrWhiteSpace(text) || text == EmptyResponseMarker)
            {
                // LLM returned neither tool calls nor usable text. Surface as failure
                // so the parent session sees a real error instead of fabricating
                // "subagent still working" from an empty tool result.
                _log.Warning(
                    "SubAgent [{AgentName}] LLM returned empty response (no text content, no tool calls) — reporting as failure",
                    _definition.Name);
                Complete(false, "Subagent returned an empty response (no text content and no tool calls). "
                    + "The provider may have dropped reasoning content or the model produced no output.");
                return;
            }
            Complete(true, text);
        });

        // Streaming keepalive from InvokeLlmAsync — resets the inactivity watchdog
        // so a sub-agent whose model is actively responding is never killed.
        Receive<SubAgentLlmActivity>(_ =>
        {
            // _currentPhase is already LlmStreaming — FireLlmCall set it before
            // this call's stream began.
            if (!_anyContentStreamedThisCall)
            {
                _anyContentStreamedThisCall = true;
                _watchdog.Promote(_budget.FirstTokenTimeout, Timers);
            }
            else
            {
                _watchdog.Refresh(_budget.FirstTokenTimeout, Timers);
            }
        });

        Receive<ToolExecutionCompleted>(msg =>
        {
            // Append tool results as MEAI messages and log each result
            foreach (var result in msg.ToolResults)
            {
                _history.Add(ChatMessageConverter.ToAiMessage(result));
                var preview = result.Content is { Length: > 200 }
                    ? result.Content[..200] + "..."
                    : result.Content ?? "(null)";
                _log.Info("SubAgent [{AgentName}] tool [{ToolName}] result: {Result}",
                    _definition.Name, result.Name ?? "unknown", preview);
            }

            _toolIterationCount++;
            EmitHeartbeat(SubAgentHeartbeatPhase.ToolComplete);

            if (_toolIterationCount >= MaxToolIterations)
            {
                _log.Warning("SubAgent [{AgentName}] hit tool iteration limit ({Count}), forcing text response",
                    _definition.Name, _toolIterationCount);
                FireLlmCall(forceNoTools: true);
                return;
            }

            _log.Debug("SubAgent [{AgentName}] tool iteration {Count}, continuing",
                _definition.Name, _toolIterationCount);
            FireLlmCall();
        });

        Receive<ToolExecutionFailed>(msg =>
        {
            _log.Error(msg.Cause, "SubAgent [{AgentName}] tool execution failed", _definition.Name);
            Complete(false, $"Tool execution failed: {msg.Cause.Message}");
        });

        Receive<LlmCallFailed>(msg =>
        {
            _log.Error(msg.Cause, "SubAgent [{AgentName}] LLM call failed", _definition.Name);
            Complete(false, $"LLM call failed: {msg.Cause.Message}");
        });

        // Periodic liveness signal to the parent session's spawn_agent watchdog.
        // Sent unconditionally: it proves this actor's message loop is alive.
        // A stalled operation is caught by this sub-agent's own inactivity
        // watchdog (which self-terminates with a SubAgentResult); the parent
        // watchdog is the backstop for a fully-wedged sub-agent that cannot even
        // process its own timers.
        Receive<SubAgentHeartbeatTick>(_ => EmitHeartbeat(_currentPhase));

        Receive<ProcessingWatchdogExpired>(msg =>
        {
            if (!_watchdog.IsCurrent(msg))
                return; // stale — the operation already ended or advanced

            _watchdog.Stop(Timers);
            _executionCts?.Cancel();

            var (what, budget) = msg.OperationName switch
            {
                ProcessingWatchdog.LlmCall => (
                    "no LLM streaming activity",
                    _anyContentStreamedThisCall ? _budget.FirstTokenTimeout : _budget.PrefillTimeout),
                ProcessingWatchdog.ToolExecution => ("no tool activity", _budget.ToolExecutionTimeout),
                _ => ("no activity", _budget.AbsoluteBackstop)
            };

            _log.Warning(
                "SubAgent [{AgentName}] inactivity watchdog expired operation={Operation} iterations={Iterations}",
                _definition.Name, msg.OperationName, _toolIterationCount);
            Complete(false,
                $"Subagent '{_definition.Name}' timed out: {what} for {budget.TotalSeconds:F0}s "
                + $"(iteration {_toolIterationCount}).");
        });

        Receive<SubAgentCancelled>(_ =>
        {
            _executionCts?.Cancel();
            _log.Warning("SubAgent [{AgentName}] cancelled by parent", _definition.Name);
            Complete(false, "Subagent cancelled by parent");
        });

        Receive<SubAgentBackstopExpired>(_ =>
        {
            _executionCts?.Cancel();
            _log.Warning("SubAgent [{AgentName}] exceeded absolute backstop after {Iterations} tool iterations",
                _definition.Name, _toolIterationCount);
            Complete(false,
                $"Subagent '{_definition.Name}' timed out: exceeded its absolute time limit of "
                + $"{_budget.AbsoluteBackstop.TotalSeconds:F0}s.");
        });
    }

    private void HandleToolCalls(AiChatMessage assistantMessage, List<FunctionCallContent> toolCalls)
    {
        // Add assistant message (with tool calls) to history
        _history.Add(assistantMessage);

        var toolNames = string.Join(", ", toolCalls.Select(tc => tc.Name));
        _log.Info("SubAgent [{AgentName}] calling tools: [{ToolNames}]",
            _definition.Name, toolNames);

        // Switch the inactivity watchdog from the LLM-call budget to the
        // tool-batch budget for the duration of tool execution.
        _watchdog.Stop(Timers);
        _watchdog.Start(ProcessingWatchdog.ToolExecution, _budget.ToolExecutionTimeout, Timers);
        EmitHeartbeat(SubAgentHeartbeatPhase.ToolDispatch);

        var self = Self;
        var executor = new DispatchingToolExecutor(_toolRegistry, _toolAccessPolicy, _approvalService);

        _ = ExecuteToolsAsync(
            executor,
            toolCalls,
            _toolExecutionContext,
            _executionCts?.Token ?? CancellationToken.None,
            self,
            _approvalBridge);
    }

    private void FireLlmCall(bool forceNoTools = false)
    {
        // Arm the inactivity watchdog with the generous prefill budget; the first
        // streaming delta promotes it to the tighter inter-delta budget.
        _watchdog.Stop(Timers);
        _watchdog.Start(ProcessingWatchdog.LlmCall, _budget.PrefillTimeout, Timers);
        _anyContentStreamedThisCall = false;
        _currentPhase = SubAgentHeartbeatPhase.LlmStreaming;

        var self = Self;
        var client = _chatClient;
        var messages = new List<AiChatMessage>(_history);

        ChatOptions? options = null;
        if (!forceNoTools && _aiTools.Count > 0)
        {
            options = new ChatOptions
            {
                Tools = [.. _aiTools]
            };
        }

        var sessionId = _toolExecutionContext.SessionId is null ? (SessionId?)null : new SessionId(_toolExecutionContext.SessionId);
        _ = InvokeLlmAsync(client, messages, options, sessionId, self, _executionCts?.Token ?? CancellationToken.None);
    }

    /// <summary>
    /// Send a liveness heartbeat to the parent session so it can refresh the
    /// <c>spawn_agent</c> tool-execution watchdog. No-op for a standalone run.
    /// </summary>
    private void EmitHeartbeat(SubAgentHeartbeatPhase phase)
    {
        _currentPhase = phase;
        if (_heartbeatSink is null)
            return;

        _heartbeatSink.Tell(new SubAgentHeartbeat
        {
            AgentName = _definition.Name,
            RunId = _runId,
            ParentWatchdogOpId = _parentWatchdogOpId,
            Phase = phase,
            InputTokens = _lastInputTokens,
            OutputTokens = _lastOutputTokens
        });
    }

    private void Complete(bool success, string output)
    {
        // Multiple termination paths (inactivity watchdog, absolute backstop,
        // parent cancellation, normal finish) can converge — only the first runs.
        if (_completed)
            return;
        _completed = true;

        _watchdog.Stop(Timers);
        Timers.CancelAll();
        _executionCts?.Cancel();
        _externalCancellationRegistration.Dispose();
        _executionCts?.Dispose();
        _executionCts = null;

        _log.Info("SubAgent [{AgentName}] completed (success={Success}, output={OutputLength} chars, iterations={Iterations})",
            _definition.Name, success, output.Length, _toolIterationCount);

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

    private static TimeSpan ComputeHeartbeatInterval(TimeSpan toolExecutionTimeout)
    {
        var halfBudget = TimeSpan.FromTicks(Math.Max(1, toolExecutionTimeout.Ticks / 2));
        if (halfBudget < MinHeartbeatInterval)
            return MinHeartbeatInterval;
        return halfBudget < MaxHeartbeatInterval ? halfBudget : MaxHeartbeatInterval;
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

    internal static async Task InvokeLlmAsync(
        IChatClient client, List<AiChatMessage> messages, ChatOptions? options, SessionId? sessionId, IActorRef self, CancellationToken ct)
    {
        // Sub-agents share the parent's diagnostics scope: SessionDiagnosticsContext
        // strips the "/subagent/..." suffix back to the parent id, so logs roll up
        // to the parent session. The session-affinity id, by contrast, keeps the
        // full subagent scope id — the reverse proxy hash-routes that to its own
        // sticky GPU bucket (off the parent's card) with KV-cache reuse across the
        // sub-agent's own calls. Null is intentional for runs outside any session.
        SessionAffinityContext.SessionId = sessionId?.Value;
        using var diagnosticsScope = SessionDiagnosticsContext.Push(sessionId?.Value);
        try
        {
            // Use streaming to match the main session path. The non-streaming
            // GetResponseAsync path drops reasoning content for some providers
            // (e.g., Qwen emits <think> blocks that surface as TextReasoningContent
            // only in streaming mode), leaving the assistant message with no
            // TextContent and causing the subagent to report empty "(no response)".
            var updates = new List<ChatResponseUpdate>();
            var throttle = Stopwatch.StartNew();
            var pinged = false;
            await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct))
            {
                updates.Add(update);

                // Liveness ping so the actor's inactivity watchdog resets. The
                // first update always pings (it promotes the watchdog off the
                // prefill budget); later ones are throttled — the actor needs the
                // signal, not every delta's content.
                if (!pinged || throttle.Elapsed >= ActivityPingInterval)
                {
                    pinged = true;
                    throttle.Restart();
                    self.Tell(SubAgentLlmActivity.Instance);
                }
            }

            var response = updates.ToChatResponse();
            if (response.Messages.Count == 0)
            {
                response = new ChatResponse(new AiChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, []));
            }

            self.Tell(new LlmResponseReceived { Response = response });
        }
        catch (Exception ex)
        {
            self.Tell(new LlmCallFailed(ex));
        }
        finally
        {
            SessionAffinityContext.SessionId = null;
        }
    }

    private static async Task ExecuteToolsAsync(
        IToolExecutor executor,
        List<FunctionCallContent> toolCalls,
        ToolExecutionContext executionContext,
        CancellationToken ct,
        IActorRef self,
        IParentApprovalBridge? approvalBridge = null)
    {
        try
        {
            var tasks = toolCalls.Select(async tc =>
            {
                var toolContext = CreatePerToolExecutionContext(executionContext);
                try
                {
                    var result = await executor.ExecuteAsync(tc, toolContext, ct);
                    return new SerializableChatMessage
                    {
                        Role = Protocol.ChatRole.Tool,
                        Content = result,
                        ToolCallId = new ToolCallId(tc.CallId),
                        Name = tc.Name
                    };
                }
                catch (ToolApprovalRequiredException approvalEx)
                    when (approvalBridge is not null)
                {
                    var ctx = approvalEx.ApprovalContext;
                    var decision = await approvalBridge.RequestApprovalAsync(
                        new ToolCallId(tc.CallId),
                        ctx.ToolName,
                        ctx.DisplayText,
                        ctx.Patterns,
                        ctx.CandidateVerbs,
                        ctx.IsMessy,
                        ct);

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
                        return new SerializableChatMessage
                        {
                            Role = Protocol.ChatRole.Tool,
                            Content = result,
                            ToolCallId = new ToolCallId(tc.CallId),
                            Name = tc.Name
                        };
                    }

                    var reason = decision == ParentApprovalDecision.TimedOut
                        ? "Tool access denied: approval_timed_out"
                        : "Tool access denied: approval_denied_by_user";
                    return new SerializableChatMessage
                    {
                        Role = Protocol.ChatRole.Tool,
                        Content = reason,
                        ToolCallId = new ToolCallId(tc.CallId),
                        Name = tc.Name
                    };
                }
                catch (Exception ex)
                {
                    return new SerializableChatMessage
                    {
                        Role = Protocol.ChatRole.Tool,
                        Content = $"Error: {ex.Message}",
                        ToolCallId = new ToolCallId(tc.CallId),
                        Name = tc.Name
                    };
                }
            });

            var results = await Task.WhenAll(tasks);
            self.Tell(new ToolExecutionCompleted
            {
                ToolResults = [.. results]
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
            ProjectDirectory = source.ProjectDirectory,
            SupportsInteractiveApproval = source.SupportsInteractiveApproval,
            OnSubAgentActivity = source.OnSubAgentActivity,
            SpawnChildActor = source.SpawnChildActor,
            ApprovalBridge = source.ApprovalBridge
        };

    private static string BuildSystemPrompt(SubAgentDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.ProjectInstructions))
            return definition.SystemPrompt;

        return SystemPromptAssembler.Assemble(agents: definition.SystemPrompt, projectInstructions: definition.ProjectInstructions);
    }

    /// <summary>Singleton absolute-backstop timer marker — fired once per run.</summary>
    private sealed class SubAgentBackstopExpired
    {
        public static readonly SubAgentBackstopExpired Instance = new();
        private SubAgentBackstopExpired() { }
    }

    private sealed class SubAgentCancelled
    {
        public static readonly SubAgentCancelled Instance = new();
        private SubAgentCancelled() { }
    }

    /// <summary>Streaming keepalive self-message emitted by <see cref="InvokeLlmAsync"/>.</summary>
    private sealed class SubAgentLlmActivity
    {
        public static readonly SubAgentLlmActivity Instance = new();
        private SubAgentLlmActivity() { }
    }

    /// <summary>Periodic timer marker that triggers a parent liveness heartbeat.</summary>
    private sealed class SubAgentHeartbeatTick
    {
        public static readonly SubAgentHeartbeatTick Instance = new();
        private SubAgentHeartbeatTick() { }
    }

    // ── Reuse LlmSessionActor's internal message types ──
    // These are internal to Netclaw.Actors so accessible here.

    // LlmResponseReceived, LlmCallFailed, ToolExecutionCompleted, ToolExecutionFailed
    // are defined in Sessions/LlmMessages.cs
}
