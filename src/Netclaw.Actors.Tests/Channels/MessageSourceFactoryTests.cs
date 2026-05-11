// -----------------------------------------------------------------------
// <copyright file="MessageSourceFactoryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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

    [Fact]
    public void Create_uses_strict_pipeline_defaults_when_input_has_no_hints()
    {
        var input = new ChannelInput
        {
            SenderId = "user-1",
            Contents = [new TextContent("hello")],
            ReceivedAt = DateTimeOffset.UtcNow
        };

        var options = new SessionPipelineOptions
        {
            ChannelType = ChannelType.Slack
        };

        var result = MessageSourceFactory.Create(input, options, "turn-1");

        Assert.Equal(TrustAudience.Public, result.Audience);
        Assert.Equal(SecurityPolicyDefaults.SlackWorkspaceBoundary, result.Boundary);
        Assert.Equal(PrincipalClassification.UntrustedExternal, result.Principal);
        Assert.Equal(TransportAuthenticity.Unverified, result.Provenance.TransportAuthenticity);
        Assert.Equal(PayloadTaint.Public, result.Provenance.PayloadTaint);
    }

    [Fact]
    public void Create_prefers_explicit_input_hints_over_pipeline_defaults()
    {
        var input = new ChannelInput
        {
            SenderId = "user-1",
            Audience = TrustAudience.Team,
            Boundary = SecurityPolicyDefaults.TeamBoundary,
            Principal = PrincipalClassification.TrustedInternal,
            Provenance = new SourceProvenance
            {
                TransportAuthenticity = TransportAuthenticity.Verified,
                PayloadTaint = PayloadTaint.Community,
                SourceKind = "slack"
            },
            Contents = [new TextContent("hello")],
            ReceivedAt = DateTimeOffset.UtcNow
        };

        var options = new SessionPipelineOptions
        {
            ChannelType = ChannelType.Slack,
            DefaultAudience = TrustAudience.Public,
            DefaultPrincipal = PrincipalClassification.UntrustedExternal,
            DefaultProvenance = SourceProvenance.StrictDefault()
        };

        var result = MessageSourceFactory.Create(input, options, "turn-1");

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
        var input = new ChannelInput
        {
            SenderId = "user-1",
            Contents = [new TextContent("hello")],
            ReceivedAt = DateTimeOffset.UtcNow
        };

        var options = new SessionPipelineOptions { ChannelType = ChannelType.Slack };

        var result = MessageSourceFactory.Create(input, options, "turn-1");

        Assert.Null(result.ReminderId);
        Assert.Null(result.AckTarget);
    }

    [Fact]
    public void Create_propagates_ReminderId_and_AckTarget_from_ChannelInput()
    {
        var probe = CreateTestProbe("ack-probe");

        var input = new ChannelInput
        {
            SenderId = "reminder-system",
            Contents = [new TextContent("check PR")],
            ReceivedAt = DateTimeOffset.UtcNow,
            ReminderId = "check-pr:1712000000000",
            AckTarget = probe.Ref
        };

        var options = new SessionPipelineOptions { ChannelType = ChannelType.Slack };

        var result = MessageSourceFactory.Create(input, options, "turn-1");

        Assert.Equal("check-pr:1712000000000", result.ReminderId);
        Assert.Same(probe.Ref, result.AckTarget);
    }

    [Fact]
    public void Create_propagates_self_only_adopted_context_without_third_party_flag()
    {
        var input = new ChannelInput
        {
            SenderId = "user-1",
            Contents = [new TextContent("hello")],
            ReceivedAt = DateTimeOffset.UtcNow,
            HasThirdPartyAdoptedContext = false,
            AdoptedSpeakerIds = ["user-1"]
        };

        var result = MessageSourceFactory.Create(input, new SessionPipelineOptions { ChannelType = ChannelType.Slack }, "turn-1");

        Assert.True(result.HasAdoptedContext);
        Assert.False(result.HasThirdPartyAdoptedContext);
        Assert.Equal(["user-1"], result.AdoptedSpeakerIds);
    }

    [Fact]
    public void Create_propagates_third_party_adopted_context_flag()
    {
        var input = new ChannelInput
        {
            SenderId = "user-1",
            Contents = [new TextContent("hello")],
            ReceivedAt = DateTimeOffset.UtcNow,
            HasThirdPartyAdoptedContext = true,
            AdoptedSpeakerIds = ["user-1", "user-2"]
        };

        var result = MessageSourceFactory.Create(input, new SessionPipelineOptions { ChannelType = ChannelType.Slack }, "turn-1");

        Assert.True(result.HasAdoptedContext);
        Assert.True(result.HasThirdPartyAdoptedContext);
        Assert.Equal(["user-1", "user-2"], result.AdoptedSpeakerIds);
    }
}
