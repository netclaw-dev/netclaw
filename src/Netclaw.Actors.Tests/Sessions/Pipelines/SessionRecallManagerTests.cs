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

    [Fact]
    public void ResolveForTurn_uses_turn_context_over_live_source_for_recovered_turns()
    {
        var manager = new SessionRecallManager();
        var coordinator = new TrackingCoordinator();
        var source = new MessageSource
        {
            ChannelType = ChannelType.Slack,
            SenderId = new SenderId("U123"),
            Audience = TrustAudience.Public,
            Boundary = TrustBoundary.Public,
            Principal = PrincipalClassification.UntrustedExternal,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public)
        };
        var turnContext = new TurnContext
        {
            SessionId = new SessionId("slack/thread-1"),
            TurnId = new TurnId("turn-1"),
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            ChannelType = ChannelType.Slack,
            RequesterSenderId = new SenderId("U123"),
            RequesterPrincipal = PrincipalClassification.TrustedInternal,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            SupportsInteractiveApproval = true
        };
        var state = SessionState.Empty.AddUserMessage("Search the team memory");

        manager.ResolveForTurn(
            recallQuery: null,
            state,
            new SessionId("slack/thread-1"),
            source,
            coordinator,
            memoryEnabled: true,
            turnContext: turnContext);

        Assert.Equal(1, coordinator.CallCount);
        Assert.NotNull(coordinator.LastRequest);
        Assert.Equal(TrustAudience.Team, coordinator.LastRequest.Audience);
        Assert.Equal(TrustBoundary.Team.Value, coordinator.LastRequest.Boundary);
    }

    /// <summary>
    /// Tracking coordinator that counts invocations and returns empty results.
    /// </summary>
    private sealed class TrackingCoordinator : IMemoryRecallCoordinator
    {
        public int CallCount { get; private set; }

        public AutomaticRecallRequest? LastRequest { get; private set; }

        public Task<AutomaticRecallResult> RecallAsync(AutomaticRecallRequest request, CancellationToken ct = default)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(new AutomaticRecallResult([]));
        }
    }
}
