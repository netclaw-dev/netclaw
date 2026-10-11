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
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
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

public sealed class SubAgentSpawnerTests : TestKit, IAsyncDisposable
{
    private readonly TestSessionTempDirectory _parentSessionDir =
        TestSessionTempDirectory.Create("netclaw-spawner-session-");
    private readonly TestSessionTempDirectory _testProjectDir =
        TestSessionTempDirectory.Create("netclaw-spawner-project-");

    public SubAgentSpawnerTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // No hosting or persistence needed; the probe stands in for the child actor.
    }

    // TestKit stops the actor system only after AfterAllAsync returns, and it fails
    // the test when AfterAllAsync takes more than 5 seconds. Delete the directories
    // after TestKit has disposed, and not in AfterAllAsync.
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            await _parentSessionDir.DisposeAsync();
            await _testProjectDir.DisposeAsync();
        }
    }

    [Fact]
    public async Task Background_preparation_propagates_parent_resolved_cwd_on_run_message()
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            ShellSyntaxTree.PwshDialect.PowerShell7);
        var toolRegistry = new ToolRegistry();
        toolRegistry.Register(new FakeNetclawTool("inspect_context", "ok"));

        var spawner = new SubAgentSpawner(
            new SingleClientProvider(new FakeChatClient()),
            toolRegistry,
            new ToolAccessPolicy(new NetclawPaths(),
                new ToolConfig(),
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(environment),
                new ToolPathPolicy(environment, [])),
            approvalService: null,
            new StaticSystemPromptProvider("You are a summarizer."),
            new WorkingContextSnapshotProvider(
                new GitWorkingContextInspector(TimeProvider.System),
                NullLogger<WorkingContextSnapshotProvider>.Instance,
                environment),
            NullLogger<SubAgentSpawner>.Instance);

        var childProbe = CreateTestProbe("subagent-child");
        var context = TestToolExecutionContext.CreateBound("console/subagent-parent", _parentSessionDir.Path, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            ProjectDirectory = _testProjectDir.Path,
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

        var prepared = Assert.IsType<SubAgentSpawner.ChildPreparation.Ready>(await spawner.PrepareRunAsync(
            profile,
            "Summarize the repo.",
            runtimeContext: null,
            context.Invocation,
            TestContext.Current.CancellationToken, systemPromptOverlay: null)).Run;

        var run = prepared.Execution;
        var bound = Assert.IsType<ToolSessionScope.Bound>(run.Scope.Authority.Session);
        Assert.Equal(_parentSessionDir.Path, bound.SessionDirectory);
        Assert.Equal(_testProjectDir.Path, run.Scope.Authority.ProjectDirectory);
        Assert.Equal(_testProjectDir.Path, run.Scope.Authority.InheritedCwd);
        Assert.Same(environment, run.Scope.InitialWorkingSnapshot.ShellEnvironment);
    }

    [Theory]
    [InlineData(ChannelType.Headless)]
    [InlineData(ChannelType.Reminder)]
    [InlineData(ChannelType.Webhook)]
    public async Task Background_preparation_does_not_bridge_approval_for_non_interactive_parent(ChannelType channelType)
    {
        var childProbe = CreateTestProbe($"non-interactive-{channelType}-child");
        var spawner = CreateSpawner();
        var context = TestToolExecutionContext.CreateBound(
            "automation/subagent-parent",
            _parentSessionDir.Path,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                ChannelType = channelType.ToWireValue(),
                InteractiveApproval = new InteractiveApprovalCapability.Unavailable(),
                SpawnChildActor = (_, _, _) => Task.FromResult<object>(childProbe.Ref)
            });

        var prepared = Assert.IsType<SubAgentSpawner.ChildPreparation.Ready>(await spawner.PrepareRunAsync(
            CreateProfile(),
            "Inspect the system.",
            runtimeContext: null,
            context.Invocation,
            TestContext.Current.CancellationToken, systemPromptOverlay: null)).Run;

        var run = prepared.Execution;
        Assert.IsType<InteractiveApprovalCapability.Unavailable>(run.Scope.Authority.InteractiveApproval);
    }

    [Fact]
    public async Task Background_preparation_preserves_approval_bridge_for_interactive_parent()
    {
        var childProbe = CreateTestProbe("interactive-approval-child");
        var approvalBridge = new RecordingParentApprovalBridge(ConsentAnswer.Once.Instance);
        var spawner = CreateSpawner();
        var context = TestToolExecutionContext.CreateBound(
            "interactive/subagent-parent",
            _parentSessionDir.Path,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                ChannelType = ChannelType.Tui.ToWireValue(),
                InteractiveApproval = new InteractiveApprovalCapability.Available(approvalBridge),
                SpawnChildActor = (_, _, _) => Task.FromResult<object>(childProbe.Ref)
            });

        var prepared = Assert.IsType<SubAgentSpawner.ChildPreparation.Ready>(await spawner.PrepareRunAsync(
            CreateProfile(),
            "Inspect the system.",
            runtimeContext: null,
            context.Invocation,
            TestContext.Current.CancellationToken, systemPromptOverlay: null)).Run;

        var run = prepared.Execution;
        var available = Assert.IsType<InteractiveApprovalCapability.Available>(
            run.Scope.Authority.InteractiveApproval);
        Assert.Same(approvalBridge, available.Bridge);
    }

    [Fact]
    public async Task Background_preparation_ignores_definition_tool_metadata_for_runtime_tool_resolution()
    {
        var toolRegistry = new ToolRegistry();
        toolRegistry.Register(new FakeNetclawTool("inspect_context", "ok"));

        var spawner = new SubAgentSpawner(
            new SingleClientProvider(new FakeChatClient()),
            toolRegistry,
            new ToolAccessPolicy(new NetclawPaths(),
                new ToolConfig(),
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])),
            approvalService: null,
            new StaticSystemPromptProvider("You are a summarizer."),
            new WorkingContextSnapshotProvider(
                new GitWorkingContextInspector(TimeProvider.System),
                NullLogger<WorkingContextSnapshotProvider>.Instance),
            NullLogger<SubAgentSpawner>.Instance);

        var notifications = new List<SubAgentNotificationInfo>();
        var childProbe = CreateTestProbe("subagent-tool-metadata-child");
        var context = TestToolExecutionContext.CreateBound("console/subagent-parent", _parentSessionDir.Path, new TestToolExecutionContextOptions
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

        var prepared = Assert.IsType<SubAgentSpawner.ChildPreparation.Ready>(await spawner.PrepareRunAsync(
            profile,
            "Summarize the repo.",
            runtimeContext: null,
            context.Invocation,
            TestContext.Current.CancellationToken, systemPromptOverlay: null)).Run;

        Assert.Equal(1, prepared.ToolCount);
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task Terminal_enrichment_returns_only_unconfirmed_git_changes_as_observed()
    {
        await using var spawnerContextDir = TestSessionTempDirectory.Create("netclaw-spawner-");
        var projectDirectory = spawnerContextDir.Path;
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
        var initial = snapshots.Dequeue();
        var result = await SubAgentSpawner.EnrichWorkingContextResultAsync(SuccessfulResult() with
        {
            Completion = new ChildRunCompletion.Completed(new WorkingContextDelta
            {
                ProjectDirectory = projectDirectory,
                ConfirmedChangedFiles = [confirmedPath]
            })
        }, initial, TrustAudience.Personal, new SequenceWorkingContextSnapshotProvider(snapshots),
            TestContext.Current.CancellationToken);

        Assert.Equal([confirmedPath], result.WorkingContext!.ConfirmedChangedFiles);
        Assert.Equal([observedPath], result.WorkingContext.ObservedChangedFiles);
    }

    [Fact]
    public async Task Initial_snapshot_cancellation_rejects_before_owner_acceptance()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var spawner = CreateSpawner(new CancelledWorkingContextSnapshotProvider());
        var childSpawned = false;
        var notifications = new List<SubAgentNotificationInfo>();
        var context = TestToolExecutionContext.CreateBound(
            "console/subagent-parent",
            _parentSessionDir.Path,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                SpawnChildActor = (_, _, _) =>
                {
                    childSpawned = true;
                    return Task.FromResult<object>(TestActor);
                },
                SubAgentActivitySink = notifications.Add
            });

        var result = await spawner.PrepareRunAsync(
            CreateProfile(),
            "Inspect the system.",
            runtimeContext: null,
            context.Invocation,
            cancellation.Token,
            systemPromptOverlay: null);

        Assert.IsType<ChildRunCompletion.Cancelled>(Assert.IsType<SubAgentSpawner.ChildPreparation.Rejected>(result).Result.Response.Completion);
        Assert.False(childSpawned);
        Assert.Empty(notifications);
    }

    [Fact]
    public async Task Initial_snapshot_failure_rejects_before_owner_acceptance()
    {
        var spawner = CreateSpawner(new FailedWorkingContextSnapshotProvider());
        var childSpawned = false;
        var context = TestToolExecutionContext.CreateBound(
            "console/subagent-parent",
            _parentSessionDir.Path,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                SpawnChildActor = (_, _, _) =>
                {
                    childSpawned = true;
                    return Task.FromResult<object>(TestActor);
                }
            });

        var result = await spawner.PrepareRunAsync(
            CreateProfile(),
            "Inspect the system.",
            runtimeContext: null,
            context.Invocation,
            TestContext.Current.CancellationToken,
            systemPromptOverlay: null);

        var failed = Assert.IsType<ChildRunCompletion.Failed>(Assert.IsType<SubAgentSpawner.ChildPreparation.Rejected>(result).Result.Response.Completion);
        Assert.Equal(SubAgentOutcomeReason.SpawnError, failed.FailureReason);
        Assert.False(childSpawned);
    }

    [Fact]
    public async Task Fatal_initial_snapshot_failure_propagates_before_owner_acceptance()
    {
        var spawner = CreateSpawner(new FatalWorkingContextSnapshotProvider());
        var childSpawned = false;
        var context = TestToolExecutionContext.CreateBound(
            "console/subagent-parent",
            _parentSessionDir.Path,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                SpawnChildActor = (_, _, _) =>
                {
                    childSpawned = true;
                    return Task.FromResult<object>(TestActor);
                }
            });

        await Assert.ThrowsAsync<OutOfMemoryException>(() => spawner.PrepareRunAsync(
            CreateProfile(),
            "Inspect the system.",
            runtimeContext: null,
            context.Invocation,
            TestContext.Current.CancellationToken,
            systemPromptOverlay: null));

        Assert.False(childSpawned);
    }

    [Fact]
    public async Task Spawned_sub_agent_bills_its_llm_calls_to_session_metrics()
    {
        // The actual prepared Props must preserve the process-wide metrics sink.
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
            new ToolAccessPolicy(new NetclawPaths(),
                new ToolConfig(),
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])),
            approvalService: null,
            new StaticSystemPromptProvider("You are a summarizer."),
            new WorkingContextSnapshotProvider(
                new GitWorkingContextInspector(TimeProvider.System),
                NullLogger<WorkingContextSnapshotProvider>.Instance),
            NullLogger<SubAgentSpawner>.Instance,
            sessionMetrics: metrics);

        var context = TestToolExecutionContext.CreateBound("console/subagent-parent", _parentSessionDir.Path, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            SpawnChildActor = (_, _, _) => Task.FromResult<object>(TestActor),
        });

        var profile = new SubAgentProfile
        {
            Name = "summarizer",
            Description = "Summarize content",
            SystemPrompt = "You are a summarizer.",
            ToolNames = ["inspect_context"],
            Visibility = SubAgentVisibility.UserFacing
        };

        var prepared = Assert.IsType<SubAgentSpawner.ChildPreparation.Ready>(await spawner.PrepareRunAsync(
            profile, "Summarize the repo.", null, context.Invocation,
            TestContext.Current.CancellationToken, null)).Run;
        var owner = Sys.ActorOf(Props.Create(() => new MetricsRunOwner(prepared, TestActor)));
        owner.Tell("start", TestActor);
        var terminal = await ExpectMsgAsync<BackgroundChildTerminal>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(terminal.Result.Success);
        owner.Tell(new BackgroundChildTerminalAck(prepared.RunId), TestActor);
        // One text-only LLM call → exactly one usage record, carrying the fake's tokens.
        var call = Assert.Single(metrics.TokenUsageCalls);
        Assert.Equal((175L, 60L), call);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AuthorityRegression_Child_spawner_retains_team_file_authority(bool legacy, bool partial)
    {
        using var directory = new DisposableTempDir();
        var paths = new NetclawPaths(directory.Path);
        paths.EnsureDirectoriesExist();
        var parentRoot = Path.Combine(paths.SessionsDirectory, "parent");
        var storage = legacy
            ? SessionStoragePaths.CreateLegacy(parentRoot, paths.SessionLogsDirectory, "parent")
            : SessionStoragePaths.CreateVersion2(new SessionStorageEnvelopeRoot(parentRoot));
        Directory.CreateDirectory(storage.SessionDirectory.Value);
        Directory.CreateDirectory(Path.GetDirectoryName(storage.LogPath.Value)!);
        await File.WriteAllTextAsync(storage.LogPath.Value, "parent-marker", TestContext.Current.CancellationToken);
        var sibling = Path.Combine(paths.SessionsDirectory, "foreign", "hidden.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        await File.WriteAllTextAsync(sibling, "foreign-marker", TestContext.Current.CancellationToken);
        var config = new ToolConfig();
        var protectedPaths = new ToolPathPolicy([]);
        var pathPolicy = new PathAccessPolicy(config, paths, protectedPaths);
        var read = new FileReadTool(config, pathPolicy);
        var registry = new ToolRegistry();
        registry.Register(read);
        var spawner = new SubAgentSpawner(
            new SingleClientProvider(new FakeChatClient()), registry,
            new ToolAccessPolicy(paths, config,
                new EffectivePolicyDefaults(DeploymentPosture.Personal, TrustAudience.Personal, ShellExecutionMode.HostAllowed, UsedStrictFallback: false),
                new ShellCommandPolicy(), protectedPaths),
            approvalService: null,
            new StaticSystemPromptProvider("Read the supplied file."),
            new WorkingContextSnapshotProvider(new GitWorkingContextInspector(TimeProvider.System), NullLogger<WorkingContextSnapshotProvider>.Instance),
            NullLogger<SubAgentSpawner>.Instance);
        var probe = CreateTestProbe("authority-child");
        var parent = TestToolExecutionContext.CreateBoundWithStorage("slack/parent", storage, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            ChannelType = "slack",
            SpawnChildActor = (_, _, _) => Task.FromResult<object>(probe.Ref)
        });
        var profile = new SubAgentProfile
        {
            Name = "reader", Description = "Read a file", SystemPrompt = "Read the supplied file.",
            ToolNames = ["file_read"], Visibility = SubAgentVisibility.UserFacing
        };
        var prepared = Assert.IsType<SubAgentSpawner.ChildPreparation.Ready>(await spawner.PrepareRunAsync(
            profile, "Read the supplied file.", null, parent.Invocation, TestContext.Current.CancellationToken, null)).Run;
        var run = prepared.Execution;
        Assert.Equal(parent.Audience, run.Scope.Authority.Audience);
        Assert.Equal(parent.Boundary, run.Scope.Authority.Boundary);
        var childStorage = Assert.IsType<ToolSessionScope.Bound>(run.Scope.Authority.Session).Storage;
        Assert.Equal(storage.SessionDirectory, childStorage.SessionDirectory);
        Assert.Equal(storage.Binding, childStorage.Binding);
        foreach (var (target, allowed) in new[] { (storage.LogPath.Value, !legacy), (sibling, false), (childStorage.LogPath.Value, true) })
        {
            var child = new ToolExecutionContext(run.Scope.Authority, ToolExecutionTimeout.Default);
            var result = await read.ExecuteAsync(ToolInput.Create("Path", target), child, TestContext.Current.CancellationToken);
            Assert.Equal(allowed ? ToolInvocationOutcomeCategory.Success : ToolInvocationOutcomeCategory.AccessDenied, child.Receipt?.Category);
            Assert.DoesNotContain("foreign-marker", result);
        }
        Directory.CreateDirectory(childStorage.ArtifactDirectory.Value);
        var artifact = Path.Combine(childStorage.ArtifactDirectory.Value, "result.txt");
        await File.WriteAllTextAsync(artifact, "child-artifact", TestContext.Current.CancellationToken);
        var actorResult = new SubAgentResult
        {
            Completion = partial
                ? new ChildRunCompletion.Partial(SubAgentOutcomeReason.ToolIterationBudgetExhausted, WorkingContextDelta.Empty)
                : new ChildRunCompletion.Completed(WorkingContextDelta.Empty),
            Output = "child-summary", AgentName = new AgentName("reader")
        };
        var completed = new EnrichedChildRunResult.SuccessfulRun(actorResult,
            new EnrichedChildRunResult.RunLocations(childStorage.LogPath, childStorage.ArtifactDirectory)).ToProtocolResult();
        Assert.True(completed.Success);
        Assert.Equal("child-summary", completed.Output);
        Assert.Equal(childStorage.LogPath.Value, completed.LogPath);
        Assert.Equal(childStorage.ArtifactDirectory.Value, completed.ArtifactDirectory);
        var resultText = await read.ExecuteAsync(ToolInput.Create("Path", artifact), parent, TestContext.Current.CancellationToken);
        Assert.Contains("child-artifact", resultText);
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
            new ToolAccessPolicy(new NetclawPaths(),
                new ToolConfig(),
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false),
                new ShellCommandPolicy(),
                new ToolPathPolicy([])),
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

    private sealed class MetricsRunOwner : ReceiveActor
    {
        public MetricsRunOwner(PreparedChildRun prepared, IActorRef observer)
        {
            var child = Context.ActorOf(prepared.Props);
            Receive<string>(_ => child.Tell(new RunBackgroundSubAgent(
                prepared.RunId, prepared.Execution, new ChildRunDispatch()), Self));
            Receive<BackgroundChildTerminal>(terminal => observer.Tell(terminal, Self));
            Receive<BackgroundChildTerminalAck>(ack => child.Tell(ack, Self));
        }
    }

    private static WorkingContextSnapshot EmptySnapshot() => new()
    {
        WorkingContext = WorkingContext.Empty,
        Git = new GitWorkingContextInspection.Skipped()
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

    private sealed class CancelledWorkingContextSnapshotProvider : IWorkingContextSnapshotProvider
    {
        public Task<WorkingContextSnapshot> CreateAsync(
            WorkingContext context,
            TrustAudience audience,
            CancellationToken cancellationToken) => Task.FromCanceled<WorkingContextSnapshot>(cancellationToken);
    }

    private sealed class FailedWorkingContextSnapshotProvider : IWorkingContextSnapshotProvider
    {
        public Task<WorkingContextSnapshot> CreateAsync(
            WorkingContext context,
            TrustAudience audience,
            CancellationToken cancellationToken) =>
            Task.FromException<WorkingContextSnapshot>(new IOException("snapshot failed"));
    }

    private sealed class FatalWorkingContextSnapshotProvider : IWorkingContextSnapshotProvider
    {
        public Task<WorkingContextSnapshot> CreateAsync(
            WorkingContext context,
            TrustAudience audience,
            CancellationToken cancellationToken) =>
            Task.FromException<WorkingContextSnapshot>(new OutOfMemoryException("snapshot failed fatally"));
    }

}
