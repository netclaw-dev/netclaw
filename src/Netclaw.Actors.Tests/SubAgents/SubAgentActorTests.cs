// -----------------------------------------------------------------------
// <copyright file="SubAgentActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

public class SubAgentActorTests : TestKit
{
    public SubAgentActorTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // No persistence or hosting needed — SubAgentActor is standalone
    }

    private static SubAgentDefinition CreateDefinition(IReadOnlyList<INetclawTool>? tools = null)
    {
        return new SubAgentDefinition
        {
            Name = "test-agent",
            SystemPrompt = "You are a test agent.",
            Tools = tools ?? [],
            EmitStructuredFindings = false
        };
    }

    [Fact]
    public async Task Text_response_returns_success_result()
    {
        var fakeClient = new FakeChatClient();
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Say hello", Timeout = TimeSpan.FromSeconds(5) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Contains("Response #1", result.Output);
        Assert.Equal("test-agent", result.AgentName);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Tool_call_executes_and_continues()
    {
        var fakeTool = new FakeNetclawTool("greet", "Hello from tool!");
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                new FunctionCallContent("call-1", "greet",
                    new Dictionary<string, object?> { ["name"] = "World" })
            ]
        };

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Greet the user", Timeout = TimeSpan.FromSeconds(5) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(fakeTool.WasCalled);
        // Second LLM call returns text (tool calls only on first call)
        Assert.Contains("Response #2", result.Output);
    }

    [Fact]
    public async Task Approval_gated_tool_is_denied_inside_subagent()
    {
        var fakeTool = new FakeNetclawTool("shell_execute", "should not run");
        var toolConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        toolConfig.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Approval
            }
        };
        var policy = new ToolAccessPolicy(
            toolConfig,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy());
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                new FunctionCallContent("call-approval", "shell_execute",
                    new Dictionary<string, object?> { ["Command"] = "git push origin main" })
            ]
        };

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, policy, approvalService: null));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Try the shell tool", Timeout = TimeSpan.FromSeconds(5) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(fakeTool.WasCalled);
        Assert.Contains("Response #2", result.Output);
    }

    [Fact]
    public async Task Max_iterations_forces_text_response()
    {
        var fakeTool = new FakeNetclawTool("looper", "loop result");
        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                new FunctionCallContent("call-loop", "looper")
            ],
            AlwaysReturnToolCalls = true
        };

        var definition = CreateDefinition([fakeTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Loop forever", Timeout = TimeSpan.FromSeconds(10) },
            TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // After 10 tool iterations, forces a no-tools call which returns text
        Assert.True(result.Success);
        // Should have made multiple calls: 10 tool calls + 1 initial + 1 forced text
        Assert.True(fakeClient.CallCount >= 11);
    }

    [Fact]
    public async Task Timeout_returns_failure()
    {
        var fakeClient = new FakeChatClient
        {
            Delay = TimeSpan.FromSeconds(30) // Much longer than timeout
        };
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Slow task", Timeout = TimeSpan.FromMilliseconds(500) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task LLM_failure_returns_failure()
    {
        var throwingClient = new ThrowingChatClient();
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, throwingClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Fail", Timeout = TimeSpan.FromSeconds(5) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("LLM call failed", result.Output);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Actor_stops_after_completion()
    {
        var fakeClient = new FakeChatClient();
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));
        Watch(agent);

        agent.Tell(new RunSubAgent { Task = "Done", Timeout = TimeSpan.FromSeconds(5) });

        // SubAgentResult arrives before Terminated — drain it first
        await ExpectMsgAsync<SubAgentResult>(cancellationToken: TestContext.Current.CancellationToken);
        await ExpectTerminatedAsync(agent, TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Tool_execution_uses_session_scope_for_mcp_invocation()
    {
        var invoker = new RecordingMcpToolInvoker("ok");
        var fakePlaywrightTool = new McpToolAdapter(
            AIFunctionFactory.Create((string url) => url, "navigate_page"),
            "browser_playwright",
            "navigate_page",
            invoker: invoker);

        var fakeClient = new FakeChatClient
        {
            ToolCallsOnFirstCall =
            [
                new FunctionCallContent(
                    "call-1",
                    "browser_playwright/navigate_page",
                    new Dictionary<string, object?> { ["url"] = "https://example.com" })
            ]
        };

        var toolConfig = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        toolConfig.AudienceProfiles.Team.McpServersMode = ToolProfileMode.All;
        var policy = new ToolAccessPolicy(
            toolConfig,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy());

        var definition = CreateDefinition([fakePlaywrightTool]);
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient, policy, approvalService: null));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Open example.com",
                Timeout = TimeSpan.FromSeconds(5),
                SessionScopeId = "session/subagent-scope",
                Audience = TrustAudience.Team.ToWireValue()
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("session/subagent-scope", invoker.SessionId);
        Assert.Equal(TrustAudience.Team.ToWireValue(), invoker.Audience);
        Assert.Equal("browser_playwright", invoker.ServerName);
        Assert.Equal("navigate_page", invoker.ToolName);
    }

    [Fact]
    public async Task Long_text_response_does_not_emit_findings_by_default()
    {
        var fakeClient = new FakeChatClient
        {
            ResponseText = "This is a durable subagent summary with enough detail to be considered a memory candidate for parent-session checkpoint review."
        };
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Summarize research", Timeout = TimeSpan.FromSeconds(5) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Long_text_response_emits_findings_when_enabled()
    {
        var fakeClient = new FakeChatClient
        {
            ResponseText = "This is a durable subagent summary with enough detail to be considered a memory candidate for parent-session checkpoint review."
        };
        var definition = CreateDefinition() with { EmitStructuredFindings = true };
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent { Task = "Summarize research", Timeout = TimeSpan.FromSeconds(5) },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Single(result.Findings);
        Assert.Equal(SubAgentFindingShape.Conclusion, result.Findings[0].Shape);
        Assert.Equal("subagent:test-agent", result.Findings[0].Title);
        Assert.Equal(SubAgentFindingDurability.Durable, result.Findings[0].Durability);
        Assert.Equal(SubAgentFindingReusability.Reusable, result.Findings[0].Reusability);
        Assert.Equal(SubAgentFindingRecallMode.Searchable, result.Findings[0].RecallMode);
    }

    [Fact]
    public async Task RuntimeContext_is_prefixed_onto_first_user_message()
    {
        var fakeClient = new FakeChatClient();
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Summarize the recent commits.",
                RuntimeContext = "Workspace is netclaw on branch feature/foo.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeClient.LastReceivedMessages);

        // System prompt is message 0, user message is message 1.
        Assert.Equal(2, fakeClient.LastReceivedMessages.Count);
        Assert.Equal(ChatRole.User, fakeClient.LastReceivedMessages[1].Role);

        var userText = fakeClient.LastReceivedMessages[1].Text;
        Assert.Contains("Context:", userText);
        Assert.Contains("Workspace is netclaw on branch feature/foo.", userText);
        Assert.Contains("Task:", userText);
        Assert.Contains("Summarize the recent commits.", userText);
    }

    [Fact]
    public async Task Null_RuntimeContext_leaves_first_user_message_as_raw_task()
    {
        var fakeClient = new FakeChatClient();
        var definition = CreateDefinition();
        var agent = Sys.ActorOf(SubAgentActor.CreateProps(definition, fakeClient));

        var result = await agent.Ask<SubAgentResult>(
            new RunSubAgent
            {
                Task = "Do the thing.",
                Timeout = TimeSpan.FromSeconds(5)
            },
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(fakeClient.LastReceivedMessages);
        Assert.Equal("Do the thing.", fakeClient.LastReceivedMessages[1].Text);
        Assert.DoesNotContain("Context:", fakeClient.LastReceivedMessages[1].Text);
    }

    /// <summary>
    /// IChatClient that always throws on GetResponseAsync.
    /// </summary>
    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("LLM connection failed");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("LLM connection failed");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

/// <summary>
/// Fake IChatClient for SubAgentActor tests (and other test files that need it).
/// Copied from LlmSessionIntegrationTests — kept internal for cross-file reuse.
/// </summary>
internal sealed class FakeChatClient : IChatClient
{
    private int _callCount;

    public int CallCount => _callCount;

    /// <summary>
    /// Snapshot of the messages passed to the most recent call. Replaced on every call.
    /// </summary>
    public IReadOnlyList<ChatMessage>? LastReceivedMessages { get; private set; }

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// When set, the first response returns these tool calls instead of text.
    /// Subsequent calls return normal text (simulating the LLM completing after tool results).
    /// When <see cref="AlwaysReturnToolCalls"/> is true, every call returns tool calls
    /// as long as tools are available in options.
    /// </summary>
    public List<FunctionCallContent>? ToolCallsOnFirstCall { get; set; }

    /// <summary>
    /// When true, every call returns tool calls as long as options.Tools is non-empty.
    /// </summary>
    public bool AlwaysReturnToolCalls { get; set; }

    public string? ResponseText { get; set; }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        LastReceivedMessages = messages.ToList();

        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken);

        if (ToolCallsOnFirstCall is not null)
        {
            var returnToolCalls = AlwaysReturnToolCalls
                ? options?.Tools?.Count > 0
                : _callCount == 1;

            if (returnToolCalls)
            {
                var toolCallContents = new List<AIContent>(ToolCallsOnFirstCall);
                var toolCallMessage = new ChatMessage(
                    ChatRole.Assistant, toolCallContents);
                return new ChatResponse(toolCallMessage);
            }
        }

        var responseMessage = new ChatMessage(
            ChatRole.Assistant,
            [new TextContent(ResponseText ?? $"[fake] Response #{_callCount}")]);
        return new ChatResponse(responseMessage);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => CreateStreamingUpdatesAsync(messages, options, cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> CreateStreamingUpdatesAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

internal sealed class RecordingMcpToolInvoker(string result) : IMcpToolInvoker
{
    public string? ServerName { get; private set; }
    public string? ToolName { get; private set; }
    public string? SessionId { get; private set; }
    public string? Audience { get; private set; }

    public Task<string> InvokeAsync(
        string serverName,
        string toolName,
        IDictionary<string, object?>? arguments,
        ToolExecutionContext? context,
        CancellationToken ct = default)
    {
        ServerName = serverName;
        ToolName = toolName;
        SessionId = context?.SessionId;
        Audience = context?.Audience;
        return Task.FromResult(result);
    }
}
