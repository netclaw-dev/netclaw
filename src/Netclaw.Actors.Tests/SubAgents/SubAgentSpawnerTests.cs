// -----------------------------------------------------------------------
// <copyright file="SubAgentSpawnerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.SubAgents.SubAgentProtocol;

namespace Netclaw.Actors.Tests.SubAgents;

public sealed class SubAgentSpawnerTests : TestKit
{
    public SubAgentSpawnerTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // No hosting or persistence needed; the probe stands in for the child actor.
    }

    [Fact]
    public async Task Spawn_async_propagates_parent_resolved_cwd_on_run_message()
    {
        var toolRegistry = new ToolRegistry();
        toolRegistry.Register(new FakeNetclawTool("inspect_context", "ok"));

        var spawner = new SubAgentSpawner(
            new SingleClientProvider(new NoOpChatClient()),
            toolRegistry,
            new ToolAccessPolicy(
                new ToolConfig(),
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy()),
            approvalService: null,
            new StaticSystemPromptProvider("You are a summarizer."),
            NullLogger<SubAgentSpawner>.Instance);

        var childProbe = CreateTestProbe("subagent-child");
        var context = new ToolExecutionContext("console/subagent-parent", "/tmp/netclaw/sessions/parent")
        {
            Audience = TrustAudience.Personal,
            ProjectDirectory = "/home/user/repos/foo"
        };
        context.SpawnChildActor = (_, _, _) => Task.FromResult<object>(childProbe.Ref);

        var profile = new SubAgentProfile
        {
            Name = "summarizer",
            Description = "Summarize content",
            SystemPrompt = "You are a summarizer.",
            ToolNames = ["inspect_context"],
            Visibility = SubAgentVisibility.UserFacing
        };

        var spawnTask = spawner.SpawnAsync(
            profile,
            "Summarize the repo.",
            runtimeContext: null,
            context,
            TestContext.Current.CancellationToken);

        var run = await childProbe.ExpectMsgAsync<RunSubAgent>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("/tmp/netclaw/sessions/parent", run.ParentSessionDirectory);
        Assert.Equal("/home/user/repos/foo", run.ParentProjectDirectory);
        Assert.Equal("/home/user/repos/foo", run.ParentCwd);

        childProbe.Reply(new SubAgentResult
        {
            Success = true,
            Output = "ok",
            AgentName = new AgentName(profile.Name)
        });

        var result = await spawnTask;
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Spawn_async_ignores_definition_tool_metadata_for_runtime_tool_resolution()
    {
        var toolRegistry = new ToolRegistry();
        toolRegistry.Register(new FakeNetclawTool("inspect_context", "ok"));

        var spawner = new SubAgentSpawner(
            new SingleClientProvider(new NoOpChatClient()),
            toolRegistry,
            new ToolAccessPolicy(
                new ToolConfig(),
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy()),
            approvalService: null,
            new StaticSystemPromptProvider("You are a summarizer."),
            NullLogger<SubAgentSpawner>.Instance);

        var notifications = new List<SubAgentNotificationInfo>();
        var childProbe = CreateTestProbe("subagent-tool-metadata-child");
        var context = new ToolExecutionContext("console/subagent-parent", "/tmp/netclaw/sessions/parent")
        {
            Audience = TrustAudience.Personal,
            OnSubAgentActivity = notifications.Add
        };
        context.SpawnChildActor = (_, _, _) => Task.FromResult<object>(childProbe.Ref);

        var profile = new SubAgentProfile
        {
            Name = "summarizer",
            Description = "Summarize content",
            SystemPrompt = "You are a summarizer.",
            ToolNames = ["not_registered"],
            Visibility = SubAgentVisibility.UserFacing
        };

        var spawnTask = spawner.SpawnAsync(
            profile,
            "Summarize the repo.",
            runtimeContext: null,
            context,
            TestContext.Current.CancellationToken);

        await childProbe.ExpectMsgAsync<RunSubAgent>(cancellationToken: TestContext.Current.CancellationToken);
        childProbe.Reply(new SubAgentResult
        {
            Success = true,
            Output = "ok",
            AgentName = new AgentName(profile.Name)
        });

        var result = await spawnTask;

        Assert.True(result.Success);
        var started = Assert.Single(notifications, n => n.IsStarted);
        Assert.Equal(1, started.ToolCount);
    }

    [Fact]
    public async Task Spawned_sub_agent_bills_its_llm_calls_to_session_metrics()
    {
        // Full-wiring regression guard for #1597: a sub-agent spawned through the real
        // SubAgentSpawner must record its LLM-call tokens to the ISessionMetrics handed
        // to the spawner. Unlike the actor-level tests, this exercises the
        // spawner -> CreateProps -> actor pass-through, so dropping the metrics argument
        // anywhere along that chain fails here. The SpawnChildActor factory materializes
        // the spawner-built Props into a real SubAgentActor (a probe stand-in would
        // bypass CreateProps entirely and hide a broken pass-through).
        var toolRegistry = new ToolRegistry();
        toolRegistry.Register(new FakeNetclawTool("inspect_context", "ok"));

        var metrics = new RecordingSessionMetrics();
        var chatClient = new FakeChatClient
        {
            UsageOverride = new UsageDetails { InputTokenCount = 175, OutputTokenCount = 60 }
        };

        var spawner = new SubAgentSpawner(
            new SingleClientProvider(chatClient),
            toolRegistry,
            new ToolAccessPolicy(
                new ToolConfig(),
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy()),
            approvalService: null,
            new StaticSystemPromptProvider("You are a summarizer."),
            NullLogger<SubAgentSpawner>.Instance,
            sessionMetrics: metrics);

        var context = new ToolExecutionContext("console/subagent-parent", "/tmp/netclaw/sessions/parent")
        {
            Audience = TrustAudience.Personal
        };
        context.SpawnChildActor = (props, name, _) => Task.FromResult<object>(Sys.ActorOf((Props)props, name));

        var profile = new SubAgentProfile
        {
            Name = "summarizer",
            Description = "Summarize content",
            SystemPrompt = "You are a summarizer.",
            ToolNames = ["inspect_context"],
            Visibility = SubAgentVisibility.UserFacing
        };

        var result = await spawner.SpawnAsync(
            profile,
            "Summarize the repo.",
            runtimeContext: null,
            context,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, $"Expected success but got: {result.Output}");
        // One text-only LLM call → exactly one usage record, carrying the fake's tokens.
        var call = Assert.Single(metrics.TokenUsageCalls);
        Assert.Equal((175L, 60L), call);
    }

    private sealed class NoOpChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "noop")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
