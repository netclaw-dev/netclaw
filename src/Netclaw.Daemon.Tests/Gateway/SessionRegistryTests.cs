using Akka;
using Akka.Actor;
using Akka.Streams;
using Akka.Streams.Dsl;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Daemon.Gateway;
using Xunit;

namespace Netclaw.Daemon.Tests.Gateway;

/// <summary>
/// Integration tests for <see cref="SessionRegistry"/> using a real ActorSystem
/// for Akka.Streams materialization and a fake <see cref="ISessionPipeline"/>.
/// </summary>
public sealed class SessionRegistryTests : IAsyncLifetime
{
    private ActorSystem _system = null!;

    public Task InitializeAsync()
    {
        _system = ActorSystem.Create("session-registry-tests",
            "akka { loglevel = WARNING }");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _system.Terminate();
    }

    [Fact]
    public async Task EnsureSession_rematerializes_pipeline_when_output_stream_has_completed()
    {
        var pipeline = new TrackingFakeSessionPipeline(_system, neverComplete: false);
        var registry = BuildRegistry(pipeline);

        var sessionId = await registry.CreateSessionAsync("conn-1", "tui");
        var sessionIdParsed = new SessionId(sessionId);

        // Source.Empty completes immediately; wait for the output sink to finish
        var outputCompletion = registry.GetOutputCompletionForTesting(sessionIdParsed);
        Assert.NotNull(outputCompletion);
        await outputCompletion.WaitAsync(TimeSpan.FromSeconds(5));

        // EnsureSession from a new connection ID (simulate reconnect)
        var result = await registry.EnsureSessionAsync("conn-2", sessionId, "tui");

        // Pipeline should have been re-created because the output stream was dead
        Assert.Equal(2, pipeline.CreateCount);
        Assert.Equal(sessionId, result.SessionId);
        Assert.False(result.Created);
    }

    [Fact]
    public async Task EnsureSession_reuses_pipeline_when_output_stream_still_active()
    {
        var pipeline = new TrackingFakeSessionPipeline(_system, neverComplete: true);
        var registry = BuildRegistry(pipeline);

        var sessionId = await registry.CreateSessionAsync("conn-1", "tui");

        // EnsureSession from another connection — stream is alive, no re-materialization needed
        var result = await registry.EnsureSessionAsync("conn-2", sessionId, "tui");

        Assert.Equal(1, pipeline.CreateCount);
        Assert.Equal(sessionId, result.SessionId);
        Assert.False(result.Created);
    }

    [Fact]
    public async Task AttachSession_rematerializes_pipeline_when_output_stream_has_completed()
    {
        var pipeline = new TrackingFakeSessionPipeline(_system, neverComplete: false);
        var registry = BuildRegistry(pipeline);

        var sessionId = await registry.CreateSessionAsync("conn-1", "tui");
        var sessionIdParsed = new SessionId(sessionId);

        var outputCompletion = registry.GetOutputCompletionForTesting(sessionIdParsed);
        Assert.NotNull(outputCompletion);
        await outputCompletion.WaitAsync(TimeSpan.FromSeconds(5));

        // AttachSession from new connection after passivation
        await registry.AttachSessionAsync("conn-2", sessionId);

        Assert.Equal(2, pipeline.CreateCount);
    }

    [Fact]
    public async Task AttachSession_reuses_pipeline_when_output_stream_still_active()
    {
        var pipeline = new TrackingFakeSessionPipeline(_system, neverComplete: true);
        var registry = BuildRegistry(pipeline);

        var sessionId = await registry.CreateSessionAsync("conn-1", "tui");

        // Attach from another connection — stream is alive, no re-materialization needed
        await registry.AttachSessionAsync("conn-2", sessionId);

        Assert.Equal(1, pipeline.CreateCount);
    }

    private SessionRegistry BuildRegistry(ISessionPipeline pipeline)
        => new(
            pipeline,
            _system,
            TimeProvider.System,
            new NoopHubContext(),
            NullLogger<SessionRegistry>.Instance);

    /// <summary>
    /// Fake pipeline that returns <see cref="MaterializedSession"/> instances backed by
    /// controllable Akka.Streams sources. When <paramref name="neverComplete"/> is false,
    /// output uses <see cref="Source.Empty{TOut}"/> which completes immediately — simulating
    /// a dead output stream after actor passivation. When true, uses
    /// <see cref="Source.Never{T}"/> to keep the stream alive.
    /// </summary>
    private sealed class TrackingFakeSessionPipeline : ISessionPipeline
    {
        private readonly ActorSystem _system;
        private readonly bool _neverComplete;
        private int _createCount;

        public int CreateCount => _createCount;

        public TrackingFakeSessionPipeline(ActorSystem system, bool neverComplete)
        {
            _system = system;
            _neverComplete = neverComplete;
        }

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
        {
            var n = Interlocked.Increment(ref _createCount);

            var killSwitch = KillSwitches.Shared($"test-{sessionId.Value}-{n}");

            var input = Sink.Ignore<ChannelInput>()
                .MapMaterializedValue<NotUsed>(_ => NotUsed.Instance);

            Source<SessionOutput, NotUsed> output = _neverComplete
                ? Source.Never<SessionOutput>().Via(killSwitch.Flow<SessionOutput>())
                : Source.Empty<SessionOutput>().Via(killSwitch.Flow<SessionOutput>());

            return Task.FromResult(new MaterializedSession(input, output, killSwitch));
        }
    }

    /// <summary>Minimal hub context that silently accepts all output.</summary>
    private sealed class NoopHubContext : IHubContext<SessionHub, ISessionHubClient>
    {
        private static readonly NoopHubClients s_clients = new();

        public IHubClients<ISessionHubClient> Clients => s_clients;
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class NoopHubClients : IHubClients<ISessionHubClient>
    {
        private static readonly NoopClient s_client = new();

        public ISessionHubClient All => s_client;
        public ISessionHubClient AllExcept(IReadOnlyList<string> excluded) => s_client;
        public ISessionHubClient Client(string connectionId) => s_client;
        public ISessionHubClient Clients(IReadOnlyList<string> connectionIds) => s_client;
        public ISessionHubClient Group(string groupName) => s_client;
        public ISessionHubClient GroupExcept(string groupName, IReadOnlyList<string> excluded) => s_client;
        public ISessionHubClient Groups(IReadOnlyList<string> groupNames) => s_client;
        public ISessionHubClient User(string userId) => s_client;
        public ISessionHubClient Users(IReadOnlyList<string> userIds) => s_client;
    }

    private sealed class NoopClient : ISessionHubClient
    {
        public Task ReceiveOutput(SessionOutputDto dto) => Task.CompletedTask;
    }
}
