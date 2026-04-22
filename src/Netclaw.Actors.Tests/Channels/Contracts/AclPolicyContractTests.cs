using Netclaw.Actors.Channels;
using Netclaw.Actors.Tests.Channels.TestHelpers;
using Netclaw.Channels;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

public abstract class AclPolicyContractTests
{
    protected abstract IAclDecision EvaluateDm(string userId, ChannelOptionsBuilder options);

    protected abstract IAclDecision EvaluateChannel(
        string channelId, string userId, ChannelOptionsBuilder options);

    protected abstract IAclDecision EvaluateMessage(
        string channelId, string userId, bool isDm, ChannelOptionsBuilder options);

    protected abstract string ExpectedSourceKind { get; }

    // --- Deny cases ---

    [Fact]
    public void Denies_missing_user_id()
    {
        var options = new ChannelOptionsBuilder { AllowDirectMessages = true };
        var result = EvaluateDm("", options);
        Assert.False(result.IsAllowed);
        Assert.Contains("missing_user_id", result.DenyReason!);
    }

    [Fact]
    public void Denies_dm_when_disabled()
    {
        var options = new ChannelOptionsBuilder { AllowDirectMessages = false };
        var result = EvaluateDm("user-1", options);
        Assert.False(result.IsAllowed);
        Assert.NotNull(result.DenyReason);
    }

    [Fact]
    public void Denies_channel_not_in_allowlist()
    {
        var options = new ChannelOptionsBuilder
        {
            AllowedChannelIds = ["ch-allowed"]
        };
        var result = EvaluateChannel("ch-other", "user-1", options);
        Assert.False(result.IsAllowed);
        Assert.Contains("channel_not_allowed", result.DenyReason!);
    }

    [Fact]
    public void Denies_user_outside_allowlist()
    {
        var options = new ChannelOptionsBuilder
        {
            AllowedChannelIds = ["ch-1"],
            AllowedUserIds = ["user-allowed"]
        };
        var result = EvaluateChannel("ch-1", "user-denied", options);
        Assert.False(result.IsAllowed);
        Assert.Contains("user_not_allowed", result.DenyReason!);
    }

    // --- Allow cases ---

    [Fact]
    public void Allows_all_users_when_allowlist_empty()
    {
        var options = new ChannelOptionsBuilder
        {
            AllowedChannelIds = ["ch-1"],
            AllowedUserIds = []
        };
        var result = EvaluateChannel("ch-1", "any-user", options);
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public void Allows_default_channel_id()
    {
        var options = new ChannelOptionsBuilder
        {
            AllowedChannelIds = [],
            DefaultChannelId = "ch-default"
        };
        var result = EvaluateChannel("ch-default", "user-1", options);
        Assert.True(result.IsAllowed);
    }

    // --- Principal classification ---

    [Fact]
    public void Explicit_user_gets_TrustedInternal()
    {
        var options = new ChannelOptionsBuilder
        {
            AllowedChannelIds = ["ch-1"],
            AllowedUserIds = ["user-trusted"]
        };
        var result = EvaluateChannel("ch-1", "user-trusted", options);
        Assert.True(result.IsAllowed);
        Assert.Equal(PrincipalClassification.TrustedInternal, result.Principal);
    }

    [Fact]
    public void Non_explicit_user_gets_UntrustedExternal()
    {
        var options = new ChannelOptionsBuilder
        {
            AllowedChannelIds = ["ch-1"],
            AllowedUserIds = []
        };
        var result = EvaluateChannel("ch-1", "any-user", options);
        Assert.True(result.IsAllowed);
        Assert.Equal(PrincipalClassification.UntrustedExternal, result.Principal);
    }

    // --- Audience resolution ---

    [Fact]
    public void DM_defaults_to_Team_audience()
    {
        var options = new ChannelOptionsBuilder { AllowDirectMessages = true };
        var result = EvaluateDm("user-1", options);
        Assert.True(result.IsAllowed);
        Assert.Equal(TrustAudience.Team, result.Audience);
    }

    [Fact]
    public void Non_explicit_channel_defaults_to_Public_audience()
    {
        var options = new ChannelOptionsBuilder
        {
            DefaultChannelId = "ch-default"
        };
        var result = EvaluateChannel("ch-default", "user-1", options);
        Assert.True(result.IsAllowed);
        Assert.Equal(TrustAudience.Public, result.Audience);
    }

    [Fact]
    public void Channel_audience_override()
    {
        var options = new ChannelOptionsBuilder
        {
            AllowedChannelIds = ["ch-1"],
            ChannelAudiences = new Dictionary<string, string> { ["ch-1"] = "personal" }
        };
        var result = EvaluateChannel("ch-1", "user-1", options);
        Assert.True(result.IsAllowed);
        Assert.Equal(TrustAudience.Personal, result.Audience);
    }

    [Fact]
    public void DM_audience_override_via_dm_key()
    {
        var options = new ChannelOptionsBuilder
        {
            AllowDirectMessages = true,
            ChannelAudiences = new Dictionary<string, string> { ["dm"] = "personal" }
        };
        var result = EvaluateDm("user-1", options);
        Assert.True(result.IsAllowed);
        Assert.Equal(TrustAudience.Personal, result.Audience);
    }

    [Fact]
    public void Channel_id_override_beats_dm_key()
    {
        var options = new ChannelOptionsBuilder
        {
            AllowDirectMessages = true,
            AllowedChannelIds = ["dm-ch-1"],
            ChannelAudiences = new Dictionary<string, string>
            {
                ["dm"] = "personal",
                ["dm-ch-1"] = "public"
            }
        };
        var result = EvaluateMessage("dm-ch-1", "user-1", isDm: true, options);
        Assert.True(result.IsAllowed);
        Assert.Equal(TrustAudience.Public, result.Audience);
    }

    [Fact]
    public void Invalid_audience_string_denies()
    {
        var options = new ChannelOptionsBuilder
        {
            AllowedChannelIds = ["ch-1"],
            ChannelAudiences = new Dictionary<string, string> { ["ch-1"] = "typo" }
        };
        var result = EvaluateChannel("ch-1", "user-1", options);
        Assert.False(result.IsAllowed);
        Assert.Contains("invalid_channel_audience", result.DenyReason!);
    }

    // --- Provenance ---

    [Fact]
    public void Provenance_has_Verified_transport()
    {
        var options = new ChannelOptionsBuilder
        {
            AllowedChannelIds = ["ch-1"]
        };
        var result = EvaluateChannel("ch-1", "user-1", options);
        Assert.True(result.IsAllowed);
        Assert.Equal(TransportAuthenticity.Verified, result.Provenance.TransportAuthenticity);
    }

    [Fact]
    public void Provenance_sourceKind_matches_channel()
    {
        var options = new ChannelOptionsBuilder
        {
            AllowedChannelIds = ["ch-1"]
        };
        var result = EvaluateChannel("ch-1", "user-1", options);
        Assert.True(result.IsAllowed);
        Assert.Equal(ExpectedSourceKind, result.Provenance.SourceKind);
    }
}
