// -----------------------------------------------------------------------
// <copyright file="SubscriberDeathDuringPersistenceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Configuration;
using Akka.Dispatch;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence;
using Akka.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class SubscriberDeathDuringPersistenceTests(ITestOutputHelper output)
    : PersistenceTestKit(output: output), IAsyncDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(10);
    private const string DispatcherName = "subscriber-death-dispatcher";
    private readonly FakeChatClient _model = new();
    private TestSessionTempDirectory? _directory;

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        _directory = TestSessionTempDirectory.Create("netclaw-subscriber-death-", createDirectoryTree: true);
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        base.ConfigureAkka(builder, provider);
        builder.WithNetclawSerialization().AddHocon($$"""
            akka.scheduler.implementation = "Akka.TestKit.TestScheduler, Akka.TestKit"
            {{DispatcherName}} {
                type = "{{typeof(CompletionDispatcherConfigurator).AssemblyQualifiedName}}"
                throughput = 2147483647
            }
            """, HoconAddMode.Prepend);
    }

    [Fact]
    public async Task Real_subscriber_death_during_admission_persistence_preserves_the_turn_and_allows_passivation()
    {
        var session = new SessionId("signalr/subscriber-death-during-write");
        var outputs = CreateTestProbe();
        var owner = CreateOwner(session);
        var subscriberOutput = CreateTestProbe();
        var subscriber = Sys.ActorOf(Props.Create(() => new Subscriber(subscriberOutput.Ref))
            .WithDispatcher(DispatcherName));
        await JoinAsync(owner, subscriber, session);
        await JoinAsync(owner, outputs.Ref, session);
        await subscriberOutput.ExpectMsgAsync<SessionJoined>(Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        var entered = NewSignal();
        var release = NewSignal();
        InputAdmitted? admitted = null;
        await Journal.OnWrite.FailIf(async record =>
        {
            if (record.Payload is not InputAdmitted input) return false;
            admitted = input;
            entered.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return false;
        });
        try
        {
            var send = owner.Ask<CommandAck>(Input(session, "accepted-before-disconnect"), Ceiling,
                TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            Assert.False(send.IsCompleted);
            Assert.Equal(0, _model.CallCount);

            // A completed runnable releases the dispatcher acknowledgement.
            // The journal hold excludes a persistence acknowledgement between these barriers.
            var subscriberWatcher = CreateTestProbe();
            subscriberWatcher.Watch(subscriber);
            await DispatcherIdleAsync();
            Sys.Stop(subscriber);
            await DispatcherIdleAsync();
            await subscriberWatcher.ExpectTerminatedAsync(subscriber, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
            await BarrierAsync(owner);
            Assert.False(release.Task.IsCompleted);
            Assert.False(send.IsCompleted);
            Assert.Equal(0, _model.CallCount);
            release.TrySetResult();
            await send.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            await outputs.FishForMessageAsync<SessionOutput>(message => message is TurnCompleted, Ceiling,
                cancellationToken: TestContext.Current.CancellationToken);
            var events = await ReadEventsAsync(session);
            var persistedAdmission = Assert.Single(events.OfType<InputAdmitted>());
            Assert.NotNull(admitted);
            Assert.Equal(admitted.InputId, persistedAdmission.InputId);
            var turn = Assert.Single(events.OfType<TurnRecorded>());
            Assert.Contains(persistedAdmission.InputId, turn.ConsumedInputIds);
            Assert.Equal("accepted-before-disconnect", turn.UserMessage.Content);
            Assert.Equal(1, _model.CallCount);
            await PassivateAsync(owner, outputs.Ref, session);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_leave_then_real_subscriber_death_preserves_the_session(bool changeFilter)
    {
        var session = new SessionId("signalr/explicit-leave-before-death");
        var outputs = CreateTestProbe();
        var owner = CreateOwner(session);
        var subscriberOutput = CreateTestProbe();
        var subscriber = Sys.ActorOf(Props.Create(() => new Subscriber(subscriberOutput.Ref))
            .WithDispatcher(DispatcherName));
        await JoinAsync(owner, subscriber, session);
        await JoinAsync(owner, outputs.Ref, session);
        await JoinAsync(owner, subscriber, session, changeFilter ? OutputFilter.Full : OutputFilter.TextOnly);
        owner.Tell(new LeaveSession(subscriber) { SessionId = session });
        await BarrierAsync(owner);
        var subscriberWatcher = CreateTestProbe();
        subscriberWatcher.Watch(subscriber);
        await DispatcherIdleAsync();
        Sys.Stop(subscriber);
        await BarrierAsync(owner);
        await subscriberWatcher.ExpectTerminatedAsync(subscriber, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        await owner.Ask<CommandAck>(Input(session, "after-explicit-leave"), Ceiling, TestContext.Current.CancellationToken);
        await outputs.FishForMessageAsync<SessionOutput>(message => message is TurnCompleted, Ceiling,
            cancellationToken: TestContext.Current.CancellationToken);
        var turn = Assert.Single((await ReadEventsAsync(session)).OfType<TurnRecorded>());
        Assert.Equal("after-explicit-leave", turn.UserMessage.Content);
        Assert.Single(turn.ConsumedInputIds);
        Assert.Equal(1, _model.CallCount);
        await PassivateAsync(owner, outputs.Ref, session);
    }

    private IActorRef CreateOwner(SessionId session)
    {
        var paths = _directory!.Paths;
        var services = new SessionServices(new SingleClientProvider(_model),
            new StaticSystemPromptProvider("Reply to the user."), [], new ControllableWorkingContextSnapshotProvider(),
            TimeProvider.System, paths, new TestSessionStorageResolver(paths));
        return Sys.ActorOf(Props.Create(() => new LlmSessionActor(session.Value,
            new ModelCapabilities { ModelId = "fake-model", ContextWindowTokens = 128_000 },
            new SessionConfig { IdleTimeout = TimeSpan.Zero, Tuning = new SessionTuning
                { TitleGenerationInterval = 0, SnapshotInterval = 1000 } }, services, null, null, null))
            .WithDispatcher(DispatcherName));
    }

    private static Task<SessionJoined> JoinAsync(IActorRef owner, IActorRef subscriber, SessionId session,
        OutputFilter filter = OutputFilter.TextOnly) => owner.Ask<SessionJoined>(
        new JoinSession(subscriber) { SessionId = session, Filter = filter }, Ceiling, TestContext.Current.CancellationToken);

    private async Task BarrierAsync(IActorRef owner)
    {
        var identity = await owner.Ask<ActorIdentity>(new Identify("subscriber-death-barrier"), Ceiling,
            TestContext.Current.CancellationToken);
        Assert.Equal(owner, identity.Subject);
        await DispatcherIdleAsync();
    }

    private Task DispatcherIdleAsync() => Assert.IsType<CompletionDispatcher>(Sys.Dispatchers.Lookup(DispatcherName))
        .WhenIdle().WaitAsync(Ceiling, TestContext.Current.CancellationToken);

    private async Task PassivateAsync(IActorRef owner, IActorRef outputs, SessionId session)
    {
        owner.Tell(new LeaveSession(outputs) { SessionId = session });
        await BarrierAsync(owner);
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        owner.Tell(ReceiveTimeout.Instance);
        await BarrierAsync(owner);
        ((TestScheduler)Sys.Scheduler).Advance(TimeSpan.FromSeconds(1));
        await watcher.ExpectTerminatedAsync(owner, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<ISessionEvent[]> ReadEventsAsync(SessionId session)
    {
        var completed = new TaskCompletionSource<ISessionEvent[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Sys.ActorOf(Props.Create(() => new EventReader($"session-{session.Value}", completed)));
        var watcher = CreateTestProbe();
        watcher.Watch(reader);
        try { return await completed.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken); }
        finally
        {
            Sys.Stop(reader);
            await watcher.ExpectTerminatedAsync(reader, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private static SendUserMessage Input(SessionId session, string text) => new()
    {
        SessionId = session, Content = text,
        Source = new MessageSource
        {
            ChannelType = ChannelType.SignalR, SenderId = new SenderId("operator-a"), TurnId = new TurnId(text),
            Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal, Principal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted)
        }
    };

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Subscriber : ReceiveActor
    {
        public Subscriber(IActorRef output) => Receive<SessionOutput>(message => output.Tell(message));
    }

    public sealed class CompletionDispatcherConfigurator : MessageDispatcherConfigurator
    {
        private readonly CompletionDispatcher _dispatcher;
        public CompletionDispatcherConfigurator(Config config, IDispatcherPrerequisites prerequisites)
            : base(config, prerequisites) => _dispatcher = new CompletionDispatcher(this);
        public override MessageDispatcher Dispatcher() => _dispatcher;
    }

    public sealed class CompletionDispatcher(MessageDispatcherConfigurator configurator) : CallingThreadDispatcher(configurator)
    {
        private readonly object _sync = new();
        private int _pending;
        private TaskCompletionSource _idle = NewSignal();

        public Task WhenIdle()
        {
            lock (_sync) return _pending == 0 ? Task.CompletedTask : _idle.Task;
        }

        protected override void ExecuteTask(IRunnable run)
        {
            lock (_sync)
            {
                if (_pending++ == 0) _idle = NewSignal();
            }
            try { base.ExecuteTask(run); }
            finally
            {
                lock (_sync)
                {
                    if (--_pending == 0) _idle.TrySetResult();
                }
            }
        }
    }

    private sealed class EventReader : ReceivePersistentActor
    {
        public EventReader(string id, TaskCompletionSource<ISessionEvent[]> completed)
        {
            PersistenceId = id;
            var events = new List<ISessionEvent>();
            Recover<ISessionEvent>(events.Add);
            Recover<RecoveryCompleted>(_ => completed.TrySetResult(events.ToArray()));
        }
        public override string PersistenceId { get; }
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally { if (_directory is not null) await _directory.DisposeAsync(); }
    }
}
