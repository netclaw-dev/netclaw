// -----------------------------------------------------------------------
// <copyright file="ToolRecurrencePersistenceFaultTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
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
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using ChatRole = Microsoft.Extensions.AI.ChatRole;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

public sealed class ToolRecurrencePersistenceFaultTests(ITestOutputHelper output)
    : PersistenceTestKit(output: output), IAsyncDisposable
{
    private static readonly TimeSpan FaultCeiling = TimeSpan.FromSeconds(20);
    private readonly RepeatedClient _main = new();
    private readonly SummaryClient _compaction = new();
    private readonly ReceiptExecutor _executor = new();
    private TestSessionTempDirectory? _directory;

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // The existing fault store acknowledges each interceptor change.
        base.ConfigureAkka(builder, provider);
        builder.WithNetclawSerialization()
            .WithNetclawActors(provider.GetRequiredService<ShellExecutionEnvironment>());
    }

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        _directory = TestSessionTempDirectory.Create("netclaw-recurrence-fault-");
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
        services.AddSingleton<IChatClientProvider>(new RoleProvider(_main, _compaction));
        services.AddSingleton(new ModelCapabilities { ModelId = "fake-model", ContextWindowTokens = 1000 });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning
            {
                TitleGenerationInterval = 0, SnapshotInterval = 1000,
                KeepRecentMessages = 0, KeepRecentToolResults = 0, CompactionThreshold = 0.75
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider("Complete the admitted task."));
        services.AddSingleton<IToolExecutor>(_executor);
        var registry = new ToolRegistry();
        registry.RegisterCore(AIFunctionFactory.Create(() => "same", "recurrence_probe"), "builtin");
        services.AddSingleton(registry);
        services.AddLlmSessionCompositeRecords();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Correction_admission_commit_precedes_feedback_and_preserves_completed_effects_on_failure(bool failCommit)
    {
        var session = new SessionId("signalr/correction-store-failure");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        var owner = await OwnerAsync(session);
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        var reached = NewSignal();
        await Journal.OnWrite.FailIf(representation =>
        {
            if (representation.Payload is not ToolBatchStarted { LoopAdmission.RefusedCallIds.Count: > 0 }) return false;
            reached.TrySetResult();
            return failCommit;
        });

        async Task RunAsync()
        {
            await StartAsync(manager, session);
            await reached.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
            if (failCommit)
                await watcher.ExpectTerminatedAsync(owner, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
            else
                await CompletedAsync(subscriber);
        }
        if (failCommit)
            await EventFilter.Error(contains: "Failed to persist event type").ExpectOneAsync(RunAsync,
                cancellationToken: TestContext.Current.CancellationToken);
        else
            await RunAsync();

        Assert.Equal(2, _executor.Count);
        Assert.Equal(failCommit ? 3 : 4, _main.Count);
        if (!failCommit) return;
        Assert.DoesNotContain(_main.Requests.SelectMany(request => request).SelectMany(message => message.Contents.OfType<FunctionResultContent>()),
            result => result.CallId == "call-3");
        var evidence = await ReadJournalAsync(session);
        Assert.Equal(2, evidence.Events.OfType<ToolBatchStarted>().Count());
        Assert.Equal(2, evidence.Events.OfType<ToolCallRecorded>().Count());
        Assert.All(evidence.Events.OfType<ToolCallRecorded>(), result => Assert.False(result.LoopObservation!.Synthetic));

        await Journal.OnWrite.Pass();
        subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        await manager.Ask<CommandAck>(Restart(session), FaultCeiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        Assert.Equal(5, _main.Count);
        Assert.Equal(2, _executor.Count);
        Assert.All(_executor.Requesters, requester => Assert.Equal("operator-a", requester));
        Assert.Contains(_main.Requests[^1].SelectMany(message => message.Contents.OfType<FunctionResultContent>()),
            result => result.CallId == "call-4");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_admission_or_result_commit_stops_dispatch_or_continuation_at_its_durable_boundary(bool failAdmission)
    {
        var session = new SessionId("signalr/result-store-failure");
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        var owner = await OwnerAsync(session);
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        var reached = NewSignal();
        await Journal.OnWrite.FailIf(representation =>
        {
            if (failAdmission ? representation.Payload is not ToolBatchStarted : representation.Payload is not ToolCallRecorded)
                return false;
            reached.TrySetResult();
            return true;
        });
        await EventFilter.Error(contains: "Failed to persist event type").ExpectOneAsync(async () =>
        {
            await StartAsync(manager, session);
            await reached.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
            await watcher.ExpectTerminatedAsync(owner, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, _main.Count);
        Assert.Equal(failAdmission ? 0 : 1, _executor.Count);
        Assert.Equal(failAdmission ? [] : new[] { "call-1" }, _executor.CallIds);
        var evidence = await ReadJournalAsync(session);
        Assert.Empty(evidence.Events.OfType<ToolCallRecorded>());
        Assert.Empty(evidence.Events.OfType<TurnRecorded>());
        if (failAdmission)
        {
            Assert.Empty(evidence.Events.OfType<ToolBatchStarted>());
            return;
        }
        var admitted = Assert.Single(evidence.Events.OfType<ToolBatchStarted>());
        Assert.Equal("call-1", Assert.Single(admitted.LoopAdmission!.Calls).CallId);
        // The durable journal cannot distinguish this completed effect from an effect that never ran.
        Assert.Empty(admitted.LoopDelta!.Upserts);
    }

    [Fact]
    public async Task Failed_compaction_snapshot_reports_the_error_and_retains_journal_correction_without_reexecution()
    {
        var session = new SessionId("signalr/compaction-snapshot-failure");
        _main.CompactOnCorrection = true;
        _main.HoldFourthRequest = true;
        var reached = NewSignal();
        await Snapshots.OnSave.FailIf((persistenceId, _) =>
        {
            if (persistenceId != $"session-{session.Value}") return false;
            reached.TrySetResult();
            return true;
        });
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        await EventFilter.Warning(contains: "Snapshot failed:").ExpectOneAsync(async () =>
        {
            await StartAsync(manager, session);
            await reached.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
            await _main.FourthEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, _executor.Count);
        Assert.Equal(1, _compaction.Count);
        _main.ReleaseFourth.TrySetResult();
        await CompletedAsync(subscriber);
        Assert.Equal(4, _main.Count);
        Assert.Equal(2, _executor.Count);
        var owner = await OwnerAsync(session);
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        Sys.Stop(owner);
        await watcher.ExpectTerminatedAsync(owner, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);

        var evidence = await ReadJournalAsync(session);
        Assert.Null(evidence.Snapshot);
        Assert.Single(evidence.Events.OfType<SessionCompacted>());
        Assert.Equal(3, evidence.Events.OfType<ToolBatchStarted>().Count());
        var results = evidence.Events.OfType<ToolCallRecorded>().ToArray();
        Assert.Equal(3, results.Length);
        Assert.Equal(2, results.Count(result => !result.LoopObservation!.Synthetic));
        Assert.Single(results, result => result.LoopObservation!.Synthetic);
        var state = SessionState.Empty;
        foreach (var evt in evidence.Events)
        {
            state = evt switch
            {
                ToolBatchStarted batch => state.ApplyLoopAdmission(batch),
                ToolCallRecorded result => state.ApplyLoopObservation(result),
                SessionCompacted compacted => state.Apply(compacted),
                _ => state
            };
        }
        Assert.Contains(state.LoopCheckpoint.Entries, entry => entry.EqualRounds == 2 && entry.Corrected);
        var restored = new TurnStateTracker();
        restored.RestoreCheckpoint(state.LoopCheckpoint);
        Assert.Equal(ToolCycleDecisionKind.Stop, restored.EvaluateBeforeDispatch(
            ToolCycleSignatureFactory.Prepare([RepeatedClient.Call("new-call")], _executor)).Kind);
    }

    private async Task<JournalEvidence> ReadJournalAsync(SessionId session)
    {
        var completed = new TaskCompletionSource<JournalEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Sys.ActorOf(Props.Create(() => new EvidenceReader($"session-{session.Value}", completed)));
        var evidence = await completed.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        var watcher = CreateTestProbe();
        watcher.Watch(reader);
        Sys.Stop(reader);
        await watcher.ExpectTerminatedAsync(reader, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        return evidence;
    }

    private Task<IActorRef> OwnerAsync(SessionId session) => Sys.ActorSelection(
        $"/user/session-manager/{Uri.EscapeDataString(session.Value)}").ResolveOne(FaultCeiling, TestContext.Current.CancellationToken);
    private static async Task JoinAsync(IActorRef manager, Akka.TestKit.TestProbe subscriber, SessionId session)
    {
        manager.Tell(new JoinSession(subscriber) { SessionId = session, Filter = OutputFilter.Full });
        await subscriber.ExpectMsgAsync<SessionJoined>(FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
    }
    private static Task<CommandAck> StartAsync(IActorRef manager, SessionId session) => manager.Ask<CommandAck>(
        new SendUserMessage { SessionId = session, Content = "Inspect the neutral fixture.", Source = Source("original", "operator-a") },
        FaultCeiling, TestContext.Current.CancellationToken);
    private static ValueTask<object> CompletedAsync(Akka.TestKit.TestProbe subscriber) => subscriber.FishForMessageAsync<object>(
        message => message is TurnCompleted, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static MessageSource Source(string turn, string requester) => new()
    {
        ChannelType = ChannelType.SignalR, SenderId = new SenderId(requester), MessageId = turn, TurnId = new TurnId(turn),
        Audience = TrustAudience.Personal, Boundary = TrustBoundary.Personal, Principal = PrincipalClassification.Operator,
        Provenance = new SourceProvenance(TransportAuthenticity.Verified, PayloadTaint.Trusted),
        DefaultDeliveryTarget = new ChannelDeliveryTargetInfo("signalr", "destination", requester, requester)
    };
    private static SendUserMessage Restart(SessionId session) => new()
    {
        SessionId = session, Content = "Resume the work that was interrupted by the daemon restart.",
        Source = Source("restart-resume-fault:1", "reminder-system") with
        {
            ReminderId = new ReminderId("restart-resume-fault:1"), Principal = PrincipalClassification.VerifiedAutomation
        }
    };

    private sealed record JournalEvidence(ISessionEvent[] Events, SessionSnapshot? Snapshot);
    private sealed class EvidenceReader : ReceivePersistentActor
    {
        public EvidenceReader(string id, TaskCompletionSource<JournalEvidence> completed)
        {
            PersistenceId = id;
            var events = new List<ISessionEvent>();
            SessionSnapshot? snapshot = null;
            Recover<SnapshotOffer>(offer => snapshot = Assert.IsType<SessionSnapshot>(offer.Snapshot));
            Recover<ISessionEvent>(events.Add);
            Recover<RecoveryCompleted>(_ => completed.TrySetResult(new JournalEvidence(events.ToArray(), snapshot)));
        }
        public override string PersistenceId { get; }
    }
    private sealed class ReceiptExecutor : IToolExecutor
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public List<string?> Requesters { get; } = [];
        public List<string> CallIds { get; } = [];
        public Task<string> ExecuteAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _count);
            CallIds.Add(call.CallId);
            Requesters.Add(context.RunScope.DefaultDeliveryTarget?.DestinationId);
            context.Outputs.TryComplete(new ToolInvocationReceipt.Succeeded([], null));
            return Task.FromResult("same");
        }
    }
    private sealed class RoleProvider(IChatClient main, IChatClient compaction) : IChatClientProvider
    {
        public IChatClient GetClient(Netclaw.Configuration.ModelRole role) => role switch
        {
            Netclaw.Configuration.ModelRole.Main => main,
            Netclaw.Configuration.ModelRole.Compaction => compaction,
            _ => throw new InvalidOperationException($"Unexpected model role: {role}.")
        };
    }
    private abstract class ScriptedClient : IChatClient
    {
        public abstract Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default);
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
    private sealed class RepeatedClient : ScriptedClient
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public bool CompactOnCorrection { get; set; }
        public bool HoldFourthRequest { get; set; }
        public TaskCompletionSource FourthEntered { get; } = NewSignal();
        public TaskCompletionSource ReleaseFourth { get; } = NewSignal();
        public List<ChatMessage[]> Requests { get; } = [];
        public static FunctionCallContent Call(string id) => new(id, "recurrence_probe", new Dictionary<string, object?>());
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref _count);
            Requests.Add(messages.ToArray());
            if (count == 4 && HoldFourthRequest)
            {
                FourthEntered.TrySetResult();
                await ReleaseFourth.Task.WaitAsync(cancellationToken);
            }
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, [Call($"call-{count}")]))
            {
                Usage = new UsageDetails { InputTokenCount = count == 3 && CompactOnCorrection ? 800 : 100, OutputTokenCount = 10 }
            };
        }
    }
    private sealed class SummaryClient : ScriptedClient
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "The original task remains active. Two equal calls completed.")));
        }
    }
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally { if (_directory is not null) await _directory.DisposeAsync(); }
    }
}
