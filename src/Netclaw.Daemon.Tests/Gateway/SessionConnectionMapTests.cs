// -----------------------------------------------------------------------
// <copyright file="SessionConnectionMapTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Daemon.Gateway;
using Xunit;

namespace Netclaw.Daemon.Tests.Gateway;

public sealed class SessionConnectionMapTests
{
    [Fact]
    public void BindNewSession_adds_bidirectional_mapping()
    {
        var map = new SessionConnectionMap();
        var sessionId = new SessionId("signalr/session-1");
        var connectionId = SignalRConnectionId.Create("connection-1");

        var replacedSession = map.BindNewSession(sessionId, connectionId);

        Assert.False(replacedSession.HasValue);
        Assert.True(map.IsAttached(connectionId, sessionId));
        Assert.True(map.TryGetConnectionForSession(sessionId, out var mappedConnection));
        Assert.Equal(connectionId, mappedConnection);
        Assert.True(map.TryGetSessionForConnection(connectionId, out var mappedSession));
        Assert.Equal(sessionId, mappedSession);
    }

    [Fact]
    public void BindNewSession_replaces_existing_session_for_same_connection()
    {
        var map = new SessionConnectionMap();
        var firstSession = new SessionId("signalr/session-1");
        var secondSession = new SessionId("signalr/session-2");
        var connectionId = SignalRConnectionId.Create("connection-1");

        map.BindNewSession(firstSession, connectionId);
        var replacedSession = map.BindNewSession(secondSession, connectionId);

        Assert.True(replacedSession.HasValue);
        Assert.Equal(firstSession, replacedSession.Value);
        Assert.True(map.IsAttached(connectionId, secondSession));
        Assert.False(map.TryGetConnectionForSession(firstSession, out _));
    }

    [Fact]
    public void AttachSession_moves_session_to_new_connection_and_detaches_previous_owner()
    {
        var map = new SessionConnectionMap();
        var firstSession = new SessionId("signalr/session-1");
        var secondSession = new SessionId("signalr/session-2");
        var firstConnection = SignalRConnectionId.Create("connection-1");
        var secondConnection = SignalRConnectionId.Create("connection-2");

        map.BindNewSession(firstSession, firstConnection);
        map.BindNewSession(secondSession, secondConnection);

        map.AttachSession(firstSession, secondConnection);

        Assert.True(map.IsAttached(secondConnection, firstSession));
        Assert.False(map.IsAttached(firstConnection, firstSession));
        Assert.False(map.TryGetConnectionForSession(secondSession, out _));
    }

    [Fact]
    public void Disconnect_removes_mappings_for_connection_and_session()
    {
        var map = new SessionConnectionMap();
        var sessionId = new SessionId("signalr/session-1");
        var connectionId = SignalRConnectionId.Create("connection-1");

        map.BindNewSession(sessionId, connectionId);
        map.Disconnect(connectionId);

        Assert.False(map.IsAttached(connectionId, sessionId));
        Assert.False(map.TryGetConnectionForSession(sessionId, out _));
        Assert.False(map.TryGetSessionForConnection(connectionId, out _));
    }
}
