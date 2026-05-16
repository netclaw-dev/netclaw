// -----------------------------------------------------------------------
// <copyright file="SignalRMessageExtractorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Xunit;

namespace Netclaw.Daemon.Tests.Gateway;

/// <summary>
/// Tests for <see cref="SignalRMessageExtractor"/>'s entity-id routing.
/// The extractor must match channel-internal
/// <see cref="ISignalRSessionMessage"/> first and fall through to
/// <see cref="IWithSessionId"/> for upstream protocol messages (Mode B
/// reminder re-entry via <see cref="DeliverTrustedSessionTurn"/>).
/// </summary>
public sealed class SignalRMessageExtractorTests
{
    private static readonly MessageSource EmptySource = new()
    {
        ChannelType = ChannelType.SignalR,
        SenderId = new SenderId("test"),
        Audience = TrustAudience.Public,
        Boundary = TrustBoundary.Public,
        Principal = PrincipalClassification.UntrustedExternal,
        Provenance = new SourceProvenance(TransportAuthenticity.Unverified, PayloadTaint.Public)
    };

    [Fact]
    public void EntityId_returns_SessionId_for_ISignalRSessionMessage()
    {
        var extractor = new SignalRMessageExtractor();
        var sessionId = new SessionId("signalr/abc123");
        var msg = new StartSignalRSession(sessionId, ChannelType.SignalR, new SignalRConnectionId("conn-1"));

        var entityId = extractor.EntityId(msg);

        Assert.Equal("signalr/abc123", entityId);
    }

    [Fact]
    public void EntityId_returns_SessionId_for_DeliverTrustedSessionTurn_via_IWithSessionId_fallback()
    {
        var extractor = new SignalRMessageExtractor();
        var sessionId = new SessionId("signalr/reminder-target");
        var msg = new DeliverTrustedSessionTurn(sessionId, "reminder content", EmptySource);

        var entityId = extractor.EntityId(msg);

        Assert.Equal("signalr/reminder-target", entityId);
    }

    [Fact]
    public void EntityId_returns_null_for_unknown_message_types()
    {
        var extractor = new SignalRMessageExtractor();

        Assert.Null(extractor.EntityId(new object()));
        Assert.Null(extractor.EntityId("a string"));
        Assert.Null(extractor.EntityId(42));
    }
}
