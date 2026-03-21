using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class MessageSourceFactoryTests
{
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
}
