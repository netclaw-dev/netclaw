// -----------------------------------------------------------------------
// <copyright file="TurnContextRequesterTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// The requester check decides who owns a child run (status and cancel). Each requester field must deny a
/// stranger. Each per-turn field must not lock the owner out. The focused mutation gate links this file.
/// </summary>
public sealed class TurnContextRequesterTests
{
    private static readonly ChannelDeliveryTargetInfo ThreadTarget = new("slack", "thread", "C1/1.0");
    private static readonly ChannelDeliveryTargetInfo OtherTarget = new("slack", "channel", "C2");

    [Theory]
    [InlineData("session")]
    [InlineData("audience")]
    [InlineData("boundary")]
    [InlineData("channel")]
    [InlineData("sender")]
    [InlineData("missing-sender")]
    [InlineData("principal")]
    public void A_different_requester_does_not_own_the_work(string field)
    {
        var owner = Owner();
        var stranger = field switch
        {
            "session" => owner with { SessionId = new SessionId("C1/2.0") },
            "audience" => owner with { Audience = TrustAudience.Public },
            "boundary" => owner with { Boundary = TrustBoundary.Public },
            "channel" => owner with { ChannelType = ChannelType.Discord },
            "sender" => owner with { RequesterSenderId = new SenderId("U-bob") },
            "missing-sender" => owner with { RequesterSenderId = null },
            "principal" => owner with { RequesterPrincipal = PrincipalClassification.VerifiedAutomation },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };

        Assert.False(owner.HasSameRequester(stranger));
        Assert.False(owner.HasSameAuthority(stranger));
    }

    // These fields describe one message. They change between turns of the same person, so they must not
    // change the owner. Full authority still compares them for start idempotency and approval recovery.
    [Theory]
    [InlineData("turn")]
    [InlineData("provenance")]
    [InlineData("default-target")]
    [InlineData("requested-target")]
    [InlineData("adopted")]
    [InlineData("third-party-adopted")]
    [InlineData("adopted-speakers")]
    [InlineData("interactive-approval")]
    public void A_later_turn_of_the_same_requester_owns_the_work(string field)
    {
        var owner = Owner();
        var later = field switch
        {
            "turn" => owner with { TurnId = new TurnId("turn-2") },
            "provenance" => owner with { Provenance = new SourceProvenance(TransportAuthenticity.Unverified, PayloadTaint.Public) },
            "default-target" => owner with { DefaultDeliveryTarget = OtherTarget },
            "requested-target" => owner with { RequestedDeliveryTarget = OtherTarget },
            "adopted" => owner with { HasAdoptedContext = true },
            "third-party-adopted" => owner with { HasThirdPartyAdoptedContext = true },
            "adopted-speakers" => owner with { AdoptedSpeakerIds = ["U-alice"] },
            "interactive-approval" => owner with { SupportsInteractiveApproval = false },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };

        Assert.True(owner.HasSameRequester(later));
        if (field != "turn")
            Assert.False(owner.HasSameAuthority(later));
    }

    private static TurnContext Owner() => new()
    {
        SessionId = new SessionId("C1/1.0"),
        TurnId = new TurnId("turn-1"),
        Audience = TrustAudience.Team,
        Boundary = TrustBoundary.TrustedInstance,
        ChannelType = ChannelType.Slack,
        RequesterSenderId = new SenderId("U-alice"),
        RequesterPrincipal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
        DefaultDeliveryTarget = ThreadTarget,
        SupportsInteractiveApproval = true
    };
}
