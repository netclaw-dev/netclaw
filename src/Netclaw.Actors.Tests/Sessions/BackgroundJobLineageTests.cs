// -----------------------------------------------------------------------
// <copyright file="BackgroundJobLineageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Google.Protobuf;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class BackgroundJobLineageTests
{
    private static readonly SessionId Session = new("job-lineage/test");
    private static readonly BackgroundJobOrigin Origin = new(new TurnId("original-task"), new ToolCallId("launch"));

    [Fact]
    public void Job_continuation_preserves_original_lineage_after_a_fresh_task_and_snapshot()
    {
        var state = SessionState.Empty.TrackBackgroundJob("bg-job:first", Job("first"))
            .TrackBackgroundJob("bg-job:second", Job("second"));
        var fresh = UserInput("fresh-task");
        state = state.Apply(fresh).Apply(new ToolTaskAdopted(false)
        { SessionId = Session, TurnContext = fresh.TurnContext, InputIds = new[] { fresh.InputId } });
        Assert.Equal("fresh-task", state.AdoptedTaskContext!.TurnId);
        state = state.CloseInputs(new[] { fresh.InputId });
        state = RoundTrip(state);
        var delivery = Delivery("first");
        state = state.Apply(delivery);
        Assert.True(state.TryGetBackgroundContinuation(delivery, out _, out _));
        state = state.Apply(new ToolTaskAdopted(false)
        { SessionId = Session, TurnContext = delivery.TurnContext, InputIds = new[] { delivery.InputId }, ContinuedJobKey = "bg-job:first" });
        Assert.Equal("bg-job:first", state.AdoptedTaskContext!.TurnId);
        var restored = RoundTrip(state.CloseInputs(new[] { delivery.InputId }));
        Assert.Equal("bg-job:first", restored.AdoptedTaskContext!.TurnId);
        var final = restored.CompleteTurnBackgroundJobBookkeeping(new BackgroundJobId(restored.AdoptedTaskContext.TurnId));
        Assert.DoesNotContain("bg-job:first", final.ActiveBackgroundJobs.Keys);
        Assert.Equal(Origin, final.ActiveBackgroundJobs["bg-job:second"].Origin);
        Assert.Contains(new BackgroundJobId("bg-job:first"), RoundTrip(final).ProcessedBackgroundJobIds);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void Missing_parent_receipt_allows_only_a_genuine_legacy_origin(int version, bool hasOrigin)
    {
        var delivery = Delivery("orphan") with { BackgroundJobLineageVersion = version, BackgroundJobOrigin = hasOrigin ? Origin : null };
        Assert.Equal(version == 0, SessionState.Empty.TryGetBackgroundContinuation(delivery, out _, out _));
        var tracked = SessionState.Empty.TrackBackgroundJob("bg-job:orphan", Job("orphan"));
        Assert.False(tracked.TryGetBackgroundContinuation(delivery with
        { BackgroundJobLineageVersion = 0, BackgroundJobOrigin = null }, out _, out _));
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("authority")]
    [InlineData("message")]
    [InlineData("principal")]
    [InlineData("key")]
    public void A_foreign_or_forged_delivery_cannot_continue_lineage(string mutation)
    {
        var state = SessionState.Empty.TrackBackgroundJob("bg-job:first", Job("first"));
        var delivery = Delivery("first");
        delivery = mutation switch
        {
            "origin" => delivery with { BackgroundJobOrigin = Origin with { TurnId = new TurnId("another-task") } },
            "authority" => delivery with { TurnContext = delivery.TurnContext with { TurnId = "another-delivery" } },
            "message" => delivery with { SourceMessageId = "another-delivery" },
            "principal" => delivery with { TurnContext = delivery.TurnContext with { RequesterPrincipal = PrincipalClassification.UntrustedExternal } },
            "key" => delivery with { SourceBackgroundJobId = new BackgroundJobId("first") },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        Assert.False(state.TryGetBackgroundContinuation(delivery, out _, out _));
    }

    [Fact]
    public void Rejected_delivery_report_is_durable_and_does_not_close_another_task()
    {
        var user = UserInput("active-user");
        var delivery = Delivery("orphan");
        var later = UserInput("later-user");
        var state = SessionState.Empty.Apply(user).Apply(new ToolTaskAdopted(false)
        { SessionId = Session, TurnContext = user.TurnContext, InputIds = new[] { user.InputId } })
            .CloseInputs(new[] { user.InputId });
        state = state with { LoopReceiptFailure = true };
        state = state.Apply(delivery).Apply(later);
        var closed = new InputClosed
        {
            SessionId = Session, TaskId = delivery.TurnContext.TurnId, InputIds = new[] { delivery.InputId },
            SourceBackgroundJobId = delivery.SourceBackgroundJobId,
            RejectedJobReport = new SerializableChatMessage
            { Role = ChatRole.Assistant, Content = "The job lineage is invalid. Job result data: partial output." }
        };
        var replayed = NetclawProtoMapper.FromProto(NetclawProtoMapper.ToProto(closed));
        state = state.Apply(replayed);
        var restored = RoundTrip(state);
        Assert.Equal("active-user", restored.AdoptedTaskContext!.TurnId);
        Assert.True(restored.LoopReceiptFailure);
        Assert.Equal(later.InputId, Assert.Single(restored.PendingInputs).InputId);
        Assert.Contains(new BackgroundJobId("bg-job:orphan"), restored.ProcessedBackgroundJobIds);
        Assert.Equal(closed.RejectedJobReport.Content, Assert.Single(restored.History).Content);
        var retry = restored.Apply(replayed);
        Assert.Equal(NetclawProtoMapper.ToProto(restored.ToSnapshot()).ToByteArray(),
            NetclawProtoMapper.ToProto(retry.ToSnapshot()).ToByteArray());
        Assert.Throws<InvalidDataException>(() => state.Apply(closed with
        { InputIds = new[] { later.InputId }, SourceBackgroundJobId = new BackgroundJobId("bg-job:foreign") }));
    }

    [Fact]
    public void Receipt_records_only_the_admitted_job_origin()
    {
        var state = SessionState.Empty.TrackBackgroundJob("bg-job:first", Job("first"));
        state = state.ApplyLoopAdmission(new ToolBatchStarted
        {
            LoopAdmission = new ToolLoopAdmission
            { TaskId = Origin.TurnId.Value, ActionHash = "launch-action", Calls = new[] { new ToolLoopPreparedCall("launch", "shell", "args", false) } }
        });
        var receipt = new ToolCallRecorded
        {
            SessionId = Session,
            StartedBackgroundJob = Job("second"),
            LoopObservation = new ToolLoopObservation { CallId = "launch", Category = (int)ToolInvocationOutcomeCategory.Success, ResultHash = "accepted-ack" }
        };
        Assert.Throws<InvalidDataException>(() => state.ApplyLoopObservation(receipt with
        { StartedBackgroundJob = receipt.StartedBackgroundJob with { Origin = Origin with { CallId = new ToolCallId("other-call") } } }));
        state = state.ApplyLoopObservation(NetclawProtoMapper.FromProto(NetclawProtoMapper.ToProto(receipt)));
        Assert.Equal(2, state.ActiveBackgroundJobs.Count);
        foreach (var job in RoundTrip(state).ActiveBackgroundJobs.Values)
        {
            Assert.Equal(Origin, job.Origin);
        }
    }

    private static SessionState RoundTrip(SessionState state)
        => SessionState.FromSnapshot(NetclawProtoMapper.FromProto(NetclawProtoMapper.ToProto(state.ToSnapshot())));

    private static ActiveJobInfo Job(string id) => new()
    {
        JobId = new BackgroundJobId(id), Command = "echo result", Rationale = "Read the result.", StartedAtMs = 0,
        Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
        LineageVersion = 1, Origin = Origin
    };

    private static InputAdmitted UserInput(string turn) => new()
    {
        SessionId = Session, InputId = InputId.New(), TurnContext = TurnContext.FromMessageSource(Session, new TurnId(turn), null).ToRecord(),
        UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "Read one value." }
    };

    private static InputAdmitted Delivery(string id)
    {
        var key = $"bg-job:{id}";
        var source = new MessageSource
        {
            ChannelType = ChannelType.Tui, SenderId = new SenderId("background-job-system"), MessageId = key,
            Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal, Principal = PrincipalClassification.VerifiedAutomation,
            Provenance = new SourceProvenance(TransportAuthenticity.LocalProcess, PayloadTaint.Trusted)
            { SourceKind = new SourceKind(BackgroundJobManagerActor.SourceKind) }
        };
        return new InputAdmitted
        {
            SessionId = Session, InputId = InputId.New(), SourceMessageId = key,
            SourceBackgroundJobId = new BackgroundJobId(key), BackgroundJobLineageVersion = 1, BackgroundJobOrigin = Origin,
            TurnContext = TurnContext.FromMessageSource(Session, new TurnId(key), source).ToRecord(),
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "The job returned partial output." }
        };
    }
}
