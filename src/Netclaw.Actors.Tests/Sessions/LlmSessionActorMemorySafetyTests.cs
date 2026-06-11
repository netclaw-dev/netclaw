// -----------------------------------------------------------------------
// <copyright file="LlmSessionActorMemorySafetyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class LlmSessionActorMemorySafetyTests
{
    [Fact]
    public void Memory_curation_skip_uses_recovered_turn_context_when_source_is_absent()
    {
        var turnContext = BuildTurnContext(hasThirdPartyAdoptedContext: true);

        var skip = LlmSessionActor.ShouldSkipMemoryCurationForThirdPartyAdoptedContext(
            turnContext,
            turnSource: null);

        Assert.True(skip);
    }

    [Fact]
    public void Memory_curation_skip_prefers_turn_context_over_stale_source()
    {
        var turnContext = BuildTurnContext(hasThirdPartyAdoptedContext: false);
        var source = new MessageSource
        {
            ChannelType = ChannelType.Slack,
            SenderId = new SenderId("U12345"),
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            Principal = PrincipalClassification.TrustedInternal,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
            HasThirdPartyAdoptedContext = true
        };

        var skip = LlmSessionActor.ShouldSkipMemoryCurationForThirdPartyAdoptedContext(
            turnContext,
            source);

        Assert.False(skip);
    }

    private static TurnContext BuildTurnContext(bool hasThirdPartyAdoptedContext) => new()
    {
        SessionId = new SessionId("slack/thread-1"),
        TurnId = new TurnId("turn-1"),
        Audience = TrustAudience.Team,
        Boundary = TrustBoundary.Team,
        ChannelType = ChannelType.Slack,
        RequesterSenderId = new SenderId("U12345"),
        RequesterPrincipal = PrincipalClassification.TrustedInternal,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community),
        HasAdoptedContext = hasThirdPartyAdoptedContext,
        HasThirdPartyAdoptedContext = hasThirdPartyAdoptedContext,
        AdoptedSpeakerIds = hasThirdPartyAdoptedContext ? ["U67890"] : [],
        SupportsInteractiveApproval = true
    };
}
