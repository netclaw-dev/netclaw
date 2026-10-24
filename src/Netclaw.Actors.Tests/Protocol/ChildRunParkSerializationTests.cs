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
/// and the read of a run record that 0.28.0-beta.2 wrote.
/// </summary>
public sealed class ChildRunParkSerializationTests : TestKit
{
    private static readonly SessionId Owner = new("test/park-wire");
    private static readonly SubAgentRunId RunId = new("park-run");

    public ChildRunParkSerializationTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
        => builder.WithNetclawSerialization();

    /// <summary>
    /// The Parked and Resumed events travel with the cre-v2 manifest and come back equal.
    /// Planted bug: the mapper drops a field of the park or of a request, or a new manifest appears.
    /// </summary>
    [Fact]
    public void The_park_and_resume_events_round_trip_with_the_cre_v2_manifest()
    {
        // Arrange
        var serializer = Codec();
        var parked = new ChildRunEvent.Parked(FullPark(), [Request(1), Request(2)], PromptsIssued: 2)
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
    /// mapper drops cannot pass as a default.
    /// Planted bug: the snapshot drops the answer of a parked request, the prompt counter, or a park field.
    /// </summary>
    [Fact]
    public void A_parked_run_record_round_trips_through_the_snapshot()
    {
        // Arrange
        var serializer = Codec();
        var lifecycle = new ChildRunLifecycle.Parked(FullPark(),
            [new ChildParkRequest(Request(1), Resolution(1)), new ChildParkRequest(Request(2), null)]);
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
    /// A run record that 0.28.0-beta.2 wrote has no fields 21 and 22. It reads as before: an accepted run with a
    /// prompt counter of 0. This row passes before PR 2, and it pins the upgrade path.
    /// Planted bug: the reader takes a missing prompt counter as damage, or the writer adds field 21 or 22 to a run that
    /// never parked.
    /// </summary>
    [Fact]
    public void A_run_record_of_beta_2_reads_as_before()
    {
        // Arrange
        var proto = NetclawProtoMapper.ToProtoV2(new SessionSnapshot { ChildRuns = [Run(new ChildRunLifecycle.Accepted(null))] });
        var record = WireFieldNumbers(proto.ChildRuns.Single().ToByteArray());
        Assert.DoesNotContain(21, record);
        Assert.DoesNotContain(22, record);

        // Act
        var restored = Assert.Single(NetclawProtoMapper.FromProto(
            Serialization.Proto.SessionSnapshotV2Proto.Parser.ParseFrom(proto.ToByteArray())).ChildRuns);

        // Assert
        Assert.IsType<ChildRunLifecycle.Accepted>(restored.Lifecycle);
        Assert.Equal(0, restored.PromptsIssued);
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

    private static HashSet<int> WireFieldNumbers(byte[] bytes)
    {
        var fields = new HashSet<int>();
        var input = new CodedInputStream(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            fields.Add(WireFormat.GetTagFieldNumber(tag));
            input.SkipLastField();
        }

        return fields;
    }

    // A park with a value in each field.
    private static ChildRunPark FullPark() => new()
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
            RefusedRepeatCount: 1),
        ScopeDirectories = new ChildParkScopeDirectories("/repo", "/repo/tools"),
        FileActivity = new WorkingContextDelta
        {
            ProjectDirectory = "/repo", Worktree = "/repo", Branch = "release", Head = "abc123",
            ReadFiles = ["/repo/a.cs"], ConfirmedChangedFiles = ["/repo/b.cs"], ObservedChangedFiles = ["/repo/c.cs"]
        },
        SkillOverlay = "Follow the release checklist."
    };

    private static ToolApprovalRequested Request(int number) => new()
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

    private static ToolApprovalResolved Resolution(int number) => new()
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

    private static BackgroundChildRun Run(ChildRunLifecycle lifecycle) => new()
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
