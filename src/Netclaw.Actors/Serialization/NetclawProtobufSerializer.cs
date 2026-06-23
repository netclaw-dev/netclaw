// -----------------------------------------------------------------------
// <copyright file="NetclawProtobufSerializer.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Frozen;
using Akka.Actor;
using Akka.Serialization;
using Google.Protobuf;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;

namespace Netclaw.Actors.Serialization;

/// <summary>
/// Akka.NET serializer using Google Protobuf for Netclaw protocol types.
/// Manifests are constant strings decoupled from type names for safe schema evolution.
/// </summary>
public sealed class NetclawProtobufSerializer : SerializerWithStringManifest
{
    // Stable manifest strings - NEVER change these, add new versions instead
    private const string SessionIdManifest = "sid-v1";
    private const string SendUserMessageManifest = "sum-v1";
    private const string SerializableChatMessageManifest = "scm-v1";
    private const string SerializableMediaReferenceManifest = "smr-v1";
    private const string SerializableToolCallManifest = "stc-v1";
    private const string TurnRecordedManifest = "tr-v1";
    private const string SessionTitleSetManifest = "sts-v1";
    private const string SessionCompactedManifest = "sc-v1";
    private const string SessionSnapshotManifest = "ss-v1";
    private const string TurnBroadcastManifest = "tb-v1";
    private const string CompactionBroadcastManifest = "cb-v1";
    private const string WorkingContextManifest = "wc-v1";
    private const string ReminderIdManifest = "rid-v1";
    private const string ReminderDeliveryManifest = "rd-v1";
    private const string ReminderScheduleManifest = "rs-v1";
    private const string ReminderPayloadManifest = "rp-v1";
    private const string AdoptedContextRecordedManifest = "acr-v1";
    private const string CursorAdvancedManifest = "ca-v1";
    private const string MemoriesDistilledV2Manifest = "mdv2-v1";
    private const string ToolBatchStartedManifest = "tbs-v1";
    private const string ToolCallRecordedManifest = "tcr-v1";
    private const string ToolApprovalRequestedManifest = "tar-v1";
    private const string ToolApprovalResolvedManifest = "tares-v1";
    private const string ToolBatchAbandonedManifest = "tba-v1";
    private const string SessionBackgroundJobsReapedManifest = "sbjr-v1";
    private const string PendingApprovalPromptTrackedManifest = "papt-v1";
    private const string PendingApprovalPromptClearedManifest = "papc-v1";

    private static readonly FrozenDictionary<Type, string> TypeToManifest = new Dictionary<Type, string>
    {
        [typeof(SessionId)] = SessionIdManifest,
        [typeof(SendUserMessage)] = SendUserMessageManifest,
        [typeof(SerializableChatMessage)] = SerializableChatMessageManifest,
        [typeof(SerializableMediaReference)] = SerializableMediaReferenceManifest,
        [typeof(SerializableToolCall)] = SerializableToolCallManifest,
        [typeof(TurnRecorded)] = TurnRecordedManifest,
        [typeof(SessionTitleSet)] = SessionTitleSetManifest,
        [typeof(SessionCompacted)] = SessionCompactedManifest,
        [typeof(SessionSnapshot)] = SessionSnapshotManifest,
        [typeof(TurnBroadcast)] = TurnBroadcastManifest,
        [typeof(CompactionBroadcast)] = CompactionBroadcastManifest,
        [typeof(WorkingContext)] = WorkingContextManifest,
        [typeof(ReminderId)] = ReminderIdManifest,
        [typeof(ReminderDelivery)] = ReminderDeliveryManifest,
        [typeof(ReminderSchedule)] = ReminderScheduleManifest,
        [typeof(ReminderPayload)] = ReminderPayloadManifest,
        [typeof(AdoptedContextRecorded)] = AdoptedContextRecordedManifest,
        [typeof(Channels.CursorAdvanced)] = CursorAdvancedManifest,
        [typeof(MemoriesDistilledV2)] = MemoriesDistilledV2Manifest,
        [typeof(ToolBatchStarted)] = ToolBatchStartedManifest,
        [typeof(ToolCallRecorded)] = ToolCallRecordedManifest,
        [typeof(ToolApprovalRequested)] = ToolApprovalRequestedManifest,
        [typeof(ToolApprovalResolved)] = ToolApprovalResolvedManifest,
        [typeof(ToolBatchAbandoned)] = ToolBatchAbandonedManifest,
        [typeof(SessionBackgroundJobsReaped)] = SessionBackgroundJobsReapedManifest,
        [typeof(Channels.PendingApprovalPromptTracked)] = PendingApprovalPromptTrackedManifest,
        [typeof(Channels.PendingApprovalPromptCleared)] = PendingApprovalPromptClearedManifest,
    }.ToFrozenDictionary();

    public override int Identifier => 150;

    public NetclawProtobufSerializer(ExtendedActorSystem system) : base(system)
    {
    }

    public override string Manifest(object o)
    {
        var type = o.GetType();
        if (TypeToManifest.TryGetValue(type, out var manifest))
            return manifest;

        throw new ArgumentException($"No manifest registered for type {type.FullName}. Add it to NetclawProtobufSerializer.");
    }

    public override byte[] ToBinary(object obj)
    {
        return NetclawProtoMapper.ToProtoMessage(obj).ToByteArray();
    }

    public override object FromBinary(byte[] bytes, string manifest)
    {
        return manifest switch
        {
            SessionIdManifest => NetclawProtoMapper.FromProto(
                Proto.SessionIdProto.Parser.ParseFrom(bytes)),
            SendUserMessageManifest => NetclawProtoMapper.FromProto(
                Proto.SendUserMessageProto.Parser.ParseFrom(bytes)),
            SerializableChatMessageManifest => NetclawProtoMapper.FromProto(
                Proto.SerializableChatMessageProto.Parser.ParseFrom(bytes)),
            SerializableMediaReferenceManifest => NetclawProtoMapper.FromProto(
                Proto.SerializableMediaReferenceProto.Parser.ParseFrom(bytes)),
            SerializableToolCallManifest => NetclawProtoMapper.FromProto(
                Proto.SerializableToolCallProto.Parser.ParseFrom(bytes)),
            TurnRecordedManifest => NetclawProtoMapper.FromProto(
                Proto.TurnRecordedProto.Parser.ParseFrom(bytes)),
            SessionTitleSetManifest => NetclawProtoMapper.FromProto(
                Proto.SessionTitleSetProto.Parser.ParseFrom(bytes)),
            SessionCompactedManifest => NetclawProtoMapper.FromProto(
                Proto.SessionCompactedProto.Parser.ParseFrom(bytes)),
            SessionSnapshotManifest => NetclawProtoMapper.FromProto(
                Proto.SessionSnapshotProto.Parser.ParseFrom(bytes)),
            TurnBroadcastManifest => NetclawProtoMapper.FromProto(
                Proto.TurnBroadcastProto.Parser.ParseFrom(bytes)),
            CompactionBroadcastManifest => NetclawProtoMapper.FromProto(
                Proto.CompactionBroadcastProto.Parser.ParseFrom(bytes)),
            WorkingContextManifest => NetclawProtoMapper.FromProto(
                Proto.WorkingContextProto.Parser.ParseFrom(bytes)),
            ReminderIdManifest => NetclawProtoMapper.FromProto(
                Proto.ReminderIdProto.Parser.ParseFrom(bytes)),
            ReminderDeliveryManifest => NetclawProtoMapper.FromProto(
                Proto.ReminderDeliveryProto.Parser.ParseFrom(bytes)),
            ReminderScheduleManifest => NetclawProtoMapper.FromProto(
                Proto.ReminderScheduleProto.Parser.ParseFrom(bytes)),
            ReminderPayloadManifest => NetclawProtoMapper.FromProto(
                Proto.ReminderPayloadProto.Parser.ParseFrom(bytes)),
            AdoptedContextRecordedManifest => NetclawProtoMapper.FromProto(
                Proto.AdoptedContextRecordedProto.Parser.ParseFrom(bytes)),
            CursorAdvancedManifest => NetclawProtoMapper.FromProto(
                Proto.CursorAdvancedProto.Parser.ParseFrom(bytes)),
            MemoriesDistilledV2Manifest => NetclawProtoMapper.FromProto(
                Proto.MemoriesDistilledV2Proto.Parser.ParseFrom(bytes)),
            ToolBatchStartedManifest => NetclawProtoMapper.FromProto(
                Proto.ToolBatchStartedProto.Parser.ParseFrom(bytes)),
            ToolCallRecordedManifest => NetclawProtoMapper.FromProto(
                Proto.ToolCallRecordedProto.Parser.ParseFrom(bytes)),
            ToolApprovalRequestedManifest => NetclawProtoMapper.FromProto(
                Proto.ToolApprovalRequestedProto.Parser.ParseFrom(bytes)),
            ToolApprovalResolvedManifest => NetclawProtoMapper.FromProto(
                Proto.ToolApprovalResolvedProto.Parser.ParseFrom(bytes)),
            ToolBatchAbandonedManifest => NetclawProtoMapper.FromProto(
                Proto.ToolBatchAbandonedProto.Parser.ParseFrom(bytes)),
            SessionBackgroundJobsReapedManifest => NetclawProtoMapper.FromProto(
                Proto.SessionBackgroundJobsReapedProto.Parser.ParseFrom(bytes)),
            PendingApprovalPromptTrackedManifest => NetclawProtoMapper.FromProto(
                Proto.PendingApprovalPromptTrackedProto.Parser.ParseFrom(bytes)),
            PendingApprovalPromptClearedManifest => NetclawProtoMapper.FromProto(
                Proto.PendingApprovalPromptClearedProto.Parser.ParseFrom(bytes)),
            _ => throw new ArgumentException(
                $"Unknown manifest '{manifest}'. Add it to NetclawProtobufSerializer.")
        };
    }
}
