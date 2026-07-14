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
using Netclaw.Actors.Channels;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
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
            new SingleClientProvider(new FakeChatClient()),
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
            new WorkingContextSnapshotProvider(
                new GitWorkingContextInspector(TimeProvider.System),
                NullLogger<WorkingContextSnapshotProvider>.Instance),
            NullLogger<SubAgentSpawner>.Instance);

        var childProbe = CreateTestProbe("subagent-child");
        var context = TestToolExecutionContext.CreateBound("console/subagent-parent", "/tmp/netclaw/sessions/parent", new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            ProjectDirectory = "/home/user/repos/foo",
            SpawnChildActor = (_, _, _) => Task.FromResult<object>(childProbe.Ref),
        });

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
            context.Invocation,
            TestContext.Current.CancellationToken);

        var run = await childProbe.ExpectMsgAsync<RunSubAgent>(cancellationToken: TestContext.Current.CancellationToken);
        var bound = Assert.IsType<ToolSessionScope.Bound>(run.Scope.Authority.Session);
        Assert.Equal("/tmp/netclaw/sessions/parent", bound.SessionDirectory);
        Assert.Equal("/home/user/repos/foo", run.Scope.Authority.ProjectDirectory);
        Assert.Equal("/home/user/repos/foo", run.Scope.Authority.InheritedCwd);

        childProbe.Reply(new SubAgentResult
        {
            Completion = new ChildRunCompletion.Completed(WorkingContextDelta.Empty),
            Output = "ok",
            AgentName = new AgentName(profile.Name)
        });

        var result = await spawnTask;
        Assert.True(result.Success);
    }

    [Theory]
    [InlineData(ChannelType.Headless)]
    [InlineData(ChannelType.Reminder)]
    [InlineData(ChannelType.Webhook)]
    public async Task Spawn_async_does_not_bridge_approval_for_non_interactive_parent(ChannelType channelType)
    {
        var childProbe = CreateTestProbe($"non-interactive-{channelType}-child");
        var spawner = CreateSpawner();
        var context = new ToolExecutionContext(new ToolRunScope
        {
            Session = new ToolSessionScope.Bound("automation/subagent-parent", "/tmp/netclaw/sessions/parent"),
            Audience = TrustAudience.Personal,
            InlineOutputBudget = InlineOutputBudget.Default,
            ChannelType = channelType.ToWireValue(),
            SupportsInteractiveApproval = false,
            ApprovalBridge = new RecordingParentApprovalBridge(ParentApprovalDecision.ApprovedOnce),
            SpawnChildActor = (_, _, _) => Task.FromResult<object>(childProbe.Ref)
        }, ToolExecutionTimeout.Default);

        var spawnTask = spawner.SpawnAsync(
            CreateProfile(),
            "Inspect the system.",
            runtimeContext: null,
            context.Invocation,
            TestContext.Current.CancellationToken);

        var run = await childProbe.ExpectMsgAsync<RunSubAgent>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(run.Scope.Authority.ApprovalBridge);

        childProbe.Reply(SuccessfulResult());
        Assert.True((await spawnTask).Success);
    }

    [Fact]
    public async Task Spawn_async_preserves_approval_bridge_for_interactive_parent()
    {
        var childProbe = CreateTestProbe("interactive-approval-child");
        var approvalBridge = new RecordingParentApprovalBridge(ParentApprovalDecision.ApprovedOnce);
        var spawner = CreateSpawner();
        var context = new ToolExecutionContext(new ToolRunScope
        {
            Session = new ToolSessionScope.Bound("interactive/subagent-parent", "/tmp/netclaw/sessions/parent"),
            Audience = TrustAudience.Personal,
            InlineOutputBudget = InlineOutputBudget.Default,
            ChannelType = ChannelType.Tui.ToWireValue(),
            SupportsInteractiveApproval = true,
            ApprovalBridge = approvalBridge,
            SpawnChildActor = (_, _, _) => Task.FromResult<object>(childProbe.Ref)
        }, ToolExecutionTimeout.Default);

        var spawnTask = spawner.SpawnAsync(
            CreateProfile(),
            "Inspect the system.",
            runtimeContext: null,
            context.Invocation,
            TestContext.Current.CancellationToken);

        var run = await childProbe.ExpectMsgAsync<RunSubAgent>(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Same(approvalBridge, run.Scope.Authority.ApprovalBridge);

        childProbe.Reply(SuccessfulResult());
        Assert.True((await spawnTask).Success);
    }

    [Fact]
    public async Task Spawn_async_ignores_definition_tool_metadata_for_runtime_tool_resolution()
    {
        var toolRegistry = new ToolRegistry();
        toolRegistry.Register(new FakeNetclawTool("inspect_context", "ok"));

        var spawner = new SubAgentSpawner(
            new SingleClientProvider(new FakeChatClient()),
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
            new WorkingContextSnapshotProvider(
                new GitWorkingContextInspector(TimeProvider.System),
                NullLogger<WorkingContextSnapshotProvider>.Instance),
            NullLogger<SubAgentSpawner>.Instance);

        var notifications = new List<SubAgentNotificationInfo>();
        var childProbe = CreateTestProbe("subagent-tool-metadata-child");
        var context = TestToolExecutionContext.CreateBound("console/subagent-parent", "/tmp/netclaw/sessions/parent", new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            SpawnChildActor = (_, _, _) => Task.FromResult<object>(childProbe.Ref),
            SubAgentActivitySink = notifications.Add,
        });

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
            context.Invocation,
            TestContext.Current.CancellationToken);

        await childProbe.ExpectMsgAsync<RunSubAgent>(cancellationToken: TestContext.Current.CancellationToken);
        childProbe.Reply(new SubAgentResult
        {
            Completion = new ChildRunCompletion.Completed(WorkingContextDelta.Empty),
            Output = "ok",
            AgentName = new AgentName(profile.Name)
        });

        var result = await spawnTask;

        Assert.True(result.Success);
        var started = Assert.Single(notifications, n => n.IsStarted);
        Assert.Equal(1, started.ToolCount);
    }

    [Fact]
    public async Task Spawn_async_returns_only_unconfirmed_git_changes_as_observed()
    {
        var projectDirectory = Path.GetFullPath(Path.Join(Path.GetTempPath(), "netclaw-spawner-context"));
        var confirmedPath = Path.GetFullPath(Path.Join(projectDirectory, "src", "Confirmed.cs"));
        var observedPath = Path.GetFullPath(Path.Join(projectDirectory, "src", "Observed.cs"));
        var snapshots = new Queue<WorkingContextSnapshot>(
        [
            new WorkingContextSnapshot
            {
                WorkingContext = WorkingContext.Empty.WithProjectDirectory(projectDirectory),
                Git = new GitWorkingContextInspection.Available(GitSnapshot(projectDirectory))
            },
            new WorkingContextSnapshot
            {
                WorkingContext = WorkingContext.Empty.WithProjectDirectory(projectDirectory),
                Git = new GitWorkingContextInspection.Available(
                    GitSnapshot(projectDirectory, "src/Confirmed.cs", "src/Observed.cs"))
            }
        ]);
        var spawner = CreateSpawner(new SequenceWorkingContextSnapshotProvider(snapshots));
        var childProbe = CreateTestProbe("working-context-child");
        var context = TestToolExecutionContext.CreateBound("console/subagent-parent", "/tmp/netclaw/sessions/parent", new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            ProjectDirectory = projectDirectory,
            SpawnChildActor = (_, _, _) => Task.FromResult<object>(childProbe.Ref)
        });

        var spawnTask = spawner.SpawnAsync(
            CreateProfile(),
            "Update the project.",
            runtimeContext: null,
            context.Invocation,
            TestContext.Current.CancellationToken);

        await childProbe.ExpectMsgAsync<RunSubAgent>(cancellationToken: TestContext.Current.CancellationToken);
        childProbe.Reply(SuccessfulResult() with
        {
            Completion = new ChildRunCompletion.Completed(new WorkingContextDelta
            {
                ProjectDirectory = projectDirectory,
                ConfirmedChangedFiles = [confirmedPath]
            })
        });

        var result = await spawnTask;

        Assert.Equal([confirmedPath], result.WorkingContext!.ConfirmedChangedFiles);
        Assert.Equal([observedPath], result.WorkingContext.ObservedChangedFiles);
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
            new WorkingContextSnapshotProvider(
                new GitWorkingContextInspector(TimeProvider.System),
                NullLogger<WorkingContextSnapshotProvider>.Instance),
            NullLogger<SubAgentSpawner>.Instance,
            sessionMetrics: metrics);

        var context = TestToolExecutionContext.CreateBound("console/subagent-parent", "/tmp/netclaw/sessions/parent", new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            SpawnChildActor = (props, name, _) => Task.FromResult<object>(Sys.ActorOf((Props)props, name)),
        });

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
            context.Invocation,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, $"Expected success but got: {result.Output}");
        // One text-only LLM call → exactly one usage record, carrying the fake's tokens.
        var call = Assert.Single(metrics.TokenUsageCalls);
        Assert.Equal((175L, 60L), call);
    }

    private static SubAgentSpawner CreateSpawner()
        => CreateSpawner(new WorkingContextSnapshotProvider(
            new GitWorkingContextInspector(TimeProvider.System),
            NullLogger<WorkingContextSnapshotProvider>.Instance));

    private static SubAgentSpawner CreateSpawner(IWorkingContextSnapshotProvider workingContextSnapshots)
    {
        var toolRegistry = new ToolRegistry();
        toolRegistry.Register(new FakeNetclawTool("inspect_context", "ok"));

        return new SubAgentSpawner(
            new SingleClientProvider(new FakeChatClient()),
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
            new StaticSystemPromptProvider("You are an inspector."),
            workingContextSnapshots,
            NullLogger<SubAgentSpawner>.Instance);
    }

    private static GitWorkingContextSnapshot GitSnapshot(string worktree, params string[] changedFiles) => new()
    {
        Worktree = worktree,
        CommonDirectory = Path.Join(worktree, ".git"),
        ChangedFiles = [.. changedFiles]
    };

    private static SubAgentProfile CreateProfile() => new()
    {
        Name = "inspector",
        Description = "Inspect the system",
        SystemPrompt = "You are an inspector.",
        ToolNames = ["inspect_context"],
        Visibility = SubAgentVisibility.UserFacing
    };

    private static SubAgentResult SuccessfulResult() => new()
    {
        Completion = new ChildRunCompletion.Completed(WorkingContextDelta.Empty),
        Output = "ok",
        AgentName = new AgentName("inspector")
    };

    private sealed class SequenceWorkingContextSnapshotProvider(Queue<WorkingContextSnapshot> snapshots)
        : IWorkingContextSnapshotProvider
    {
        public Task<WorkingContextSnapshot> CreateAsync(
            WorkingContext context,
            TrustAudience audience,
            CancellationToken cancellationToken)
            => Task.FromResult(snapshots.Dequeue());
    }
}
