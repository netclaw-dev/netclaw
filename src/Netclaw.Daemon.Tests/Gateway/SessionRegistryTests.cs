// -----------------------------------------------------------------------
// <copyright file="SessionRegistryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Claims;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Tests.Gateway;

/// <summary>
/// Unit tests for <see cref="SessionRegistry"/> coordination behavior.
/// Stream materialization and pipeline lifecycle are now handled by
/// <see cref="SignalRSessionActor"/> — these tests focus on the registry's
/// session-tracking, connection-binding, and message-routing responsibilities.
/// </summary>
public sealed class SessionRegistryTests(ITestOutputHelper output) : TestKit(output: output)
{
    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
        => builder.AddHocon("akka.loglevel = ERROR", HoconAddMode.Prepend);

    private SessionRegistry BuildRegistry(
        SessionIngressGate? ingressGate = null,
        IRequiredActor<SignalRGatewayActorKey>? actorProvider = null)
        => new(
            actorProvider ?? new StubRequiredActor(),
            new NoopSessionPipeline(),
            ingressGate ?? new SessionIngressGate(),
            new ClaimsPrincipalMapper(),
            TimeProvider.System,
            NullLogger<SessionRegistry>.Instance);

    [Fact]
    public async Task CreateSession_returns_valid_session_id()
    {
        var registry = BuildRegistry();

        var sessionId = await registry.CreateSessionAsync("conn-1", "tui");

        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        Assert.StartsWith("signalr/", sessionId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureSession_creates_new_session_when_no_id_provided()
    {
        var registry = BuildRegistry();

        var result = await registry.EnsureSessionAsync("conn-1", null, "tui");

        Assert.True(result.Created);
        Assert.False(string.IsNullOrWhiteSpace(result.SessionId));
    }

    [Fact]
    public async Task EnsureSession_reuses_existing_session_when_id_is_known()
    {
        var registry = BuildRegistry();
        var sessionId = await registry.CreateSessionAsync("conn-1", "tui");

        var result = await registry.EnsureSessionAsync("conn-2", sessionId, "tui");

        Assert.False(result.Created);
        Assert.Equal(sessionId, result.SessionId);
    }

    [Fact]
    public async Task EnsureSession_creates_binding_when_id_is_unknown()
    {
        var registry = BuildRegistry();
        // Provide a session ID we haven't seen before
        var unknownId = "signalr/00000000000000000000000000000000";

        var result = await registry.EnsureSessionAsync("conn-1", unknownId, "tui");

        Assert.False(result.Created);
        Assert.Equal(unknownId, result.SessionId);

        // Subsequent EnsureSession should find it now
        var result2 = await registry.EnsureSessionAsync("conn-2", unknownId, "tui");
        Assert.False(result2.Created);
        Assert.Equal(unknownId, result2.SessionId);
    }

    [Fact]
    public async Task AttachSession_throws_when_session_not_found()
    {
        var registry = BuildRegistry();

        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => registry.AttachSessionAsync("conn-1", "signalr/nonexistent"));
    }

    [Fact]
    public async Task AttachSession_succeeds_for_known_session()
    {
        var registry = BuildRegistry();
        var sessionId = await registry.CreateSessionAsync("conn-1", "tui");

        // Should not throw
        await registry.AttachSessionAsync("conn-2", sessionId);
    }

    [Fact]
    public async Task SendMessage_throws_when_connection_has_no_session()
    {
        var registry = BuildRegistry();
        await registry.CreateSessionAsync("conn-1", "tui");

        // conn-2 is not attached to any session
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => registry.SendMessageAsync("conn-2", "signalr/any", "hello"));
    }

    [Fact]
    public async Task SendMessage_throws_when_connection_attached_to_different_session()
    {
        var registry = BuildRegistry();
        var sessionId = await registry.CreateSessionAsync("conn-1", "tui");
        var otherSessionId = await registry.CreateSessionAsync("conn-2", "tui");

        // conn-1 is attached to sessionId, not otherSessionId
        await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => registry.SendMessageAsync("conn-1", otherSessionId, "hello"));
    }

    [Fact]
    public async Task ShutdownAsync_clears_all_sessions()
    {
        var registry = BuildRegistry();
        await registry.CreateSessionAsync("conn-1", "tui");
        await registry.CreateSessionAsync("conn-2", "tui");

        await registry.ShutdownAsync(CancellationToken.None);

        // After shutdown, no sessions should be known; EnsureSession creates a fresh one
        var result = await registry.EnsureSessionAsync("conn-3", "signalr/any-old-id", "tui");
        // The old ID was unknown after shutdown, so it's created fresh
        Assert.Equal("signalr/any-old-id", result.SessionId);
    }

    [Fact]
    public async Task EnsureSession_throws_when_ingress_closed()
    {
        var gate = new SessionIngressGate();
        gate.TryClose(SessionIngressGate.RestartInProgressMessage);
        var registry = BuildRegistry(gate);

        var ex = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => registry.EnsureSessionAsync("conn-1", null, "tui"));

        Assert.Equal(SessionIngressGate.RestartInProgressMessage, ex.Message);
    }

    [Fact]
    public async Task SendMessage_throws_when_ingress_closed()
    {
        var gate = new SessionIngressGate();
        var registry = BuildRegistry(gate);
        var sessionId = await registry.CreateSessionAsync("conn-1", "tui");
        gate.TryClose(SessionIngressGate.RestartInProgressMessage);

        var ex = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(
            () => registry.SendMessageAsync("conn-1", sessionId, "hello"));

        Assert.Equal(SessionIngressGate.RestartInProgressMessage, ex.Message);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task SendMessage_preserves_identity_and_waits_for_the_admission_result(bool authenticated, bool reject)
    {
        var inputs = CreateTestProbe();
        var replies = CreateTestProbe();
        var gateway = Sys.ActorOf(Props.Create(() => new AdmissionGateway(inputs, replies)));
        var registry = BuildRegistry(actorProvider: new RequiredGateway(gateway));
        var sessionId = await registry.CreateSessionAsync("conn-1", "tui");
        var principal = authenticated ? new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(NetclawClaimTypes.PrincipalClassification, nameof(PrincipalClassification.Operator)),
            new Claim(NetclawClaimTypes.TransportAuthenticity, nameof(TransportAuthenticity.LocalProcess)),
            new Claim(NetclawClaimTypes.DeviceId, "local")
        ], "test")) : null;
        var send = registry.SendMessageAsync("conn-1", sessionId, "hello", principal);
        var enqueue = await inputs.ExpectMsgAsync<EnqueueSignalRInput>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(authenticated ? "local" : "unknown", enqueue.Input.SenderId.Value);
        Assert.Equal(authenticated ? PrincipalClassification.Operator : PrincipalClassification.UntrustedExternal, enqueue.Input.Principal);
        Assert.Equal(authenticated ? TransportAuthenticity.LocalProcess : TransportAuthenticity.Unknown, enqueue.Input.Provenance!.TransportAuthenticity);
        Assert.False(send.IsCompleted);
        enqueue.Input.AckTarget!.Tell(reject ? CommandNack.For(enqueue.SessionId, "rejected") : CommandAck.For(enqueue.SessionId));
        if (reject) Assert.Equal("Text rejected: rejected", (await Assert.ThrowsAsync<HubException>(() => send)).Message);
        else await send;
    }

    /// <summary>
    /// Stub implementation of <see cref="IRequiredActor{T}"/> that returns
    /// <see cref="ActorRefs.Nobody"/> for all requests. Used to isolate
    /// <see cref="SessionRegistry"/> from the actor system in unit tests.
    /// </summary>
    private sealed class StubRequiredActor : IRequiredActor<SignalRGatewayActorKey>
    {
        public IActorRef ActorRef => ActorRefs.Nobody;

        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IActorRef>(ActorRefs.Nobody);
    }

    private sealed record RequiredGateway(IActorRef ActorRef) : IRequiredActor<SignalRGatewayActorKey>
    {
        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(ActorRef);
    }

    private sealed class AdmissionGateway : ReceiveActor
    {
        public AdmissionGateway(IActorRef inputs, IActorRef replies)
        {
            Receive<EnqueueSignalRInput>(input => inputs.Tell(input));
            ReceiveAny(_ => replies.Tell(Akka.Done.Instance));
        }
    }

    private sealed class NoopSessionPipeline : ISessionPipeline
    {
        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            Akka.Streams.IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default)
            => Task.FromResult<ISessionResponse>(CommandAck.For(feedback.SessionId));
    }
}
