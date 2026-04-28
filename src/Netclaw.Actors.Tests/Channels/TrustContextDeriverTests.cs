// -----------------------------------------------------------------------
// <copyright file="TrustContextDeriverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class TrustContextDeriverTests
{
    [Fact]
    public void Derive_uses_deployment_defaults_when_source_missing()
    {
        var deriver = new TrustContextDeriver(new EffectivePolicyDefaults(
            DeploymentPosture.Public,
            TrustAudience.Public,
            ShellExecutionMode.Off,
            UsedStrictFallback: true));

        var result = deriver.Derive(null);

        Assert.Equal(TrustAudience.Public, result.EffectiveAudience);
        Assert.Equal(PrincipalClassification.UntrustedExternal, result.Principal);
        Assert.Equal(TransportAuthenticity.Unverified, result.TransportAuthenticity);
        Assert.Equal(PayloadTaint.Public, result.PayloadTaint);
        Assert.True(result.UsedStrictFallback);
    }

    [Fact]
    public void Derive_takes_narrowest_of_deployment_and_source_audience()
    {
        var deriver = new TrustContextDeriver(new EffectivePolicyDefaults(
            DeploymentPosture.Team,
            TrustAudience.Team,
            ShellExecutionMode.Off,
            UsedStrictFallback: false));

        var result = deriver.Derive(new MessageSource
        {
            ChannelType = ChannelType.Slack,
            SenderId = "U123",
            Audience = TrustAudience.Public,
            Principal = PrincipalClassification.TrustedInternal,
            Provenance = new SourceProvenance
            {
                TransportAuthenticity = TransportAuthenticity.Verified,
                PayloadTaint = PayloadTaint.Public,
                SourceKind = "slack"
            },
            ReceivedAt = DateTimeOffset.UtcNow
        });

        Assert.Equal(TrustAudience.Public, result.EffectiveAudience);
        Assert.True(result.WasDowngraded);
    }

    [Fact]
    public void Derive_applies_working_context_downgrade_last()
    {
        var deriver = new TrustContextDeriver(new EffectivePolicyDefaults(
            DeploymentPosture.Personal,
            TrustAudience.Personal,
            ShellExecutionMode.HostAllowed,
            UsedStrictFallback: false));

        var result = deriver.Derive(new MessageSource
        {
            ChannelType = ChannelType.SignalR,
            SenderId = "local-user",
            Audience = TrustAudience.Personal,
            Principal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance
            {
                TransportAuthenticity = TransportAuthenticity.LocalProcess,
                PayloadTaint = PayloadTaint.Trusted,
                SourceKind = "signalr"
            },
            ReceivedAt = DateTimeOffset.UtcNow
        }, new WorkingContextOverride(TrustAudience.Team, "sensitive-read"));

        Assert.Equal(TrustAudience.Team, result.EffectiveAudience);
        Assert.True(result.WasDowngraded);
        Assert.Equal("sensitive-read", result.DowngradeReason);
    }

    [Fact]
    public void Derive_does_not_upgrade_when_working_context_is_broader_than_effective_audience()
    {
        var deriver = new TrustContextDeriver(new EffectivePolicyDefaults(
            DeploymentPosture.Personal,
            TrustAudience.Personal,
            ShellExecutionMode.HostAllowed,
            UsedStrictFallback: false));

        var result = deriver.Derive(new MessageSource
        {
            ChannelType = ChannelType.Slack,
            SenderId = "U123",
            Audience = TrustAudience.Team,
            Principal = PrincipalClassification.TrustedInternal,
            Provenance = new SourceProvenance
            {
                TransportAuthenticity = TransportAuthenticity.Verified,
                PayloadTaint = PayloadTaint.Community,
                SourceKind = "slack"
            },
            ReceivedAt = DateTimeOffset.UtcNow
        }, new WorkingContextOverride(TrustAudience.Personal, "broader-than-source"));

        Assert.Equal(TrustAudience.Team, result.EffectiveAudience);
        Assert.True(result.WasDowngraded);
        Assert.Null(result.DowngradeReason);
    }
}
