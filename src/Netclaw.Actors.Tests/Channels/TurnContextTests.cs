// -----------------------------------------------------------------------
// <copyright file="TurnContextTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Channels;

public sealed class TurnContextTests
{
    [Fact]
    public void FromMessageSource_captures_durable_authority_fields()
    {
        var sessionId = new SessionId("C123/1700000000.000001");
        var turnId = new TurnId("turn-1");
        var source = new MessageSource
        {
            ChannelType = ChannelType.Slack,
            SenderId = new SenderId("U12345"),
            ChannelId = "C123",
            MessageId = "1700000000.000001",
            TurnId = turnId,
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            Principal = PrincipalClassification.TrustedInternal,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community)
            {
                SourceScope = new SourceScope("slack-workspace:T123"),
                SourceKind = new SourceKind("slack")
            },
            ReceivedAt = new DateTimeOffset(2026, 5, 28, 12, 0, 0, TimeSpan.Zero),
            ExecutableText = "run git status",
            DefaultDeliveryTarget = new ChannelDeliveryTargetInfo(
                "slack",
                "destination",
                "C123",
                "#alerts",
                "1700000000.000001"),
            RequestedDeliveryTarget = new ChannelDeliveryTargetInfo(
                "mattermost",
                "direct_message",
                "user1234567890123456789012",
                "@alice"),
            HasThirdPartyAdoptedContext = true,
            AdoptedSpeakerIds = ["U12345", "U67890"],
            AdoptedContextProjection = "quoted context",
            AdoptedContextLowerBound = "1700000000.000000",
            AdoptedContextUpperBound = "1700000000.000001",
            ReminderId = "reminder:1700000000000",
            BackgroundJobId = "bg-job:42",
            AckTarget = ActorRefs.Nobody
        };

        var context = TurnContext.FromMessageSource(sessionId, turnId, source);

        Assert.Equal(sessionId, context.SessionId);
        Assert.Equal(turnId, context.TurnId);
        Assert.Equal(TrustAudience.Team, context.Audience);
        Assert.Equal(TrustBoundary.Team, context.Boundary);
        Assert.Equal(ChannelType.Slack, context.ChannelType);
        Assert.Equal(new SenderId("U12345"), context.RequesterSenderId);
        Assert.Equal(PrincipalClassification.TrustedInternal, context.RequesterPrincipal);
        Assert.Equal(TransportAuthenticity.Verified, context.Provenance.TransportAuthenticity);
        Assert.Equal(PayloadTaint.Community, context.Provenance.PayloadTaint);
        var sourceScope = Assert.NotNull(context.Provenance.SourceScope);
        var sourceKind = Assert.NotNull(context.Provenance.SourceKind);
        Assert.Equal("slack-workspace:T123", sourceScope.Value);
        Assert.Equal("slack", sourceKind.Value);
        Assert.Equal(source.DefaultDeliveryTarget, context.DefaultDeliveryTarget);
        Assert.Equal(source.RequestedDeliveryTarget, context.RequestedDeliveryTarget);
        Assert.Equal(source.RequestedDeliveryTarget, context.EffectiveDeliveryTarget);
        Assert.True(context.HasAdoptedContext);
        Assert.True(context.HasThirdPartyAdoptedContext);
        Assert.Equal(["U12345", "U67890"], context.AdoptedSpeakerIds);
        Assert.True(context.SupportsInteractiveApproval);
        Assert.True(context.HasApprovalRequester);
    }

    [Fact]
    public void FromMessageSource_without_source_fails_closed_without_synthesizing_requester()
    {
        var context = TurnContext.FromMessageSource(
            new SessionId("unknown-channel/thread-1"),
            new TurnId("turn-1"),
            source: null);

        Assert.Equal(TrustAudience.Public, context.Audience);
        Assert.Equal(TrustBoundary.Public, context.Boundary);
        Assert.Null(context.ChannelType);
        Assert.Null(context.RequesterSenderId);
        Assert.Equal(PrincipalClassification.UntrustedExternal, context.RequesterPrincipal);
        Assert.Equal(TransportAuthenticity.Unverified, context.Provenance.TransportAuthenticity);
        Assert.Equal(PayloadTaint.Public, context.Provenance.PayloadTaint);
        Assert.False(context.SupportsInteractiveApproval);
        Assert.False(context.HasApprovalRequester);
    }

    [Fact]
    public void TurnContext_excludes_transport_and_lifecycle_only_fields()
    {
        var durableFields = typeof(TurnContext).GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain(nameof(MessageSource.AckTarget), durableFields);
        Assert.DoesNotContain(nameof(MessageSource.BackgroundJobId), durableFields);
        Assert.DoesNotContain(nameof(MessageSource.ReminderId), durableFields);
        Assert.DoesNotContain(nameof(MessageSource.ExecutableText), durableFields);
        Assert.DoesNotContain(nameof(MessageSource.MessageId), durableFields);
        Assert.DoesNotContain(nameof(MessageSource.ChannelId), durableFields);
        Assert.DoesNotContain(nameof(MessageSource.ReceivedAt), durableFields);
    }

    [Fact]
    public void Record_round_trip_preserves_authority_context()
    {
        var original = new TurnContext
        {
            SessionId = new SessionId("C123/1700000000.000001"),
            TurnId = new TurnId("turn-1"),
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            ChannelType = ChannelType.Slack,
            RequesterSenderId = new SenderId("U12345"),
            RequesterPrincipal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Community)
            {
                SourceScope = new SourceScope("slack-workspace:T123"),
                SourceKind = new SourceKind("slack")
            },
            DefaultDeliveryTarget = new ChannelDeliveryTargetInfo(
                "slack",
                "destination",
                "C123",
                "#alerts",
                "1700000000.000001"),
            RequestedDeliveryTarget = new ChannelDeliveryTargetInfo(
                "mattermost",
                "direct_message",
                "user1234567890123456789012",
                "@alice"),
            HasAdoptedContext = true,
            HasThirdPartyAdoptedContext = true,
            AdoptedSpeakerIds = ["U12345", "U67890"],
            SupportsInteractiveApproval = true
        };

        var success = TurnContext.TryFromRecord(original.ToRecord(), out var restored, out var reason);

        Assert.True(success, reason);
        Assert.NotNull(restored);
        Assert.Equal(original.SessionId, restored.SessionId);
        Assert.Equal(original.TurnId, restored.TurnId);
        Assert.Equal(original.Audience, restored.Audience);
        Assert.Equal(original.Boundary, restored.Boundary);
        Assert.Equal(original.ChannelType, restored.ChannelType);
        Assert.Equal(original.RequesterSenderId, restored.RequesterSenderId);
        Assert.Equal(original.RequesterPrincipal, restored.RequesterPrincipal);
        Assert.Equal(original.Provenance, restored.Provenance);
        Assert.Equal(original.DefaultDeliveryTarget, restored.DefaultDeliveryTarget);
        Assert.Equal(original.RequestedDeliveryTarget, restored.RequestedDeliveryTarget);
        Assert.Equal(original.HasAdoptedContext, restored.HasAdoptedContext);
        Assert.Equal(original.HasThirdPartyAdoptedContext, restored.HasThirdPartyAdoptedContext);
        Assert.Equal(original.AdoptedSpeakerIds, restored.AdoptedSpeakerIds);
        Assert.Equal(original.SupportsInteractiveApproval, restored.SupportsInteractiveApproval);
    }

    [Fact]
    public void Restore_legacy_approval_event_builds_turn_context_from_legacy_fields()
    {
        var evt = new ToolApprovalRequested
        {
            SessionId = new SessionId("C123/1700000000.000001"),
            CallId = "call-legacy-1",
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            ChannelType = ChannelType.Slack.ToWireValue(),
            SupportsInteractiveApproval = true,
            RequesterSenderId = new SenderId("U12345"),
            RequesterPrincipal = PrincipalClassification.TrustedInternal,
            HasThirdPartyAdoptedContext = true,
            AdoptedSpeakerIds = ["U67890"]
        };

        var context = ToolApprovalTurnContext.Restore(evt, out var failure);

        Assert.Null(failure);
        Assert.NotNull(context);
        Assert.Equal(evt.SessionId, context.SessionId);
        Assert.Equal(new TurnId("recovered-approval/call-legacy-1"), context.TurnId);
        Assert.Equal(TrustAudience.Team, context.Audience);
        Assert.Equal(TrustBoundary.Team, context.Boundary);
        Assert.Equal(ChannelType.Slack, context.ChannelType);
        Assert.Equal(new SenderId("U12345"), context.RequesterSenderId);
        Assert.Equal(PrincipalClassification.TrustedInternal, context.RequesterPrincipal);
        Assert.Equal(TransportAuthenticity.Verified, context.Provenance.TransportAuthenticity);
        Assert.Equal(PayloadTaint.Unknown, context.Provenance.PayloadTaint);
        var sourceKind = Assert.NotNull(context.Provenance.SourceKind);
        Assert.Equal(ChannelType.Slack.ToWireValue(), sourceKind.Value);
        Assert.True(context.HasAdoptedContext);
        Assert.True(context.HasThirdPartyAdoptedContext);
        Assert.Equal(["U67890"], context.AdoptedSpeakerIds);
        Assert.True(context.SupportsInteractiveApproval);
    }

    [Fact]
    public void Restore_legacy_approval_event_defaults_missing_principal_without_broadening_approval()
    {
        var evt = new ToolApprovalRequested
        {
            SessionId = new SessionId("C123/1700000000.000001"),
            CallId = "call-legacy-no-principal",
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            ChannelType = ChannelType.Slack.ToWireValue(),
            RequesterSenderId = new SenderId("U12345")
        };

        var context = ToolApprovalTurnContext.Restore(evt, out var failure);

        Assert.Null(failure);
        Assert.NotNull(context);
        Assert.Equal(PrincipalClassification.UntrustedExternal, context.RequesterPrincipal);
        Assert.Equal(new SenderId("U12345"), context.RequesterSenderId);
        Assert.True(context.HasApprovalRequester);
    }

    [Fact]
    public void Restore_legacy_approval_event_fails_closed_without_requester_sender()
    {
        var evt = new ToolApprovalRequested
        {
            SessionId = new SessionId("C123/1700000000.000001"),
            CallId = "call-legacy-missing-requester",
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            ChannelType = ChannelType.Slack.ToWireValue(),
            RequesterPrincipal = PrincipalClassification.TrustedInternal
        };

        var context = ToolApprovalTurnContext.Restore(evt, out var failure);

        Assert.Null(context);
        Assert.Equal("legacy approval event is missing requester sender", failure);
    }
}
