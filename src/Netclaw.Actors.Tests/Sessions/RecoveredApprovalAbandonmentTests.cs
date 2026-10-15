// -----------------------------------------------------------------------
// <copyright file="RecoveredApprovalAbandonmentTests.cs" company="Petabridge, LLC">
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
/// Executable spec: who may abandon an open approval of a recovered session.
/// <para>
/// After a restart, a tool call waits for its requester to answer. A new message from that requester ends the wait:
/// the session closes the parked tool call with a synthetic result (tool-authorization TA-11, "a new user message
/// that abandons the parked batch"). A reminder fire is automation, not a person. It must not end the wait. The
/// operator decided this contract. TA-11 does not say whether a reminder counts as a user message.
/// </para>
/// <para>
/// Scope: the case with a child result as the automated input needs child-run types that exist only in the
/// coordination stack. It is not part of this class.
/// </para>
/// </summary>
public sealed class RecoveredApprovalAbandonmentTests : LlmSessionTestBase
{
    private const string CallId = "call-shell-recovered-approval";
    private const string Requester = "U-requester";
    private static readonly DateTimeOffset FixedReceivedAt = new(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeChatClient _fakeChatClient = new();
    private readonly ApprovalGateToolExecutor _toolExecutor = new();

    public RecoveredApprovalAbandonmentTests(ITestOutputHelper output) : base(output)
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

    // Positive control. The requester who owns the open approval sends a new message. That message may end the wait.
    [Fact]
    public async Task A_new_message_from_the_requester_abandons_the_recovered_approval()
    {
        // Arrange
        var sessionId = new SessionId("test-channel/abandon-by-requester");
        var recovered = await ParkApprovalThenRecoverAsync(sessionId);

        // Act
        await SendAsync(sessionId, "Never mind - just say hello", RequesterSource(Requester));

        // Assert
        await recovered.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var completed = await recovered.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Completed, completed.Outcome);
        Assert.Equal(0, _toolExecutor.SuccessfulExecutions);
        Assert.True(SentToModel(SupersededNotice),
            "The model call after the new message must carry the synthetic result that closes the parked tool call.");
    }

    // The reminder fire arrives while the requester has an open approval. The approval must survive it.
    [Fact]
    public async Task A_reminder_fire_does_not_abandon_the_recovered_approval()
    {
        // Arrange
        var sessionId = new SessionId("test-channel/abandon-by-reminder");
        var recovered = await ParkApprovalThenRecoverAsync(sessionId);

        // Act
        await SendAsync(sessionId, "Reminder: check the build.", ReminderSource("build-check:1700000000000"));
        ActorRegistry.Get<SessionManagerActorKey>().Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(CallId),
            SelectedKey = new ApprovalOptionKey(ApprovalOptionKeys.ApproveOnce),
            SenderId = new SenderId(Requester)
        }, ActorRefs.Nobody);

        // Assert: The next model request that carries a result for the parked call shows how the session settled it.
        // An abandoned call carries the synthetic result. An approved call carries the tool output.
        await AwaitAssertAsync(
            () =>
            {
                Assert.NotNull(ResultSentToModel());
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        var result = ResultSentToModel();
        Assert.False(result?.Contains(SupersededNotice, StringComparison.Ordinal),
            "The reminder abandoned the approval. The model received this result for the parked call: " + result);
        Assert.Equal(1, _toolExecutor.SuccessfulExecutions);
    }

    private const string SupersededNotice = "superseded by a new message";

    private bool SentToModel(string resultText) => ResultSentToModel()?.Contains(resultText, StringComparison.Ordinal) == true;

    private string? ResultSentToModel()
        => _fakeChatClient.ReceivedMessages
            .SelectMany(request => request)
            .Where(message => message.Role == Microsoft.Extensions.AI.ChatRole.Tool)
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .Where(result => result.CallId == CallId)
            .Select(result => result.Result?.ToString())
            .FirstOrDefault();

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
        await sessionManager.Ask<SessionJoined>(new JoinSession(first)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await first.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
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
        await sessionManager.Ask<SessionJoined>(new JoinSession(recovered)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await recovered.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
        return recovered;
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
