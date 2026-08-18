// -----------------------------------------------------------------------
// <copyright file="SignalRSessionActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.AspNetCore.SignalR;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Xunit;
using static Netclaw.Actors.Reminders.ReminderProtocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Tests.Gateway;

public sealed class SignalRSessionActorTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider) { }

    [Fact]
    public async Task Busy_session_defers_a_CurrentSession_reminder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var sessionId = new SessionId("signalr/busy-reminder-session");
        var pipeline = new CapturingSessionPipeline();
        var actor = Sys.ActorOf(
            SignalRSessionActor.CreateProps(sessionId.Value, pipeline, new StubHubContext()),
            "signalr-busy-reminder-session");

        actor.Tell(new StartSignalRSession(
            sessionId,
            ChannelType.SignalR,
            new SignalRConnectionId("connection-1")));
        await pipeline.Created.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        actor.Tell(CreateReminderTurn(sessionId, "reminder-a"));
        await AwaitAssertAsync(
            () => Assert.Single(pipeline.Inputs),
            cancellationToken: cancellationToken);

        var secondAttempt = CreateTestProbe();
        secondAttempt.Send(actor, CreateReminderTurn(sessionId, "reminder-b"));
        var deferred = await secondAttempt.ExpectMsgAsync<CommandDeferred>(
            TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);

        Assert.Equal(sessionId, deferred.SessionId);
        Assert.Contains("active turn", deferred.Reason, StringComparison.OrdinalIgnoreCase);
        await AwaitAssertAsync(
            () => Assert.Single(pipeline.Inputs),
            cancellationToken: cancellationToken);
    }

    private static DeliverTrustedSessionTurn CreateReminderTurn(SessionId sessionId, string reminderId)
    {
        var key = new ReminderId(reminderId);
        return new DeliverTrustedSessionTurn(
            sessionId,
            "Run the reminder.",
            new MessageSource
            {
                ChannelType = ChannelType.SignalR,
                SenderId = new SenderId("reminder-system"),
                Audience = TrustAudience.Team,
                Boundary = TrustBoundary.Team,
                Principal = PrincipalClassification.VerifiedAutomation,
                Provenance = new SourceProvenance(
                    TransportAuthenticity.LocalProcess,
                    PayloadTaint.Trusted)
                {
                    SourceKind = new SourceKind("reminder")
                },
                ReceivedAt = DateTimeOffset.UnixEpoch,
                ReminderId = key
            });
    }

    private sealed class CapturingSessionPipeline : ISessionPipeline
    {
        private readonly TaskCompletionSource _created = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Created => _created.Task;

        public ConcurrentQueue<ChannelInput> Inputs { get; } = new();

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
        {
            var killSwitch = KillSwitches.Shared($"signalr-test-{sessionId.Value}");
            var input = Sink.ForEach<ChannelInput>(Inputs.Enqueue).ObservingFault();
            var output = Source.Never<SessionOutput>().Via(killSwitch.Flow<SessionOutput>());
            _created.TrySetResult();
            return Task.FromResult(new MaterializedSession(input, output, killSwitch));
        }

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(
            IWithSessionId feedback,
            CancellationToken ct = default) =>
            Task.FromResult<ISessionResponse>(CommandAck.For(feedback.SessionId));
    }

    private sealed class StubHubContext : IHubContext<SessionHub, ISessionHubClient>
    {
        public IHubClients<ISessionHubClient> Clients { get; } = new StubHubClients();

        public IGroupManager Groups { get; } = new StubGroupManager();
    }

    private sealed class StubHubClients : IHubClients<ISessionHubClient>
    {
        private static readonly ISessionHubClient ClientInstance = new StubHubClient();

        public ISessionHubClient All => ClientInstance;

        public ISessionHubClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => ClientInstance;

        public ISessionHubClient Client(string connectionId) => ClientInstance;

        public ISessionHubClient Clients(IReadOnlyList<string> connectionIds) => ClientInstance;

        public ISessionHubClient Group(string groupName) => ClientInstance;

        public ISessionHubClient GroupExcept(
            string groupName,
            IReadOnlyList<string> excludedConnectionIds) => ClientInstance;

        public ISessionHubClient Groups(IReadOnlyList<string> groupNames) => ClientInstance;

        public ISessionHubClient User(string userId) => ClientInstance;

        public ISessionHubClient Users(IReadOnlyList<string> userIds) => ClientInstance;
    }

    private sealed class StubHubClient : ISessionHubClient
    {
        public Task ReceiveOutput(SessionOutputDto output) => Task.CompletedTask;
    }

    private sealed class StubGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveFromGroupAsync(
            string connectionId,
            string groupName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
