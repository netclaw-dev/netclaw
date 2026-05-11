// -----------------------------------------------------------------------
// <copyright file="NetclawProtoMapper.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Google.Protobuf;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Sessions;
using Proto = Netclaw.Actors.Serialization.Proto;

namespace Netclaw.Actors.Serialization;

internal static class NetclawProtoMapper
{
    internal static IMessage ToProtoMessage(object obj) => obj switch
    {
        SessionId v => ToProto(v),
        SendUserMessage v => ToProto(v),
        SerializableChatMessage v => ToProto(v),
        SerializableMediaReference v => ToProto(v),
        SerializableToolCall v => ToProto(v),
        TurnRecorded v => ToProto(v),
        SessionTitleSet v => ToProto(v),
        SessionCompacted v => ToProto(v),
        SessionSnapshot v => ToProto(v),
        TurnBroadcast v => ToProto(v),
        CompactionBroadcast v => ToProto(v),
        WorkingContext v => ToProto(v),
        ReminderId v => ToProto(v),
        ReminderDelivery v => ToProto(v),
        ReminderSchedule v => ToProto(v),
        ReminderPayload v => ToProto(v),
        AdoptedContextRecorded v => ToProto(v),
        CursorAdvanced v => ToProto(v),
        _ => throw new ArgumentException($"No proto mapping for {obj.GetType().FullName}")
    };

    // ── SessionId ──

    internal static Proto.SessionIdProto ToProto(SessionId id) => new() { Value = id.Value };
    internal static SessionId FromProto(Proto.SessionIdProto proto) => new(proto.Value);

    // ── ReminderId ──

    internal static Proto.ReminderIdProto ToProto(ReminderId id) => new() { Value = id.Value };
    internal static ReminderId FromProto(Proto.ReminderIdProto proto) => new(proto.Value);

    // ── SerializableMediaReference ──

    internal static Proto.SerializableMediaReferenceProto ToProto(SerializableMediaReference r) => new()
    {
        RelativePath = r.RelativePath,
        MimeType = r.MimeType,
        Modality = r.Modality
    };

    internal static SerializableMediaReference FromProto(Proto.SerializableMediaReferenceProto proto) => new()
    {
        RelativePath = proto.RelativePath,
        MimeType = proto.MimeType,
        Modality = proto.Modality
    };

    // ── SerializableToolCall ──

    internal static Proto.SerializableToolCallProto ToProto(SerializableToolCall tc)
    {
        var proto = new Proto.SerializableToolCallProto
        {
            CallId = tc.CallId,
            Name = tc.Name,
            ArgumentsJson = tc.ArgumentsJson
        };
        if (tc.MetaJson is not null)
            proto.MetaJson = tc.MetaJson;
        return proto;
    }

    internal static SerializableToolCall FromProto(Proto.SerializableToolCallProto proto) => new()
    {
        CallId = proto.CallId,
        Name = proto.Name,
        ArgumentsJson = proto.ArgumentsJson,
        MetaJson = proto.HasMetaJson ? proto.MetaJson : null
    };

    // ── SerializableChatMessage ──

    internal static Proto.SerializableChatMessageProto ToProto(SerializableChatMessage msg)
    {
        var proto = new Proto.SerializableChatMessageProto
        {
            Role = (Proto.ChatRole)(int)msg.Role,
            Content = msg.Content
        };
        if (msg.Name is not null)
            proto.Name = msg.Name;
        if (msg.ToolCallId is not null)
            proto.ToolCallId = msg.ToolCallId;
        proto.ToolCalls.AddRange(msg.ToolCalls.Select(ToProto));
        proto.MediaReferences.AddRange(msg.MediaReferences.Select(ToProto));
        return proto;
    }

    internal static SerializableChatMessage FromProto(Proto.SerializableChatMessageProto proto)
    {
        var msg = new SerializableChatMessage
        {
            Role = (ChatRole)(int)proto.Role,
            Content = proto.Content,
            Name = proto.HasName ? proto.Name : null,
            ToolCallId = proto.HasToolCallId ? proto.ToolCallId : null
        };
        msg.ToolCalls.AddRange(proto.ToolCalls.Select(FromProto));
        msg.MediaReferences.AddRange(proto.MediaReferences.Select(FromProto));
        return msg;
    }

    // ── SendUserMessage ──

    internal static Proto.SendUserMessageProto ToProto(SendUserMessage cmd)
    {
        var proto = new Proto.SendUserMessageProto
        {
            SessionId = ToProto(cmd.SessionId),
            Content = cmd.Content
        };
        proto.MediaReferences.AddRange(cmd.MediaReferences.Select(ToProto));
        return proto;
    }

    internal static SendUserMessage FromProto(Proto.SendUserMessageProto proto)
    {
        var cmd = new SendUserMessage
        {
            SessionId = FromProto(proto.SessionId),
            Content = proto.Content
        };
        cmd.MediaReferences.AddRange(proto.MediaReferences.Select(FromProto));
        return cmd;
    }

    // ── TurnRecorded ──

    internal static Proto.TurnRecordedProto ToProto(TurnRecorded evt)
    {
        var proto = new Proto.TurnRecordedProto
        {
            SessionId = ToProto(evt.SessionId),
            UserMessage = ToProto(evt.UserMessage),
            AssistantReply = ToProto(evt.AssistantReply),
            RecordedAtMs = evt.RecordedAtMs
        };
        if (evt.SourceReminderId is not null)
            proto.SourceReminderId = evt.SourceReminderId;
        if (evt.SourceBackgroundJobId is not null)
            proto.SourceBackgroundJobId = evt.SourceBackgroundJobId;
        return proto;
    }

    internal static TurnRecorded FromProto(Proto.TurnRecordedProto proto) => new()
    {
        SessionId = FromProto(proto.SessionId),
        UserMessage = FromProto(proto.UserMessage),
        AssistantReply = FromProto(proto.AssistantReply),
        RecordedAtMs = proto.RecordedAtMs,
        SourceReminderId = proto.HasSourceReminderId ? proto.SourceReminderId : null,
        SourceBackgroundJobId = proto.HasSourceBackgroundJobId ? proto.SourceBackgroundJobId : null
    };

    // ── SessionTitleSet ──

    internal static Proto.SessionTitleSetProto ToProto(SessionTitleSet evt) => new()
    {
        SessionId = ToProto(evt.SessionId),
        Title = evt.Title,
        SetAtMs = evt.SetAtMs
    };

    internal static SessionTitleSet FromProto(Proto.SessionTitleSetProto proto) => new()
    {
        SessionId = FromProto(proto.SessionId),
        Title = proto.Title,
        SetAtMs = proto.SetAtMs
    };

    // ── SessionCompacted ──

    internal static Proto.SessionCompactedProto ToProto(SessionCompacted evt)
    {
        var proto = new Proto.SessionCompactedProto
        {
            SessionId = ToProto(evt.SessionId),
            Summary = evt.Summary,
            TurnCountBefore = evt.TurnCountBefore,
            CompactedAtMs = evt.CompactedAtMs
        };
        proto.CompactedMessages.AddRange(evt.CompactedMessages.Select(ToProto));
        if (evt.WorkingContext is not null)
            proto.WorkingContext = ToProto(evt.WorkingContext);
        return proto;
    }

    internal static SessionCompacted FromProto(Proto.SessionCompactedProto proto)
    {
        var evt = new SessionCompacted
        {
            SessionId = FromProto(proto.SessionId),
            Summary = proto.Summary,
            TurnCountBefore = proto.TurnCountBefore,
            CompactedAtMs = proto.CompactedAtMs,
            WorkingContext = proto.WorkingContext is not null ? FromProto(proto.WorkingContext) : null
        };
        evt.CompactedMessages.AddRange(proto.CompactedMessages.Select(FromProto));
        return evt;
    }

    // ── SessionSnapshot ──

    internal static Proto.SessionSnapshotProto ToProto(SessionSnapshot snap)
    {
        var proto = new Proto.SessionSnapshotProto
        {
            TurnCount = snap.TurnCount
        };
        if (snap.Title is not null)
            proto.Title = snap.Title;
        if (snap.EligibleDeliveryTurnNumber is not null)
            proto.EligibleDeliveryTurnNumber = snap.EligibleDeliveryTurnNumber.Value;
        if (snap.WorkingContext is not null)
            proto.WorkingContext = ToProto(snap.WorkingContext);
        proto.History.AddRange(snap.History.Select(ToProto));
        proto.ActiveBackgroundJobs.AddRange(snap.ActiveBackgroundJobs.Select(ToProto));
        proto.AdoptedContextRecords.AddRange(snap.AdoptedContextRecords.Select(ToAdoptedContextSnapshotRecord));
        return proto;
    }

    internal static SessionSnapshot FromProto(Proto.SessionSnapshotProto proto)
    {
        var snap = new SessionSnapshot
        {
            TurnCount = proto.TurnCount,
            Title = proto.HasTitle ? proto.Title : null,
            EligibleDeliveryTurnNumber = proto.HasEligibleDeliveryTurnNumber ? proto.EligibleDeliveryTurnNumber : null,
            WorkingContext = proto.WorkingContext is not null ? FromProto(proto.WorkingContext) : null
        };
        snap.History.AddRange(proto.History.Select(FromProto));
        snap.ActiveBackgroundJobs.AddRange(proto.ActiveBackgroundJobs.Select(FromProto));
        snap.AdoptedContextRecords.AddRange(proto.AdoptedContextRecords.Select(FromAdoptedContextSnapshotRecord));
        return snap;
    }

    private static Proto.SessionSnapshotProto.Types.AdoptedContextSnapshotRecord ToAdoptedContextSnapshotRecord(
        SessionSnapshot.AdoptedContextSnapshotRecord r)
    {
        var proto = new Proto.SessionSnapshotProto.Types.AdoptedContextSnapshotRecord
        {
            AuthorizedMessageId = r.AuthorizedMessageId,
            Projection = r.Projection,
            HasAdoptedContext = r.HasAdoptedContext,
            HasThirdPartyAdoptedContext = r.HasThirdPartyAdoptedContext,
            ProjectionPersisted = r.ProjectionPersisted
        };
        if (r.AuthorizerSenderId is not null)
            proto.AuthorizerSenderId = r.AuthorizerSenderId;
        if (r.LowerBound is not null)
            proto.LowerBound = r.LowerBound;
        if (r.UpperBound is not null)
            proto.UpperBound = r.UpperBound;
        proto.AdoptedSpeakerIds.AddRange(r.AdoptedSpeakerIds);
        proto.Messages.AddRange(r.Messages.Select(ToAdoptedContextSnapshotMessage));
        return proto;
    }

    private static SessionSnapshot.AdoptedContextSnapshotRecord FromAdoptedContextSnapshotRecord(
        Proto.SessionSnapshotProto.Types.AdoptedContextSnapshotRecord proto)
    {
        var r = new SessionSnapshot.AdoptedContextSnapshotRecord
        {
            AuthorizedMessageId = proto.AuthorizedMessageId,
            AuthorizerSenderId = proto.HasAuthorizerSenderId ? proto.AuthorizerSenderId : null,
            LowerBound = proto.HasLowerBound ? proto.LowerBound : null,
            UpperBound = proto.HasUpperBound ? proto.UpperBound : null,
            Projection = proto.Projection,
            HasAdoptedContext = proto.HasAdoptedContext || proto.Messages.Count > 0,
            HasThirdPartyAdoptedContext = proto.HasThirdPartyAdoptedContext,
            ProjectionPersisted = proto.ProjectionPersisted
        };
        r.AdoptedSpeakerIds.AddRange(proto.AdoptedSpeakerIds.Count > 0
            ? proto.AdoptedSpeakerIds
            : proto.Messages.Select(m => m.SenderId).Distinct(StringComparer.Ordinal));
        r.Messages.AddRange(proto.Messages.Select(FromAdoptedContextSnapshotMessage));
        return r;
    }

    private static Proto.SessionSnapshotProto.Types.AdoptedContextSnapshotRecord.Types.AdoptedContextSnapshotMessage
        ToAdoptedContextSnapshotMessage(SessionSnapshot.AdoptedContextSnapshotRecord.AdoptedContextSnapshotMessage m) => new()
    {
        MessageId = m.MessageId,
        SenderId = m.SenderId,
        TimestampMs = m.TimestampMs,
        AuthorityAtInclusion = m.AuthorityAtInclusion
    };

    private static SessionSnapshot.AdoptedContextSnapshotRecord.AdoptedContextSnapshotMessage
        FromAdoptedContextSnapshotMessage(
            Proto.SessionSnapshotProto.Types.AdoptedContextSnapshotRecord.Types.AdoptedContextSnapshotMessage proto) => new()
    {
        MessageId = proto.MessageId,
        SenderId = proto.SenderId,
        TimestampMs = proto.TimestampMs,
        AuthorityAtInclusion = proto.AuthorityAtInclusion
    };

    // ── TurnBroadcast ──

    internal static Proto.TurnBroadcastProto ToProto(TurnBroadcast evt) => new()
    {
        SessionId = ToProto(evt.SessionId),
        AssistantReply = ToProto(evt.AssistantReply),
        BroadcastAtMs = evt.BroadcastAtMs
    };

    internal static TurnBroadcast FromProto(Proto.TurnBroadcastProto proto) => new()
    {
        SessionId = FromProto(proto.SessionId),
        AssistantReply = FromProto(proto.AssistantReply),
        BroadcastAtMs = proto.BroadcastAtMs
    };

    // ── CompactionBroadcast ──

    internal static Proto.CompactionBroadcastProto ToProto(CompactionBroadcast evt) => new()
    {
        SessionId = ToProto(evt.SessionId),
        Summary = evt.Summary,
        CompactedAtMs = evt.CompactedAtMs
    };

    internal static CompactionBroadcast FromProto(Proto.CompactionBroadcastProto proto) => new()
    {
        SessionId = FromProto(proto.SessionId),
        Summary = proto.Summary,
        CompactedAtMs = proto.CompactedAtMs
    };

    // ── WorkingContext ──

    internal static Proto.WorkingContextProto ToProto(WorkingContext wc)
    {
        var proto = new Proto.WorkingContextProto();
        proto.RecentFiles.AddRange(wc.RecentFiles);
        if (wc.ProjectDirectory is not null)
            proto.ProjectDirectory = wc.ProjectDirectory;
        return proto;
    }

    internal static WorkingContext FromProto(Proto.WorkingContextProto proto) => new()
    {
        RecentFiles = ImmutableList.CreateRange(proto.RecentFiles),
        ProjectDirectory = proto.HasProjectDirectory ? proto.ProjectDirectory : null
    };

    // ── ReminderDelivery ──

    internal static Proto.ReminderDeliveryProto ToProto(ReminderDelivery rd)
    {
        var proto = new Proto.ReminderDeliveryProto
        {
            Kind = (Proto.DeliveryKind)(int)rd.Kind
        };
        if (rd.Transport is not null)
            proto.Transport = rd.Transport;
        if (rd.Address is not null)
            proto.Address = rd.Address;
        if (rd.SessionId is not null)
            proto.SessionId = rd.SessionId;
        if (rd.OriginChannelType is not null)
            proto.OriginChannelType = (Proto.ChannelType)(int)rd.OriginChannelType.Value;
        return proto;
    }

    internal static ReminderDelivery FromProto(Proto.ReminderDeliveryProto proto) => new()
    {
        Kind = (Reminders.DeliveryKind)(int)proto.Kind,
        Transport = proto.HasTransport ? proto.Transport : null,
        Address = proto.HasAddress ? proto.Address : null,
        SessionId = proto.HasSessionId ? proto.SessionId : null,
        OriginChannelType = proto.HasOriginChannelType ? (ChannelType)(int)proto.OriginChannelType : null
    };

    // ── ReminderSchedule ──

    internal static Proto.ReminderScheduleProto ToProto(ReminderSchedule rs)
    {
        var proto = new Proto.ReminderScheduleProto
        {
            Type = (Proto.ReminderScheduleType)(int)rs.Type
        };
        if (rs.FireAtMs is not null)
            proto.FireAtMs = rs.FireAtMs.Value;
        if (rs.IntervalTicks is not null)
            proto.IntervalTicks = rs.IntervalTicks.Value;
        if (rs.CronExpression is not null)
            proto.CronExpression = rs.CronExpression;
        if (rs.OriginalExpression is not null)
            proto.OriginalExpression = rs.OriginalExpression;
        return proto;
    }

    internal static ReminderSchedule FromProto(Proto.ReminderScheduleProto proto) => new()
    {
        Type = (Reminders.ReminderScheduleType)(int)proto.Type,
        FireAtMs = proto.HasFireAtMs ? proto.FireAtMs : null,
        IntervalTicks = proto.HasIntervalTicks ? proto.IntervalTicks : null,
        CronExpression = proto.HasCronExpression ? proto.CronExpression : null,
        OriginalExpression = proto.HasOriginalExpression ? proto.OriginalExpression : null
    };

    // ── ReminderPayload ──

    internal static Proto.ReminderPayloadProto ToProto(ReminderPayload rp) => new()
    {
        Id = ToProto(rp.Id)
    };

    internal static ReminderPayload FromProto(Proto.ReminderPayloadProto proto) => new()
    {
        Id = FromProto(proto.Id)
    };

    // ── AdoptedContextRecorded ──

    internal static Proto.AdoptedContextRecordedProto ToProto(AdoptedContextRecorded evt)
    {
        var proto = new Proto.AdoptedContextRecordedProto
        {
            SessionId = ToProto(evt.SessionId),
            AuthorizedMessageId = evt.AuthorizedMessageId,
            Projection = evt.Projection,
            HasAdoptedContext = evt.HasAdoptedContext,
            HasThirdPartyAdoptedContext = evt.HasThirdPartyAdoptedContext,
            ProjectionPersisted = evt.ProjectionPersisted,
            RecordedAtMs = evt.RecordedAtMs
        };
        if (evt.AuthorizerSenderId is not null)
            proto.AuthorizerSenderId = evt.AuthorizerSenderId;
        if (evt.LowerBound is not null)
            proto.LowerBound = evt.LowerBound;
        if (evt.UpperBound is not null)
            proto.UpperBound = evt.UpperBound;
        proto.AdoptedSpeakerIds.AddRange(evt.AdoptedSpeakerIds);
        proto.Messages.AddRange(evt.Messages.Select(ToAdoptedMessageRecord));
        return proto;
    }

    internal static AdoptedContextRecorded FromProto(Proto.AdoptedContextRecordedProto proto)
    {
        var evt = new AdoptedContextRecorded
        {
            SessionId = FromProto(proto.SessionId),
            AuthorizedMessageId = proto.AuthorizedMessageId,
            AuthorizerSenderId = proto.HasAuthorizerSenderId ? proto.AuthorizerSenderId : null,
            LowerBound = proto.HasLowerBound ? proto.LowerBound : null,
            UpperBound = proto.HasUpperBound ? proto.UpperBound : null,
            Projection = proto.Projection,
            HasAdoptedContext = proto.HasAdoptedContext || proto.Messages.Count > 0,
            HasThirdPartyAdoptedContext = proto.HasThirdPartyAdoptedContext,
            ProjectionPersisted = proto.ProjectionPersisted,
            RecordedAtMs = proto.RecordedAtMs
        };
        evt.AdoptedSpeakerIds.AddRange(proto.AdoptedSpeakerIds.Count > 0
            ? proto.AdoptedSpeakerIds
            : proto.Messages.Select(m => m.SenderId).Distinct(StringComparer.Ordinal));
        evt.Messages.AddRange(proto.Messages.Select(FromAdoptedMessageRecord));
        return evt;
    }

    private static Proto.AdoptedContextRecordedProto.Types.AdoptedMessageRecordProto ToAdoptedMessageRecord(
        AdoptedContextRecorded.AdoptedMessageRecord m) => new()
    {
        MessageId = m.MessageId,
        SenderId = m.SenderId,
        TimestampMs = m.TimestampMs,
        AuthorityAtInclusion = m.AuthorityAtInclusion
    };

    private static AdoptedContextRecorded.AdoptedMessageRecord FromAdoptedMessageRecord(
        Proto.AdoptedContextRecordedProto.Types.AdoptedMessageRecordProto proto) => new()
    {
        MessageId = proto.MessageId,
        SenderId = proto.SenderId,
        TimestampMs = proto.TimestampMs,
        AuthorityAtInclusion = proto.AuthorityAtInclusion
    };

    // ── CursorAdvanced ──

    internal static Proto.CursorAdvancedProto ToProto(CursorAdvanced ca) => new() { Cursor = ca.Cursor };
    internal static CursorAdvanced FromProto(Proto.CursorAdvancedProto proto) => new(proto.Cursor);

    // ── ActiveJobInfo ──

    internal static Proto.ActiveJobInfoProto ToProto(ActiveJobInfo job) => new()
    {
        JobId = job.JobId,
        Command = job.Command,
        Rationale = job.Rationale,
        StartedAtMs = job.StartedAtMs,
        Audience = (Proto.TrustAudience)(int)job.Audience,
        Boundary = job.Boundary
    };

    internal static ActiveJobInfo FromProto(Proto.ActiveJobInfoProto proto) => new()
    {
        JobId = proto.JobId,
        Command = proto.Command,
        Rationale = proto.Rationale,
        StartedAtMs = proto.StartedAtMs,
        Audience = (Configuration.TrustAudience)(int)proto.Audience,
        Boundary = proto.Boundary
    };
}
