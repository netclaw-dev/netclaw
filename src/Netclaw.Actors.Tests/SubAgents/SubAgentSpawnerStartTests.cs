// -----------------------------------------------------------------------
// <copyright file="SubAgentSpawnerStartTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
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
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.SubAgents;

/// <summary>
/// Own specs of the S2 implementation, not part of the specs that the test author wrote. They cover the cleanup after a
/// rejected start, which the session specs cannot observe: a journal rejection stops the session actor before the spawner
/// learns of it. The delegate in these specs plays the owner and the wrapper that forwards a prepared start.
/// </summary>
public sealed class SubAgentSpawnerStartTests : TestKit, IAsyncDisposable
{
    private static readonly TurnContextRecord Requester = new()
    {
        SessionId = new SessionId("console/start-owner"),
        TurnId = "start-turn",
        RequesterSenderId = new SenderId("alice"),
        RequesterPrincipal = PrincipalClassification.Operator,
        ChannelType = "signalr"
    };

    private readonly TestSessionTempDirectory _sessionDir = TestSessionTempDirectory.Create("netclaw-spawner-start-");

    public SubAgentSpawnerStartTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // The delegate stands in for the session owner, so the specs need no actor system features.
    }

    // TestKit stops its actor system after AfterAllAsync, so the folder is deleted after the base disposes.
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            await _sessionDir.DisposeAsync();
        }
    }

    /// <summary>
    /// The owner rejects the start after the wrapper wrote <c>task.md</c>. The spawner removes the whole run folder,
    /// including the task file and the log target that it created, and the caller sees the rejection.
    /// Planted bug: the spawner leaves the folder after a rejection.
    /// </summary>
    [Fact]
    public async Task A_start_that_the_owner_rejects_leaves_no_run_folder()
    {
        // Arrange
        string? runDirectory = null;
        var spawner = CreateSpawner(withFileRead: true);
        var context = CreateContext(async (request, _, token) =>
        {
            var prepared = Assert.IsType<PreparedChildStart>(request);
            runDirectory = prepared.RunDirectory;
            await prepared.WriteTaskFileAsync(Requester, token);
            Assert.True(File.Exists(Path.Combine(runDirectory, "task.md")), "The wrapper writes task.md before the owner decides.");
            throw new ChildStartRejectedException(SubAgentOutcomeReason.SpawnUnavailable, "The owner says no.");
        });

        // Act
        var error = await Assert.ThrowsAsync<ChildStartRejectedException>(() => spawner.StartRunAsync(
            CreateProfile(), "Summarize the report.", runtimeContext: null, context, TestContext.Current.CancellationToken,
            systemPromptOverlay: null));

        // Assert
        Assert.Equal("The owner says no.", error.Message);
        Assert.NotNull(runDirectory);
        Assert.False(Directory.Exists(runDirectory), $"The rejected start must not leave {runDirectory}.");
    }

    /// <summary>
    /// The owner knows the start under another run: a repeated start key names the earlier run. This run never started.
    /// The spawner removes its folder and keeps the folder of the earlier run.
    /// Planted bug: the spawner removes only the folder of a thrown rejection.
    /// </summary>
    [Fact]
    public async Task A_start_that_the_owner_knows_as_another_run_leaves_no_run_folder()
    {
        // Arrange
        string? runDirectory = null;
        var earlierRun = new SubAgentRunId("earlier-run");
        var spawner = CreateSpawner(withFileRead: true);
        var context = CreateContext(async (request, _, token) =>
        {
            var prepared = Assert.IsType<PreparedChildStart>(request);
            runDirectory = prepared.RunDirectory;
            await prepared.WriteTaskFileAsync(Requester, token);
            return new ChildRunAcceptance(earlierRun, new SubAgentScopeId("earlier-scope"));
        });

        // Act
        var acceptance = await spawner.StartRunAsync(
            CreateProfile(), "Summarize the report.", runtimeContext: null, context, TestContext.Current.CancellationToken,
            systemPromptOverlay: null);

        // Assert
        Assert.Equal(earlierRun, acceptance.RunId);
        Assert.NotNull(runDirectory);
        Assert.False(Directory.Exists(runDirectory), $"The duplicate start must not leave {runDirectory}.");
    }

    /// <summary>
    /// A cancelled or timed-out start is unclear: the owner may have journaled the acceptance. The spawner never removes
    /// the folder of a child that can be live.
    /// Planted bug: the spawner removes the folder after any failure.
    /// </summary>
    [Fact]
    public async Task A_start_with_an_unclear_outcome_keeps_the_run_folder()
    {
        // Arrange
        string? runDirectory = null;
        var spawner = CreateSpawner(withFileRead: true);
        var context = CreateContext(async (request, _, token) =>
        {
            var prepared = Assert.IsType<PreparedChildStart>(request);
            runDirectory = prepared.RunDirectory;
            await prepared.WriteTaskFileAsync(Requester, token);
            throw new OperationCanceledException("The ask timed out.");
        });

        // Act
        await Assert.ThrowsAsync<OperationCanceledException>(() => spawner.StartRunAsync(
            CreateProfile(), "Summarize the report.", runtimeContext: null, context, TestContext.Current.CancellationToken,
            systemPromptOverlay: null));

        // Assert
        Assert.NotNull(runDirectory);
        Assert.True(File.Exists(Path.Combine(runDirectory, "task.md")));
    }

    /// <summary>
    /// <c>task.md</c> names the agent, the requester sender and principal, and the brief word for word.
    /// Planted bug: the file leaves out the requester or changes the brief.
    /// </summary>
    [Fact]
    public async Task The_task_file_holds_the_agent_the_requester_and_the_brief()
    {
        // Arrange
        string? taskText = null;
        var spawner = CreateSpawner(withFileRead: true);
        var context = CreateContext(async (request, _, token) =>
        {
            var prepared = Assert.IsType<PreparedChildStart>(request);
            await prepared.WriteTaskFileAsync(Requester, token);
            taskText = await File.ReadAllTextAsync(Path.Combine(prepared.RunDirectory, "task.md"), token);
            return new ChildRunAcceptance(prepared.RunId, prepared.Execution.Scope.ScopeId);
        });

        // Act
        await spawner.StartRunAsync(
            CreateProfile(), "Summarize the report.\nKeep the table.", runtimeContext: "Quarterly numbers.", context,
            TestContext.Current.CancellationToken, systemPromptOverlay: null);

        // Assert
        Assert.NotNull(taskText);
        Assert.Contains("agent: inspector", taskText, StringComparison.Ordinal);
        Assert.Contains("requester: alice", taskText, StringComparison.Ordinal);
        Assert.Contains("principal: Operator", taskText, StringComparison.Ordinal);
        Assert.Contains("Summarize the report.\nKeep the table.", taskText.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("Quarterly numbers.", taskText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A requester whose policy has no <c>file_read</c> could not read the result file, so the spawner refuses the start.
    /// The refusal names <c>file_read</c>, creates no run folder, and never calls the owner.
    /// Planted bug: the spawner starts the child without the check.
    /// </summary>
    [Fact]
    public async Task A_start_is_refused_when_the_requester_cannot_read_files()
    {
        // Arrange
        var ownerCalled = false;
        var spawner = CreateSpawner(withFileRead: false);
        var context = CreateContext((_, _, _) =>
        {
            ownerCalled = true;
            return Task.FromResult<object>(new object());
        });

        // Act
        var error = await Assert.ThrowsAsync<ChildStartRejectedException>(() => spawner.StartRunAsync(
            CreateProfile(), "Summarize the report.", runtimeContext: null, context, TestContext.Current.CancellationToken,
            systemPromptOverlay: null));

        // Assert
        Assert.Contains("file_read", error.Message, StringComparison.Ordinal);
        Assert.Equal(SubAgentOutcomeReason.NoResultReader, error.Reason);
        Assert.False(ownerCalled);
        var subagents = Path.Combine(_sessionDir.Path, "sessions");
        Assert.False(Directory.Exists(subagents) && Directory.EnumerateDirectories(
            subagents, "subagents", SearchOption.AllDirectories).Any(), "A refused start must not create a run folder.");
    }

    private ToolInvocationContext CreateContext(Func<object, string, CancellationToken, Task<object>> spawn)
        => TestToolExecutionContext.CreateBound("console/start-owner", _sessionDir.Path, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Personal,
            SpawnChildActor = spawn
        }).Invocation;

    private static SubAgentSpawner CreateSpawner(bool withFileRead)
    {
        var toolRegistry = new ToolRegistry();
        toolRegistry.Register(new FakeNetclawTool("inspect_context", "ok"));
        if (withFileRead)
            toolRegistry.Register(new FakeNetclawTool(FileReadTool.ToolName, "ok"));

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
            new WorkingContextSnapshotProvider(
                new GitWorkingContextInspector(TimeProvider.System),
                NullLogger<WorkingContextSnapshotProvider>.Instance),
            NullLogger<SubAgentSpawner>.Instance,
            new SessionConfig());
    }

    private static SubAgentProfile CreateProfile() => new()
    {
        Name = "inspector",
        Description = "Inspect the system",
        SystemPrompt = "You are an inspector.",
        ToolNames = ["inspect_context"],
        Visibility = SubAgentVisibility.UserFacing
    };

    private sealed class SingleClientProvider(IChatClient client) : IChatClientProvider
    {
        public IChatClient GetClient(ModelRole role) => client;
    }
}
