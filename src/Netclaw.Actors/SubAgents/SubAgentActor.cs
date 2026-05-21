// -----------------------------------------------------------------------
// <copyright file="SubAgentActor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
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
    private const string TimeoutTimerKey = "subagent-timeout";
    private static readonly TimeSpan StreamPingInterval = TimeSpan.FromSeconds(2);

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
    private ChannelWriter<ToolActivityUpdate>? _activitySink;
    private TimeSpan _inactivityBudget;

    public ITimerScheduler Timers { get; set; } = null!;
    private CancellationTokenRegistration _externalCancellationRegistration;
    private ToolExecutionContext _toolExecutionContext = ToolExecutionContext.Empty;

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

        // Build a private ToolRegistry for this subagent's tool subset
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
                InheritedCwd = msg.ParentCwd,
            };
            _toolExecutionContext.Boundary = msg.Boundary;
            _toolExecutionContext.ChannelType = msg.ChannelType;
            _toolExecutionContext.ProjectDirectory = msg.ParentProjectDirectory;
            _toolExecutionContext.SupportsInteractiveApproval = _approvalBridge is not null;
            _executionCts = new CancellationTokenSource();
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

        Receive<ToolExecutionCompleted>(msg =>
        {
            RecordProgress("processing tool results");

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

        Receive<SubAgentCancelled>(_ =>
        {
            _executionCts?.Cancel();
            _log.Warning("SubAgent [{AgentName}] cancelled by parent", _definition.Name);
            Complete(false, "Subagent cancelled by parent");
        });

        Receive<SubAgentTimeout>(_ =>
        {
            _executionCts?.Cancel();
            _log.Warning(
                "SubAgent [{AgentName}] timed out: no activity for {Budget}s after {Iterations} tool iterations",
                _definition.Name, _inactivityBudget.TotalSeconds, _toolIterationCount);
            Complete(false, $"Subagent timed out: no activity for {_inactivityBudget.TotalSeconds:F0}s.");
        });

        // Throttled liveness ping from a streaming LLM call — progress, even
        // before the full response message arrives.
        Receive<SubAgentStreamPing>(_ => RecordProgress("the model is responding"));
    }

    private void HandleToolCalls(AiChatMessage assistantMessage, List<FunctionCallContent> toolCalls)
    {
        // Add assistant message (with tool calls) to history
        _history.Add(assistantMessage);

        var toolNames = string.Join(", ", toolCalls.Select(tc => tc.Name));
        RecordProgress($"running tools: {toolNames}");
        _log.Info("SubAgent [{AgentName}] calling tools: [{ToolNames}]",
            _definition.Name, toolNames);

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
        RecordProgress("calling the model");
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

    private void Complete(bool success, string output)
    {
        _executionCts?.Cancel();
        Timers.Cancel(TimeoutTimerKey);
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

    /// <summary>Re-arm the inactivity watchdog (Akka replaces the same-key timer).</summary>
    private void ArmInactivityTimer()
        => Timers.StartSingleTimer(TimeoutTimerKey, SubAgentTimeout.Instance, _inactivityBudget);

    /// <summary>Emit a liveness/progress item to the spawning tool's stream, if any.</summary>
    private void EmitActivity(string phase)
        => _activitySink?.TryWrite(new ToolActivityUpdate(phase));

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
            await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct))
            {
                updates.Add(update);

                // Throttled liveness ping so the actor re-arms its inactivity
                // watchdog and surfaces activity during a long streaming call.
                if (pingThrottle.Elapsed >= StreamPingInterval)
                {
                    pingThrottle.Restart();
                    self.Tell(SubAgentStreamPing.Instance);
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
                    var bridgeCandidates = ctx.Candidates is { Count: > 0 } c
                        ? c.Select(x => new ParentApprovalCandidate(x.Verb, x.Directory)).ToList()
                        : (IReadOnlyList<ParentApprovalCandidate>)[];
                    var bridgeOptions = ctx.Options
                        .Select(o => new ParentApprovalOption(o.Key.Value, o.Label))
                        .ToList();
                    var decision = await approvalBridge.RequestApprovalAsync(
                        new ToolCallId(tc.CallId),
                        ctx.ToolName,
                        ctx.DisplayText,
                        ctx.Patterns,
                        ctx.CandidateVerbs,
                        bridgeCandidates,
                        ctx.Cwd,
                        bridgeOptions,
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
            InheritedCwd = source.InheritedCwd,
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

    /// <summary>Throttled self-message: the streaming LLM call is still producing output.</summary>
    private sealed class SubAgentStreamPing
    {
        public static readonly SubAgentStreamPing Instance = new();
        private SubAgentStreamPing() { }
    }

    // ── Reuse LlmSessionActor's internal message types ──
    // These are internal to Netclaw.Actors so accessible here.

    // LlmResponseReceived, LlmCallFailed, ToolExecutionCompleted, ToolExecutionFailed
    // are defined in Sessions/LlmMessages.cs
}
