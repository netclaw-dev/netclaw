// -----------------------------------------------------------------------
// <copyright file="TeamsPersistenceSerializer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Frozen;
using Akka.Actor;
using Akka.Hosting;
using Akka.Serialization;
using Google.Protobuf;
using Netclaw.Configuration;
using Proto = Netclaw.Channels.Teams.Serialization.Proto;

namespace Netclaw.Channels.Teams.Serialization;

/// <summary>
/// Serializer for every Team-owned persistence record written after the
/// dual-read migration. Legacy Team manifests remain readable only through
/// the generic serializer's compatibility envelope.
/// </summary>
public sealed class TeamsPersistenceSerializer : SerializerWithStringManifest
{
    private const string ApprovalPendingManifest = "teams-approval-pending-v2";
    private const string ApprovalDeliveredManifest = "teams-approval-delivered-v2";
    private const string ApprovalReissuedManifest = "teams-approval-reissued-v2";
    private const string ApprovalForwardingManifest = "teams-approval-forwarding-v2";
    private const string ApprovalConsumedManifest = "teams-approval-consumed-v2";
    private const string DestinationCapturedManifest = "teams-destination-captured-v2";
    private const string DeliveryRecordedManifest = "teams-delivery-recorded-v2";
    private const string BindingSnapshotManifest = "teams-binding-snapshot-v2";
    private const string ChannelMappedManifest = "teams-channel-mapped-v2";
    private const string ChannelSnapshotManifest = "teams-channel-snapshot-v2";

    private static readonly FrozenDictionary<Type, string> TypeToManifest = new Dictionary<Type, string>
    {
        [typeof(TeamsApprovalPendingCreated)] = ApprovalPendingManifest,
        [typeof(TeamsApprovalCardDelivered)] = ApprovalDeliveredManifest,
        [typeof(TeamsApprovalCardReissued)] = ApprovalReissuedManifest,
        [typeof(TeamsApprovalForwardingStarted)] = ApprovalForwardingManifest,
        [typeof(TeamsApprovalConsumed)] = ApprovalConsumedManifest,
        [typeof(TeamsProactiveDestinationCaptured)] = DestinationCapturedManifest,
        [typeof(TeamsProactiveDeliveryRecorded)] = DeliveryRecordedManifest,
        [typeof(TeamsBindingSnapshot)] = BindingSnapshotManifest,
        [typeof(TeamsChannelActivityMapped)] = ChannelMappedManifest,
        [typeof(TeamsChannelActivityIndexSnapshot)] = ChannelSnapshotManifest
    }.ToFrozenDictionary();

    public override int Identifier => 151;

    public TeamsPersistenceSerializer(ExtendedActorSystem system) : base(system)
    {
    }

    public override string Manifest(object o) => TypeToManifest.TryGetValue(o.GetType(), out var manifest)
        ? manifest
        : throw new ArgumentException($"No Teams persistence manifest is registered for {o.GetType().FullName}.");

    public override byte[] ToBinary(object obj) => ToProtoMessage(obj).ToByteArray();

    public override object FromBinary(byte[] bytes, string manifest) => manifest switch
    {
        ApprovalPendingManifest => FromProto(Proto.TeamsApprovalPendingCreatedProto.Parser.ParseFrom(bytes)),
        ApprovalDeliveredManifest => FromProto(Proto.TeamsApprovalCardDeliveredProto.Parser.ParseFrom(bytes)),
        ApprovalReissuedManifest => FromProto(Proto.TeamsApprovalCardReissuedProto.Parser.ParseFrom(bytes)),
        ApprovalForwardingManifest => FromProto(Proto.TeamsApprovalForwardingStartedProto.Parser.ParseFrom(bytes)),
        ApprovalConsumedManifest => FromProto(Proto.TeamsApprovalConsumedProto.Parser.ParseFrom(bytes)),
        DestinationCapturedManifest => FromProto(Proto.TeamsProactiveDestinationCapturedProto.Parser.ParseFrom(bytes)),
        DeliveryRecordedManifest => FromProto(Proto.TeamsProactiveDeliveryRecordedProto.Parser.ParseFrom(bytes)),
        BindingSnapshotManifest => FromProto(Proto.TeamsBindingSnapshotProto.Parser.ParseFrom(bytes)),
        ChannelMappedManifest => FromProto(Proto.TeamsChannelActivityMappedProto.Parser.ParseFrom(bytes)),
        ChannelSnapshotManifest => FromProto(Proto.TeamsChannelActivityIndexSnapshotProto.Parser.ParseFrom(bytes)),
        _ => throw new ArgumentException($"Unknown Teams persistence manifest '{manifest}'.")
    };

    private static IMessage ToProtoMessage(object value) => value switch
    {
        TeamsApprovalPendingCreated item => ToProto(item),
        TeamsApprovalCardDelivered item => ToProto(item),
        TeamsApprovalCardReissued item => ToProto(item),
        TeamsApprovalForwardingStarted item => ToProto(item),
        TeamsApprovalConsumed item => ToProto(item),
        TeamsProactiveDestinationCaptured item => ToProto(item),
        TeamsProactiveDeliveryRecorded item => ToProto(item),
        TeamsBindingSnapshot item => ToProto(item),
        TeamsChannelActivityMapped item => ToProto(item),
        TeamsChannelActivityIndexSnapshot item => ToProto(item),
        _ => throw new ArgumentException($"No Teams persistence protobuf mapping exists for {value.GetType().FullName}.")
    };

    private static Proto.TeamsApprovalPendingCreatedProto ToProto(TeamsApprovalPendingCreated value)
    {
        var proto = new Proto.TeamsApprovalPendingCreatedProto
        {
            CallId = value.CallId,
            CorrelationId = value.CorrelationId,
            NonceHash = value.NonceHash,
            ExpiresAtUnixMilliseconds = value.ExpiresAtUnixMilliseconds
        };
        if (value.RequesterSenderId is not null) proto.RequesterSenderId = value.RequesterSenderId;
        if (value.RequesterPrincipal is { } principal) proto.RequesterPrincipal = (int)principal;
        proto.OfferedOptionKeys.AddRange(value.OfferedOptionKeys);
        proto.IsMcpTool = value.IsMcpTool;
        proto.ToolName = value.ToolName;
        proto.RequestDisplayText = value.RequestDisplayText;
        proto.PresentationPending = value.PresentationPending;
        return proto;
    }

    private static TeamsApprovalPendingCreated FromProto(Proto.TeamsApprovalPendingCreatedProto value) => new()
    {
        CallId = value.CallId,
        CorrelationId = value.CorrelationId,
        NonceHash = value.NonceHash,
        RequesterSenderId = value.HasRequesterSenderId ? value.RequesterSenderId : null,
        RequesterPrincipal = value.HasRequesterPrincipal ? (PrincipalClassification)value.RequesterPrincipal : null,
        ExpiresAtUnixMilliseconds = value.ExpiresAtUnixMilliseconds,
        OfferedOptionKeys = value.OfferedOptionKeys.ToArray(),
        IsMcpTool = value.IsMcpTool,
        ToolName = value.ToolName,
        RequestDisplayText = value.RequestDisplayText,
        PresentationPending = value.HasPresentationPending ? value.PresentationPending : true
    };

    private static Proto.TeamsApprovalCardDeliveredProto ToProto(TeamsApprovalCardDelivered value)
    {
        var proto = new Proto.TeamsApprovalCardDeliveredProto { CorrelationId = value.CorrelationId };
        if (value.PromptId is not null) proto.PromptId = value.PromptId;
        return proto;
    }

    private static TeamsApprovalCardDelivered FromProto(Proto.TeamsApprovalCardDeliveredProto value) => new()
    {
        CorrelationId = value.CorrelationId,
        PromptId = value.HasPromptId ? value.PromptId : null
    };

    private static Proto.TeamsApprovalCardReissuedProto ToProto(TeamsApprovalCardReissued value) => new()
    {
        CorrelationId = value.CorrelationId,
        NonceHash = value.NonceHash,
        ExpiresAtUnixMilliseconds = value.ExpiresAtUnixMilliseconds
    };

    private static TeamsApprovalCardReissued FromProto(Proto.TeamsApprovalCardReissuedProto value) => new()
    {
        CorrelationId = value.CorrelationId,
        NonceHash = value.NonceHash,
        ExpiresAtUnixMilliseconds = value.ExpiresAtUnixMilliseconds
    };

    private static Proto.TeamsApprovalForwardingStartedProto ToProto(TeamsApprovalForwardingStarted value) => new()
    {
        CorrelationId = value.CorrelationId,
        Decision = value.Decision,
        SenderId = value.SenderId,
        StartedAtUnixMilliseconds = value.StartedAtUnixMilliseconds
    };

    private static TeamsApprovalForwardingStarted FromProto(Proto.TeamsApprovalForwardingStartedProto value) => new()
    {
        CorrelationId = value.CorrelationId,
        Decision = value.Decision,
        SenderId = value.SenderId,
        StartedAtUnixMilliseconds = value.StartedAtUnixMilliseconds
    };

    private static Proto.TeamsApprovalConsumedProto ToProto(TeamsApprovalConsumed value) => new()
    {
        CorrelationId = value.CorrelationId,
        Decision = value.Decision,
        ConsumedAtUnixMilliseconds = value.ConsumedAtUnixMilliseconds
    };

    private static TeamsApprovalConsumed FromProto(Proto.TeamsApprovalConsumedProto value) => new()
    {
        CorrelationId = value.CorrelationId,
        Decision = value.Decision,
        ConsumedAtUnixMilliseconds = value.ConsumedAtUnixMilliseconds
    };

    private static Proto.TeamsProactiveDestinationCapturedProto ToProto(TeamsProactiveDestinationCaptured value)
    {
        var proto = new Proto.TeamsProactiveDestinationCapturedProto
        {
            TenantId = value.TenantId, ConversationId = value.ConversationId, Scope = value.Scope,
            ServiceUrl = value.ServiceUrl, Generation = value.Generation
        };
        if (value.RootActivityId is not null) proto.RootActivityId = value.RootActivityId;
        if (value.TeamId is not null) proto.TeamId = value.TeamId;
        if (value.ChannelId is not null) proto.ChannelId = value.ChannelId;
        if (value.UserId is not null) proto.UserId = value.UserId;
        return proto;
    }

    private static TeamsProactiveDestinationCaptured FromProto(Proto.TeamsProactiveDestinationCapturedProto value) => new()
    {
        TenantId = value.TenantId, ConversationId = value.ConversationId, Scope = value.Scope,
        ServiceUrl = value.ServiceUrl, RootActivityId = value.HasRootActivityId ? value.RootActivityId : null,
        TeamId = value.HasTeamId ? value.TeamId : null, ChannelId = value.HasChannelId ? value.ChannelId : null,
        UserId = value.HasUserId ? value.UserId : null, Generation = value.Generation
    };

    private static Proto.TeamsProactiveDeliveryRecordedProto ToProto(TeamsProactiveDeliveryRecorded value)
    {
        var proto = new Proto.TeamsProactiveDeliveryRecordedProto
        {
            DeliveryKey = value.DeliveryKey, State = value.State,
            DestinationGeneration = value.DestinationGeneration,
            InvalidatesDestination = value.InvalidatesDestination
        };
        if (value.EvictedDeliveryKey is not null) proto.EvictedDeliveryKey = value.EvictedDeliveryKey;
        return proto;
    }

    private static TeamsProactiveDeliveryRecorded FromProto(Proto.TeamsProactiveDeliveryRecordedProto value) => new()
    {
        DeliveryKey = value.DeliveryKey, State = value.State,
        EvictedDeliveryKey = value.HasEvictedDeliveryKey ? value.EvictedDeliveryKey : null,
        DestinationGeneration = value.DestinationGeneration,
        InvalidatesDestination = value.InvalidatesDestination
    };

    private static Proto.TeamsBindingSnapshotProto ToProto(TeamsBindingSnapshot value)
    {
        var proto = new Proto.TeamsBindingSnapshotProto
        {
            MigrationVersion = value.MigrationVersion,
            LastDestinationGeneration = value.LastDestinationGeneration
        };
        proto.ActivityFingerprints.AddRange(value.ActivityFingerprints);
        proto.Approvals.AddRange(value.Approvals.Select(ToProto));
        if (value.Destination is not null) proto.Destination = ToProto(value.Destination);
        proto.ProactiveDeliveries.AddRange(value.ProactiveDeliveries.Select(ToProto));
        return proto;
    }

    private static TeamsBindingSnapshot FromProto(Proto.TeamsBindingSnapshotProto value) => new(value.ActivityFingerprints.ToArray())
    {
        Approvals = value.Approvals.Select(FromProto).ToArray(),
        Destination = value.Destination is null ? null : FromProto(value.Destination),
        LastDestinationGeneration = value.LastDestinationGeneration,
        ProactiveDeliveries = value.ProactiveDeliveries.Select(FromProto).ToArray(),
        MigrationVersion = value.MigrationVersion
    };

    private static Proto.TeamsApprovalSnapshotEntryProto ToProto(TeamsApprovalSnapshotEntry value)
    {
        var proto = new Proto.TeamsApprovalSnapshotEntryProto
        {
            CallId = value.CallId, CorrelationId = value.CorrelationId, NonceHash = value.NonceHash,
            ExpiresAtUnixMilliseconds = value.ExpiresAtUnixMilliseconds
        };
        if (value.RequesterSenderId is not null) proto.RequesterSenderId = value.RequesterSenderId;
        if (value.RequesterPrincipal is { } principal) proto.RequesterPrincipal = (int)principal;
        if (value.PromptId is not null) proto.PromptId = value.PromptId;
        if (value.ForwardingDecision is not null) proto.ForwardingDecision = value.ForwardingDecision;
        if (value.ForwardingSenderId is not null) proto.ForwardingSenderId = value.ForwardingSenderId;
        if (value.Decision is not null) proto.Decision = value.Decision;
        proto.PresentationPending = value.PresentationPending;
        proto.OfferedOptionKeys.AddRange(value.OfferedOptionKeys);
        proto.IsMcpTool = value.IsMcpTool;
        proto.ToolName = value.ToolName;
        proto.RequestDisplayText = value.RequestDisplayText;
        return proto;
    }

    private static TeamsApprovalSnapshotEntry FromProto(Proto.TeamsApprovalSnapshotEntryProto value) => new()
    {
        CallId = value.CallId, CorrelationId = value.CorrelationId, NonceHash = value.NonceHash,
        RequesterSenderId = value.HasRequesterSenderId ? value.RequesterSenderId : null,
        RequesterPrincipal = value.HasRequesterPrincipal ? (PrincipalClassification)value.RequesterPrincipal : null,
        ExpiresAtUnixMilliseconds = value.ExpiresAtUnixMilliseconds,
        OfferedOptionKeys = value.OfferedOptionKeys.ToArray(),
        IsMcpTool = value.IsMcpTool,
        ToolName = value.ToolName,
        RequestDisplayText = value.RequestDisplayText,
        PromptId = value.HasPromptId ? value.PromptId : null,
        ForwardingDecision = value.HasForwardingDecision ? value.ForwardingDecision : null,
        ForwardingSenderId = value.HasForwardingSenderId ? value.ForwardingSenderId : null,
        Decision = value.HasDecision ? value.Decision : null,
        PresentationPending = value.HasPresentationPending ? value.PresentationPending : true
    };

    private static Proto.TeamsChannelActivityMappedProto ToProto(TeamsChannelActivityMapped value)
    {
        var proto = new Proto.TeamsChannelActivityMappedProto { ActivityFingerprint = value.ActivityFingerprint, SessionId = value.SessionId };
        if (value.EvictedActivityFingerprint is not null) proto.EvictedActivityFingerprint = value.EvictedActivityFingerprint;
        if (value.SenderFingerprint is not null) proto.SenderFingerprint = value.SenderFingerprint;
        return proto;
    }

    private static TeamsChannelActivityMapped FromProto(Proto.TeamsChannelActivityMappedProto value) => new(
        value.ActivityFingerprint, value.SessionId,
        value.HasEvictedActivityFingerprint ? value.EvictedActivityFingerprint : null,
        value.HasSenderFingerprint ? value.SenderFingerprint : null);

    private static Proto.TeamsChannelActivityIndexSnapshotProto ToProto(TeamsChannelActivityIndexSnapshot value)
    {
        var proto = new Proto.TeamsChannelActivityIndexSnapshotProto();
        proto.Entries.AddRange(value.Entries.Select(ToProto));
        return proto;
    }

    private static TeamsChannelActivityIndexSnapshot FromProto(Proto.TeamsChannelActivityIndexSnapshotProto value) => new(
        value.Entries.Select(FromProto).ToArray());
}

/// <summary>Registers the non-overlapping Teams serializer binding.</summary>
public static class TeamsPersistenceSerializationExtensions
{
    public static AkkaConfigurationBuilder WithTeamsPersistenceSerialization(this AkkaConfigurationBuilder builder) =>
        builder.WithCustomSerializer(
            serializerIdentifier: "netclaw-teams-protobuf",
            boundTypes: [typeof(ITeamsPersistenceMessage)],
            serializerFactory: system => new TeamsPersistenceSerializer(system));
}
