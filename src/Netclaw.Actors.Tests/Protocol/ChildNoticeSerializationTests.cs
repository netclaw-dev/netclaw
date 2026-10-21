// -----------------------------------------------------------------------
// <copyright file="ChildNoticeSerializationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Protocol;

/// <summary>
/// Own specs of the S2 implementation, not part of the specs that the test author wrote. They state the durable shapes of
/// the notice: proto field 14 on the admitted input, the reported child state in the snapshot, and the read-only status of
/// the deployed shapes. The recorded bytes of the deployed shapes are in <c>Fixtures/CoordinationV2</c>.
/// </summary>
public sealed class ChildNoticeSerializationTests : TestKit
{
    private static readonly SessionId Owner = new("test/notice-wire");

    public ChildNoticeSerializationTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
        => builder.WithNetclawSerialization();

    /// <summary>
    /// The notice kind and its reviewed context travel in field 14 of the admitted input. The legacy field 13 stays unset,
    /// because the legacy detector sends any input with a source child run to quarantine.
    /// Planted bug: the notice uses field 13, or the kind is lost on the wire.
    /// </summary>
    [Fact]
    public void The_notice_kind_travels_in_field_14_and_survives_a_round_trip()
    {
        // Arrange
        var serializer = Codec();
        var delta = new WorkingContextDelta { ProjectDirectory = "/child", ReadFiles = ["/child/a.cs"] };
        var admitted = new InputAdmitted
        {
            SessionId = Owner,
            InputId = new InputId("notice-input"),
            UserMessage = new SerializableChatMessage { Role = ChatRole.User, Content = "Child run ended." },
            TurnContext = new TurnContextRecord
            {
                SessionId = Owner, TurnId = "t1", RequesterSenderId = new SenderId("alice"),
                RequesterPrincipal = PrincipalClassification.Operator
            },
            ChildNotice = new ChildNotice(new SubAgentRunId("run-1"), delta),
            AdmittedAtMs = 5
        };

        // Act
        var bytes = serializer.ToBinary(admitted);
        var restored = Assert.IsType<InputAdmitted>(serializer.FromBinary(bytes, serializer.Manifest(admitted)));

        // Assert
        var proto = Netclaw.Actors.Serialization.Proto.InputAdmittedProto.Parser.ParseFrom(bytes);
        Assert.False(proto.HasSourceChildRunId);
        Assert.Equal("run-1", proto.ChildNotice.RunId);
        Assert.Equal("/child", proto.ChildNotice.ReviewedWorkingContext.ProjectDirectory);
        Assert.Equal("run-1", restored.ChildNotice!.RunId.Value);
        Assert.Equal(["/child/a.cs"], restored.ChildNotice!.ReviewedWorkingContext!.ReadFiles);
    }

    /// <summary>
    /// Every lifecycle state that a snapshot can hold comes back equal: a notice report, a deployed delivery report, and a
    /// result that a deployed runtime prepared and the parent never read.
    /// Planted bug: a report or a prepared pair is lost in the snapshot.
    /// </summary>
    [Fact]
    public void The_snapshot_keeps_reported_and_deployed_prepared_runs()
    {
        // Arrange
        var serializer = Codec();
        var callId = new ToolCallId("legacy-call");
        var prepared = new LegacyPreparedResult(
            new SerializableChatMessage
            {
                Role = ChatRole.Assistant,
                ToolCalls = [new SerializableToolCall { CallId = callId, Name = new ToolName("child_run_result"), ArgumentsJson = "{}" }]
            },
            new SerializableChatMessage { Role = ChatRole.Tool, Name = "child_run_result", ToolCallId = callId, Content = "Done." },
            new WorkingContextDelta { ProjectDirectory = "/reviewed" });
        var runs = new[]
        {
            Run("notice-run", new ChildRunLifecycle.Reported(SubAgentRunOutcome.Completed, null,
                new ChildRunReport.Notice(new InputId("notice-input")))),
            Run("delivered-run", new ChildRunLifecycle.Reported(SubAgentRunOutcome.Failed, SubAgentOutcomeReason.ActorStopped,
                new ChildRunReport.LegacyDelivery(callId))),
            Run("prepared-run", new ChildRunLifecycle.Terminal(Terminal("prepared-run")) { LegacyPrepared = prepared }),
            Run("ended-run", new ChildRunLifecycle.Terminal(Terminal("ended-run")))
        };
        var state = SessionState.Empty with
        {
            ChildRuns = runs.ToImmutableDictionary(run => run.RunId)
        };

        // Act
        var restored = SessionState.FromSnapshot(RoundTrip(serializer, state.ToSnapshot()));

        // Assert
        foreach (var run in runs)
        {
            var actual = restored.ChildRuns[run.RunId];
            Assert.Equal(run.Lifecycle.GetType(), actual.Lifecycle.GetType());
            Assert.Equal(run.InitialWorkingSnapshot is null, actual.InitialWorkingSnapshot is null);
        }

        Assert.Equal(new ChildRunReport.Notice(new InputId("notice-input")),
            ((ChildRunLifecycle.Reported)restored.ChildRuns[new SubAgentRunId("notice-run")].Lifecycle).Report);
        var delivered = (ChildRunLifecycle.Reported)restored.ChildRuns[new SubAgentRunId("delivered-run")].Lifecycle;
        Assert.Equal(new ChildRunReport.LegacyDelivery(callId), delivered.Report);
        Assert.Equal(SubAgentOutcomeReason.ActorStopped, delivered.Reason);
        var kept = Assert.IsType<ChildRunLifecycle.Terminal>(restored.ChildRuns[new SubAgentRunId("prepared-run")].Lifecycle);
        Assert.Equal(callId, kept.LegacyPrepared!.Result.ToolCallId);
        Assert.Equal("/reviewed", kept.LegacyPrepared.ReviewedWorkingContext!.ProjectDirectory);
        Assert.Null(Assert.IsType<ChildRunLifecycle.Terminal>(restored.ChildRuns[new SubAgentRunId("ended-run")].Lifecycle).LegacyPrepared);
    }

    /// <summary>
    /// A reported snapshot state that names neither a notice nor a delivery is corrupt data. The reader fails loudly.
    /// Planted bug: the reader maps the empty report to a default.
    /// </summary>
    [Fact]
    public void A_reported_state_without_a_report_is_corrupt_data()
    {
        // Arrange
        var run = Run("corrupt-run", new ChildRunLifecycle.Reported(SubAgentRunOutcome.Completed, null,
            new ChildRunReport.Notice(new InputId("notice-input"))));
        var proto = NetclawProtoMapper.ToProtoV2((SessionState.Empty with
        {
            ChildRuns = SessionState.Empty.ChildRuns.Add(run.RunId, run)
        }).ToSnapshot());
        proto.ChildRuns[0].Closed.ClearNoticeInputId();

        // Act and Assert
        Assert.Throws<InvalidDataException>(() => NetclawProtoMapper.FromProto(proto));
    }

    private NetclawProtobufSerializer Codec() => new((Akka.Actor.ExtendedActorSystem)Sys);

    private static T RoundTrip<T>(NetclawProtobufSerializer serializer, T value) where T : notnull
    {
        var bytes = serializer.ToBinary(value);
        return Assert.IsType<T>(serializer.FromBinary(bytes, serializer.Manifest(value)));
    }

    private static ChildRunTerminal Terminal(string runId) => new(new SubAgentProtocol.SubAgentResult
    {
        AgentName = new AgentName("worker"),
        RunId = new SubAgentRunId(runId),
        ScopeId = new SubAgentScopeId($"{runId}-scope"),
        Output = "Done.",
        Completion = new ChildRunCompletion.Completed(WorkingContextDelta.Empty)
    }, Lost: false, EvidenceWarning: null);

    private static BackgroundChildRun Run(string runId, ChildRunLifecycle lifecycle)
    {
        var needsSnapshot = lifecycle is ChildRunLifecycle.Terminal { LegacyPrepared: null };
        return new BackgroundChildRun
        {
            RunId = new SubAgentRunId(runId),
            ScopeId = new SubAgentScopeId($"{runId}-scope"),
            StartKey = new ChildRunStartKey.Slash(Owner, "turn-1", new InputId($"{runId}-slash")),
            AgentName = new AgentName("worker"),
            SourceOperation = "slash",
            ArgumentsDigest = "digest",
            OriginalContext = new TurnContextRecord
            {
                SessionId = Owner, TurnId = "turn-1", RequesterSenderId = new SenderId("alice"),
                RequesterPrincipal = PrincipalClassification.Operator, Boundary = TrustBoundary.Personal
            },
            InitialWorkingSnapshot = needsSnapshot
                ? new WorkingContextSnapshot { WorkingContext = WorkingContext.Empty, Git = new GitWorkingContextInspection.Skipped() }
                : null,
            AcceptedAtMs = 1,
            Lifecycle = lifecycle,
            SourceReminderId = null
        };
    }
}
