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
        ToolBatchStarted v => ToProto(v),
        ToolCallRecorded v => ToProto(v),
        ToolApprovalRequested v => ToProto(v),
        ToolApprovalResolved v => ToProto(v),
        ToolBatchAbandoned v => ToProto(v),
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
        MemoriesDistilledV2 v => ToProto(v),
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
        MimeType = r.MimeType.Value,
        Modality = r.Modality,
        FileSizeBytes = r.FileSizeBytes
    };

    internal static SerializableMediaReference FromProto(Proto.SerializableMediaReferenceProto proto) => new()
    {
        RelativePath = proto.RelativePath,
        MimeType = new Netclaw.Security.MimeType(proto.MimeType),
        Modality = proto.Modality,
        FileSizeBytes = proto.FileSizeBytes
    };

    // ── SerializableToolCall ──

    internal static Proto.SerializableToolCallProto ToProto(SerializableToolCall tc)
    {
        var proto = new Proto.SerializableToolCallProto
        {
            CallId = tc.CallId.Value,
            Name = tc.Name.Value,
            ArgumentsJson = tc.ArgumentsJson
        };
        if (tc.MetaJson is not null)
            proto.MetaJson = tc.MetaJson;
        return proto;
    }

    internal static SerializableToolCall FromProto(Proto.SerializableToolCallProto proto) => new()
    {
        CallId = new Netclaw.Tools.ToolCallId(proto.CallId),
        Name = new Netclaw.Tools.ToolName(proto.Name),
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
            proto.ToolCallId = msg.ToolCallId.Value.Value;
        proto.ToolCalls.AddRange(msg.ToolCalls.Select(ToProto));
        proto.MediaReferences.AddRange(msg.MediaReferences.Select(ToProto));
        return proto;
    }

    internal static SerializableChatMessage FromProto(Proto.SerializableChatMessageProto proto) => new()
    {
        Role = (ChatRole)(int)proto.Role,
        Content = proto.Content,
        Name = proto.HasName ? proto.Name : null,
        ToolCallId = proto.HasToolCallId ? new Netclaw.Tools.ToolCallId(proto.ToolCallId) : null,
        ToolCalls = proto.ToolCalls.Select(FromProto).ToArray(),
        MediaReferences = proto.MediaReferences.Select(FromProto).ToArray()
    };

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

    internal static SendUserMessage FromProto(Proto.SendUserMessageProto proto) => new()
    {
        SessionId = FromProto(proto.SessionId),
        Content = proto.Content,
        MediaReferences = proto.MediaReferences.Select(FromProto).ToArray()
    };

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

    internal static SessionCompacted FromProto(Proto.SessionCompactedProto proto) => new()
    {
        SessionId = FromProto(proto.SessionId),
        Summary = proto.Summary,
        TurnCountBefore = proto.TurnCountBefore,
        CompactedAtMs = proto.CompactedAtMs,
        WorkingContext = proto.WorkingContext is not null ? FromProto(proto.WorkingContext) : null,
        CompactedMessages = proto.CompactedMessages.Select(FromProto).ToArray()
    };

    // ── Tool batch / approval events ──

    internal static Proto.ToolBatchStartedProto ToProto(ToolBatchStarted evt) => new()
    {
        SessionId = ToProto(evt.SessionId),
        UserMessage = ToProto(evt.UserMessage),
        AssistantMessage = ToProto(evt.AssistantMessage),
        StartedAtMs = evt.StartedAtMs
    };

    internal static ToolBatchStarted FromProto(Proto.ToolBatchStartedProto proto) => new()
    {
        SessionId = FromProto(proto.SessionId),
        UserMessage = FromProto(proto.UserMessage),
        AssistantMessage = FromProto(proto.AssistantMessage),
        StartedAtMs = proto.StartedAtMs
    };

    internal static Proto.ToolCallRecordedProto ToProto(ToolCallRecorded evt) => new()
    {
        SessionId = ToProto(evt.SessionId),
        ToolResult = ToProto(evt.ToolResult),
        RecordedAtMs = evt.RecordedAtMs
    };

    internal static ToolCallRecorded FromProto(Proto.ToolCallRecordedProto proto) => new()
    {
        SessionId = FromProto(proto.SessionId),
        ToolResult = FromProto(proto.ToolResult),
        RecordedAtMs = proto.RecordedAtMs
    };

    internal static Proto.ToolApprovalRequestedProto ToProto(ToolApprovalRequested evt)
    {
        var proto = new Proto.ToolApprovalRequestedProto
        {
            SessionId = ToProto(evt.SessionId),
            CallId = evt.CallId,
            ToolName = evt.ToolName,
            Audience = (Proto.TrustAudience)(int)evt.Audience,
            RequestedAtMs = evt.RequestedAtMs
        };
        proto.Patterns.AddRange(evt.Patterns);
        proto.CandidateVerbs.AddRange(evt.CandidateVerbs);
        if (evt.RequesterSenderId is not null)
            proto.RequesterSenderId = evt.RequesterSenderId.Value.Value;
        if (evt.RequesterPrincipal is not null)
            proto.RequesterPrincipal = (int)evt.RequesterPrincipal.Value;
        if (evt.Cwd is not null)
            proto.Cwd = evt.Cwd;
        if (evt.Boundary is not null)
            proto.Boundary = evt.Boundary.Value.Value;
        if (evt.ChannelType is not null)
            proto.ChannelType = evt.ChannelType;
        if (evt.SupportsInteractiveApproval is not null)
            proto.SupportsInteractiveApproval = evt.SupportsInteractiveApproval.Value;
        proto.OptionKeys.AddRange(evt.OptionKeys);
        proto.Candidates.AddRange(evt.Candidates.Select(ToApprovalCandidateProto));
        return proto;
    }

    internal static ToolApprovalRequested FromProto(Proto.ToolApprovalRequestedProto proto) => new()
    {
        SessionId = FromProto(proto.SessionId),
        CallId = proto.CallId,
        ToolName = proto.ToolName,
        Patterns = proto.Patterns.ToArray(),
        CandidateVerbs = proto.CandidateVerbs.ToArray(),
        Audience = (Configuration.TrustAudience)(int)proto.Audience,
        RequesterSenderId = proto.HasRequesterSenderId ? new SenderId(proto.RequesterSenderId) : null,
        RequesterPrincipal = proto.HasRequesterPrincipal
            ? (Configuration.PrincipalClassification)proto.RequesterPrincipal
            : null,
        Cwd = proto.HasCwd ? proto.Cwd : null,
        Boundary = proto.HasBoundary ? new Configuration.TrustBoundary(proto.Boundary) : null,
        ChannelType = proto.HasChannelType ? proto.ChannelType : null,
        SupportsInteractiveApproval = proto.HasSupportsInteractiveApproval ? proto.SupportsInteractiveApproval : null,
        OptionKeys = proto.OptionKeys.ToArray(),
        Candidates = proto.Candidates.Select(FromApprovalCandidateProto).ToArray(),
        RequestedAtMs = proto.RequestedAtMs
    };

    internal static Proto.ToolApprovalResolvedProto ToProto(ToolApprovalResolved evt) => new()
    {
        SessionId = ToProto(evt.SessionId),
        CallId = evt.CallId,
        Decision = evt.Decision,
        ResolvedAtMs = evt.ResolvedAtMs
    };

    internal static ToolApprovalResolved FromProto(Proto.ToolApprovalResolvedProto proto) => new()
    {
        SessionId = FromProto(proto.SessionId),
        CallId = proto.CallId,
        Decision = proto.Decision,
        ResolvedAtMs = proto.ResolvedAtMs
    };

    internal static Proto.ToolBatchAbandonedProto ToProto(ToolBatchAbandoned evt)
    {
        var proto = new Proto.ToolBatchAbandonedProto
        {
            SessionId = ToProto(evt.SessionId),
            AbandonedAtMs = evt.AbandonedAtMs
        };
        proto.ToolResults.AddRange(evt.ToolResults.Select(ToProto));
        return proto;
    }

    internal static ToolBatchAbandoned FromProto(Proto.ToolBatchAbandonedProto proto) => new()
    {
        SessionId = FromProto(proto.SessionId),
        ToolResults = proto.ToolResults.Select(FromProto).ToArray(),
        AbandonedAtMs = proto.AbandonedAtMs
    };

    private static Proto.ToolApprovalRequestedProto.Types.ApprovalCandidateProto ToApprovalCandidateProto(
        ToolApprovalRequested.ApprovalCandidateRecord c)
    {
        var proto = new Proto.ToolApprovalRequestedProto.Types.ApprovalCandidateProto
        {
            Verb = c.Verb
        };
        if (c.Directory is not null)
            proto.Directory = c.Directory;
        return proto;
    }

    private static ToolApprovalRequested.ApprovalCandidateRecord FromApprovalCandidateProto(
        Proto.ToolApprovalRequestedProto.Types.ApprovalCandidateProto proto) => new()
    {
        Verb = proto.Verb,
        Directory = proto.HasDirectory ? proto.Directory : null
    };

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
            proto.EligibleDeliveryTurnNumber = snap.EligibleDeliveryTurnNumber.Value.Value;
        if (snap.WorkingContext is not null)
            proto.WorkingContext = ToProto(snap.WorkingContext);
        proto.History.AddRange(snap.History.Select(ToProto));
        proto.ActiveBackgroundJobs.AddRange(snap.ActiveBackgroundJobs.Select(ToProto));
        proto.AdoptedContextRecords.AddRange(snap.AdoptedContextRecords.Select(ToAdoptedContextSnapshotRecord));
        return proto;
    }

    internal static SessionSnapshot FromProto(Proto.SessionSnapshotProto proto) => new()
    {
        TurnCount = proto.TurnCount,
        Title = proto.HasTitle ? proto.Title : null,
        EligibleDeliveryTurnNumber = proto.HasEligibleDeliveryTurnNumber
            ? new TurnNumber(proto.EligibleDeliveryTurnNumber)
            : null,
        WorkingContext = proto.WorkingContext is not null ? FromProto(proto.WorkingContext) : null,
        History = proto.History.Select(FromProto).ToArray(),
        ActiveBackgroundJobs = proto.ActiveBackgroundJobs.Select(FromProto).ToArray(),
        AdoptedContextRecords = proto.AdoptedContextRecords.Select(FromAdoptedContextSnapshotRecord).ToArray()
    };

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
            proto.AuthorizerSenderId = r.AuthorizerSenderId.Value.Value;
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
        var speakerIds = proto.AdoptedSpeakerIds.Count > 0
            ? (IReadOnlyList<string>)proto.AdoptedSpeakerIds.ToArray()
            : proto.Messages.Select(m => m.SenderId).Distinct(StringComparer.Ordinal).ToArray();

        return new SessionSnapshot.AdoptedContextSnapshotRecord
        {
            AuthorizedMessageId = proto.AuthorizedMessageId,
            AuthorizerSenderId = proto.HasAuthorizerSenderId ? new SenderId(proto.AuthorizerSenderId) : null,
            LowerBound = proto.HasLowerBound ? proto.LowerBound : null,
            UpperBound = proto.HasUpperBound ? proto.UpperBound : null,
            Projection = proto.Projection,
            HasAdoptedContext = proto.HasAdoptedContext || proto.Messages.Count > 0,
            HasThirdPartyAdoptedContext = proto.HasThirdPartyAdoptedContext,
            ProjectionPersisted = proto.ProjectionPersisted,
            AdoptedSpeakerIds = speakerIds,
            Messages = proto.Messages.Select(FromAdoptedContextSnapshotMessage).ToArray()
        };
    }

    private static Proto.SessionSnapshotProto.Types.AdoptedContextSnapshotRecord.Types.AdoptedContextSnapshotMessage
        ToAdoptedContextSnapshotMessage(SessionSnapshot.AdoptedContextSnapshotRecord.AdoptedContextSnapshotMessage m) => new()
    {
        MessageId = m.MessageId,
        SenderId = m.SenderId.Value,
        TimestampMs = m.TimestampMs,
        AuthorityAtInclusion = m.AuthorityAtInclusion
    };

    private static SessionSnapshot.AdoptedContextSnapshotRecord.AdoptedContextSnapshotMessage
        FromAdoptedContextSnapshotMessage(
            Proto.SessionSnapshotProto.Types.AdoptedContextSnapshotRecord.Types.AdoptedContextSnapshotMessage proto) => new()
    {
        MessageId = proto.MessageId,
        SenderId = new SenderId(proto.SenderId),
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
            proto.AuthorizerSenderId = evt.AuthorizerSenderId.Value.Value;
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
        var speakerIds = proto.AdoptedSpeakerIds.Count > 0
            ? (IReadOnlyList<string>)proto.AdoptedSpeakerIds.ToArray()
            : proto.Messages.Select(m => m.SenderId).Distinct(StringComparer.Ordinal).ToArray();

        return new AdoptedContextRecorded
        {
            SessionId = FromProto(proto.SessionId),
            AuthorizedMessageId = proto.AuthorizedMessageId,
            AuthorizerSenderId = proto.HasAuthorizerSenderId ? new SenderId(proto.AuthorizerSenderId) : null,
            LowerBound = proto.HasLowerBound ? proto.LowerBound : null,
            UpperBound = proto.HasUpperBound ? proto.UpperBound : null,
            Projection = proto.Projection,
            HasAdoptedContext = proto.HasAdoptedContext || proto.Messages.Count > 0,
            HasThirdPartyAdoptedContext = proto.HasThirdPartyAdoptedContext,
            ProjectionPersisted = proto.ProjectionPersisted,
            RecordedAtMs = proto.RecordedAtMs,
            AdoptedSpeakerIds = speakerIds,
            Messages = proto.Messages.Select(FromAdoptedMessageRecord).ToArray()
        };
    }

    private static Proto.AdoptedContextRecordedProto.Types.AdoptedMessageRecordProto ToAdoptedMessageRecord(
        AdoptedContextRecorded.AdoptedMessageRecord m) => new()
    {
        MessageId = m.MessageId,
        SenderId = m.SenderId.Value,
        TimestampMs = m.TimestampMs,
        AuthorityAtInclusion = m.AuthorityAtInclusion
    };

    private static AdoptedContextRecorded.AdoptedMessageRecord FromAdoptedMessageRecord(
        Proto.AdoptedContextRecordedProto.Types.AdoptedMessageRecordProto proto) => new()
    {
        MessageId = proto.MessageId,
        SenderId = new SenderId(proto.SenderId),
        TimestampMs = proto.TimestampMs,
        AuthorityAtInclusion = proto.AuthorityAtInclusion
    };

    // ── CursorAdvanced ──

    internal static Proto.CursorAdvancedProto ToProto(CursorAdvanced ca) => new() { Cursor = ca.Cursor };
    internal static CursorAdvanced FromProto(Proto.CursorAdvancedProto proto) => new(proto.Cursor);

    // ── MemoriesDistilledV2 / ProposedMemoryContext ──

    internal static Proto.ProposedMemoryContextProto ToProto(ProposedMemoryContext ctx) => new()
    {
        Anchor = ctx.Anchor,
        Title = ctx.Title,
        Content = ctx.Content
    };

    internal static ProposedMemoryContext FromProto(Proto.ProposedMemoryContextProto proto) =>
        new(proto.Anchor, proto.Title, proto.Content);

    internal static Proto.MemoriesDistilledV2Proto ToProto(MemoriesDistilledV2 evt)
    {
        var proto = new Proto.MemoriesDistilledV2Proto { TimestampMs = evt.TimestampMs };
        proto.Anchors.AddRange(evt.Anchors);
        foreach (var p in evt.Proposals)
            proto.Proposals.Add(ToProto(p));
        return proto;
    }

    internal static MemoriesDistilledV2 FromProto(Proto.MemoriesDistilledV2Proto proto) => new(
        Anchors: proto.Anchors.ToList(),
        Proposals: proto.Proposals.Select(FromProto).ToList(),
        TimestampMs: proto.TimestampMs);

    // ── ActiveJobInfo ──

    internal static Proto.ActiveJobInfoProto ToProto(ActiveJobInfo job) => new()
    {
        JobId = job.JobId.Value,
        Command = job.Command,
        Rationale = job.Rationale,
        StartedAtMs = job.StartedAtMs,
        Audience = (Proto.TrustAudience)(int)job.Audience,
        Boundary = job.Boundary.Value
    };

    internal static ActiveJobInfo FromProto(Proto.ActiveJobInfoProto proto) => new()
    {
        JobId = new BackgroundJobId(proto.JobId),
        Command = proto.Command,
        Rationale = proto.Rationale,
        StartedAtMs = proto.StartedAtMs,
        Audience = (Configuration.TrustAudience)(int)proto.Audience,
        // proto3 cannot express an absent field; a legacy record with no
        // persisted boundary deserializes to "" — fall closed to the
        // legacy-restricted boundary rather than throwing on construction.
        Boundary = string.IsNullOrEmpty(proto.Boundary)
            ? Configuration.TrustBoundary.LegacyRestricted
            : new Configuration.TrustBoundary(proto.Boundary)
    };
}
