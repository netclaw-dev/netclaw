// -----------------------------------------------------------------------
// <copyright file="ApprovalTurnBoundaryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Drives the approval turn state through the session actor with the
/// production tool authorization registration and native shell processes.
/// Old coverage: ToolApprovalStateTests (per-call resolution and the
/// mapping from an option key to a decision) in a live session.
/// </summary>
[Collection(BackgroundJobProcessCollection.Name)]
public sealed class ApprovalTurnBoundaryTests : LlmSessionTestBase
{
    private static readonly DateTimeOffset ReceivedAt =
        new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeChatClient _chatClient = new();
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        $".netclaw-approval-turn-{Guid.NewGuid():N}");
    private ShellExecutionEnvironment _environment = null!;

    public ApprovalTurnBoundaryTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        var paths = new NetclawPaths(_root, Path.Combine(_root, "workspaces"));
        paths.EnsureDirectoriesExist();
        _environment = TestShellEnvironment.Current;
        var config = new ToolConfig
        {
            ShellMode = ShellExecutionMode.HostAllowed,
            AudienceProfiles = ToolAudienceProfileDefaults.CreateProfilesForPosture(DeploymentPosture.Personal)
        };

        services.AddSingleton(paths);
        services.AddSingleton(config);
        ShellApprovalHarness.AddProductionToolAuthorization(services, paths, _environment, config);
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
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
            new StaticSystemPromptProvider("You are a test assistant with tools."));
    }

    protected override async Task AfterAllAsync()
    {
        await base.AfterAllAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // One model turn asks for two gated shell calls. The operator approves one
    // call once and denies the other. Only the approved call runs, and the turn
    // completes with one result for each call.
    [Fact]
    public async Task Mixed_answers_in_one_batch_run_only_the_approved_call()
    {
        var approvedDirectory = Directory.CreateDirectory(Path.Combine(_root, "approved")).FullName;
        var deniedDirectory = Directory.CreateDirectory(Path.Combine(_root, "denied")).FullName;
        const string approvedCall = "call-approved";
        const string deniedCall = "call-denied";
        _chatClient.ToolCallsOnFirstCall =
        [
            CreateMarkerCall(approvedCall, approvedDirectory),
            CreateMarkerCall(deniedCall, deniedDirectory)
        ];
        var sessionId = new SessionId("approval-turn/mixed");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("approval-turn-mixed");

        await manager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run both test commands.",
            Source = RequesterSource()
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        var requests = new Dictionary<string, ToolInteractionRequest>(StringComparer.Ordinal);
        while (requests.Count < 2)
        {
            var request = await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
                TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
            requests[request.CallId.Value] = request;
        }

        Assert.IsType<CommandAck>(await ReplyAsync(manager, sessionId, requests[approvedCall], ApprovalOptionKeys.ApproveOnceKey));
        Assert.IsType<CommandAck>(await ReplyAsync(manager, sessionId, requests[deniedCall], ApprovalOptionKeys.DenyKey));

        var results = new Dictionary<string, ToolResultOutput>(StringComparer.Ordinal);
        var outcomes = new Dictionary<string, ApprovalOutcomeOutput>(StringComparer.Ordinal);
        while (results.Count < 2 || outcomes.Count < 2)
        {
            var output = await subscriber.ExpectMsgAsync<SessionOutput>(
                TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
            if (output is ApprovalOutcomeOutput outcome)
                outcomes.Add(outcome.CallId.Value, outcome);
            else
            {
                var result = Assert.IsType<ToolResultOutput>(output);
                results.Add(result.CallId.Value, result);
            }
        }
        Assert.Equal(ApprovalOptionKeys.ApproveOnceKey, outcomes[approvedCall].SelectedKey);
        Assert.Equal(ApprovalOptionKeys.DenyKey, outcomes[deniedCall].SelectedKey);

        await subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
        Assert.Null(results[approvedCall].FailureCode);
        Assert.Contains("approval_denied_by_user", results[deniedCall].Result, StringComparison.Ordinal);
        Assert.Equal("x", await File.ReadAllTextAsync(MarkerPath(approvedDirectory), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(MarkerPath(deniedDirectory)));
    }

    private FunctionCallContent CreateMarkerCall(string callId, string workingDirectory)
        => new(
            callId,
            ShellTool.ToolName,
            ToolInput.Create(
                "Command", MarkerCommand,
                "WorkingDirectory", workingDirectory,
                "_rationale", "Verify the approval turn."));

    private string MarkerCommand => _environment.Grammar == ShellGrammar.PowerShell
        ? "Add-Content -NoNewline -Path launch-count.txt -Value x"
        : "printf x >> launch-count.txt";

    private static string MarkerPath(string directory)
        => Path.Combine(directory, "launch-count.txt");

    private static Task<ISessionResponse> ReplyAsync(
        IActorRef manager,
        SessionId sessionId,
        ToolInteractionRequest request,
        ApprovalOptionKey option)
    {
        Assert.Contains(request.Options, offered => offered.Key == option);
        return manager.Ask<ISessionResponse>(
            new ToolInteractionResponse
            {
                SessionId = sessionId,
                CallId = request.CallId,
                SelectedKey = option,
                SenderId = new SenderId("local-user")
            },
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
    }

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
}

/// <summary>
/// Drives recovered approval batches through a session restart with the
/// production tool authorization registration and native shell processes.
/// Old coverage: ToolApprovalStateTests (BuildRedrivePlan: the "Once"
/// pre-seed, the "Deny" override, and the exact redrive of an assignment grant).
/// </summary>
[Collection(BackgroundJobProcessCollection.Name)]
public sealed class ApprovalRedriveBoundaryTests : LlmSessionTestBase
{
    private static readonly DateTimeOffset ReceivedAt =
        new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeChatClient _chatClient = new();
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        $".netclaw-approval-redrive-{Guid.NewGuid():N}");
    private ShellExecutionEnvironment _environment = null!;
    private ToolApprovalStore _store = null!;

    public ApprovalRedriveBoundaryTests(ITestOutputHelper output) : base(output)
    {
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        var paths = new NetclawPaths(_root, Path.Combine(_root, "workspaces"));
        paths.EnsureDirectoriesExist();
        // Linux CI runs Bash 5.2 at /bin/bash. The daemon probes that version
        // and declares it, so the parser resolves an assignment prefix.
        _environment = OperatingSystem.IsLinux()
            ? ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux, new Version(5, 2))
            : TestShellEnvironment.Current;
        var config = new ToolConfig
        {
            ShellMode = ShellExecutionMode.HostAllowed,
            AudienceProfiles = ToolAudienceProfileDefaults.CreateProfilesForPosture(DeploymentPosture.Personal)
        };

        services.AddSingleton(paths);
        services.AddSingleton(config);
        ShellApprovalHarness.AddProductionToolAuthorization(services, paths, _environment, config);
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
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
            new StaticSystemPromptProvider("You are a test assistant with tools."));
    }

    protected override async Task AfterAllAsync()
    {
        await base.AfterAllAsync();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // One model turn asks for two gated shell calls, and the session restarts
    // before the operator answers. After the restart, "Once" for one call and
    // "Deny" for the other run only the approved call, without a second prompt.
    [Fact]
    public async Task Recovered_batch_with_mixed_answers_runs_only_the_approved_call()
    {
        var approvedDirectory = Directory.CreateDirectory(Path.Combine(_root, "approved")).FullName;
        var deniedDirectory = Directory.CreateDirectory(Path.Combine(_root, "denied")).FullName;
        const string approvedCall = "call-recovered-approved";
        const string deniedCall = "call-recovered-denied";
        _chatClient.ToolCallsOnFirstCall =
        [
            CreateMarkerCall(approvedCall, approvedDirectory, MarkerCommand),
            CreateMarkerCall(deniedCall, deniedDirectory, MarkerCommand)
        ];
        var sessionId = new SessionId("approval-redrive/mixed");

        var requests = await StartTurnAsync(sessionId, expectedPrompts: 2);
        await ColdRespawnAsync(sessionId);
        var subscriber = await JoinAsync(sessionId, "approval-redrive-mixed-after");

        Assert.IsType<CommandAck>(await ReplyAsync(sessionId, requests[approvedCall], ApprovalOptionKeys.ApproveOnceKey));
        Assert.IsType<CommandAck>(await ReplyAsync(sessionId, requests[deniedCall], ApprovalOptionKeys.DenyKey));

        var results = new Dictionary<string, ToolResultOutput>(StringComparer.Ordinal);
        while (results.Count < 2)
        {
            var result = await subscriber.ExpectMsgAsync<ToolResultOutput>(
                TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
            results[result.CallId.Value] = result;
        }

        await ExpectCompletedTurnAsync(subscriber);
        Assert.Null(results[approvedCall].FailureCode);
        Assert.Contains("approval_denied_by_user", results[deniedCall].Result, StringComparison.Ordinal);
        Assert.Equal("x", await File.ReadAllTextAsync(MarkerPath(approvedDirectory), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(MarkerPath(deniedDirectory)));
        Assert.Empty(_store.GetApprovedEntries(TrustAudience.Personal, ShellTool.ToolName));
    }

    // An assignment "Always" answer after a restart stores one grant for the
    // exact assignment and redrives the exact call one time.
    [SlopwatchSuppress("SW001", "The assignment prefix needs the declared Bash 5.2 environment of Linux CI.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "The assignment prefix needs the declared Bash 5.2 environment of Linux CI.")]
    public async Task Recovered_assignment_always_answer_redrives_the_exact_call_once()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "assignment")).FullName;
        const string callId = "call-recovered-assignment";
        _chatClient.ToolCallsOnFirstCall =
        [
            CreateMarkerCall(callId, directory, "MARK=x mkdir marker")
        ];
        var sessionId = new SessionId("approval-redrive/assignment");

        var requests = await StartTurnAsync(sessionId, expectedPrompts: 1);
        await ColdRespawnAsync(sessionId);
        var subscriber = await JoinAsync(sessionId, "approval-redrive-assignment-after");

        var assignmentAlways = new ApprovalOptionKey(ApprovalOptionKeys.ApproveAssignmentAlwaysV1);
        Assert.IsType<CommandAck>(await ReplyAsync(sessionId, requests[callId], assignmentAlways));

        var result = await subscriber.ExpectMsgAsync<ToolResultOutput>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        await ExpectCompletedTurnAsync(subscriber);
        // A second run of mkdir fails on the existing directory, so exit code 0
        // shows that the redrive ran the call one time.
        Assert.Null(result.FailureCode);
        Assert.StartsWith("Exit code: 0", result.Result, StringComparison.Ordinal);
        Assert.True(Directory.Exists(Path.Combine(directory, "marker")));
        Assert.Single(_store.GetApprovedEntries(TrustAudience.Personal, ShellTool.ToolName));
    }

    private async Task<Dictionary<string, ToolInteractionRequest>> StartTurnAsync(
        SessionId sessionId,
        int expectedPrompts)
    {
        _store = Host.Services.GetRequiredService<ToolApprovalStore>();
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = await JoinAsync(sessionId, $"approval-redrive-{Guid.NewGuid():N}");
        await manager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Run the test commands.",
            Source = RequesterSource()
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        for (var i = 0; i < expectedPrompts; i++)
        {
            await subscriber.ExpectMsgAsync<ToolCallOutput>(
                TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        }

        var requests = new Dictionary<string, ToolInteractionRequest>(StringComparer.Ordinal);
        while (requests.Count < expectedPrompts)
        {
            var request = await subscriber.ExpectMsgAsync<ToolInteractionRequest>(
                TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
            requests[request.CallId.Value] = request;
        }

        return requests;
    }

    private async Task<TestProbe> JoinAsync(SessionId sessionId, string probeName)
    {
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe(probeName);
        await manager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
        return subscriber;
    }

    // Stops the session child and waits for termination. The next join
    // re-creates the session from its journal and snapshot. The resolve budget
    // bounds a multi-hop startup under a slow CI scheduler; it is not a
    // correctness wait.
    private async Task ColdRespawnAsync(SessionId sessionId)
    {
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Watch(child);
        Sys.Stop(child);
        await ExpectTerminatedAsync(child, cancellationToken: TestContext.Current.CancellationToken);
    }

    private Task<ISessionResponse> ReplyAsync(
        SessionId sessionId,
        ToolInteractionRequest request,
        ApprovalOptionKey option)
    {
        Assert.Contains(request.Options, offered => offered.Key == option);
        return ActorRegistry.Get<SessionManagerActorKey>().Ask<ISessionResponse>(
            new ToolInteractionResponse
            {
                SessionId = sessionId,
                CallId = request.CallId,
                SelectedKey = option,
                SenderId = new SenderId("local-user")
            },
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
    }

    private static async Task ExpectCompletedTurnAsync(TestProbe subscriber)
    {
        await subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
    }

    private static FunctionCallContent CreateMarkerCall(string callId, string workingDirectory, string command)
        => new(
            callId,
            ShellTool.ToolName,
            ToolInput.Create(
                "Command", command,
                "WorkingDirectory", workingDirectory,
                "_rationale", "Verify the approval redrive."));

    private string MarkerCommand => _environment.Grammar == ShellGrammar.PowerShell
        ? "Add-Content -NoNewline -Path launch-count.txt -Value x"
        : "printf x >> launch-count.txt";

    private static string MarkerPath(string directory)
        => Path.Combine(directory, "launch-count.txt");

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
}
