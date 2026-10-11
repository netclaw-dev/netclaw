// -----------------------------------------------------------------------
// <copyright file="SignalRAdmissionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.TestKit;
using Akka.Persistence;
using Akka.Persistence.Hosting;
using Akka.Persistence.Journal;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Tools;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Security;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Tests.Gateway;

public sealed class SignalRAdmissionTests(ITestOutputHelper output) : TestKit(output: output), IAsyncDisposable
{
    private readonly DisposableTempDir _directory = new();
    private readonly HeldModel _model = new();
    private readonly HeldContext _context = new();
    private bool _failInitialization;
    private QueueFaultPipeline? _queueFault;
    private WebApplication? _app;

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        var paths = new NetclawPaths(_directory.Path);
        paths.EnsureDirectoriesExist();
        services.AddSingleton(paths);
        services.AddSingleton(new SessionConfig { Tuning = new SessionTuning { TitleGenerationInterval = 0 } });
        services.AddSingleton(new ModelCapabilities { ModelId = "held-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionServices(new SingleClientProvider(_model),
            new StaticSystemPromptProvider("Reply to the user."), [], _context,
            TimeProvider.System, paths, new TestSessionStorageResolver(paths)));
        services.AddSingleton<ISessionStorageResolver>(new TestSessionStorageResolver(paths));
        services.AddSingleton<ISessionPipeline>(provider => _failInitialization
            ? new BrokenPipeline() : _queueFault ?? (ISessionPipeline)ActivatorUtilities.CreateInstance<SessionPipeline>(provider));
        services.AddSingleton<IHubContext<SessionHub, ISessionHubClient>>(_ =>
            (_app ?? throw new InvalidOperationException("The test server is not initialized."))
            .Services.GetRequiredService<IHubContext<SessionHub, ISessionHubClient>>());
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.AddHocon("akka.scheduler.implementation = \"Akka.TestKit.TestScheduler, Akka.TestKit\"", HoconAddMode.Prepend)
            .WithInMemoryJournal().WithInMemorySnapshotStore().WithNetclawSerialization()
            .AddHocon($$"""
                akka.persistence.journal.plugin = test-journal
                test-journal {
                    class = "{{typeof(GatedJournal).AssemblyQualifiedName}}"
                    plugin-dispatcher = akka.actor.default-dispatcher
                }
                """, HoconAddMode.Prepend)
            .WithSessionManager().WithSignalRGateway();
    }

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, false, false, true)]
    public async Task Hub_success_follows_journal_admission_and_disconnect_does_not_stop_the_turn(bool rejectWrite, bool beforeModel, bool buffered, bool compacting, bool eager)
    {
        _context.Hold = beforeModel;
        var ct = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync();
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var output = client.On<SessionOutputDto>("ReceiveOutput", value =>
        {
            if (value.Type == SessionOutputTypes.SessionJoined) initialized.TrySetResult();
        });
        var journal = Persistence.Instance.Apply(Sys).JournalFor("test-journal");
        var writes = CreateTestProbe();
        var completion = CreateTestProbe();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ensured = await client.InvokeAsync<SessionEnsureResultDto>("EnsureSession", null, "tui", ct);
        Assert.Equal(1, ensured.TextAdmissionVersion);
        if (eager) await initialized.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        if (compacting)
        {
            _model.Release.TrySetResult();
            await journal.Ask<Done>(new WriteGate(writes, Task.CompletedTask, false), TimeSpan.FromSeconds(10), ct);
            for (var turn = 0; turn < 3; turn++)
            {
                await client.InvokeAsync("SendMessage", ensured.SessionId, $"history-{turn}", ct);
                await writes.FishForMessageAsync(message => message is TurnRecorded, TimeSpan.FromSeconds(10), cancellationToken: ct);
            }
            _model.Overflow = true;
            await client.InvokeAsync("SendMessage", ensured.SessionId, "trigger-compaction", ct);
            await _model.CompactionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        if (buffered)
        {
            await client.InvokeAsync("SendMessage", ensured.SessionId, "warmup", ct);
            await _model.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        await journal.Ask<Done>(new WriteGate(writes, release.Task, rejectWrite), TimeSpan.FromSeconds(10), ct);
        var send = client.InvokeAsync("SendMessage", ensured.SessionId, "admission-marker", ct);
        _ = send.ContinueWith(task => completion.Ref.Tell(task.Status), ct, TaskContinuationOptions.None, TaskScheduler.Default);
        try
        {
            var admitted = await writes.FishForMessageAsync<InputAdmitted>(input => input.UserMessage.Content == "admission-marker", TimeSpan.FromSeconds(10), cancellationToken: ct);
            Assert.Equal("admission-marker", admitted.UserMessage.Content);
            Assert.Equal(PrincipalClassification.Operator, admitted.TurnContext.RequesterPrincipal);
            await completion.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(250), ct);
            release.TrySetResult();
            if (rejectWrite)
            {
                await Assert.ThrowsAnyAsync<Exception>(() => send);
                Assert.Equal(buffered ? 1 : 0, _model.Calls);
                return;
            }

            await send.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.False((compacting ? _model.CompactionRelease : _model.Release).Task.IsCompleted);
            if (beforeModel)
            {
                await _context.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                Assert.Equal(0, _model.Calls);
            }
            else await _model.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

            TestProbe? sessionWatcher = null;
            IActorRef? sessionActor = null;
            if (compacting)
            {
                var gateway = ActorRegistry.Get<SignalRGatewayActorKey>();
                sessionActor = await Sys.ActorSelection(
                        $"{gateway.Path}/{Uri.EscapeDataString(ensured.SessionId)}")
                    .ResolveOne(TimeSpan.FromSeconds(10), ct);
                sessionWatcher = CreateTestProbe("signalr-session-watch");
                sessionWatcher.Watch(sessionActor);
            }

            await client.StopAsync(ct);
            if (sessionActor is not null)
            {
                await sessionWatcher!.ExpectTerminatedAsync(
                    sessionActor,
                    TimeSpan.FromSeconds(10),
                    cancellationToken: ct);
            }

            _context.Release.TrySetResult();
            _model.Release.TrySetResult();
            _model.CompactionRelease.TrySetResult();
            await writes.FishForMessageAsync<TurnRecorded>(turn => turn.ConsumedInputIds.Contains(admitted.InputId), TimeSpan.FromSeconds(10), cancellationToken: ct);
            Assert.Equal(compacting ? 5 : buffered ? 2 : 1, _model.Calls);
        }
        finally
        {
            release.TrySetResult();
            _context.Release.TrySetResult();
            _model.Release.TrySetResult();
            _model.CompactionRelease.TrySetResult();
        }
    }

    [Fact]
    public async Task Subscriber_termination_during_admission_persist_does_not_stop_the_turn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync();
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var output = client.On<SessionOutputDto>("ReceiveOutput", value =>
        {
            if (value.Type == SessionOutputTypes.SessionJoined) initialized.TrySetResult();
        });
        var ensured = await client.InvokeAsync<SessionEnsureResultDto>("EnsureSession", null, "tui", ct);
        await initialized.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        var sessionId = new SessionId(ensured.SessionId);
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var departingSubscriber = CreateTestProbe("departing-session-subscriber");
        await sessionManager.Ask<SessionJoined>(
            new JoinSession(departingSubscriber.Ref) { SessionId = sessionId, Filter = OutputFilter.None },
            TimeSpan.FromSeconds(10),
            ct);
        var liveSubscriber = CreateTestProbe("live-session-subscriber");
        await sessionManager.Ask<SessionJoined>(
            new JoinSession(liveSubscriber.Ref) { SessionId = sessionId, Filter = OutputFilter.None },
            TimeSpan.FromSeconds(10),
            ct);
        await liveSubscriber.ExpectMsgAsync<SessionJoined>(TimeSpan.FromSeconds(10), cancellationToken: ct);

        var journal = Persistence.Instance.Apply(Sys).JournalFor("test-journal");
        var writes = CreateTestProbe();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await journal.Ask<Done>(new WriteGate(writes, release.Task, false), TimeSpan.FromSeconds(10), ct);

        var send = client.InvokeAsync("SendMessage", ensured.SessionId, "subscriber-death-during-persist", ct);
        try
        {
            var admitted = await writes.FishForMessageAsync<InputAdmitted>(
                input => input.UserMessage.Content == "subscriber-death-during-persist",
                TimeSpan.FromSeconds(10),
                cancellationToken: ct);

            var subscriberWatcher = CreateTestProbe("session-subscriber-watch");
            subscriberWatcher.Watch(departingSubscriber.Ref);
            Sys.Stop(departingSubscriber.Ref);
            await subscriberWatcher.ExpectTerminatedAsync(
                departingSubscriber.Ref,
                TimeSpan.FromSeconds(10),
                cancellationToken: ct);

            release.TrySetResult();
            await send.WaitAsync(TimeSpan.FromSeconds(10), ct);
            await _model.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            _model.Release.TrySetResult();

            await liveSubscriber.FishForMessageAsync<TurnCompleted>(
                _ => true,
                TimeSpan.FromSeconds(10),
                cancellationToken: ct);
            var recorded = await writes.FishForMessageAsync<TurnRecorded>(
                turn => turn.ConsumedInputIds.Contains(admitted.InputId),
                TimeSpan.FromSeconds(10),
                cancellationToken: ct);
            Assert.Contains(admitted.InputId, recorded.ConsumedInputIds);
        }
        finally
        {
            release.TrySetResult();
            _model.Release.TrySetResult();
            _context.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task Pipeline_initialization_failure_rejects_text_without_a_model_call()
    {
        _failInitialization = true;
        await using var client = await ConnectAsync();
        var ensured = await client.InvokeAsync<SessionEnsureResultDto>("EnsureSession", null, "tui", TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<HubException>(() => client.InvokeAsync("SendMessage", ensured.SessionId, "blocked", TestContext.Current.CancellationToken));
        Assert.Contains(SessionEnsureResultDto.TextRejectionPrefix, error.Message);
        Assert.Equal(0, _model.Calls);
    }

    [Theory]
    [InlineData("missing", "not initialized", false)]
    [InlineData("missing", "not initialized", true)]
    [InlineData("timeout", "write timed out", false)]
    [InlineData("timeout", "write timed out", true)]
    public async Task A_queue_failure_returns_a_negative_admission_result(string fault, string reason, bool trusted)
    {
        _queueFault = new QueueFaultPipeline();
        var ct = TestContext.Current.CancellationToken;
        await using var client = await ConnectAsync();
        var ensured = await client.InvokeAsync<SessionEnsureResultDto>("EnsureSession", null, "tui", ct);
        await _queueFault.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        if (fault == "missing")
        {
            _queueFault.Output!.Complete();
            await _queueFault.ReinitFailed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        if (trusted || fault == "timeout")
        {
            var rejected = CreateTestProbe();
            var gateway = ActorRegistry.Get<SignalRGatewayActorKey>();
            for (var index = 0; index < (fault == "timeout" ? 600 : 1); index++)
            {
                var source = new MessageSource
                {
                    ChannelType = ChannelType.SignalR,
                    Audience = TrustAudience.Personal, Boundary = TrustBoundary.TrustedInstance,
                    Principal = PrincipalClassification.Operator,
                    Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted),
                    SenderId = new SenderId("test"), ReceivedAt = TimeProvider.System.GetUtcNow()
                };
                object message = trusted
                    ? new DeliverTrustedSessionTurn(new SessionId(ensured.SessionId), "queue-pressure", source)
                    : new EnqueueSignalRInput(new SessionId(ensured.SessionId), new ChannelInput
                    {
                        Audience = source.Audience, Boundary = source.Boundary, Principal = source.Principal,
                        Provenance = source.Provenance, SenderId = source.SenderId,
                        Contents = [new TextContent("queue-pressure")], ReceivedAt = source.ReceivedAt,
                        AckTarget = rejected
                    });
                gateway.Tell(message, rejected);
            }
            var nack = await rejected.ExpectMsgAsync<CommandNack>(TimeSpan.FromSeconds(20), cancellationToken: ct);
            Assert.Contains(reason, nack.Reason);
        }
        else
        {
            var error = await Assert.ThrowsAsync<HubException>(() => client.InvokeAsync("SendMessage", ensured.SessionId, "blocked", ct));
            Assert.Contains(SessionEnsureResultDto.TextRejectionPrefix, error.Message);
            Assert.Contains(reason, error.Message);
        }
        Assert.Equal(0, _model.Calls);
    }

    private async Task<HubConnection> ConnectAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(new DaemonConfig());
        builder.Services.AddAuthentication(LoopbackAuthenticationHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, LoopbackAuthenticationHandler>(
                LoopbackAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(new SessionRegistry(
            new RequiredGateway(ActorRegistry.Get<SignalRGatewayActorKey>()),
            Host.Services.GetRequiredService<ISessionPipeline>(), new SessionIngressGate(),
            new ClaimsPrincipalMapper(), TimeProvider.System, NullLogger<SessionRegistry>.Instance));
        _app = builder.Build();
        _app.Use(async (context, next) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; await next(context); });
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapHub<SessionHub>("/hub/session");
        await _app.StartAsync();
        var client = new HubConnectionBuilder().WithUrl("http://localhost/hub/session", options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => _app.GetTestServer().CreateHandler();
        }).Build();
        await client.StartAsync();
        return client;
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        _model.Release.TrySetResult();
        _model.CompactionRelease.TrySetResult();
        _context.Release.TrySetResult();
        if (_app is not null)
            await _app.DisposeAsync();
        try { await base.DisposeAsync(); }
        finally { _directory.Dispose(); }
    }

    private sealed record RequiredGateway(IActorRef ActorRef) : IRequiredActor<SignalRGatewayActorKey>
    {
        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(ActorRef);
    }

    private sealed class HeldContext : IWorkingContextSnapshotProvider
    {
        public bool Hold { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<WorkingContextSnapshot> CreateAsync(WorkingContext context, TrustAudience audience, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            if (Hold) await Release.Task.WaitAsync(cancellationToken);
            return new WorkingContextSnapshot { WorkingContext = context, Git = new GitWorkingContextInspection.Skipped() };
        }
    }

    private sealed class BrokenPipeline : ISessionPipeline
    {
        public Task<MaterializedSession> CreateAsync(SessionId sessionId, SessionPipelineOptions options,
            Akka.Streams.IMaterializer? materializer = null, CancellationToken cancellationToken = default)
            => Task.FromException<MaterializedSession>(new IOException("Pipeline creation failed."));
        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class QueueFaultPipeline : ISessionPipeline
    {
        private int _calls;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReinitFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ISourceQueueWithComplete<SessionOutput>? Output { get; private set; }
        public Task<MaterializedSession> CreateAsync(SessionId sessionId, SessionPipelineOptions options,
            IMaterializer? materializer = null, CancellationToken cancellationToken = default)
        {
            if (++_calls > 1)
            {
                ReinitFailed.TrySetResult();
                return Task.FromException<MaterializedSession>(new IOException("Reinitialization failed."));
            }
            var stream = materializer ?? throw new InvalidOperationException("The actor must supply its materializer.");
            var kill = KillSwitches.Shared("queue-fault");
            var sink = Sink.Queue<ChannelInput>().MapMaterializedValue(_ => NotUsed.Instance);
            var input = Flow.Create<ChannelInput>().Via(kill.Flow<ChannelInput>())
                .WatchTermination((_, done) => { StreamTaskObservation.ObserveSilently(done); Ready.TrySetResult(); return NotUsed.Instance; })
                .To(sink);
            var (queue, source) = Source.Queue<SessionOutput>(16, OverflowStrategy.Fail)
                .Via(kill.Flow<SessionOutput>()).PreMaterialize(stream);
            Output = queue;
            return Task.FromResult(new MaterializedSession(input, source, kill));
        }
        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class HeldModel : IChatClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public bool Overflow { get; set; }
        public TaskCompletionSource CompactionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompactionRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var compacting = messages.Any(message => message.Role == Microsoft.Extensions.AI.ChatRole.System
                && message.Text?.Contains("You are a session summarizer", StringComparison.Ordinal) == true);
            if (compacting)
            {
                CompactionStarted.TrySetResult();
                await CompactionRelease.Task.WaitAsync(cancellationToken);
            }
            else
            {
                Calls++;
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return new ChatResponse(new ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, "done"))
            {
                Usage = Overflow && !compacting ? new UsageDetails { InputTokenCount = 200_000, OutputTokenCount = 1 } : null
            };
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed record WriteGate(IActorRef Observer, Task Release, bool Reject);

    public sealed class GatedJournal : MemoryJournal
    {
        private WriteGate? _gate;
        protected override bool ReceivePluginInternal(object message)
        {
            if (message is not WriteGate gate) return base.ReceivePluginInternal(message);
            _gate = gate;
            Sender.Tell(Done.Instance);
            return true;
        }
        protected override async Task<IImmutableList<Exception>> WriteMessagesAsync(IEnumerable<AtomicWrite> messages, CancellationToken cancellationToken)
        {
            var writes = messages.ToArray();
            var payloads = writes.SelectMany(write => (IEnumerable<IPersistentRepresentation>)write.Payload).Select(write => write.Payload).ToArray();
            var gate = _gate;
            if (gate is not null && payloads.OfType<InputAdmitted>().FirstOrDefault() is { } admitted)
            {
                gate.Observer.Tell(admitted);
                await gate.Release.WaitAsync(cancellationToken);
                if (gate.Reject) return writes.Select(_ => new IOException("Journal rejected admission.")).Cast<Exception>().ToImmutableList();
            }
            var result = await base.WriteMessagesAsync(writes, cancellationToken);
            foreach (var turn in payloads.OfType<TurnRecorded>()) gate?.Observer.Tell(turn);
            return result;
        }
    }
}
