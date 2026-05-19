// -----------------------------------------------------------------------
// <copyright file="SpawnAgentStreamingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// Full-chain coverage for the spawn_agent streaming path: a real
/// <see cref="DispatchingToolExecutor"/> resolves <c>spawn_agent</c>, dispatches
/// through the <c>INetclawTool</c> interface into <see cref="SpawnAgentTool"/>'s
/// streaming override, runs a real <see cref="SubAgentActor"/>, and surfaces the
/// sub-agent's progress as activity items into a real
/// <see cref="StreamingToolWatchdog"/>.
///
/// Regression guard for the default-interface-method dispatch gap: a plain
/// <c>public</c> <c>ExecuteStreamAsync</c> on <see cref="SpawnAgentTool"/> was
/// never reached through the <c>INetclawTool</c> interface (the interface slot
/// was bound to the DIM default at the <c>NetclawTool&lt;T&gt;</c> base). So
/// <c>spawn_agent</c> ran the non-streaming path, emitted zero activity items,
/// and the parent's per-call watchdog killed a healthy sub-agent at the flat
/// tool timeout. The watchdog's <c>onActivity</c> callback makes the otherwise
/// ephemeral activity items observable here.
/// </summary>
public class SpawnAgentStreamingTests : TestKit
{
    public SpawnAgentStreamingTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // No persistence or hosting needed — SubAgentActor is spawned standalone.
    }

    [Fact]
    public async Task Spawn_agent_streams_activity_through_executor_dispatch_to_watchdog()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();

        var toolAccessPolicy = new ToolAccessPolicy(
            new ToolConfig(),
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy());

        // The sub-agent resolves "file_read" from this registry; the fake LLM
        // never calls it — it just has to resolve so the spawn proceeds.
        var registry = new ToolRegistry();
        registry.Register(new FakeNetclawTool("file_read", "stub content"));

        var subAgentRegistry = new SubAgentDefinitionRegistry();
        subAgentRegistry.Register(new SubAgentProfile
        {
            Name = "summarizer",
            Description = "Summarize content",
            SystemPrompt = "You are a summarizer.",
            ToolNames = ["file_read"],
            Visibility = SubAgentVisibility.UserFacing
        });

        var spawner = new SubAgentSpawner(
            new SingleClientProvider(new FakeChatClient()),
            registry,
            toolAccessPolicy,
            approvalService: null,
            new StaticSystemPromptProvider("You are a summarizer."),
            NullLogger<SubAgentSpawner>.Instance);

        registry.Register(new SpawnAgentTool(subAgentRegistry, spawner, paths));

        var executor = new DispatchingToolExecutor(
            registry, toolAccessPolicy, approvalService: null, NullLogger<DispatchingToolExecutor>.Instance);

        var ctx = new ToolExecutionContext("console/streaming-test", dir.Path)
        {
            Audience = TrustAudience.Personal
        };
        ctx.SpawnChildActor = (props, name, _) => Task.FromResult<object>(Sys.ActorOf((Props)props, name));

        var spawnCall = new FunctionCallContent(
            "call-1",
            "spawn_agent",
            new Dictionary<string, object?>
            {
                ["agent"] = "summarizer",
                ["task"] = "Summarize the project."
            });

        var activity = new List<ToolActivityUpdate>();
        var result = await StreamingToolWatchdog.ConsumeAsync(
            executor.ExecuteStreamAsync(spawnCall, ctx, TestContext.Current.CancellationToken),
            "spawn_agent",
            ToolWatchdogBudget.Flat(TimeSpan.FromSeconds(30)),
            TimeProvider.System,
            onActivity: activity.Add,
            TestContext.Current.CancellationToken);

        // With the DIM dispatch gap, spawn_agent ran the non-streaming path and
        // produced zero activity items — the watchdog would see only silence and
        // kill a healthy sub-agent at the flat budget. The streaming override
        // emits at least one progress item ("calling the model").
        Assert.NotEmpty(activity);
        Assert.Contains("Response #1", result, StringComparison.Ordinal);
    }

    private sealed class SingleClientProvider(IChatClient client) : IChatClientProvider
    {
        public IChatClient GetClient(ModelRole role) => client;
    }
}
