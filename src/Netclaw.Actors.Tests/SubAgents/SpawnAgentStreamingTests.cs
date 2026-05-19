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
/// Regression guard for the default-interface-method dispatch gap that let
/// <c>spawn_agent</c> bypass its streaming override and run the non-streaming
/// path — emitting zero activity items, so the parent's per-call watchdog
/// killed healthy sub-agents at the flat tool timeout. Exercises the full
/// chain: <see cref="DispatchingToolExecutor"/> → <c>INetclawTool</c> dispatch
/// → <see cref="SpawnAgentTool"/> → <see cref="SubAgentActor"/> →
/// <see cref="StreamingToolWatchdog"/>.
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

        // The DIM dispatch gap ran spawn_agent on the non-streaming path: zero
        // activity items, so the watchdog saw only silence and killed healthy
        // sub-agents at the flat budget. The streaming override emits progress.
        Assert.NotEmpty(activity);

        // The terminal result still flows through — a successful sub-agent run,
        // not a "Subagent '...' failed: ..." message from FormatResult.
        Assert.NotEmpty(result);
        Assert.DoesNotContain("failed", result, StringComparison.OrdinalIgnoreCase);
    }
}
