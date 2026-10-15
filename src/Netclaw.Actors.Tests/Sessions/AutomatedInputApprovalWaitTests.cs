// -----------------------------------------------------------------------
// <copyright file="AutomatedInputApprovalWaitTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// A reminder fire that arrives while a recovered session has an open approval waits in the buffer.
/// It must not abandon the approval, and it must not be lost: it runs as its own turn when the approval closes.
/// <c>RecoveredApprovalAbandonmentTests</c> proves that the approval survives. These tests prove that the waiting
/// input runs later, after the turn that closes the approval, and never inside that turn.
/// </summary>
public sealed class AutomatedInputApprovalWaitTests : LlmSessionTestBase
{
    private const string CallId = "call-shell-waiting-reminder";
    private const string Requester = "U-requester";
    private const string ReminderText = "Reminder: check the build.";
    private static readonly DateTimeOffset FixedReceivedAt = new(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeChatClient _fakeChatClient = new();
    private readonly ApprovalGateToolExecutor _toolExecutor = new();

    public AutomatedInputApprovalWaitTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_fakeChatClient));
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
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant with tools."));
        services.AddSingleton<IToolExecutor>(_toolExecutor);

        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create((string command) => $"ran {command}", "shell_execute"),
            "shell_execute");
        services.AddSingleton(registry);
    }

    [Fact]
    public async Task A_waiting_reminder_runs_as_its_own_turn_after_the_approved_batch()
    {
        // Arrange
        var sessionId = new SessionId("test-channel/reminder-waits-for-approval");
        var recovered = await ParkApprovalThenRecoverAsync(sessionId);
        await SendAsync(sessionId, ReminderText, ReminderSource("build-check:1700000000000"));

        // Act
        ActorRegistry.Get<SessionManagerActorKey>().Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(CallId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId(Requester)
        }, ActorRefs.Nobody);

        // Assert: the approved batch finishes its turn, then the reminder starts the next turn.
        await ExpectTurnAsync(recovered);
        await ExpectTurnAsync(recovered);
        Assert.Equal(1, _toolExecutor.SuccessfulExecutions);
        var resultCall = FirstCallWithToolResult();
        Assert.DoesNotContain(ReminderText, UserTexts(resultCall));
        Assert.Equal(ReminderText, UserTexts(_fakeChatClient.ReceivedMessages[^1]).Last());
    }

    [Fact]
    public async Task A_waiting_reminder_runs_after_the_turn_of_a_person_who_abandons_the_approval()
    {
        // Arrange
        var sessionId = new SessionId("test-channel/reminder-waits-for-person");
        var recovered = await ParkApprovalThenRecoverAsync(sessionId);
        await SendAsync(sessionId, ReminderText, ReminderSource("build-check:1700000000001"));

        // Act
        await SendAsync(sessionId, "Never mind - just say hello", RequesterSource(Requester));

        // Assert: the person's turn closes the parked call first. The reminder runs in the next turn.
        await ExpectTurnAsync(recovered);
        await ExpectTurnAsync(recovered);
        Assert.Equal(0, _toolExecutor.SuccessfulExecutions);
        var abandonCall = FirstCallWithToolResult();
        Assert.Contains("superseded by a new message", ToolResultText(abandonCall));
        Assert.DoesNotContain(ReminderText, UserTexts(abandonCall));
        Assert.Equal(ReminderText, UserTexts(_fakeChatClient.ReceivedMessages[^1]).Last());
    }

    private static async Task ExpectTurnAsync(Akka.TestKit.TestProbe subscriber)
    {
        await subscriber.FishForMessageAsync(
            message => message is TurnCompleted { Outcome: TurnOutcome.Completed },
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private IReadOnlyList<ChatMessage> FirstCallWithToolResult()
        => _fakeChatClient.ReceivedMessages.First(request => ToolResultText(request) is not null);

    private static string? ToolResultText(IReadOnlyList<ChatMessage> request)
        => request
            .Where(message => message.Role == Microsoft.Extensions.AI.ChatRole.Tool)
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .Where(result => result.CallId == CallId)
            .Select(result => result.Result?.ToString())
            .FirstOrDefault();

    private static string[] UserTexts(IReadOnlyList<ChatMessage> request)
        => request
            .Where(message => message.Role == Microsoft.Extensions.AI.ChatRole.User)
            .Select(message => message.Text)
            .ToArray();

    // Parks a tool call on an approval, stops the session, and joins the recovered session.
    private async Task<Akka.TestKit.TestProbe> ParkApprovalThenRecoverAsync(SessionId sessionId)
    {
        _toolExecutor.GatedTools.Add("shell_execute");
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(CallId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];

        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var first = CreateTestProbe("first-subscriber");
        await JoinAsync(sessionManager, first, sessionId);
        await SendAsync(sessionId, "Run git status", RequesterSource(Requester));
        await first.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await first.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        var session = await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(sessionId.Value)}")
            .ResolveOne(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Watch(session);
        Sys.Stop(session);
        await ExpectTerminatedAsync(session, cancellationToken: TestContext.Current.CancellationToken);

        var recovered = CreateTestProbe("recovered-subscriber");
        await JoinAsync(sessionManager, recovered, sessionId);
        return recovered;
    }

    // Ask keeps the join reply away from the test actor, which must see only the Terminated message.
    private static async Task JoinAsync(IActorRef sessionManager, Akka.TestKit.TestProbe subscriber, SessionId sessionId)
    {
        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
    }

    private Task<CommandAck> SendAsync(SessionId sessionId, string content, MessageSource source)
        => ActorRegistry.Get<SessionManagerActorKey>().Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = content,
            Source = source
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    private static MessageSource RequesterSource(string senderId) => new()
    {
        ChannelType = ChannelType.Slack,
        SenderId = new SenderId(senderId),
        Audience = TrustAudience.Team,
        Boundary = TrustBoundary.Team,
        Principal = PrincipalClassification.TrustedInternal,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public),
        ReceivedAt = FixedReceivedAt,
    };

    // Mirrors the source that ReminderExecutionActor builds for a reminder in an existing session.
    private static MessageSource ReminderSource(string fireKey) => new()
    {
        ChannelType = ChannelType.Slack,
        SenderId = new SenderId("reminder-system"),
        MessageId = fireKey,
        TurnId = new TurnId(fireKey),
        ReminderId = new ReminderId(fireKey),
        Audience = TrustAudience.Team,
        Boundary = TrustBoundary.Team,
        Principal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
        {
            SourceKind = new SourceKind("reminder")
        },
        ReceivedAt = FixedReceivedAt,
    };
}
