using System.Collections.Concurrent;
using System.Collections.Frozen;
using Akka.Actor;
using Akka.Serialization;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using ProtoBuf;

namespace Netclaw.Actors.Serialization;

/// <summary>
/// Akka.NET serializer using protobuf-net for Netclaw protocol types.
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
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, Type> ManifestToType = new Dictionary<string, Type>
    {
        [SessionIdManifest] = typeof(SessionId),
        [SendUserMessageManifest] = typeof(SendUserMessage),
        [SerializableChatMessageManifest] = typeof(SerializableChatMessage),
        [SerializableMediaReferenceManifest] = typeof(SerializableMediaReference),
        [SerializableToolCallManifest] = typeof(SerializableToolCall),
        [TurnRecordedManifest] = typeof(TurnRecorded),
        [SessionTitleSetManifest] = typeof(SessionTitleSet),
        [SessionCompactedManifest] = typeof(SessionCompacted),
        [SessionSnapshotManifest] = typeof(SessionSnapshot),
        [TurnBroadcastManifest] = typeof(TurnBroadcast),
        [CompactionBroadcastManifest] = typeof(CompactionBroadcast),
        [WorkingContextManifest] = typeof(WorkingContext),
        [ReminderIdManifest] = typeof(ReminderId),
        [ReminderDeliveryManifest] = typeof(ReminderDelivery),
        [ReminderScheduleManifest] = typeof(ReminderSchedule),
        [ReminderPayloadManifest] = typeof(ReminderPayload),
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
        using var stream = new MemoryStream();
        ProtoBuf.Serializer.Serialize(stream, obj);
        return stream.ToArray();
    }

    public override object FromBinary(byte[] bytes, string manifest)
    {
        if (!ManifestToType.TryGetValue(manifest, out var type))
            throw new ArgumentException($"Unknown manifest '{manifest}'. Add it to NetclawProtobufSerializer.");

        using var stream = new MemoryStream(bytes);
        return ProtoBuf.Serializer.Deserialize(type, stream)
            ?? throw new InvalidOperationException($"Deserialization returned null for manifest '{manifest}'");
    }
}
