// -----------------------------------------------------------------------
// <copyright file="BackgroundChildLedgerAdversarialTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text.Json;
using Akka;
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Serialization;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.SubAgents;
using Netclaw.Actors.Tests.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using SessionFakeChatClient = Netclaw.Actors.Tests.Sessions.FakeChatClient;
using StoredRole = Netclaw.Actors.Protocol.ChatRole;

namespace Netclaw.Actors.Tests.SubAgents;

public sealed partial class BackgroundChildLedgerAdversarialTests(ITestOutputHelper output)
    : PersistenceTestKit(output: output), IAsyncDisposable
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(20);
    private static readonly SessionId Owner = new("signalr/child-ledger-owner");
    private readonly SessionFakeChatClient _model = new();
    private readonly FakeToolExecutor _tools = new();
    private TestSessionTempDirectory? _directory;

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        base.ConfigureAkka(builder, provider);
        builder.WithNetclawSerialization()
            .WithNetclawActors(provider.GetRequiredService<ShellExecutionEnvironment>());
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        _directory = TestSessionTempDirectory.Create("netclaw-child-ledger-");
        services.AddSingleton(_directory);
        services.AddSingleton(_directory.Paths);
        services.AddSingleton<IModelCapabilityResolver>(new FakeCapabilityResolver());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(SecurityPolicyDefaults.Resolve(null));
        services.AddSingleton<BackgroundJobDefinitionStore>();
        services.AddSingleton(new SchedulingConfig());
        services.AddSingleton<ReminderDefinitionStore>();
        services.AddSingleton<ReminderHistoryStore>();
        services.AddSingleton<IOperationalNotificationSink>(NullNotificationSink.Instance);
        services.AddSingleton<IReminderChannelNotifier>(NullReminderChannelNotifier.Instance);
        services.AddSingleton<SessionPipeline>();
        services.AddSingleton<ISessionPipeline>(sp => sp.GetRequiredService<SessionPipeline>());
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_model));
        services.AddSingleton(new ModelCapabilities { ModelId = "fake-model", ContextWindowTokens = 128_000 });
        services.AddSingleton(new SessionConfig { Tuning = new SessionTuning { TitleGenerationInterval = 0 } });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("Inspect recorded run facts."));
        services.AddSingleton<IToolExecutor>(_tools);
        services.AddSingleton(new ToolRegistry());
        services.AddLlmSessionCompositeRecords();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Registered_event_and_snapshot_keep_canonical_run_facts_and_distinct_task_identity(bool slash)
    {
        var (state, run) = Acceptance(Owner, slash);
        var evt = new ChildRunAccepted { SessionId = Owner, Run = run };
        var serializer = Codec(evt);
        Assert.Equal("cra-v1", serializer.Manifest(evt));
        var decoded = RoundTrip(evt);
        Assert.Equal(serializer.ToBinary(evt), serializer.ToBinary(decoded));
        var accepted = state.Apply(decoded);
        var restored = SessionState.FromSnapshot(RoundTrip(accepted.ToSnapshot()));
        var actual = Assert.Single(restored.ChildRuns).Value;
        Assert.Equal(run.RunId, actual.RunId);
        Assert.Equal(run.ScopeId, actual.ScopeId);
        Assert.Equal(run.AgentName, actual.AgentName);
        Assert.Equal(run.StartKey, actual.StartKey);
        Assert.Equal(run.ArgumentsDigest, actual.ArgumentsDigest);
        Assert.Equal(run.SourceOperation, actual.SourceOperation);
        Assert.Equal(run.AcceptedAtMs, actual.AcceptedAtMs);
        Assert.True(SessionState.SameCanonicalContext(run.OriginalContext, actual.OriginalContext));
        Assert.Equal(run.OriginInputIds, actual.OriginInputIds);
        Assert.Equal("authority-turn", actual.OriginalContext.TurnId);
        Assert.Equal("retained-detector-task", actual.ParentCheckpoint.TaskId);
        Assert.NotEqual(actual.OriginalContext.TurnId, actual.ParentCheckpoint.TaskId);
        Assert.Equal(Codec(accepted.ToSnapshot()).ToBinary(accepted.ToSnapshot()), Codec(restored.ToSnapshot()).ToBinary(restored.ToSnapshot()));
        Assert.Equal(run.ParentReceiptFailure, actual.ParentReceiptFailure);
        Assert.Equal(slash, actual.StartBatchSettled);
        Assert.Equal(run.InitialWorkingSnapshot.WorkingContext.ProjectDirectory, actual.InitialWorkingSnapshot.WorkingContext.ProjectDirectory);
        Assert.Equal(run.InitialWorkingSnapshot.WorkingContext.RecentFiles, actual.InitialWorkingSnapshot.WorkingContext.RecentFiles);
        Assert.Equal("main", Assert.IsType<GitWorkingContextInspection.Available>(actual.InitialWorkingSnapshot.Git).Snapshot.Branch);
        Assert.Equal(new[] { "/neutral/changed.txt" }, Assert.IsType<GitWorkingContextInspection.Available>(actual.InitialWorkingSnapshot.Git).Snapshot.ChangedFiles);
        Assert.Empty(restored.ActiveBackgroundJobs);
        Assert.Equal(state.History, restored.History);
        if (slash)
        {
            Assert.Null(restored.LoopAdmission);
            Assert.Empty(restored.LoopObservations);
        }
    }

    [Theory]
    [InlineData("foreign-call")]
    [InlineData("source-operation")]
    [InlineData("source-context")]
    [InlineData("requester")]
    [InlineData("origin-order")]
    [InlineData("event-session")]
    [InlineData("checkpoint")]
    [InlineData("receipt-failure")]
    public void An_acceptance_cannot_replace_canonical_admitted_evidence(string fault)
    {
        var (state, run) = Acceptance(Owner, slash: false);
        var evt = new ChildRunAccepted { SessionId = Owner, Run = run };
        evt = fault switch
        {
            "foreign-call" => evt with { Run = run with { StartKey = new ChildRunStartKey.Tool(new ToolCallId("unadmitted")) { SessionId = Owner, TurnId = new TurnId("authority-turn") } } },
            "source-operation" => evt with { Run = run with { SourceOperation = "skill_load" } },
            "source-context" => evt with { Run = run with { OriginalContext = run.OriginalContext with { SourceScope = "foreign-source" } } },
            "requester" => evt with { Run = run with { OriginalContext = run.OriginalContext with { RequesterSenderId = new SenderId("operator-b") } } },
            "origin-order" => evt with { Run = run with { OriginInputIds = run.OriginInputIds.Reverse().ToArray() } },
            "event-session" => evt with { SessionId = new SessionId("signalr/foreign") },
            "checkpoint" => evt with { Run = run with { ParentCheckpoint = run.ParentCheckpoint with { LastBlockedAction = "forged-correction" } } },
            "receipt-failure" => evt with { Run = run with { ParentReceiptFailure = !run.ParentReceiptFailure } },
            _ => throw new ArgumentOutOfRangeException(nameof(fault))
        };
        var bytes = Codec(state.ToSnapshot()).ToBinary(state.ToSnapshot());
        Assert.Throws<InvalidDataException>(() => state.Apply(RoundTrip(evt)));
        Assert.Equal(bytes, Codec(state.ToSnapshot()).ToBinary(state.ToSnapshot()));
        Assert.Single(state.Apply(new ChildRunAccepted { SessionId = Owner, Run = run }).ChildRuns);
    }

    [Fact]
    public void A_second_committed_identity_cannot_replace_an_existing_start_after_snapshot_restore()
    {
        var (state, run) = Acceptance(Owner, slash: false);
        state = SessionState.FromSnapshot(RoundTrip(state.Apply(new ChildRunAccepted { SessionId = Owner, Run = run }).ToSnapshot()));
        var duplicate = run with
        {
            RunId = new SubAgentRunId("another-run"), ScopeId = new SubAgentScopeId($"{Owner.Value}/subagent/worker/another-run"),
            ArgumentsDigest = new string('B', 64)
        };
        var before = Codec(state.ToSnapshot()).ToBinary(state.ToSnapshot());
        Assert.Throws<InvalidDataException>(() => state.Apply(new ChildRunAccepted { SessionId = Owner, Run = duplicate }));
        Assert.Equal(before, Codec(state.ToSnapshot()).ToBinary(state.ToSnapshot()));
        Assert.Equal(run.RunId, Assert.Single(state.ChildRuns).Key);
        Assert.Throws<InvalidDataException>(() => SessionState.FromSnapshot(RoundTrip(state.ToSnapshot() with { ChildRuns = [run, duplicate] })));
    }

    [Fact]
    public void Actual_pre_change_snapshot_bytes_restore_an_empty_child_ledger_without_transcript_inference()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "LegacyPersistence", "legacy-session-v0.json");
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal("2e6bc4f014dc96b606566709df1bd11f90ecf34a", fixture.RootElement.GetProperty("baseline").GetString());
        var entry = fixture.RootElement.GetProperty("entries").GetProperty("completed-snapshot");
        var bytes = Convert.FromBase64String(entry.GetProperty("base64").GetString()!);
        Assert.Equal(entry.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(bytes)));
        var serializer = Codec(new SessionSnapshot());
        Assert.Equal(serializer.Identifier, entry.GetProperty("serializerId").GetInt32());
        Assert.Equal(serializer.Manifest(new SessionSnapshot()), entry.GetProperty("manifest").GetString());
        var snapshot = Assert.IsType<SessionSnapshot>(Sys.Serialization.Deserialize(bytes, serializer.Identifier, entry.GetProperty("manifest").GetString()!));
        var state = SessionState.FromSnapshot(snapshot);
        Assert.Empty(snapshot.ChildRuns);
        Assert.Empty(state.ChildRuns);
        Assert.Equal(1, state.TurnCount);
        Assert.Equal("The neutral task is complete.", state.History[1].Content);
        Assert.Equal(2, state.History.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_snapshot_recovery_binds_each_run_to_the_owner_before_the_join_reply(bool foreign)
    {
        var (state, run) = Acceptance(foreign ? new SessionId("signalr/foreign-child-owner") : Owner, slash: true);
        var snapshot = state.Apply(new ChildRunAccepted { SessionId = run.StartKey.SessionId, Run = run }).ToSnapshot() with
        { History = [new SerializableChatMessage { Role = StoredRole.Assistant, Content = "The stored owner marker." }] };
        Assert.Single(SessionState.FromSnapshot(RoundTrip(snapshot)).ChildRuns);
        var seed = Sys.ActorOf(Props.Create(() => new SnapshotSeeder($"session-{Owner.Value}")));
        await seed.Ask<Done>(snapshot, Ceiling, TestContext.Current.CancellationToken);
        var seedWatcher = CreateTestProbe();
        seedWatcher.Watch(seed);
        Sys.Stop(seed);
        await seedWatcher.ExpectTerminatedAsync(seed, Ceiling, cancellationToken: TestContext.Current.CancellationToken);
        var entered = NewSignal();
        var release = NewSignal();
        await Snapshots.OnLoad.FailIf(async (id, _) =>
        {
            if (id != $"session-{Owner.Value}") return false;
            entered.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return false;
        });
        var observer = CreateTestProbe();
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        async Task RecoverAsync()
        {
            manager.Tell(new JoinSession(observer) { SessionId = Owner, Filter = OutputFilter.Full }, observer.Ref);
            await entered.Task.WaitAsync(Ceiling, TestContext.Current.CancellationToken);
            var owner = await Sys.ActorSelection($"/user/session-manager/{Uri.EscapeDataString(Owner.Value)}")
                .ResolveOne(Ceiling, TestContext.Current.CancellationToken);
            observer.Watch(owner);
            release.TrySetResult();
            var observed = await observer.ReceiveOneAsync(Ceiling, TestContext.Current.CancellationToken);
            if (foreign)
                Assert.Equal(owner, Assert.IsType<Terminated>(observed).ActorRef);
            else
            {
                var joined = Assert.IsType<SessionJoined>(observed);
                Assert.Equal(Owner, joined.SessionId);
                Assert.Equal("The stored owner marker.", Assert.Single(joined.RecentMessages!).Content);
            }
        }
        try
        {
            if (foreign)
                await EventFilter.Error().ExpectOneAsync(RecoverAsync,
                    cancellationToken: TestContext.Current.CancellationToken);
            else
                await RecoverAsync();
        }
        finally
        {
            release.TrySetResult();
        }
        if (foreign)
        {
            Assert.Equal(0, _model.CallCount);
            Assert.Equal(0, _tools.CallCount);
        }
    }

    private static (SessionState State, BackgroundChildRun Run) Acceptance(SessionId session, bool slash)
    {
        var context = TurnContext.FromMessageSource(session, new TurnId("authority-turn"), new MessageSource
        {
            ChannelType = ChannelType.SignalR, SenderId = new SenderId("operator-a"), TurnId = new TurnId("authority-turn"),
            Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal, Principal = PrincipalClassification.Operator,
            Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
            DefaultDeliveryTarget = new ChannelDeliveryTargetInfo("signalr", "destination", "operator-a", "operator-a")
        }).ToRecord();
        var first = new InputAdmitted { SessionId = session, InputId = new InputId("origin-one"), TurnContext = context,
            UserMessage = new SerializableChatMessage { Role = StoredRole.User, Content = "Inspect a neutral task." } };
        var second = first with { InputId = new InputId("origin-two") };
        var state = SessionState.Empty.Apply(first).Apply(second).Apply(new ToolTaskAdopted
        { SessionId = session, TurnContext = context, InputIds = [first.InputId, second.InputId] });
        // The ledger must retain detector identity independently from the original authority turn.
        state = state with { LoopCheckpoint = new ToolLoopCheckpoint
        {
            TaskId = "retained-detector-task", LastBlockedAction = "retained-correction",
            Entries = [new ToolLoopEntry { ToolName = "neutral_probe", ArgumentsHash = "args", OutcomeHash = "result", EqualRounds = 2, Corrected = true }],
            ColdKeys = [new ToolLoopKey("old_probe", "cold-args")], AdjacentHistory = [new ToolLoopAdjacent("action", "outcome")]
        } };
        if (!slash)
            state = state.ApplyLoopAdmission(new ToolBatchStarted
            {
                SessionId = session, LoopAdmission = new ToolLoopAdmission
                { TaskId = state.LoopCheckpoint.TaskId, ActionHash = "start-action", Calls = [new ToolLoopPreparedCall("start-call", "spawn_agent", "start-args", false)] },
                LoopDelta = new ToolLoopDelta { TaskId = state.LoopCheckpoint.TaskId, LastBlockedAction = state.LoopCheckpoint.LastBlockedAction,
                    ColdKeys = state.LoopCheckpoint.ColdKeys, AdjacentHistory = state.LoopCheckpoint.AdjacentHistory }
            });
        return (state, new BackgroundChildRun
        {
            RunId = new SubAgentRunId("accepted-run"), ScopeId = new SubAgentScopeId($"{session.Value}/subagent/worker/accepted-run"),
            AgentName = new AgentName("worker"), SourceOperation = slash ? "/inspect" : "spawn_agent", ArgumentsDigest = new string('A', 64),
            StartKey = slash ? new ChildRunStartKey.Slash(first.InputId) { SessionId = session, TurnId = new TurnId(context.TurnId) }
                : new ChildRunStartKey.Tool(new ToolCallId("start-call")) { SessionId = session, TurnId = new TurnId(context.TurnId) },
            OriginalContext = context, OriginInputIds = state.AdoptedTaskInputIds, ParentCheckpoint = state.LoopCheckpoint,
            ParentReceiptFailure = state.LoopReceiptFailure, StartBatchSettled = slash, AcceptedAtMs = 123456,
            InitialWorkingSnapshot = new WorkingContextSnapshot
            {
                WorkingContext = new WorkingContext { ProjectDirectory = "/neutral", RecentFiles = ["/neutral/source.txt", "/neutral/result.txt"] },
                Git = new GitWorkingContextInspection.Available(new GitWorkingContextSnapshot
                { Worktree = "/neutral", CommonDirectory = "/neutral/.git", Branch = "main", Head = "head", ChangedFiles = ["/neutral/changed.txt"] })
            }
        });
    }

    private NetclawProtobufSerializer Codec(object value) => Assert.IsType<NetclawProtobufSerializer>(Sys.Serialization.FindSerializerFor(value));
    private T RoundTrip<T>(T value) where T : notnull
    {
        var serializer = Codec(value);
        return Assert.IsType<T>(Sys.Serialization.Deserialize(serializer.ToBinary(value), serializer.Identifier, serializer.Manifest(value)));
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class SnapshotSeeder : ReceivePersistentActor
    {
        public override string PersistenceId { get; }
        public SnapshotSeeder(string id)
        {
            PersistenceId = id;
            IActorRef? reply = null;
            RecoverAny(_ => { });
            Command<ISessionEvent>(evt =>
            {
                var acknowledgement = Sender;
                Persist(evt, _ => acknowledgement.Tell(LastSequenceNr));
            });
            Command<SessionSnapshot>(snapshot =>
            {
                reply = Sender;
                var serializer = Assert.IsType<NetclawProtobufSerializer>(Context.System.Serialization.FindSerializerFor(snapshot));
                SaveSnapshot(Context.System.Serialization.Deserialize(serializer.ToBinary(snapshot), serializer.Identifier, serializer.Manifest(snapshot)));
            });
            Command<SaveSnapshotSuccess>(_ => reply!.Tell(Done.Instance));
            Command<SaveSnapshotFailure>(failed => reply!.Tell(new Status.Failure(failed.Cause)));
        }
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally { if (_directory is not null) await _directory.DisposeAsync(); }
    }
}
