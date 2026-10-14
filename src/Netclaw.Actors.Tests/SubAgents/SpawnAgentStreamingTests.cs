// -----------------------------------------------------------------------
// <copyright file="SpawnAgentStreamingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>Verifies that spawn dispatch returns durable acceptance to its caller.</summary>
public class SpawnAgentStreamingTests : TestKit
{
    public SpawnAgentStreamingTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // The test starts one child actor without persistence.
    }

    [Fact]
    public async Task Spawn_agent_returns_acceptance_without_child_activity_updates()
    {
        using var dir = new DisposableTempDir();
        var paths = new NetclawPaths(dir.Path);
        paths.EnsureDirectoriesExist();

        var policy = new ToolAccessPolicy(paths,
            new ToolConfig(),
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy(),
            new ToolPathPolicy([]));

        var registry = new ToolRegistry();
        registry.Register(new FakeNetclawTool("file_read", "stub content"));

        var profiles = new SubAgentDefinitionRegistry();
        profiles.Register(new SubAgentProfile
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
            policy,
            approvalService: null,
            new StaticSystemPromptProvider("You are a summarizer."),
            new WorkingContextSnapshotProvider(
                new GitWorkingContextInspector(TimeProvider.System),
                NullLogger<WorkingContextSnapshotProvider>.Instance),
            NullLogger<SubAgentSpawner>.Instance);
        registry.Register(new SpawnAgentTool(profiles, spawner, paths));

        var executor = new DispatchingToolExecutor(
            registry,
            policy,
            approvalService: null,
            NullLogger<DispatchingToolExecutor>.Instance);
        var context = TestToolExecutionContext.CreateBound(
            "console/acceptance-test",
            dir.Path,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                SpawnChildActor = (request, name, _) =>
                {
                    var prepared = Assert.IsType<PreparedChildStart>(request);
                    Sys.ActorOf(prepared.Props, name);
                    return Task.FromResult<object>(new ChildRunAcceptance(
                        prepared.RunId,
                        prepared.Execution.Scope.ScopeId));
                }
            });
        var spawnCall = new Microsoft.Extensions.AI.FunctionCallContent(
            "call-1",
            "spawn_agent",
            new Dictionary<string, object?>
            {
                ["agent"] = "summarizer",
                ["task"] = "Summarize the project.",
                ["_rationale"] = "Verify durable sub-agent acceptance."
            });

        Assert.Equal(ToolLivenessMode.Opaque, executor.GetLivenessMode(spawnCall));

        var activity = new List<ToolActivityUpdate>();
        string? result = null;
        await foreach (var update in executor.ExecuteStreamAsync(
                           spawnCall,
                           context,
                           TestContext.Current.CancellationToken))
        {
            switch (update)
            {
                case ToolActivityUpdate progress:
                    activity.Add(progress);
                    break;
                case ToolCompletedUpdate completed:
                    result = completed.Result;
                    break;
            }
        }

        Assert.Empty(activity);
        Assert.NotNull(result);
        Assert.True(result.StartsWith("{", StringComparison.Ordinal), $"Expected an acceptance receipt but got: {result}");
        using var receipt = JsonDocument.Parse(result);
        Assert.Equal("accepted", receipt.RootElement.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(receipt.RootElement.GetProperty("run_id").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(receipt.RootElement.GetProperty("scope_id").GetString()));
    }
}
