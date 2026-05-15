// -----------------------------------------------------------------------
// <copyright file="MessageSourceFactoryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class MessageSourceFactoryTests : TestKit
{
    public MessageSourceFactoryTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider) { }

    private static ChannelInput BuildInput(
        TrustAudience audience = TrustAudience.Public,
        string? boundary = null,
        PrincipalClassification principal = PrincipalClassification.UntrustedExternal,
        SourceProvenance? provenance = null,
        string? reminderId = null,
        IActorRef? ackTarget = null,
        bool hasThirdParty = false,
        IReadOnlyList<string>? adoptedSpeakerIds = null)
        => new()
        {
            SenderId = "user-1",
            Audience = audience,
            Boundary = boundary ?? SecurityPolicyDefaults.PublicBoundary,
            Principal = principal,
            Provenance = provenance
                ?? new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Public),
            Contents = [new TextContent("hello")],
            ReceivedAt = DateTimeOffset.UtcNow,
            ReminderId = reminderId,
            AckTarget = ackTarget,
            HasThirdPartyAdoptedContext = hasThirdParty,
            AdoptedSpeakerIds = adoptedSpeakerIds ?? [],
        };

    [Fact]
    public void Create_copies_trust_context_verbatim_from_ChannelInput()
    {
        var input = BuildInput(
            audience: TrustAudience.Team,
            boundary: SecurityPolicyDefaults.TeamBoundary,
            principal: PrincipalClassification.TrustedInternal,
            provenance: new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community)
            {
                SourceKind = "slack"
            });

        var result = MessageSourceFactory.Create(
            input, new SessionPipelineOptions { ChannelType = ChannelType.Slack }, "turn-1");

        // The factory is a pure mapper — trust context is whatever the adapter
        // stamped on the ChannelInput, never a pipeline-synthesized default.
        Assert.Equal(TrustAudience.Team, result.Audience);
        Assert.Equal(SecurityPolicyDefaults.TeamBoundary, result.Boundary);
        Assert.Equal(PrincipalClassification.TrustedInternal, result.Principal);
        Assert.Equal(TransportAuthenticity.Verified, result.Provenance.TransportAuthenticity);
        Assert.Equal(PayloadTaint.Community, result.Provenance.PayloadTaint);
        Assert.Equal("slack", result.Provenance.SourceKind);
    }

    [Fact]
    public void Create_propagates_null_ReminderId_and_AckTarget_by_default()
    {
        var result = MessageSourceFactory.Create(
            BuildInput(), new SessionPipelineOptions { ChannelType = ChannelType.Slack }, "turn-1");

        Assert.Null(result.ReminderId);
        Assert.Null(result.AckTarget);
    }

    [Fact]
    public void Create_propagates_ReminderId_and_AckTarget_from_ChannelInput()
    {
        var probe = CreateTestProbe("ack-probe");

        var input = BuildInput(
            reminderId: "check-pr:1712000000000",
            ackTarget: probe.Ref);

        var result = MessageSourceFactory.Create(
            input, new SessionPipelineOptions { ChannelType = ChannelType.Slack }, "turn-1");

        Assert.Equal("check-pr:1712000000000", result.ReminderId);
        Assert.Same(probe.Ref, result.AckTarget);
    }

    [Fact]
    public void Create_propagates_self_only_adopted_context_without_third_party_flag()
    {
        var input = BuildInput(hasThirdParty: false, adoptedSpeakerIds: ["user-1"]);

        var result = MessageSourceFactory.Create(
            input, new SessionPipelineOptions { ChannelType = ChannelType.Slack }, "turn-1");

        Assert.True(result.HasAdoptedContext);
        Assert.False(result.HasThirdPartyAdoptedContext);
        Assert.Equal(["user-1"], result.AdoptedSpeakerIds);
    }

    [Fact]
    public void Create_propagates_third_party_adopted_context_flag()
    {
        var input = BuildInput(hasThirdParty: true, adoptedSpeakerIds: ["user-1", "user-2"]);

        var result = MessageSourceFactory.Create(
            input, new SessionPipelineOptions { ChannelType = ChannelType.Slack }, "turn-1");

        Assert.True(result.HasAdoptedContext);
        Assert.True(result.HasThirdPartyAdoptedContext);
        Assert.Equal(["user-1", "user-2"], result.AdoptedSpeakerIds);
    }
}
