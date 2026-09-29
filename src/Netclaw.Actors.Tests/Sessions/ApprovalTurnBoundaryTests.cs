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
        while (results.Count < 2)
        {
            var result = await subscriber.ExpectMsgAsync<ToolResultOutput>(
                TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
            results[result.CallId.Value] = result;
        }

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
