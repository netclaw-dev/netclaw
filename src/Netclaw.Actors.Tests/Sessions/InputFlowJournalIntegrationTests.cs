// -----------------------------------------------------------------------
// <copyright file="InputFlowJournalIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Persistence;
using Akka.Persistence.Journal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Xunit;
using AkkaPersistence = Akka.Persistence.Persistence;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class InputFlowJournalIntegrationTests(ITestOutputHelper output) : LlmSessionTestBase(output)
{
    private readonly FakeChatClient _client = new();
    private readonly FakeToolExecutor _executor = new();

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_client));
        services.AddSingleton(new ModelCapabilities { ModelId = "fake-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionConfig
        {
            // The journal reader must see every admission and terminal event.
            Tuning = new SessionTuning
            {
                SnapshotInterval = 0,
                TitleGenerationInterval = 0
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("Test assistant."));
        services.AddSingleton<IToolExecutor>(_executor);
        var registry = new ToolRegistry();
        registry.RegisterCore(AIFunctionFactory.Create(() => "same result", "search_tools"), "builtin");
        services.AddSingleton(registry);
    }

    [Fact]
    public async Task Tool_batch_closes_its_selected_input_before_the_buffered_input_closes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var firstResponseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.NextResponseGate = firstResponseGate;
        _client.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-1", "search_tools", new Dictionary<string, object?>
            {
                ["_rationale"] = "Read the current tool list."
            })
        ];
        var sessionId = new SessionId("input-flow/journal-ids");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("input-flow-journal-sub");
        await JoinSessionAsync(manager, subscriber, sessionId, OutputFilter.Full);

        // Act
        try
        {
            await manager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = "First tool request"
            }, TimeSpan.FromSeconds(5), ct);
            var committedBeforeResponse = await ReadCommittedAdmissionsAsync(sessionId);
            await _client.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await manager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = "Later buffered request"
            }, TimeSpan.FromSeconds(5), ct);
            firstResponseGate.TrySetResult();
            await subscriber.FishForMessageAsync<TurnCompleted>(
                _ => true, TimeSpan.FromSeconds(10), cancellationToken: ct);

            var events = await ReadInputEventsAsync(sessionId);
            var admitted = events.OfType<InputAdmitted>().ToArray();
            var batch = Assert.Single(events.OfType<ToolBatchStarted>());
            var turn = Assert.Single(events.OfType<TurnRecorded>());

            // Assert
            Assert.Equal("First tool request", Assert.Single(committedBeforeResponse).UserMessage.Content);
            Assert.Equal(["First tool request", "Later buffered request"],
                admitted.Select(evt => evt.UserMessage.Content));
            Assert.Equal([admitted[0].InputId], batch.ConsumedInputIds);
            Assert.Equal([admitted[1].InputId], turn.ConsumedInputIds);
            var journalOrder = events.ToArray();
            Assert.True(Array.IndexOf(journalOrder, admitted[0]) < Array.IndexOf(journalOrder, admitted[1]));
            Assert.True(Array.IndexOf(journalOrder, admitted[1]) < Array.IndexOf(journalOrder, batch));
            Assert.True(Array.IndexOf(journalOrder, batch) < Array.IndexOf(journalOrder, turn));
        }
        finally
        {
            firstResponseGate.TrySetResult();
        }
    }

    [Fact]
    public async Task Admitted_turn_id_matches_live_turn_log_context()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var responseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.NextResponseGate = responseGate;
        var sessionId = new SessionId("input-flow/live-turn-id");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("turn-id-sub");
        var diagnostics = CreateTestProbe("turn-id-log");
        Sys.EventStream.Subscribe(diagnostics.Ref, typeof(Info));
        await JoinSessionAsync(manager, subscriber, sessionId);

        // Act
        try
        {
            await manager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = "Inspect this turn",
                Source = new MessageSource
                {
                    ChannelType = ChannelType.SignalR,
                    SenderId = new SenderId("operator-1"),
                    ChannelId = "identity-test-channel",
                    MessageId = "identity-message-1",
                    Audience = TrustAudience.Personal,
                    Boundary = TrustBoundary.Personal,
                    Principal = PrincipalClassification.Operator,
                    Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
                    ReceivedAt = TimeProvider.System.GetUtcNow()
                }
            }, TimeSpan.FromSeconds(5), ct);
            await _client.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            var live = await diagnostics.FishForMessageAsync<Info>(
                evt => evt.ToString().Contains("turn_received", StringComparison.Ordinal)
                    && evt.ToString().Contains($"[SessionId={sessionId.Value}]", StringComparison.Ordinal),
                TimeSpan.FromSeconds(5), cancellationToken: ct);

            responseGate.TrySetResult();
            await subscriber.FishForMessageAsync<TurnCompleted>(
                _ => true, TimeSpan.FromSeconds(10), cancellationToken: ct);
            var admitted = Assert.Single((await ReadInputEventsAsync(sessionId)).OfType<InputAdmitted>());

            // Assert
            Assert.False(string.IsNullOrWhiteSpace(admitted.TurnContext.TurnId));
            Assert.Contains($"[TurnId={admitted.TurnContext.TurnId}]", live.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            responseGate.TrySetResult();
            Sys.EventStream.Unsubscribe(diagnostics.Ref, typeof(Info));
        }
    }

    private async Task<IReadOnlyList<object>> ReadInputEventsAsync(SessionId sessionId)
    {
        var ct = TestContext.Current.CancellationToken;
        var session = await Sys.ActorSelection(
                $"/user/session-manager/{Uri.EscapeDataString(sessionId.Value)}")
            .ResolveOne(TimeSpan.FromSeconds(5), ct);
        var stopProbe = CreateTestProbe("input-flow-stop-watch");
        stopProbe.Watch(session);
        Sys.Stop(session);
        await stopProbe.ExpectTerminatedAsync(session, cancellationToken: ct);

        var probe = CreateTestProbe("input-flow-journal-reader");
        var reader = Sys.ActorOf(Props.Create(() => new InputEventReader(
            $"session-{sessionId.Value}", probe.Ref)));
        var events = await probe.ExpectMsgAsync<IReadOnlyList<object>>(
            TimeSpan.FromSeconds(5), cancellationToken: ct);
        stopProbe.Watch(reader);
        Sys.Stop(reader);
        await stopProbe.ExpectTerminatedAsync(reader, cancellationToken: ct);
        return events;
    }

    private async Task<IReadOnlyList<InputAdmitted>> ReadCommittedAdmissionsAsync(SessionId sessionId)
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = CreateTestProbe("input-flow-committed-admissions");
        AkkaPersistence.Instance.Apply(Sys).JournalFor(null).Tell(
            new MemoryJournal.ReplayAllEvents(0, int.MaxValue, int.MaxValue, probe.Ref));
        var admitted = new List<InputAdmitted>();
        while (true)
        {
            var reply = await probe.ExpectMsgAsync<object>(TimeSpan.FromSeconds(5), cancellationToken: ct);
            switch (reply)
            {
                case MemoryJournal.ReplayedEvent replayed
                    when replayed.Persistent.Payload is InputAdmitted evt && evt.SessionId == sessionId:
                    admitted.Add(evt);
                    break;
                case MemoryJournal.EventReplaySuccess:
                    return admitted;
                case MemoryJournal.EventReplayFailure failure:
                    throw new InvalidOperationException("The admission journal replay failed.", failure.Cause);
            }
        }
    }

    private sealed class InputEventReader : ReceivePersistentActor
    {
        private readonly IActorRef _replyTo;
        private readonly List<object> _events = [];

        public InputEventReader(string persistenceId, IActorRef replyTo)
        {
            PersistenceId = persistenceId;
            _replyTo = replyTo;
            Recover<InputAdmitted>(_events.Add);
            Recover<ToolBatchStarted>(_events.Add);
            Recover<TurnRecorded>(_events.Add);
            Recover<RecoveryCompleted>(_ => _replyTo.Tell(_events.ToArray()));
        }

        public override string PersistenceId { get; }
    }
}
