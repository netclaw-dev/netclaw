// -----------------------------------------------------------------------
// <copyright file="ShellApprovalLifecycleIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Akka.Actor;
using Akka.Hosting;
using Akka.TestKit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Tests.Jobs;
using Netclaw.Actors.Tests.Tools;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

[Collection(BackgroundJobProcessCollection.Name)]
public sealed class ShellApprovalLifecycleIntegrationTests : LlmSessionTestBase
{
    private static readonly DateTimeOffset ReceivedAt =
        new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeChatClient _chatClient = new();
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        $".netclaw-approval-lifecycle-{Guid.NewGuid():N}");
    private NetclawPaths _paths = null!;
    private string _sessionDirectory = null!;
    private ShellExecutionEnvironment _environment = null!;
    private ToolApprovalStore _store = null!;

    public ShellApprovalLifecycleIntegrationTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        _paths = new NetclawPaths(_root, Path.Combine(_root, "workspaces"));
        _paths.EnsureDirectoriesExist();
        _sessionDirectory = Directory.CreateDirectory(
            Path.Combine(_root, "session-boundary")).FullName;
        _environment = TestShellEnvironment.Current;

        var config = new ToolConfig
        {
            ShellMode = ShellExecutionMode.HostAllowed,
            AudienceProfiles =
                ToolAudienceProfileDefaults.CreateProfilesForPosture(
                    DeploymentPosture.Personal)
        };
        var commandPolicy = new ShellCommandPolicy(_environment);
        var pathPolicy = new ToolPathPolicy(_environment, []);
        var policy = new ToolAccessPolicy(
            _paths,
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            commandPolicy,
            pathPolicy,
            safeVerbs: SafeVerbLoader.Load(
                _environment.Platform == ShellPlatform.Windows));
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(policy);
        var approvalShell = _environment.Grammar == ShellGrammar.Bash
            ? ApprovalShell.Bash
            : ApprovalShell.PowerShell;
        _store = new ToolApprovalStore(
            _paths.ToolApprovalsPath,
            TimeProvider.System,
            new ApprovalStoreMigrationContext(approvalShell),
            lockTimeout: TimeSpan.Zero);

        services.AddSingleton(_paths);
        services.AddSingleton(_environment);
        services.AddSingleton(config);
        services.AddSingleton(policy);
        services.AddSingleton(registry);
        services.AddSingleton(_store);
        services.AddSingleton<IToolApprovalService, AkkaToolApprovalService>();
        services.AddSingleton(provider =>
            new DispatchingToolExecutor(
                registry,
                policy,
                provider.GetRequiredService<IToolApprovalService>()));
        services.AddSingleton<IToolExecutor>(provider =>
            provider.GetRequiredService<DispatchingToolExecutor>());
        services.AddSingleton<IChatClientProvider>(
            new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            IdleTimeout = TimeSpan.Zero,
            Tuning = new SessionTuning
            {
                SnapshotInterval = 1,
                TitleGenerationInterval = 0,
                MaxInlineToolResultChars = 200,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(
            new StaticSystemPromptProvider(
                "You are a test assistant with tools."));
    }

    [Fact]
    public async Task Once_runs_one_native_process_and_rejects_a_stale_reply()
    {
        var project = CreateDirectory("once-project");
        var journey = await StartTurnAsync(
            "approval-lifecycle/once",
            "call-once",
            project);

        var reply = await ReplyAsync(
            journey,
            ApprovalOptionKeys.ApproveOnceKey);

        Assert.IsType<CommandAck>(reply);
        await ExpectCompletedAsync(journey.Subscriber);
        Assert.Equal("x", ReadMarker(project));
        Assert.Empty(ReadPersistentEntries());

        var later = await EvaluateOutcomeAsync(
            "approval-lifecycle/once",
            project);
        Assert.Equal(ApprovalOutcome.RequiresApproval, later);

        var staleReply = await ReplyAsync(
            journey,
            ApprovalOptionKeys.ApproveOnceKey,
            requireOfferedOption: false);
        var nack = Assert.IsType<CommandNack>(staleReply);
        Assert.Equal(ApprovalNackReasons.PromptExpired, nack.Reason);
        Assert.Equal("x", ReadMarker(project));
    }

    [Fact]
    public async Task Session_scope_applies_only_to_the_current_session()
    {
        var project = CreateDirectory("session-project");
        var outside = CreateDirectory("session-outside");
        var journey = await StartTurnAsync(
            "approval-lifecycle/session",
            "call-session",
            project);

        var reply = await ReplyAsync(
            journey,
            ApprovalOptionKeys.ApproveSessionKey);

        Assert.IsType<CommandAck>(reply);
        await ExpectCompletedAsync(journey.Subscriber);
        Assert.Equal("x", ReadMarker(project));
        Assert.Empty(ReadPersistentEntries());
        Assert.Equal(
            ApprovalOutcome.Allowed,
            await EvaluateOutcomeAsync(journey.SessionId.Value, outside));
        Assert.Equal(
            ApprovalOutcome.RequiresApproval,
            await EvaluateOutcomeAsync("approval-lifecycle/session-other", project));
        Assert.Equal(
            ApprovalOutcome.RequiresApproval,
            await EvaluateOutcomeAsync("approval-lifecycle/session-other", outside));
    }

    [Fact]
    public async Task Folder_scope_applies_to_children_for_other_sessions()
    {
        var project = CreateDirectory("folder-project");
        var child = Directory.CreateDirectory(Path.Combine(project, "child")).FullName;
        var outside = CreateDirectory("folder-outside");
        var journey = await StartTurnAsync(
            "approval-lifecycle/folder",
            "call-folder",
            project);

        var reply = await ReplyAsync(
            journey,
            ApprovalOptionKeys.ApproveAlwaysKey);

        Assert.IsType<CommandAck>(reply);
        await ExpectCompletedAsync(journey.Subscriber);
        Assert.Equal("x", ReadMarker(project));
        var entry = Assert.Single(ReadPersistentEntries());
        Assert.True(PathUtility.AreEquivalentPaths(project, entry.Directory!));
        Assert.Equal(
            ApprovalOutcome.Allowed,
            await EvaluateOutcomeAsync("approval-lifecycle/folder-other", child));
        Assert.Equal(
            ApprovalOutcome.RequiresApproval,
            await EvaluateOutcomeAsync("approval-lifecycle/folder-other", outside));
    }

    [Fact]
    public async Task Repository_scope_applies_to_another_worktree()
    {
        var main = CreateDirectory("repository-main");
        var sibling = Path.Combine(_root, "repository-sibling");
        var outside = CreateDirectory("repository-outside");
        await CreateRepositoryWithWorktreeAsync(main, sibling);
        var journey = await StartTurnAsync(
            "approval-lifecycle/repository",
            "call-repository",
            sibling);

        var reply = await ReplyAsync(
            journey,
            ApprovalOptionKeys.ApproveRepositoryKey);

        Assert.IsType<CommandAck>(reply);
        await ExpectCompletedAsync(journey.Subscriber);
        Assert.Equal("x", ReadMarker(sibling));
        var entry = Assert.Single(ReadPersistentEntries());
        Assert.True(PathUtility.AreEquivalentPaths(
            Path.Combine(main, ".git"),
            entry.Repository!));
        Assert.Equal(
            ApprovalOutcome.Allowed,
            await EvaluateOutcomeAsync("approval-lifecycle/repository-other", main));
        Assert.Equal(
            ApprovalOutcome.RequiresApproval,
            await EvaluateOutcomeAsync("approval-lifecycle/repository-other", outside));
    }

    [Fact]
    public async Task Global_scope_applies_outside_the_original_folder()
    {
        var project = CreateDirectory("global-project");
        var outside = CreateDirectory("global-outside");
        var journey = await StartTurnAsync(
            "approval-lifecycle/global",
            "call-global",
            project);

        var reply = await ReplyAsync(
            journey,
            ApprovalOptionKeys.ApproveEverywhereKey);

        Assert.IsType<CommandAck>(reply);
        await ExpectCompletedAsync(journey.Subscriber);
        Assert.Equal("x", ReadMarker(project));
        var entry = Assert.Single(ReadPersistentEntries());
        Assert.Null(entry.Directory);
        Assert.Null(entry.Repository);
        Assert.Equal(
            ApprovalOutcome.Allowed,
            await EvaluateOutcomeAsync("approval-lifecycle/global-other", outside));
    }

    [Fact]
    public async Task Denial_creates_no_grant_and_runs_no_process()
    {
        var project = CreateDirectory("deny-project");
        var journey = await StartTurnAsync(
            "approval-lifecycle/deny",
            "call-deny",
            project);

        var reply = await ReplyAsync(
            journey,
            ApprovalOptionKeys.DenyKey);

        Assert.IsType<CommandAck>(reply);
        var result = await journey.Subscriber.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("approval_denied_by_user", result.Result, StringComparison.Ordinal);
        await journey.Subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        var completed = await journey.Subscriber.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
        Assert.False(File.Exists(MarkerPath(project)));
        Assert.Empty(ReadPersistentEntries());
        Assert.Equal(
            ApprovalOutcome.RequiresApproval,
            await EvaluateOutcomeAsync(journey.SessionId.Value, project));
    }

    [Fact]
    public async Task Failed_store_write_rejects_the_reply_and_runs_no_process()
    {
        var project = CreateDirectory("failed-store-project");
        var journey = await StartTurnAsync(
            "approval-lifecycle/failed-store",
            "call-failed-store",
            project);
        Directory.CreateDirectory(_paths.ToolApprovalsPath);

        var reply = await ReplyAsync(
            journey,
            ApprovalOptionKeys.ApproveAlwaysKey);

        var nack = Assert.IsType<CommandNack>(reply);
        Assert.Equal(ApprovalNackReasons.PersistFailed, nack.Reason);
        var completed = await journey.Subscriber.FishForMessageAsync<TurnCompleted>(
            _ => true,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Failed, completed.Outcome);
        Assert.False(File.Exists(MarkerPath(project)));
        Directory.Delete(_paths.ToolApprovalsPath);
        Assert.Equal(
            ApprovalOutcome.RequiresApproval,
            await EvaluateOutcomeAsync(journey.SessionId.Value, project));
        Assert.Equal(
            ApprovalOutcome.RequiresApproval,
            await EvaluateOutcomeAsync("approval-lifecycle/failed-store-other", project));
        Assert.Empty(ReadPersistentEntries());
    }

    [Fact]
    public async Task Repository_identity_change_rejects_the_reply()
    {
        var main = CreateDirectory("changed-main");
        var sibling = Path.Combine(_root, "changed-sibling");
        var unrelated = CreateDirectory("changed-unrelated");
        await CreateRepositoryWithWorktreeAsync(main, sibling);
        await RunGitAsync(unrelated, "init", "--quiet");
        var journey = await StartTurnAsync(
            "approval-lifecycle/changed-repository",
            "call-changed-repository",
            sibling);
        Assert.NotNull(journey.Request.RepositoryCommonDirectory);
        SwapWorktreeRegistration(sibling, unrelated);

        var reply = await ReplyAsync(
            journey,
            ApprovalOptionKeys.ApproveRepositoryKey);

        var nack = Assert.IsType<CommandNack>(reply);
        Assert.Equal(ApprovalNackReasons.PersistFailed, nack.Reason);
        var completed = await journey.Subscriber.FishForMessageAsync<TurnCompleted>(
            _ => true,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Failed, completed.Outcome);
        Assert.False(File.Exists(MarkerPath(sibling)));
        Assert.Empty(ReadPersistentEntries());
        Assert.Equal(
            ApprovalOutcome.RequiresApproval,
            await EvaluateOutcomeAsync(journey.SessionId.Value, main));
    }

    protected override async Task AfterAllAsync()
    {
        await base.AfterAllAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string MarkerCommand => _environment.Grammar == ShellGrammar.PowerShell
        ? "Add-Content -NoNewline -Path launch-count.txt -Value x"
        : "printf x >> launch-count.txt";

    private async Task<ApprovalJourney> StartTurnAsync(
        string sessionValue,
        string callId,
        string workingDirectory)
    {
        _chatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(
                callId,
                ShellTool.ToolName,
                ToolInput.Create(
                    "Command", MarkerCommand,
                    "WorkingDirectory", workingDirectory,
                    "_rationale", "Verify the approval lifecycle."))
        ];
        var sessionId = new SessionId(sessionValue);
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe($"approval-{Guid.NewGuid():N}");

        await manager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(
            cancellationToken: TestContext.Current.CancellationToken);
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run the test command.",
            Source = RequesterSource()
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        var request = await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(callId, request.CallId.Value);
        Assert.Equal(ShellTool.ToolName, request.ToolName.Value);
        return new ApprovalJourney(sessionId, manager, subscriber, request);
    }

    private static async Task<ISessionResponse> ReplyAsync(
        ApprovalJourney journey,
        ApprovalOptionKey option,
        bool requireOfferedOption = true)
    {
        if (requireOfferedOption)
        {
            Assert.Contains(
                journey.Request.Options,
                offered => offered.Key == option);
        }

        return await journey.Manager.Ask<ISessionResponse>(
            new ToolInteractionResponse
            {
                SessionId = journey.SessionId,
                CallId = journey.Request.CallId,
                SelectedKey = option,
                SenderId = new SenderId("local-user")
            },
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
    }

    private static async Task ExpectCompletedAsync(TestProbe subscriber)
    {
        var result = await subscriber.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(result.FailureCode);
        await subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
    }

    private async Task<ApprovalOutcome> EvaluateOutcomeAsync(
        string sessionValue,
        string workingDirectory)
    {
        var executor = Host.Services.GetRequiredService<DispatchingToolExecutor>();
        var context = TestToolExecutionContext.CreateBound(
            sessionValue,
            _sessionDirectory,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                ProjectDirectory = workingDirectory,
                InteractiveApproval =
                    TestToolExecutionContext.InteractiveApproval(true)
            });
        var call = new FunctionCallContent(
            $"scope-check-{Guid.NewGuid():N}",
            ShellTool.ToolName,
            ToolInput.Create(
                "Command", MarkerCommand,
                "WorkingDirectory", workingDirectory,
                "_rationale", "Verify the saved approval scope."));
        var decision = await executor.EvaluateAuthorizationAsync(
            call,
            context,
            TestContext.Current.CancellationToken);
        return ShellApprovalHarness.ObserveOutcome(decision);
    }

    private IReadOnlyList<ApprovalEntry> ReadPersistentEntries()
        => _store.GetApprovedEntries(
            TrustAudience.Personal,
            ShellTool.ToolName);

    private string CreateDirectory(string name)
        => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static string MarkerPath(string directory)
        => Path.Combine(directory, "launch-count.txt");

    private static string ReadMarker(string directory)
        => File.ReadAllText(MarkerPath(directory));

    private static MessageSource RequesterSource() => new()
    {
        ChannelType = ChannelType.SignalR,
        SenderId = new SenderId("local-user"),
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        Principal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(
            TransportAuthenticity.LocalProcess,
            PayloadTaint.Trusted),
        ReceivedAt = ReceivedAt,
    };

    private static async Task CreateRepositoryWithWorktreeAsync(
        string main,
        string sibling)
    {
        await RunGitAsync(main, "init", "--quiet");
        await RunGitAsync(main, "config", "user.name", "Netclaw Test");
        await RunGitAsync(main, "config", "user.email", "netclaw@example.invalid");
        File.WriteAllText(Path.Combine(main, "tracked.txt"), "test");
        await RunGitAsync(main, "add", "tracked.txt");
        await RunGitAsync(main, "commit", "--quiet", "-m", "initial");
        await RunGitAsync(
            main,
            "worktree",
            "add",
            "--quiet",
            "--detach",
            sibling,
            "HEAD");
    }

    private static void SwapWorktreeRegistration(
        string sibling,
        string unrelated)
    {
        var swappedAdmin = Directory.CreateDirectory(
            Path.Combine(unrelated, ".git", "worktrees", "swapped"));
        File.WriteAllText(Path.Combine(swappedAdmin.FullName, "commondir"), "../..\n");
        File.WriteAllText(
            Path.Combine(swappedAdmin.FullName, "gitdir"),
            Path.Combine(sibling, ".git") + "\n");
        File.WriteAllText(
            Path.Combine(swappedAdmin.FullName, "HEAD"),
            File.ReadAllText(Path.Combine(unrelated, ".git", "HEAD")));
        File.SetAttributes(Path.Combine(sibling, ".git"), FileAttributes.Normal);
        File.WriteAllText(
            Path.Combine(sibling, ".git"),
            $"gitdir: {swappedAdmin.FullName}\n");
    }

    private static async Task RunGitAsync(
        string workingDirectory,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        Assert.True(process.Start());
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(
            process.ExitCode == 0,
            $"git failed: {await standardOutput}\n{await standardError}");
    }

    private sealed record ApprovalJourney(
        SessionId SessionId,
        IActorRef Manager,
        TestProbe Subscriber,
        ToolInteractionRequest Request);
}
