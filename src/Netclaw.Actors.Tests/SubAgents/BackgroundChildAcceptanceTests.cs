// -----------------------------------------------------------------------
// <copyright file="BackgroundChildAcceptanceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Serialization;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tests.Sessions;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using StoredRole = Netclaw.Actors.Protocol.ChatRole;

namespace Netclaw.Actors.Tests.SubAgents;

public sealed partial class BackgroundChildAcceptanceTests(ITestOutputHelper output) : TestKit(output: output)
{
    private static readonly SessionId Session = new("signalr/child-acceptance");

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
        => builder.WithNetclawSerialization();

    [Fact]
    public void Registered_acceptance_and_snapshot_preserve_working_facts_without_the_live_shell_host()
    {
        var state = AdmittedTask("original", SessionState.Empty);
        state = AdmitBatch(state, "first", "second");
        var run = Run(state, "run-one", "first");
        var evt = new ChildRunAccepted { SessionId = Session, Run = run };
        var decoded = RoundTrip(evt);
        Assert.Equal(Bytes(evt), Bytes(decoded));
        Assert.Null(decoded.Run.InitialWorkingSnapshot.ShellEnvironment);
        Assert.NotNull(run.InitialWorkingSnapshot.ShellEnvironment);
        Assert.Equal(run.InitialWorkingSnapshot.WorkingContext.ProjectDirectory, decoded.Run.InitialWorkingSnapshot.WorkingContext.ProjectDirectory);
        var recovered = SessionState.FromSnapshot(RoundTrip(state.Apply(decoded).ToSnapshot()));
        var restored = Assert.Single(recovered.ChildRuns).Value;
        Assert.Equal(run.StartKey, restored.StartKey);
        Assert.Equal(run.OriginalContext, restored.OriginalContext);
        Assert.Equal(run.OriginInputIds, restored.OriginInputIds);
        Assert.Equal(run.InitialWorkingSnapshot.WorkingContext.RecentFiles, restored.InitialWorkingSnapshot.WorkingContext.RecentFiles);
        var git = Assert.IsType<GitWorkingContextInspection.Available>(restored.InitialWorkingSnapshot.Git).Snapshot;
        Assert.Equal("main", git.Branch);
        Assert.Equal("head", git.Head);
        Assert.Equal("origin/main", git.Upstream);
        Assert.Equal(2, git.Ahead);
        Assert.Equal(1, git.Modified);
        Assert.Equal(new[] { "/project/changed.txt" }, git.ChangedFiles);
        Assert.Empty(recovered.ActiveBackgroundJobs);
    }

    [Fact]
    public void Sibling_acceptances_retain_the_settled_parent_round_across_fresh_task_and_compaction()
    {
        var state = AdmitBatch(AdmittedTask("original", SessionState.Empty), "first", "second");
        var first = Run(state, "run-one", "first");
        var second = Run(state, "run-two", "second");
        state = state.Apply(RoundTrip(new ChildRunAccepted { SessionId = Session, Run = first }));
        state = state.Apply(RoundTrip(new ChildRunAccepted { SessionId = Session, Run = second }));
        state = Observe(state, "first", missingReceipt: false);
        Assert.All(state.ChildRuns.Values, run => Assert.False(run.StartBatchSettled));
        state = Observe(state, "second", missingReceipt: false);
        Assert.All(state.ChildRuns.Values, run =>
        {
            Assert.True(run.StartBatchSettled);
        });
        state = state.Apply(new TurnRecorded { SessionId = Session });
        state = AdmittedTask("fresh", state);
        state = state.Apply(new SessionCompacted { SessionId = Session, Summary = "neutral summary" });
        var recovered = SessionState.FromSnapshot(RoundTrip(state.ToSnapshot()));
        Assert.Equal(2, recovered.ChildRuns.Count);
        Assert.All(recovered.ChildRuns.Values, run =>
        {
            Assert.True(run.StartBatchSettled);
        });
    }

    [Fact]
    public void Direct_activation_round_trips_without_a_fabricated_tool_round()
    {
        var state = AdmittedTask("slash", SessionState.Empty);
        var run = Run(state, "slash-run", "unused") with
        {
            StartKey = new ChildRunStartKey.Slash(state.AdoptedTaskInputIds[0]) { SessionId = Session, TurnId = new TurnId("slash") },
            SourceOperation = "/inspect", StartBatchSettled = true,
            InitialWorkingSnapshot = new WorkingContextSnapshot { WorkingContext = WorkingContext.Empty, Git = new GitWorkingContextInspection.Skipped() }
        };
        var recovered = state.Apply(RoundTrip(new ChildRunAccepted { SessionId = Session, Run = run }));
        recovered = SessionState.FromSnapshot(RoundTrip(recovered.ToSnapshot()));
        Assert.IsType<ChildRunStartKey.Slash>(Assert.Single(recovered.ChildRuns).Value.StartKey);
        Assert.Null(recovered.LoopAdmission);
        Assert.Empty(recovered.LoopObservations);
        Assert.DoesNotContain(recovered.History, message => message.Role == StoredRole.Tool);
    }

    [Fact]
    public void Old_snapshot_reads_an_empty_child_ledger_without_transcript_inference()
    {
        var legacy = new SessionSnapshot
        {
            History = [new SerializableChatMessage { Role = StoredRole.Assistant, Content = "The child is still active." }]
        };
        var recovered = SessionState.FromSnapshot(RoundTrip(legacy));
        Assert.Empty(recovered.ChildRuns);
        Assert.Equal(legacy.History[0], Assert.Single(recovered.History));
    }

    private static SessionState AdmittedTask(string turn, SessionState prior)
    {
        var context = new TurnContextRecord
        {
            SessionId = Session, TurnId = turn, Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal,
            RequesterSenderId = new SenderId("operator"), RequesterPrincipal = PrincipalClassification.Operator,
            TransportAuthenticity = TransportAuthenticity.LocalProcess, PayloadTaint = PayloadTaint.Trusted
        };
        var input = new InputAdmitted
        {
            SessionId = Session, InputId = new InputId("input-" + turn), TurnContext = context,
            UserMessage = new SerializableChatMessage { Content = turn }
        };
        return prior.Apply(input).Apply(new ToolTaskAdopted(false)
        {
            SessionId = Session, TurnContext = context, InputIds = [input.InputId]
        });
    }

    private static SessionState AdmitBatch(SessionState state, params string[] ids)
    {
        var prepared = ToolCycleSignatureFactory.Prepare(ids.Select(id => new FunctionCallContent(id, "spawn_agent", new Dictionary<string, object?>())).ToArray(), new FakeToolExecutor());
        var evt = new ToolBatchStarted
        {
            SessionId = Session,
            LoopAdmission = new ToolLoopAdmission
            {
                TaskId = state.AdoptedTaskContext!.TurnId, ActionHash = prepared.Action.Value,
                Calls = prepared.Calls.Select(call => new ToolLoopPreparedCall(call.CallId.Value, call.ToolName.Value, call.ArgumentsHash, false)).ToArray()
            }
        };
        return state.CloseInputs(state.AdoptedTaskInputIds).ApplyLoopAdmission(evt);
    }

    private static BackgroundChildRun Run(SessionState state, string id, string call) => new()
    {
        RunId = new SubAgentRunId(id), ScopeId = new SubAgentScopeId($"{Session.Value}/subagent/worker/{id}"),
        AgentName = new AgentName("worker"), SourceOperation = "spawn_agent", ArgumentsDigest = new string('A', 64),
        StartKey = new ChildRunStartKey.Tool(new ToolCallId(call)) { SessionId = Session, TurnId = new TurnId(state.AdoptedTaskContext!.TurnId) },
        OriginalContext = state.AdoptedTaskContext!, OriginInputIds = state.AdoptedTaskInputIds,
        InitialWorkingSnapshot = new WorkingContextSnapshot
        {
            WorkingContext = new WorkingContext { ProjectDirectory = "/project", RecentFiles = ["/project/input.txt"] },
            Git = new GitWorkingContextInspection.Available(new GitWorkingContextSnapshot
            {
                Worktree = "/project", CommonDirectory = "/project/.git", Branch = "main", Head = "head", Upstream = "origin/main",
                Ahead = 2, Modified = 1, ChangedFiles = ["/project/changed.txt"]
            }),
            ShellEnvironment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux)
        }
    };

    private static SessionState Observe(SessionState state, string call, bool missingReceipt)
        => state.ApplyLoopObservation(new ToolCallRecorded
        {
            SessionId = Session,
            LoopObservation = ToolCycleSignatureFactory.CreateObservation(call,
                missingReceipt ? null : new ToolInvocationReceipt.Succeeded([], null), "accepted", false)
        });

    private byte[] Bytes(object value) => Sys.Serialization.FindSerializerFor(value).ToBinary(value);

    private T RoundTrip<T>(T value) where T : notnull
    {
        var serializer = Sys.Serialization.FindSerializerFor(value);
        var manifest = Assert.IsAssignableFrom<SerializerWithStringManifest>(serializer).Manifest(value);
        return Assert.IsType<T>(Sys.Serialization.Deserialize(serializer.ToBinary(value), serializer.Identifier, manifest));
    }
}
