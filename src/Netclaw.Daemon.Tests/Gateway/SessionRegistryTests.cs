using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Protocol;
using Netclaw.Daemon.Gateway;
using Xunit;

namespace Netclaw.Daemon.Tests.Gateway;

/// <summary>
/// Unit tests for <see cref="SessionRegistry"/> coordination behavior.
/// Stream materialization and pipeline lifecycle are now handled by
/// <see cref="SignalRSessionActor"/> — these tests focus on the registry's
/// session-tracking, connection-binding, and message-routing responsibilities.
/// </summary>
public sealed class SessionRegistryTests
{
    private SessionRegistry BuildRegistry()
        => new(
            new StubRequiredActor(),
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
}
