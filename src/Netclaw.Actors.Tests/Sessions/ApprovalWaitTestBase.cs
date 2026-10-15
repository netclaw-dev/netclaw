// -----------------------------------------------------------------------
// <copyright file="ApprovalWaitTestBase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Persistence;
using Akka.Persistence.Journal;
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
using AkkaPersistence = Akka.Persistence.Persistence;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Shared setup for specs about an automated input (a reminder) that waits behind an open approval.
/// The requester and the reminder use different audiences. The system prompt names the audience of its turn.
/// A model call therefore shows which authority its turn ran under.
/// </summary>
public abstract class ApprovalWaitTestBase : LlmSessionTestBase
{
    protected const string CallId = "call-shell-waiting-input";
    protected const string Requester = "U-requester";
    protected const string ReminderText = "Reminder: check the build.";
    protected const string RequesterPromptMarker = "prompt-audience:Personal";
    protected const string ReminderPromptMarker = "prompt-audience:Team";
    private static readonly DateTimeOffset FixedReceivedAt = new(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);

    private protected readonly FakeChatClient FakeChat = new();
    private protected readonly ApprovalGateToolExecutor ToolExecutor = new();

    protected ApprovalWaitTestBase(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(FakeChat));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            // A spec that needs passivation sends ReceiveTimeout itself. A timer would race the spec.
            IdleTimeout = TimeSpan.Zero,
            Tuning = new SessionTuning
            {
                SnapshotInterval = 1,
                TitleGenerationInterval = 0,
                MaxInlineToolResultChars = 200,
                // A short history still reaches the summary call, so a spec can hold a compaction open.
                // Compaction starts only when a spec reports a full context window.
                KeepRecentMessages = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new AudienceNamingPromptProvider());
        services.AddSingleton<IToolExecutor>(ToolExecutor);

        var registry = new ToolRegistry();
        registry.Register(
            AIFunctionFactory.Create((string command) => $"ran {command}", "shell_execute"),
            "shell_execute");
        services.AddSingleton(registry);
    }

    protected IActorRef SessionManager => ActorRegistry.Get<SessionManagerActorKey>();

    // Parks a tool call on an approval. The session waits for the requester to answer.
    protected async Task ParkApprovalAsync(SessionId sessionId)
    {
        ToolExecutor.GatedTools.Add("shell_execute");
        FakeChat.ToolCallsOnFirstCall =
        [
            new FunctionCallContent(CallId, "shell_execute",
                new Dictionary<string, object?> { ["command"] = "git status" })
        ];

        var first = CreateTestProbe("first-subscriber");
        await JoinAsync(first, sessionId, OutputFilter.Full);
        await SendAsync(sessionId, "Run git status", RequesterSource(Requester));
        await first.ExpectMsgAsync<ToolCallOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await first.ExpectMsgAsync<ToolInteractionRequest>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
    }

    // Parks an approval, stops the session, and joins the recovered session.
    protected async Task<Akka.TestKit.TestProbe> ParkApprovalThenRecoverAsync(SessionId sessionId)
    {
        await ParkApprovalAsync(sessionId);
        await StopSessionAsync(sessionId);
        return await JoinNewSubscriberAsync(sessionId);
    }

    protected async Task<Akka.TestKit.TestProbe> JoinNewSubscriberAsync(SessionId sessionId)
    {
        var subscriber = CreateTestProbe("subscriber-" + Guid.NewGuid().ToString("N"));
        await JoinAsync(subscriber, sessionId, OutputFilter.TextOnly);
        return subscriber;
    }

    // Ask keeps the join reply away from the test actor, which must see only the Terminated message.
    protected async Task JoinAsync(Akka.TestKit.TestProbe subscriber, SessionId sessionId, OutputFilter filter)
    {
        await SessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = filter
        }, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);
    }

    protected async Task<IActorRef> ResolveSessionAsync(SessionId sessionId)
        => await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(sessionId.Value)}")
            .ResolveOne(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

    protected async Task StopSessionAsync(SessionId sessionId)
    {
        var session = await ResolveSessionAsync(sessionId);
        Watch(session);
        Sys.Stop(session);
        await ExpectTerminatedAsync(session, cancellationToken: TestContext.Current.CancellationToken);
    }

    protected async Task SendAsync(SessionId sessionId, string content, MessageSource source)
        => await SessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = content,
            Source = source
        }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    protected Task SendReminderAsync(SessionId sessionId, string fireKey)
        => SendAsync(sessionId, ReminderText, ReminderSource(fireKey));

    protected void Answer(SessionId sessionId, bool approve)
        => SessionManager.Tell(new ToolInteractionResponse
        {
            SessionId = sessionId,
            CallId = new Netclaw.Tools.ToolCallId(CallId),
            SelectedKey = new ApprovalOptionKey(approve ? ApprovalOptionKeys.ApproveOnce : ApprovalOptionKeys.Deny),
            SenderId = new SenderId(Requester)
        }, ActorRefs.Nobody);

    protected static async Task ExpectTurnsAsync(Akka.TestKit.TestProbe subscriber, int count)
    {
        for (var turn = 0; turn < count; turn++)
        {
            await subscriber.FishForMessageAsync(
                message => message is TurnCompleted { Outcome: TurnOutcome.Completed },
                TimeSpan.FromSeconds(10),
                cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    // The call that closes the approval runs under the requester. The reminder text is not part of that call.
    protected void AssertApprovalClosedUnderRequester(bool approved)
    {
        var resultRequest = FakeChat.ReceivedMessages.First(request => ToolResultText(request) is not null);
        Assert.Equal(approved ? 1 : 0, ToolExecutor.SuccessfulExecutions);
        Assert.DoesNotContain(ReminderText, UserTexts(resultRequest));
        Assert.Contains(RequesterPromptMarker, SystemText(resultRequest));
    }

    // The reminder runs once, after the call that closes the approval, as its own turn under its own authority.
    protected void AssertReminderRanOnceAfterApproval()
    {
        var requests = FakeChat.ReceivedMessages;
        var resultRequest = requests.First(request => ToolResultText(request) is not null);
        var reminderRequest = Assert.Single(requests, request => LastUserText(request) == ReminderText);
        Assert.Contains(ReminderPromptMarker, SystemText(reminderRequest));
        Assert.True(
            IndexOf(requests, resultRequest) < IndexOf(requests, reminderRequest),
            "The reminder must run after the call that closes the approval.");
    }

    // Every admitted input is closed, and a snapshot covers the last turn. A waiting input blocks both while it stays pending.
    protected async Task AssertNoInputStaysPendingAsync(SessionId sessionId)
    {
        await AwaitAssertAsync(
            async () =>
            {
                var events = await ReadJournalAsync(sessionId);
                Assert.Empty(PendingInputIds(events));
                var lastTurn = events.Where(item => item.Payload is TurnRecorded).Max(item => item.SequenceNr);
                var snapshot = await LatestSnapshotSequenceNrAsync(sessionId);
                Assert.True(snapshot >= lastTurn,
                    $"The latest snapshot ({snapshot}) must cover the last turn ({lastTurn}).");
            },
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    protected sealed record JournalEntry(long SequenceNr, object Payload);

    protected async Task<IReadOnlyList<JournalEntry>> ReadJournalAsync(SessionId sessionId)
    {
        var ct = TestContext.Current.CancellationToken;
        var persistenceId = $"session-{sessionId.Value}";
        var probe = CreateTestProbe("journal-reader-" + Guid.NewGuid().ToString("N"));
        AkkaPersistence.Instance.Apply(Sys).JournalFor(null).Tell(
            new MemoryJournal.ReplayAllEvents(0, int.MaxValue, int.MaxValue, probe.Ref));
        var entries = new List<JournalEntry>();
        while (true)
        {
            switch (await probe.ExpectMsgAsync<object>(TimeSpan.FromSeconds(5), cancellationToken: ct))
            {
                case MemoryJournal.ReplayedEvent replayed when replayed.Persistent.PersistenceId == persistenceId:
                    entries.Add(new JournalEntry(replayed.Persistent.SequenceNr, replayed.Persistent.Payload));
                    break;
                case MemoryJournal.EventReplaySuccess:
                    return entries;
                case MemoryJournal.EventReplayFailure failure:
                    throw new InvalidOperationException("The journal replay failed.", failure.Cause);
            }
        }
    }

    protected static IReadOnlyList<InputId> PendingInputIds(IReadOnlyList<JournalEntry> events)
    {
        var closed = new HashSet<InputId>();
        foreach (var entry in events)
        {
            switch (entry.Payload)
            {
                case TurnRecorded turn:
                    closed.UnionWith(turn.ConsumedInputIds);
                    break;
                case ToolBatchStarted batch:
                    closed.UnionWith(batch.ConsumedInputIds);
                    break;
                case InputClosed input:
                    closed.UnionWith(input.InputIds);
                    break;
            }
        }

        return events
            .Select(entry => entry.Payload)
            .OfType<InputAdmitted>()
            .Select(admitted => admitted.InputId)
            .Where(id => !closed.Contains(id))
            .ToArray();
    }

    private async Task<long> LatestSnapshotSequenceNrAsync(SessionId sessionId)
    {
        var probe = CreateTestProbe("snapshot-reader-" + Guid.NewGuid().ToString("N"));
        AkkaPersistence.Instance.Apply(Sys).SnapshotStoreFor(null).Tell(
            new LoadSnapshot($"session-{sessionId.Value}", SnapshotSelectionCriteria.Latest, long.MaxValue),
            probe.Ref);
        var result = await probe.ExpectMsgAsync<LoadSnapshotResult>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        return result.Snapshot?.Metadata.SequenceNr ?? 0;
    }

    protected static string? LastUserText(IReadOnlyList<ChatMessage> request)
        => request.LastOrDefault(message => message.Role == Microsoft.Extensions.AI.ChatRole.User)?.Text;

    protected static string[] UserTexts(IReadOnlyList<ChatMessage> request)
        => request
            .Where(message => message.Role == Microsoft.Extensions.AI.ChatRole.User)
            .Select(message => message.Text)
            .ToArray();

    protected static string SystemText(IReadOnlyList<ChatMessage> request)
        => request.FirstOrDefault(message => message.Role == Microsoft.Extensions.AI.ChatRole.System)?.Text
           ?? string.Empty;

    protected static string? ToolResultText(IReadOnlyList<ChatMessage> request)
        => request
            .Where(message => message.Role == Microsoft.Extensions.AI.ChatRole.Tool)
            .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
            .Where(result => result.CallId == CallId)
            .Select(result => result.Result?.ToString())
            .FirstOrDefault();

    private static int IndexOf(
        IReadOnlyList<IReadOnlyList<ChatMessage>> requests,
        IReadOnlyList<ChatMessage> request)
    {
        for (var index = 0; index < requests.Count; index++)
        {
            if (ReferenceEquals(requests[index], request))
                return index;
        }

        throw new InvalidOperationException("The request is not in the list.");
    }

    protected static MessageSource RequesterSource(string senderId) => new()
    {
        ChannelType = ChannelType.Slack,
        SenderId = new SenderId(senderId),
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        Principal = PrincipalClassification.TrustedInternal,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public),
        ReceivedAt = FixedReceivedAt,
    };

    // Mirrors the source that ReminderExecutionActor builds for a reminder in an existing session.
    protected static MessageSource ReminderSource(string fireKey) => new()
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

    // Mirrors the source that the restart reminder uses to resume a session after a daemon restart.
    protected static MessageSource RestartReminderSource(ReminderDefinition reminder) => new()
    {
        ChannelType = ChannelType.Slack,
        SenderId = new SenderId("reminder-system"),
        MessageId = $"{reminder.Id}:1",
        TurnId = new TurnId($"{reminder.Id}:1"),
        Audience = reminder.Audience,
        Boundary = reminder.Boundary,
        Principal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted),
        ReceivedAt = new DateTimeOffset(2026, 5, 17, 12, 0, 0, TimeSpan.Zero),
        ReminderId = new ReminderId($"{reminder.Id}:1")
    };

    protected sealed class AudienceNamingPromptProvider : ISystemPromptProvider
    {
        public string GetSystemPrompt(TrustAudience audience, string? projectDirectory = null)
            => $"You are a test assistant with tools. prompt-audience:{audience}";

        public string? GetProjectInstructions(TrustAudience audience, string? projectDirectory) => null;

        public string? GetOperatingRules(TrustAudience audience) => null;
    }
}
