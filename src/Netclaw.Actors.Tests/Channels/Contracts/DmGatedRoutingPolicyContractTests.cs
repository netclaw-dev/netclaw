// -----------------------------------------------------------------------
// <copyright file="DmGatedRoutingPolicyContractTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Actors.Tests.Channels.Contracts;

/// <summary>
/// Capability layer for channels that gate direct messages inside the
/// routing policy itself: the <c>DmNotAllowed</c> drop and the per-channel
/// <c>MentionRequiredInDm</c> option. Channels that enforce DM gating in
/// their ACL before routing (e.g. Telegram) enroll in
/// <see cref="RoutingPolicyContractTests"/> only — the drop outcome is
/// equivalent, but the ACL layer owns it and is covered by the ACL contract.
/// <para>
/// Derived through <see cref="ThreadReplyRoutingPolicyContractTests"/>
/// because today's policy-level DM gating channels are exactly the
/// thread-platform channels, so a concrete fixture at this leaf runs the
/// universal, thread, and DM cases each exactly once.
/// </para>
/// </summary>
public abstract class DmGatedRoutingPolicyContractTests : ThreadReplyRoutingPolicyContractTests
{
    /// <summary>
    /// Evaluates the channel's routing policy for a direct message.
    /// </summary>
    protected abstract RoutingVerdict EvaluateDmGating(
        bool allowDm,
        bool mentionRequiredInDm,
        bool containsMention);

    [Theory]
    [InlineData(false, false, false, RoutingVerdictKind.Ignore, RoutingIgnoreReason.DmNotAllowed)]
    [InlineData(true, true, false, RoutingVerdictKind.Ignore, RoutingIgnoreReason.DmMentionRequired)]
    [InlineData(true, true, true, RoutingVerdictKind.Route, null)]
    public void DirectMessage_routing_decision(
        bool allowDm,
        bool mentionRequiredInDm,
        bool containsMention,
        RoutingVerdictKind expectedKind,
        RoutingIgnoreReason? expectedReason)
    {
        var verdict = EvaluateDmGating(
            allowDm: allowDm,
            mentionRequiredInDm: mentionRequiredInDm,
            containsMention: containsMention);

        Assert.Equal(new RoutingVerdict(expectedKind, expectedReason), verdict);
    }
}
