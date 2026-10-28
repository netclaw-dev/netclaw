// -----------------------------------------------------------------------
// <copyright file="ChildRunParkSerializationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections;
using System.Collections.Immutable;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Google.Protobuf;
using Netclaw.Actors.Authorization.Consent;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Protocol;

/// <summary>
/// PR 2 of durable-child-consent-wait (durable-child-park-recovery, design D1). The durable shapes of a park: the Parked
/// and Resumed events (fields 8 and 9 of the cre-v2 event), the parked run record in the snapshot (fields 21 and 22),
/// and the loud failure for a case that a binary does not know.
/// </summary>
public sealed class ChildRunParkSerializationTests : TestKit
{
    internal static readonly SessionId Owner = new("test/park-wire");
    internal static readonly SubAgentRunId RunId = new("park-run");

    public ChildRunParkSerializationTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
        => builder.WithNetclawSerialization();

    /// <summary>
    /// The Parked and Resumed events travel with the cre-v2 manifest and come back equal. Rows: a consent park with two
    /// requests; a drain park with no request (running-child-drain-park task 3.4).
    /// Planted bug: the mapper drops a field of the park or of a request, a new manifest appears, or a park with no
    /// request does not come back.
    /// </summary>
    [Theory]
    [InlineData("two-requests")]
    [InlineData("zero-requests")]
    public void The_park_and_resume_events_round_trip_with_the_cre_v2_manifest(string row)
    {
        // Arrange
        var serializer = Codec();
        var parked = new ChildRunEvent.Parked(FullPark(), row == "zero-requests" ? [] : [Request(1), Request(2)])
        { SessionId = Owner, RunId = RunId, RecordedAtMs = 10 };
        var resumed = new ChildRunEvent.Resumed { SessionId = Owner, RunId = RunId, RecordedAtMs = 11 };
        AssertNoDefaults(parked.Park, nameof(ChildRunPark));

        // Act + Assert
        foreach (ChildRunEvent evt in new ChildRunEvent[] { parked, resumed })
        {
            Assert.Equal("cre-v2", serializer.Manifest(evt));
            Assert.Equivalent(evt, RoundTrip(serializer, evt), strict: true);
        }
    }

    /// <summary>
    /// A snapshot keeps a parked run with its park, its requests, the answer of the first request, and its prompt
    /// counter. The walk proves that each park field and list element holds a value before the trip, so a field that the
    /// mapper drops cannot pass as a default. Rows: an answered and an open request; a drain park with no request
    /// (running-child-drain-park task 3.4).
    /// Planted bug: the snapshot drops the answer of a parked request, the prompt counter, or a park field, or it
    /// refuses a park with no request.
    /// </summary>
    [Theory]
    [InlineData("answered-and-open")]
    [InlineData("zero-requests")]
    public void A_parked_run_record_round_trips_through_the_snapshot(string row)
    {
        // Arrange
        var serializer = Codec();
        var lifecycle = new ChildRunLifecycle.Parked(FullPark(), row == "zero-requests" ? []
            : [new ChildParkRequest(Request(1), Resolution(1)), new ChildParkRequest(Request(2), null)]);
        var run = Run(lifecycle) with { PromptsIssued = 2 };
        AssertNoDefaults(lifecycle.Park, nameof(ChildRunPark));
        Assert.All(lifecycle.Requests, request => Assert.NotNull(request.Request));
        var state = SessionState.Empty with { ChildRuns = ImmutableDictionary<SubAgentRunId, BackgroundChildRun>.Empty.Add(RunId, run) };

        // Act
        var restored = SessionState.FromSnapshot(RoundTrip(serializer, state.ToSnapshot())).ChildRuns[RunId];

        // Assert
        Assert.Equal(2, restored.PromptsIssued);
        Assert.Equivalent(lifecycle, restored.Lifecycle, strict: true);
    }

    /// <summary>
    /// "An older binary refuses a park record" (design Migration Plan), as a proxy. A binary reads a fact or a lifecycle
    /// case that it does not know as none, and its mapper then fails loudly. 0.28.0-beta.2 reads fact 8 or 9 and
    /// lifecycle 21 this way, so it fails the recovery of such a session and runs no parked call. A second binary is not
    /// in the test run, so the spec writes field 30, which this binary does not know, and proves the same rule.
    /// Planted bug: the mapper skips a record with no known case or maps it to a default.
    /// </summary>
    [Fact]
    public void A_record_with_an_unknown_fact_or_lifecycle_fails_loudly()
    {
        // Arrange
        var evt = NetclawProtoMapper.ToProto(new ChildRunEvent.Resumed { SessionId = Owner, RunId = RunId, RecordedAtMs = 1 });
        evt.ClearFact();
        var snapshot = NetclawProtoMapper.ToProtoV2(new SessionSnapshot { ChildRuns = [Run(new ChildRunLifecycle.Accepted(null))] });
        var run = snapshot.ChildRuns.Single();
        run.ClearLifecycle();
        snapshot.ChildRuns.Clear();
        snapshot.ChildRuns.Add(Serialization.Proto.BackgroundChildRunV2Proto.Parser.ParseFrom(WithUnknownField(run.ToByteArray())));

        // Act + Assert
        var unknownFact = Serialization.Proto.ChildRunEventV2Proto.Parser.ParseFrom(WithUnknownField(evt.ToByteArray()));
        Assert.Throws<InvalidDataException>(() => NetclawProtoMapper.FromProto(unknownFact));
        Assert.Throws<InvalidDataException>(() => NetclawProtoMapper.FromProto(
            Serialization.Proto.SessionSnapshotV2Proto.Parser.ParseFrom(snapshot.ToByteArray())));
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(3, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(3, true)]
    public void Cause_feedback_and_unfinished_facts_survive_the_park_wire(int unfinishedFailures, bool delivered)
    {
        var tracker = new TurnStateTracker();
        var failure = new ToolCycleResult(ToolInvocationOutcomeCategory.InvalidInput, "rejected")
            { FailureKey = new ToolFailureKey("netclaw", "invalid_rationale") };
        tracker.ObserveProgress(Enumerable.Repeat(failure, 3), true);
        if (delivered)
            tracker.CreateNudgeAfterResults(null, false);
        tracker.ObserveProgress(Enumerable.Repeat(failure, unfinishedFailures)
            .Append(new ToolCycleResult(ToolInvocationOutcomeCategory.Success, "sibling")), false);
        var evt = new ChildRunEvent.Parked(FullPark() with { LoopGuard = tracker.ExportLoopGuard() }, [Request(1)])
            { SessionId = Owner, RunId = RunId, RecordedAtMs = 10 };
        var restoredPark = RoundTrip(Codec(), evt).Park;
        var restored = new TurnStateTracker();
        restored.RestoreLoopGuard(restoredPark.LoopGuard);
        restored.ObserveProgress([failure], true);
        Assert.Equal(delivered ? RefusedRepeatAction.EscalatedNudge : RefusedRepeatAction.Nudge, restored.EvaluateProgress());
        Assert.Equal(LoopInterventionReason.FailureCauseRecurrence, restored.EvaluateProgressReason());
        Assert.Equal(0, restored.CallsSinceProgress);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Legacy_cause_fields_are_empty_and_an_orphan_advice_stage_fails(bool orphanStage)
    {
        var evt = new ChildRunEvent.Parked(FullPark(), [Request(1)])
            { SessionId = Owner, RunId = RunId, RecordedAtMs = 10 };
        var wire = NetclawProtoMapper.ToProto(evt);
        var guard = wire.Parked.LoopGuard;
        guard.CauseBatches.Clear();
        guard.IncompleteFailureKeys.Clear();
        if (orphanStage)
        {
            guard.CauseBatches.Add(new Serialization.Proto.ChildLoopGuardProto.Types.CauseBatch { AdviceStage = 1 });
            Assert.Throws<InvalidDataException>(() => NetclawProtoMapper.FromProto(wire));
        }
        else
        {
            var restored = Assert.IsType<ChildRunEvent.Parked>(NetclawProtoMapper.FromProto(wire));
            var tracker = new TurnStateTracker();
            tracker.RestoreLoopGuard(restored.Park.LoopGuard);
            Assert.Equal(LoopInterventionReason.FailureBudget, tracker.EvaluateProgressReason());
            Assert.Null(tracker.EvaluateActiveCause());
        }
    }

    // The bytes of a message with field 30 added: an empty embedded message that this binary does not know.
    private static byte[] WithUnknownField(byte[] bytes)
    {
        using var buffer = new MemoryStream();
        buffer.Write(bytes);
        var output = new CodedOutputStream(buffer);
        output.WriteTag(30, WireFormat.WireType.LengthDelimited);
        output.WriteLength(0);
        output.Flush();
        return buffer.ToArray();
    }

    private NetclawProtobufSerializer Codec() => new((Akka.Actor.ExtendedActorSystem)Sys);

    private static T RoundTrip<T>(NetclawProtobufSerializer serializer, T value) where T : notnull
    {
        var bytes = serializer.ToBinary(value);
        return Assert.IsAssignableFrom<T>(serializer.FromBinary(bytes, serializer.Manifest(value)));
    }

    // Each property of the value holds a value: a reference is not null, a string is not empty, a number is not zero, a
    // flag is true, and a collection is not empty and holds no null element. The walk goes one level into each record of
    // the value, so a nested field that the mapper drops cannot pass either.
    private static void AssertNoDefaults(object value, string path, int depth = 0)
    {
        foreach (var property in value.GetType().GetProperties())
        {
            if (property.GetIndexParameters().Length > 0 || property.Name == "EqualityContract")
                continue;
            var name = $"{path}.{property.Name}";
            var item = property.GetValue(value);
            switch (item)
            {
                case null:
                    Assert.Fail($"{name} is null.");
                    break;
                case string text:
                    Assert.False(string.IsNullOrEmpty(text), $"{name} is empty.");
                    break;
                case bool flag:
                    Assert.True(flag, $"{name} is false.");
                    break;
                case int or long:
                    Assert.True(Convert.ToInt64(item) != 0, $"{name} is zero.");
                    break;
                case IEnumerable list:
                    var elements = list.Cast<object?>().ToArray();
                    Assert.True(elements.Length > 0, $"{name} is empty.");
                    Assert.All(elements, element => Assert.NotNull(element));
                    break;
                default:
                    if (depth == 0 && item.GetType().Namespace?.StartsWith("Netclaw", StringComparison.Ordinal) == true)
                        AssertNoDefaults(item, name, depth + 1);
                    break;
            }
        }
    }

    // A park with a value in each field. ChildRunReducerTests uses the same records.
    internal static ChildRunPark FullPark() => new()
    {
        Transcript =
        [
            new SerializableChatMessage { Role = ChatRole.User, Content = "Merge the release branch." },
            new SerializableChatMessage
            {
                Role = ChatRole.Assistant,
                ToolCalls = [new SerializableToolCall { CallId = new ToolCallId("call-1"), Name = new ToolName("shell_execute"), ArgumentsJson = "{}" }]
            }
        ],
        LoopGuard = new TurnLoopGuardState(2, 3, [(new ToolActionSignature("signature-a"), "success")],
            new ToolActionSignature("signature-blocked"),
            new Dictionary<ToolRecurrenceKey, ToolRecurrenceEpisode>
            {
                [new ToolRecurrenceKey(new ToolName("shell_execute"), "hash-a")] = new("success", 1, Corrected: true, PendingJob: true)
            },
            RefusedRepeatCount: 1, CallsSinceProgress: 6, IncompleteBatchHadSuccess: true)
        {
            ProgressFailureUnits = 12, ProgressStage = RefusedRepeatAction.EscalatedNudge, IncompleteBatchHadFailure = true,
            CauseBatches = [new CauseFeedbackBatch([new ToolFailureKey("netclaw", "invalid_rationale", "validation")],
                new ProgressDecision(RefusedRepeatAction.Nudge, LoopInterventionReason.FailureCauseRecurrence,
                    new ToolFailureKey("netclaw", "invalid_rationale", "validation")))],
            IncompleteFailureKeys = [new ToolFailureKey("netclaw", "args_parse_error", "validation")]
        },
        ScopeDirectories = new ChildParkScopeDirectories("/repo", "/repo/tools"),
        FileActivity = new WorkingContextDelta
        {
            ProjectDirectory = "/repo", Worktree = "/repo", Branch = "release", Head = "abc123",
            ReadFiles = ["/repo/a.cs"], ConfirmedChangedFiles = ["/repo/b.cs"], ObservedChangedFiles = ["/repo/c.cs"]
        },
        SkillOverlay = "Follow the release checklist."
    };

    internal static ToolApprovalRequested Request(int number) => new()
    {
        SessionId = Owner,
        CallId = $"{RunId.Value}/subagent-approval/{number}",
        OriginalChildCallId = new ToolCallId($"call-{number}"),
        SourceChildRunId = RunId,
        AuthorizationAttemptId = $"auth-{number}",
        ToolName = "shell_execute",
        Patterns = ["gh pr merge 42"],
        Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal,
        ChannelType = "signalr",
        SupportsInteractiveApproval = true,
        RequesterSenderId = new SenderId("alice"),
        RequesterPrincipal = PrincipalClassification.Operator,
        OptionKeys = [ApprovalOptionKeys.ApproveOnce, ApprovalOptionKeys.Deny],
        TurnContext = Context(),
        RequestedAtMs = 5
    };

    internal static ToolApprovalResolved Resolution(int number) => new()
    {
        SessionId = Owner,
        CallId = $"{RunId.Value}/subagent-approval/{number}",
        AuthorizationAttemptId = $"auth-{number}",
        SourceChildRunId = RunId,
        Decision = ConsentAnswerCodec.ToJournalText(ConsentAnswer.Once.Instance),
        ResolvedAtMs = 6
    };

    private static TurnContextRecord Context() => new()
    {
        SessionId = Owner, TurnId = "turn-1", RequesterSenderId = new SenderId("alice"),
        RequesterPrincipal = PrincipalClassification.Operator, Audience = TrustAudience.Personal,
        Boundary = TrustBoundary.Personal, ChannelType = "signalr", SupportsInteractiveApproval = true
    };

    internal static BackgroundChildRun Run(ChildRunLifecycle lifecycle) => new()
    {
        RunId = RunId,
        ScopeId = new SubAgentScopeId("park-scope"),
        StartKey = new ChildRunStartKey.Tool(Owner, "turn-1", new ToolCallId("spawn-1")),
        AgentName = new AgentName("worker"),
        SourceOperation = "spawn_agent",
        ArgumentsDigest = "digest",
        OriginalContext = Context(),
        InitialWorkingSnapshot = new WorkingContextSnapshot { WorkingContext = WorkingContext.Empty, Git = new GitWorkingContextInspection.Skipped() },
        AcceptedAtMs = 1,
        Lifecycle = lifecycle,
        SourceReminderId = null
    };
}
