using System.Text;
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
public sealed class SubAgentActor : ReceiveActor
{
    private const int MaxToolIterations = 10;
    private const string EmptyResponseMarker = "(no response)";

    private readonly SubAgentDefinition _definition;
    private readonly IChatClient _chatClient;
    private readonly ToolAccessPolicy _toolAccessPolicy;
    private readonly IToolApprovalService? _approvalService;
    private readonly ToolRegistry _toolRegistry;
    private readonly IReadOnlyList<AITool> _aiTools;
    private readonly ILoggingAdapter _log;
    private readonly MemoryPolicyEvaluator _policyEvaluator = new();

    // Conversation state (not persisted — ephemeral)
    private readonly List<AiChatMessage> _history = new();
    private int _toolIterationCount;
    private IActorRef _replyTo = ActorRefs.Nobody;
    private ICancelable? _timeoutSchedule;
    private CancellationTokenSource? _executionCts;
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

            var scopeId = !string.IsNullOrWhiteSpace(msg.SessionScopeId)
                ? msg.SessionScopeId!
                : $"subagent/{_definition.Name}/{Guid.NewGuid():N}";
            _toolExecutionContext = new ToolExecutionContext(scopeId, null);
            _toolExecutionContext.Audience = msg.Audience ?? TrustAudience.Personal.ToWireValue();
            _toolExecutionContext.Boundary = msg.Boundary;
            _toolExecutionContext.ChannelType = msg.ChannelType;
            _toolExecutionContext.SupportsInteractiveApproval = false;
            _executionCts = new CancellationTokenSource();
            _externalCancellationRegistration = msg.Cancellation.Register(() => Self.Tell(SubAgentCancelled.Instance));

            // Schedule wall-clock timeout
            _timeoutSchedule = Context.System.Scheduler.ScheduleTellOnceCancelable(
                msg.Timeout, Self, SubAgentTimeout.Instance, ActorRefs.NoSender);

            // Build initial conversation: system prompt + task as user message
            _history.Add(new AiChatMessage(Microsoft.Extensions.AI.ChatRole.System, _definition.SystemPrompt));
            _history.Add(new AiChatMessage(Microsoft.Extensions.AI.ChatRole.User, msg.Task));

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
            _log.Warning("SubAgent [{AgentName}] timed out after {Iterations} tool iterations",
                _definition.Name, _toolIterationCount);
            Complete(false, "Subagent timed out");
        });
    }

    private void HandleToolCalls(AiChatMessage assistantMessage, List<FunctionCallContent> toolCalls)
    {
        // Add assistant message (with tool calls) to history
        _history.Add(assistantMessage);

        var toolNames = string.Join(", ", toolCalls.Select(tc => tc.Name));
        _log.Info("SubAgent [{AgentName}] calling tools: [{ToolNames}]",
            _definition.Name, toolNames);

        var self = Self;
        var executor = new DispatchingToolExecutor(_toolRegistry, _toolAccessPolicy, _approvalService);

        _ = ExecuteToolsAsync(
            executor,
            toolCalls,
            _toolExecutionContext,
            _executionCts?.Token ?? CancellationToken.None,
            self);
    }

    private void FireLlmCall(bool forceNoTools = false)
    {
        var self = Self;
        var client = _chatClient;
        var messages = new List<AiChatMessage>(_history);

        ChatOptions? options = null;
        if (!forceNoTools && _aiTools.Count > 0)
        {
            options = new ChatOptions
            {
                Tools = _aiTools.ToList()
            };
        }

        _ = InvokeLlmAsync(client, messages, options, _executionCts?.Token ?? CancellationToken.None, self);
    }

    private void Complete(bool success, string output)
    {
        _executionCts?.Cancel();
        _timeoutSchedule?.Cancel();
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

    private static async Task InvokeLlmAsync(
        IChatClient client, List<AiChatMessage> messages, ChatOptions? options, CancellationToken ct, IActorRef self)
    {
        try
        {
            // Use streaming to match the main session path. The non-streaming
            // GetResponseAsync path drops reasoning content for some providers
            // (e.g., Qwen emits <think> blocks that surface as TextReasoningContent
            // only in streaming mode), leaving the assistant message with no
            // TextContent and causing the subagent to report empty "(no response)".
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in client.GetStreamingResponseAsync(messages, options, ct))
            {
                updates.Add(update);
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
            self.Tell(new LlmCallFailed { Cause = ex });
        }
    }

    private static async Task ExecuteToolsAsync(
        IToolExecutor executor,
        List<FunctionCallContent> toolCalls,
        ToolExecutionContext executionContext,
        CancellationToken ct,
        IActorRef self)
    {
        try
        {
            var tasks = toolCalls.Select(async tc =>
            {
                try
                {
                    var result = await executor.ExecuteAsync(tc, executionContext, ct);
                    return new SerializableChatMessage
                    {
                        Role = Protocol.ChatRole.Tool,
                        Content = result,
                        ToolCallId = tc.CallId,
                        Name = tc.Name
                    };
                }
                catch (Exception ex)
                {
                    return new SerializableChatMessage
                    {
                        Role = Protocol.ChatRole.Tool,
                        Content = $"Error: {ex.Message}",
                        ToolCallId = tc.CallId,
                        Name = tc.Name
                    };
                }
            });

            var results = await Task.WhenAll(tasks);
            self.Tell(new ToolExecutionCompleted
            {
                ToolResults = results.ToList()
            });
        }
        catch (Exception ex)
        {
            self.Tell(new ToolExecutionFailed { Cause = ex });
        }
    }

    /// <summary>Singleton timeout marker message.</summary>
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

    // ── Reuse LlmSessionActor's internal message types ──
    // These are internal to Netclaw.Actors so accessible here.

    // LlmResponseReceived, LlmCallFailed, ToolExecutionCompleted, ToolExecutionFailed
    // are defined in Sessions/LlmMessages.cs
}
