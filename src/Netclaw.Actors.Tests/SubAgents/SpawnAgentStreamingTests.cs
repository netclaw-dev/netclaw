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
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Sessions;
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
/// path — emitting zero activity items. Exercises the full chain:
/// <see cref="DispatchingToolExecutor"/> → <c>INetclawTool</c> dispatch →
/// <see cref="SpawnAgentTool"/> → <see cref="SubAgentActor"/>, drained the way
/// the production pipeline drains a self-monitoring tool.
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
            new WorkingContextSnapshotProvider(NullLogger<WorkingContextSnapshotProvider>.Instance),
            NullLogger<SubAgentSpawner>.Instance);

        registry.Register(new SpawnAgentTool(subAgentRegistry, spawner, paths));

        var executor = new DispatchingToolExecutor(
            registry, toolAccessPolicy, approvalService: null, NullLogger<DispatchingToolExecutor>.Instance);

        var ctx = TestToolExecutionContext.CreateBound("console/streaming-test", dir.Path, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            SpawnChildActor = (props, name, _) => Task.FromResult<object>(Sys.ActorOf((Props)props, name)),
        });

        var spawnCall = new FunctionCallContent(
            "call-1",
            "spawn_agent",
            new Dictionary<string, object?>
            {
                ["agent"] = "summarizer",
                ["task"] = "Summarize the project."
            });

        // Drain the stream the way the production pipeline drains a self-monitoring
        // tool, collecting activity items along the way.
        var activity = new List<ToolActivityUpdate>();
        string? result = null;
        await foreach (var update in executor.ExecuteStreamAsync(spawnCall, ctx, TestContext.Current.CancellationToken))
        {
            switch (update)
            {
                case ToolActivityUpdate a:
                    activity.Add(a);
                    break;
                case ToolCompletedUpdate done:
                    result = done.Result;
                    break;
            }
        }

        // The DIM dispatch gap ran spawn_agent on the non-streaming path: zero
        // activity items. The streaming override emits progress.
        Assert.NotEmpty(activity);

        // The terminal result still flows through — a successful sub-agent run,
        // not a "Subagent '...' failed: ..." message from FormatResult.
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.Contains("Subagent run finished.", result, StringComparison.Ordinal);
        Assert.Contains("Outcome: completed", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Summary:", result, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Spawn_agent_self_monitoring_survives_quiet_window_after_first_activity()
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

        var fakeClient = new ControlledStreamingChatClient();
        var spawner = new SubAgentSpawner(
            new SingleClientProvider(fakeClient),
            registry,
            toolAccessPolicy,
            approvalService: null,
            new StaticSystemPromptProvider("You are a summarizer."),
            new WorkingContextSnapshotProvider(NullLogger<WorkingContextSnapshotProvider>.Instance),
            NullLogger<SubAgentSpawner>.Instance);

        registry.Register(new SpawnAgentTool(subAgentRegistry, spawner, paths));

        var executor = new DispatchingToolExecutor(
            registry, toolAccessPolicy, approvalService: null, NullLogger<DispatchingToolExecutor>.Instance);

        var ctx = TestToolExecutionContext.CreateBound("console/self-monitoring-test", dir.Path, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            SpawnChildActor = (props, name, _) => Task.FromResult<object>(Sys.ActorOf((Props)props, name)),
        });

        var spawnCall = new FunctionCallContent(
            "call-1",
            "spawn_agent",
            new Dictionary<string, object?>
            {
                ["agent"] = "summarizer",
                ["task"] = "Summarize the project."
            });

        // spawn_agent is self-monitoring, so the parent drains it with no watchdog at
        // all — a long quiet window can never trip a timeout; the sub-agent owns its
        // own liveness and always yields a terminal item.
        Assert.Equal(ToolLivenessMode.SelfMonitoring, executor.GetLivenessMode(spawnCall));

        async Task<string?> DrainAsync()
        {
            string? completed = null;
            await foreach (var update in executor.ExecuteStreamAsync(spawnCall, ctx, TestContext.Current.CancellationToken))
            {
                if (update is ToolCompletedUpdate done)
                    completed = done.Result;
            }
            return completed;
        }

        var drain = DrainAsync();

        // The drain cannot finish until the sub-agent produces its terminal result.
        Assert.False(drain.IsCompleted);

        fakeClient.Complete("summary complete");

        var result = await drain.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Contains("Subagent run finished.", result, StringComparison.Ordinal);
        Assert.Contains("Outcome: completed", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("summary complete", result);
    }

    private sealed class ControlledStreamingChatClient : IChatClient
    {
        private readonly TaskCompletionSource<string> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("ControlledStreamingChatClient is streaming-only.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var responseText = await _response.Task.WaitAsync(cancellationToken);
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, [new TextContent(responseText)]));

            foreach (var update in response.ToChatResponseUpdates())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }
        }

        public void Complete(string responseText) => _response.TrySetResult(responseText);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
