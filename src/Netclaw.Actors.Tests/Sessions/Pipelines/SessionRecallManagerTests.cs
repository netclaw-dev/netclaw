// -----------------------------------------------------------------------
// <copyright file="SessionRecallManagerTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Pipelines;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions.Pipelines;

public class SessionRecallManagerTests
{
    [Fact]
    public void ResolveForTurn_ReturnsEmptyForPublicAudience()
    {
        var manager = new SessionRecallManager();
        var source = new MessageSource
        {
            ChannelType = ChannelType.Slack,
            SenderId = new SenderId("U123"),
            Audience = TrustAudience.Public,
            Boundary = TrustBoundary.Public,
            Principal = PrincipalClassification.UntrustedExternal,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
        };
        var state = SessionState.Empty.AddUserMessage("Tell me a secret");

        var result = manager.ResolveForTurn(
            recallQuery: null,
            state,
            new SessionId("slack/thread-1"),
            source,
            new TrackingCoordinator(),
            memoryEnabled: true);

        Assert.Empty(result.Items);
        Assert.False(result.Degraded);
    }

    [Fact]
    public void ResolveForTurn_ReturnsEmptyWhenMemoryDisabled()
    {
        var manager = new SessionRecallManager();
        var source = new MessageSource
        {
            ChannelType = ChannelType.Tui,
            SenderId = new SenderId("local-user"),
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            Principal = PrincipalClassification.UntrustedExternal,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
        };
        var state = SessionState.Empty.AddUserMessage("Search for memories");

        var result = manager.ResolveForTurn(
            recallQuery: null,
            state,
            new SessionId("tui/session-1"),
            source,
            new TrackingCoordinator(),
            memoryEnabled: false);

        Assert.Empty(result.Items);
        Assert.False(result.Degraded);
    }

    [Fact]
    public void ResolveForTurn_InvokesCoordinatorForPersonalAudience()
    {
        var manager = new SessionRecallManager();
        var coordinator = new TrackingCoordinator();
        var source = new MessageSource
        {
            ChannelType = ChannelType.Tui,
            SenderId = new SenderId("local-user"),
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            Principal = PrincipalClassification.UntrustedExternal,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
        };
        var state = SessionState.Empty.AddUserMessage("What do you remember about the project?");

        var result = manager.ResolveForTurn(
            recallQuery: null,
            state,
            new SessionId("tui/session-1"),
            source,
            coordinator,
            memoryEnabled: true);

        // Coordinator was actually called (not short-circuited)
        Assert.Equal(1, coordinator.CallCount);
    }

    [Fact]
    public void ResolveForTurn_FallsBackToPublicWhenSourceNull()
    {
        var manager = new SessionRecallManager();
        var coordinator = new TrackingCoordinator();
        // No source — the session ID prefix is "webhook" which resolves to Public
        var state = SessionState.Empty.AddUserMessage("test query");

        var result = manager.ResolveForTurn(
            recallQuery: null,
            state,
            new SessionId("webhook/delivery-1"),
            turnSource: null,
            coordinator,
            memoryEnabled: true);

        // Should short-circuit as Public audience
        Assert.Empty(result.Items);
        Assert.Equal(0, coordinator.CallCount);
    }

    /// <summary>
    /// Tracking coordinator that counts invocations and returns empty results.
    /// </summary>
    private sealed class TrackingCoordinator : IMemoryRecallCoordinator
    {
        public int CallCount { get; private set; }

        public Task<AutomaticRecallResult> RecallAsync(AutomaticRecallRequest request, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new AutomaticRecallResult([]));
        }
    }
}
