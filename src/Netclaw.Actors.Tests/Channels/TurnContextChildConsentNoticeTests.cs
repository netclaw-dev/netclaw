// -----------------------------------------------------------------------
// <copyright file="TurnContextChildConsentNoticeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Task 1.5 (identification). The session identifies an approval notice by three facts together: the source kind
/// <c>child-consent</c>, the sender <c>child-consent</c>, and the authenticity <c>LocalProcess</c> (the
/// <c>IsReminderTurn</c> pattern). A webhook can set its event type to <c>child-consent</c>, but the webhook path sets
/// the authenticity <c>Verified</c> in code, so a webhook input is never a notice.
/// Source: design D4, agent-event-channel "A webhook cannot pose as a notice".
/// </summary>
public sealed class TurnContextChildConsentNoticeTests
{
    private static readonly SessionId Session = new("slack/C1/1700.1");

    /// <summary>
    /// The notice turn is automation. It is a notice, and under the six-fact rule it cannot control a run that a
    /// person started. A run that a reminder started is a run of the session, so the notice turn can see it.
    /// Planted bug: the check reads one fact only, or the notice borrows the requester of the run owner.
    /// </summary>
    [Fact]
    public void A_notice_context_is_a_notice_and_controls_no_run_that_a_person_started()
    {
        var notice = Context("child-consent", "child-consent", TransportAuthenticity.LocalProcess);

        Assert.True(notice.IsChildConsentNotice);
        Assert.False(notice.CanControlChildRunOf(Person()));
        Assert.True(notice.CanControlChildRunOf(Context("reminder", "reminder-system", TransportAuthenticity.LocalProcess)));
    }

    /// <summary>
    /// Each fact alone is not enough. A webhook with the event type <c>child-consent</c> has the authenticity
    /// <c>Verified</c>. A reminder or a person has another source kind or another sender.
    /// Planted bug: the check drops the authenticity, the sender, or the source kind.
    /// </summary>
    [Theory]
    [InlineData("child-consent", "child-consent", TransportAuthenticity.Verified)]
    [InlineData("child-consent", "webhook-github", TransportAuthenticity.LocalProcess)]
    [InlineData("reminder", "child-consent", TransportAuthenticity.LocalProcess)]
    [InlineData("webhook", "child-consent", TransportAuthenticity.Verified)]
    public void A_context_that_differs_in_one_fact_is_not_a_notice(string sourceKind, string sender, TransportAuthenticity authenticity)
    {
        Assert.False(Context(sourceKind, sender, authenticity).IsChildConsentNotice);
    }

    /// <summary>A person turn and a context with no source kind are never notices.</summary>
    [Fact]
    public void A_person_turn_is_not_a_notice()
    {
        Assert.False(Person().IsChildConsentNotice);
        Assert.False((Person() with
        {
            RequesterSenderId = new SenderId("child-consent"),
            Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
        }).IsChildConsentNotice);
    }

    private static TurnContext Context(string sourceKind, string sender, TransportAuthenticity authenticity) => new()
    {
        SessionId = Session,
        TurnId = new TurnId($"turn-{sourceKind}"),
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        ChannelType = ChannelType.Slack,
        RequesterSenderId = new SenderId(sender),
        RequesterPrincipal = PrincipalClassification.VerifiedAutomation,
        Provenance = new SourceProvenance(authenticity, PayloadTaint.Trusted) { SourceKind = new SourceKind(sourceKind) },
        SupportsInteractiveApproval = false
    };

    private static TurnContext Person() => new()
    {
        SessionId = Session,
        TurnId = new TurnId("turn-person"),
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        ChannelType = ChannelType.Slack,
        RequesterSenderId = new SenderId("alice"),
        RequesterPrincipal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted) { SourceKind = new SourceKind("slack") },
        SupportsInteractiveApproval = true
    };
}
