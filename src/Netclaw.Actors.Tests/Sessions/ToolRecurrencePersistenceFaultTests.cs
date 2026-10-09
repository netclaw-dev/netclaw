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

public sealed partial class ToolRecurrencePersistenceFaultTests(ITestOutputHelper output)
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
        registry.RegisterCore(AIFunctionFactory.Create(() => "diagnostic", "diagnostic_probe"), "builtin");
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
        _main.CompactionRequestNumber = 3;
        _main.HoldRequestNumber = 4;
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
            await _main.RequestEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, _executor.Count);
        Assert.Equal(1, _compaction.Count);
        _main.ReleaseRequest.TrySetResult();
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_crash_after_committed_results_replays_the_suffix_without_repeating_completed_effects(int committedRounds)
    {
        var session = new SessionId($"signalr/committed-result-cut-{committedRounds}");
        _main.HoldRequestNumber = committedRounds + 1;
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        await StartAsync(manager, session);
        await _main.RequestEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        var owner = await OwnerAsync(session);
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        Sys.Stop(owner);
        await watcher.ExpectTerminatedAsync(owner, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);

        var before = await ReadJournalAsync(session);
        Assert.Null(before.Snapshot);
        Assert.Equal(committedRounds, before.Events.OfType<ToolBatchStarted>().Count());
        var committedResults = before.Events.OfType<ToolCallRecorded>().ToArray();
        Assert.Equal(committedRounds, committedResults.Length);
        Assert.Equal(Math.Min(committedRounds, 2), _executor.Count);
        Assert.Equal(Math.Min(committedRounds, 2), committedResults.Count(result => !result.LoopObservation!.Synthetic));
        Assert.Equal(committedRounds == 3 ? 1 : 0, committedResults.Count(result => result.LoopObservation!.Synthetic));
        Assert.Empty(before.Events.OfType<TurnRecorded>());

        subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        await manager.Ask<CommandAck>(Restart(session), FaultCeiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);

        Assert.Equal(2, _executor.Count);
        Assert.Equal(5, _main.Count);
        Assert.Equal(2, _executor.CallIds.Distinct().Count());
        Assert.All(_executor.Requesters, requester => Assert.Equal("operator-a", requester));
        var resumed = _main.Requests[committedRounds + 1];
        foreach (var result in committedResults)
        {
            var pair = Assert.Single(resumed.SelectMany(message => message.Contents.OfType<FunctionResultContent>()),
                item => item.CallId == result.ToolResult.ToolCallId!.Value.Value);
            Assert.Equal(result.ToolResult.Content, pair.Result);
        }
        Assert.DoesNotContain(resumed, message => message.Role == ChatRole.User
            && message.Text == "Resume the work that was interrupted by the daemon restart.");

        var after = await ReadJournalAsync(session);
        Assert.Equal(3, after.Events.OfType<ToolBatchStarted>().Count());
        var finalResults = after.Events.OfType<ToolCallRecorded>().ToArray();
        Assert.Equal(3, finalResults.Length);
        Assert.Equal(2, finalResults.Count(result => !result.LoopObservation!.Synthetic));
        Assert.Single(finalResults, result => result.LoopObservation!.Synthetic);
        foreach (var result in committedResults)
            Assert.Single(finalResults, item => item.ToolResult.ToolCallId == result.ToolResult.ToolCallId);
        Assert.Single(after.Events.OfType<TurnRecorded>());
    }

    [Fact]
    public async Task A_successful_final_result_after_owner_death_reconstructs_the_round_without_its_live_callback()
    {
        var session = new SessionId("signalr/held-actual-result-cut");
        var reached = NewSignal();
        var release = NewSignal();
        await Journal.OnWrite.FailIf(async representation =>
        {
            if (representation.Payload is not ToolCallRecorded { ToolResult.ToolCallId: { } id } || id.Value != "call-2")
                return false;
            reached.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return false;
        });
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        var owner = await OwnerAsync(session);
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        try
        {
            await StartAsync(manager, session);
            await reached.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
            Assert.Equal(2, _executor.Count);
            Assert.Equal(2, _main.Count);
            Sys.Stop(owner);
            await watcher.ExpectTerminatedAsync(owner, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            release.TrySetResult();
        }
        await Journal.OnWrite.Pass();
        JournalEvidence? before = null;
        await AwaitAssertAsync(async () =>
        {
            before = await ReadJournalAsync(session);
            Assert.Equal(2, before.Events.OfType<ToolCallRecorded>().Count());
        }, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(before);
        Assert.Null(before.Snapshot);
        Assert.Equal(2, before.Events.OfType<ToolBatchStarted>().Count());
        Assert.Empty(before.Events.OfType<TurnRecorded>());
        Assert.Equal(2, _main.Count);
        var originalResults = before.Events.OfType<ToolCallRecorded>().ToArray();
        Assert.All(originalResults, result =>
        {
            Assert.False(result.LoopObservation!.Synthetic);
            Assert.Equal((int)ToolInvocationOutcomeCategory.Success, result.LoopObservation.Category);
            Assert.Equal("same", result.ToolResult.Content);
        });

        subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        await manager.Ask<CommandAck>(Restart(session), FaultCeiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var resumed = _main.Requests[2];
        var resumedCalls = resumed.SelectMany(message => message.Contents.OfType<FunctionCallContent>()).ToArray();
        var resumedResults = resumed.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).ToArray();
        foreach (var result in originalResults)
        {
            var id = result.ToolResult.ToolCallId!.Value.Value;
            Assert.Single(resumedCalls, call => call.CallId == id);
            var pair = Assert.Single(resumedResults, item => item.CallId == id);
            Assert.Equal(result.ToolResult.Content, pair.Result);
        }
        Assert.Equal(resumedCalls.Select(call => call.CallId).Order(), resumedResults.Select(result => result.CallId).Order());
        Assert.Equal(2, _executor.Count);
        Assert.Equal(4, _main.Count);
        Assert.All(_executor.Requesters, requester => Assert.Equal("operator-a", requester));
        var after = await ReadJournalAsync(session);
        Assert.Equal(3, after.Events.OfType<ToolBatchStarted>().Count());
        var results = after.Events.OfType<ToolCallRecorded>().ToArray();
        Assert.Equal(3, results.Length);
        var correction = Assert.Single(results, result => result.LoopObservation!.Synthetic);
        Assert.Equal("call-3", correction.ToolResult.ToolCallId!.Value.Value);
        Assert.Equal(ToolCycleMessages.Correction
            + "\nNext action: choose a different action, load a missing tool, or finish the task.", correction.ToolResult.Content);
        var feedback = Assert.Single(_main.Requests[^1].SelectMany(message => message.Contents.OfType<FunctionResultContent>()),
            result => result.CallId == "call-3");
        Assert.Equal(correction.ToolResult.Content, feedback.Result);
        Assert.Empty(after.Events.OfType<ToolBatchAbandoned>());
        var terminal = Assert.Single(after.Events.OfType<TurnRecorded>());
        Assert.Equal(ToolCycleMessages.Final, terminal.AssistantReply.Content);
    }

    [Fact]
    public async Task A_successful_correction_admission_after_owner_death_replays_its_checkpoint_before_dispatch()
    {
        var session = new SessionId("signalr/held-correction-admission-cut");
        var reached = NewSignal();
        var release = NewSignal();
        await Journal.OnWrite.FailIf(async representation =>
        {
            if (representation.Payload is not ToolBatchStarted { LoopAdmission.RefusedCallIds.Count: > 0 }) return false;
            reached.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return false;
        });
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        var owner = await OwnerAsync(session);
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        try
        {
            await StartAsync(manager, session);
            await reached.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
            Assert.Equal(2, _executor.Count);
            Assert.Equal(3, _main.Count);
            Sys.Stop(owner);
            await watcher.ExpectTerminatedAsync(owner, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            release.TrySetResult();
        }
        await Journal.OnWrite.Pass();

        JournalEvidence? before = null;
        await AwaitAssertAsync(async () =>
        {
            before = await ReadJournalAsync(session);
            Assert.Equal(3, before.Events.OfType<ToolBatchStarted>().Count());
        }, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(before);
        Assert.Null(before.Snapshot);
        var correction = before.Events.OfType<ToolBatchStarted>().Last();
        Assert.Equal(["call-3"], correction.LoopAdmission!.RefusedCallIds);
        Assert.Contains(correction.LoopDelta!.Upserts, entry => entry.EqualRounds == 2 && entry.Corrected);
        Assert.Equal(2, before.Events.OfType<ToolCallRecorded>().Count());
        Assert.Empty(before.Events.OfType<TurnRecorded>());

        subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        await manager.Ask<CommandAck>(Restart(session), FaultCeiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        Assert.Equal(2, _executor.Count);
        Assert.Equal(4, _main.Count);
        Assert.All(_executor.Requesters, requester => Assert.Equal("operator-a", requester));
        var resumed = _main.Requests[^1];
        var resumedCalls = resumed.SelectMany(message => message.Contents.OfType<FunctionCallContent>()).ToArray();
        var resumedResults = resumed.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).ToArray();
        Assert.Single(resumedCalls, call => call.CallId == "call-3");
        var closed = Assert.Single(resumedResults, result => result.CallId == "call-3");
        Assert.Equal("Tool call was not completed — the session restarted before the action completed.", closed.Result);
        foreach (var result in before.Events.OfType<ToolCallRecorded>())
        {
            var pair = Assert.Single(resumedResults, item => item.CallId == result.ToolResult.ToolCallId!.Value.Value);
            Assert.Equal(result.ToolResult.Content, pair.Result);
            Assert.False(result.LoopObservation!.Synthetic);
        }
        Assert.Equal(resumedCalls.Select(call => call.CallId).Order(), resumedResults.Select(result => result.CallId).Order());
        var after = await ReadJournalAsync(session);
        Assert.Equal(3, after.Events.OfType<ToolBatchStarted>().Count());
        Assert.Equal(2, after.Events.OfType<ToolCallRecorded>().Count());
        var abandoned = Assert.Single(after.Events.OfType<ToolBatchAbandoned>());
        Assert.Equal("call-3", Assert.Single(abandoned.ToolResults).ToolCallId!.Value.Value);
        Assert.Single(after.Events.OfType<TurnRecorded>());
    }

    [Fact]
    public async Task A_successful_compaction_snapshot_keeps_the_correction_after_owner_death()
    {
        var session = new SessionId("signalr/successful-compaction-snapshot-cut");
        _main.CompactionRequestNumber = 3;
        _main.HoldRequestNumber = 4;
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        await StartAsync(manager, session);
        await _main.RequestEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        Assert.Equal(2, _executor.Count);
        Assert.Equal(1, _compaction.Count);

        JournalEvidence? stored = null;
        await AwaitAssertAsync(async () =>
        {
            stored = await ReadJournalAsync(session);
            Assert.NotNull(stored.Snapshot);
        }, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        var snapshot = Assert.IsType<SessionSnapshot>(stored.Snapshot);
        Assert.Equal("original", snapshot.LoopCheckpoint!.TaskId);
        Assert.Contains(snapshot.LoopCheckpoint.Entries, entry => entry.EqualRounds == 2 && entry.Corrected);
        Assert.Equal("original", snapshot.AdoptedTaskContext!.TurnId);
        Assert.Equal("operator-a", snapshot.AdoptedTaskContext.DefaultDeliveryTarget!.DestinationId);
        Assert.False(snapshot.LoopReceiptFailure);
        Assert.Empty(stored.Events);
        var owner = await OwnerAsync(session);
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        Sys.Stop(owner);
        await watcher.ExpectTerminatedAsync(owner, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);

        subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        await manager.Ask<CommandAck>(Restart(session), FaultCeiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        Assert.Equal(2, _executor.Count);
        Assert.Equal(5, _main.Count);
        Assert.Equal(1, _compaction.Count);
        Assert.All(_executor.Requesters, requester => Assert.Equal("operator-a", requester));
        var after = await ReadJournalAsync(session);
        Assert.NotNull(after.Snapshot);
        Assert.Empty(after.Events.OfType<ToolBatchStarted>());
        Assert.Empty(after.Events.OfType<ToolCallRecorded>());
        Assert.Single(after.Events.OfType<TurnRecorded>());
    }

    [Fact]
    public async Task A_failed_snapshot_cold_resumes_the_committed_compaction_without_reexecution()
    {
        var session = new SessionId("signalr/failed-snapshot-cold-cut");
        _main.CompactionRequestNumber = 3;
        _main.HoldRequestNumber = 4;
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
            await _main.RequestEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        }, cancellationToken: TestContext.Current.CancellationToken);
        var owner = await OwnerAsync(session);
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        Sys.Stop(owner);
        await watcher.ExpectTerminatedAsync(owner, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        var before = await ReadJournalAsync(session);
        Assert.Null(before.Snapshot);
        var compacted = Assert.Single(before.Events.OfType<SessionCompacted>());
        Assert.Empty(before.Events.OfType<TurnRecorded>());
        var results = before.Events.OfType<ToolCallRecorded>().ToArray();
        Assert.Equal(3, results.Length);
        Assert.Equal(2, results.Count(result => !result.LoopObservation!.Synthetic));
        Assert.Single(results, result => result.LoopObservation!.Synthetic);
        Assert.Equal(2, _executor.Count);
        Assert.Equal(4, _main.Count);
        Assert.Equal(1, _compaction.Count);

        await Snapshots.OnSave.Pass();
        subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        await manager.Ask<CommandAck>(Restart(session), FaultCeiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var resumed = _main.Requests[^1];
        Assert.False(string.IsNullOrWhiteSpace(compacted.Summary));
        foreach (var message in compacted.CompactedMessages)
        {
            var expected = ChatMessageConverter.ToAiMessage(message);
            Assert.Single(resumed, actual => actual.Role == expected.Role && actual.Text == expected.Text);
        }
        var calls = resumed.SelectMany(message => message.Contents.OfType<FunctionCallContent>()).ToArray();
        var pairs = resumed.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).ToArray();
        Assert.Equal(compacted.CompactedMessages.SelectMany(message => message.ToolCalls).Select(call => call.CallId.Value).Order(),
            calls.Select(call => call.CallId).Order());
        Assert.Equal(calls.Select(call => call.CallId).Order(), pairs.Select(pair => pair.CallId).Order());
        Assert.Equal(2, _executor.Count);
        Assert.Equal(5, _main.Count);
        Assert.Equal(1, _compaction.Count);
        Assert.All(_executor.Requesters, requester => Assert.Equal("operator-a", requester));
        var after = await ReadJournalAsync(session);
        Assert.Equal(3, after.Events.OfType<ToolBatchStarted>().Count());
        Assert.Equal(3, after.Events.OfType<ToolCallRecorded>().Count());
        Assert.Equal(ToolCycleMessages.Final, Assert.Single(after.Events.OfType<TurnRecorded>()).AssistantReply.Content);
    }

    [Fact]
    public async Task A_snapshot_after_owner_death_replays_a_multicall_suffix_with_distinct_outcomes_and_duplicate_calls()
    {
        var session = new SessionId("signalr/snapshot-multicall-suffix-cut");
        _main.CompactionRequestNumber = 1;
        _main.HoldRequestNumber = 3;
        _main.CallsByRequest[1] = [RepeatedClient.Call("a-1"), RepeatedClient.Call("a-1b"),
            RepeatedClient.Call("a-1c"), new FunctionCallContent("b-1", "diagnostic_probe", new Dictionary<string, object?>())];
        _main.CallsByRequest[2] = [RepeatedClient.Call("a-2"), RepeatedClient.Call("a-2b")];
        _executor.ResultByCall["a-1b"] = "second";
        _executor.ResultByCall["a-2b"] = "second";
        _executor.ResultByCall["b-1"] = "diagnostic";
        var reached = NewSignal();
        var release = NewSignal();
        long saveSequence = 0;
        await Snapshots.OnSave.FailIf(async (persistenceId, criteria) =>
        {
            if (persistenceId != $"session-{session.Value}") return false;
            saveSequence = criteria.MaxSequenceNr;
            reached.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return false;
        });
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        var owner = await OwnerAsync(session);
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        JournalEvidence committed;
        try
        {
            await StartAsync(manager, session);
            await reached.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
            await _main.RequestEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
            committed = await ReadJournalAsync(session, new Recovery(SnapshotSelectionCriteria.None));
            Assert.Null(committed.Snapshot);
            Assert.Single(committed.Events.OfType<SessionCompacted>());
            Assert.Equal(2, committed.Events.OfType<ToolBatchStarted>().Count());
            Assert.Equal(6, committed.Events.OfType<ToolCallRecorded>().Count());
            Assert.Equal(new[] { ("a-1", "same"), ("a-1b", "second"), ("a-1c", "same"),
                    ("b-1", "diagnostic"), ("a-2", "same"), ("a-2b", "second") }.OrderBy(item => item.Item1),
                committed.Events.OfType<ToolCallRecorded>()
                    .Select(result => (result.ToolResult.ToolCallId!.Value.Value, result.ToolResult.Content)).OrderBy(item => item.Item1));
            Assert.All(committed.Events.OfType<ToolCallRecorded>(), result =>
            {
                Assert.False(result.LoopObservation!.Synthetic);
                Assert.Equal((int)ToolInvocationOutcomeCategory.Success, result.LoopObservation.Category);
            });
            Assert.Empty(committed.Events.OfType<TurnRecorded>());
            Assert.Equal(6, _executor.Count);
            Assert.Equal(3, _main.Count);
            Sys.Stop(owner);
            await watcher.ExpectTerminatedAsync(owner, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            release.TrySetResult();
        }
        await Snapshots.OnSave.Pass();
        JournalEvidence? stored = null;
        await AwaitAssertAsync(async () =>
        {
            stored = await ReadJournalAsync(session);
            Assert.NotNull(stored.Snapshot);
        }, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        Assert.Equal(saveSequence, stored.SnapshotSequenceNr);
        Assert.True(stored.SnapshotSequenceNr > 0);
        Assert.True(stored.LastSequenceNr > stored.SnapshotSequenceNr);
        var snapshot = Assert.IsType<SessionSnapshot>(stored.Snapshot);
        Assert.Equal("original", snapshot.LoopCheckpoint!.TaskId);
        Assert.Equal("original", snapshot.AdoptedTaskContext!.TurnId);
        Assert.Equal("operator-a", snapshot.AdoptedTaskContext.DefaultDeliveryTarget!.DestinationId);
        Assert.Equal(1, Assert.Single(snapshot.LoopCheckpoint.Entries, entry => entry.ToolName == "recurrence_probe").EqualRounds);
        Assert.Equal(1, Assert.Single(snapshot.LoopCheckpoint.Entries, entry => entry.ToolName == "diagnostic_probe").EqualRounds);
        Assert.False(snapshot.LoopReceiptFailure);
        var suffixBatch = Assert.Single(stored.Events.OfType<ToolBatchStarted>());
        Assert.Equal(new[] { "a-2", "a-2b" }, suffixBatch.LoopAdmission!.Calls.Select(call => call.CallId));
        Assert.Equal(2, stored.Events.OfType<ToolCallRecorded>().Count());
        Assert.All(stored.Events.OfType<ToolCallRecorded>(), result => Assert.False(result.LoopObservation!.Synthetic));
        var retained = await ReadJournalAsync(session, new Recovery(SnapshotSelectionCriteria.None));
        Assert.Null(retained.Snapshot);
        Assert.Equal(committed.Events, retained.Events);
        Assert.Equal(committed.LastSequenceNr, retained.LastSequenceNr);

        subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        await manager.Ask<CommandAck>(Restart(session), FaultCeiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var resumed = _main.Requests[3];
        var calls = resumed.SelectMany(message => message.Contents.OfType<FunctionCallContent>()).ToArray();
        var pairs = resumed.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).ToArray();
        foreach (var result in stored.Events.OfType<ToolCallRecorded>())
        {
            var id = result.ToolResult.ToolCallId!.Value.Value;
            Assert.Single(calls, call => call.CallId == id);
            Assert.Equal(result.ToolResult.Content, Assert.Single(pairs, pair => pair.CallId == id).Result);
        }
        foreach (var message in snapshot.History.Where(message => message.Role != Netclaw.Actors.Protocol.ChatRole.System))
        {
            var expected = ChatMessageConverter.ToAiMessage(message);
            Assert.Single(resumed, actual => actual.Role == expected.Role && actual.Text == expected.Text);
        }
        Assert.Equal(snapshot.History.SelectMany(message => message.ToolCalls).Select(call => call.CallId.Value)
            .Concat(suffixBatch.LoopAdmission.Calls.Select(call => call.CallId)).Order(), calls.Select(call => call.CallId).Order());
        Assert.Equal(calls.Select(call => call.CallId).Order(), pairs.Select(pair => pair.CallId).Order());
        Assert.Equal(6, _executor.Count);
        Assert.Equal(5, _main.Count);
        Assert.Equal(1, _compaction.Count);
        Assert.All(_executor.Requesters, requester => Assert.Equal("operator-a", requester));
        var after = await ReadJournalAsync(session);
        Assert.Equal(stored.SnapshotSequenceNr, after.SnapshotSequenceNr);
        Assert.Equal(2, after.Events.OfType<ToolBatchStarted>().Count());
        var results = after.Events.OfType<ToolCallRecorded>().ToArray();
        Assert.Equal(3, results.Length);
        var correction = Assert.Single(results, result => result.LoopObservation!.Synthetic);
        Assert.Equal("call-4", correction.ToolResult.ToolCallId!.Value.Value);
        Assert.Equal(ToolCycleMessages.Correction
            + "\nNext action: choose a different action, load a missing tool, or finish the task.", correction.ToolResult.Content);
        Assert.Equal(ToolCycleMessages.Final, Assert.Single(after.Events.OfType<TurnRecorded>()).AssistantReply.Content);
    }

    [Fact]
    public async Task A_committed_missing_receipt_after_owner_death_settles_without_another_model_request()
    {
        var session = new SessionId("signalr/missing-receipt-lost-callback-cut");
        _executor.EmitReceipt = false;
        var reached = NewSignal();
        var release = NewSignal();
        await Journal.OnWrite.FailIf(async representation =>
        {
            if (representation.Payload is not ToolCallRecorded { LoopObservation.MissingReceipt: true }) return false;
            reached.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return false;
        });
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        var owner = await OwnerAsync(session);
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        try
        {
            await StartAsync(manager, session);
            await reached.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
            Assert.Equal(1, _executor.Count);
            Assert.Equal(1, _main.Count);
            Sys.Stop(owner);
            await watcher.ExpectTerminatedAsync(owner, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            release.TrySetResult();
        }
        await Journal.OnWrite.Pass();
        JournalEvidence? before = null;
        await AwaitAssertAsync(async () =>
        {
            before = await ReadJournalAsync(session);
            Assert.Single(before.Events.OfType<ToolCallRecorded>());
        }, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(before);
        Assert.Null(before.Snapshot);
        var batch = Assert.Single(before.Events.OfType<ToolBatchStarted>());
        Assert.Equal("original", batch.LoopAdmission!.TaskId);
        var call = Assert.Single(batch.AssistantMessage.ToolCalls);
        var recorded = Assert.Single(before.Events.OfType<ToolCallRecorded>());
        Assert.Equal(call.CallId, recorded.ToolResult.ToolCallId);
        Assert.Equal("same", recorded.ToolResult.Content);
        Assert.True(recorded.LoopObservation!.MissingReceipt);
        Assert.False(recorded.LoopObservation.Synthetic);
        Assert.Equal(-1, recorded.LoopObservation.Category);
        Assert.Empty(before.Events.OfType<TurnRecorded>());
        Assert.Equal("operator-a", Assert.Single(before.Events.OfType<ToolTaskAdopted>()).TurnContext.DefaultDeliveryTarget!.DestinationId);

        subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        await manager.Ask<CommandAck>(Restart(session), FaultCeiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        Assert.Equal(1, _main.Count);
        Assert.Equal(1, _executor.Count);
        Assert.Equal("operator-a", Assert.Single(_executor.Requesters));
        var after = await ReadJournalAsync(session);
        Assert.Single(after.Events.OfType<ToolBatchStarted>());
        Assert.Equal(recorded, Assert.Single(after.Events.OfType<ToolCallRecorded>()));
        Assert.Empty(after.Events.OfType<ToolBatchAbandoned>());
        Assert.Equal(ToolCycleMessages.MissingReceipt, Assert.Single(after.Events.OfType<TurnRecorded>()).AssistantReply.Content);
    }

    [Fact]
    public async Task A_committed_framework_stop_after_owner_death_does_not_reopen_the_completed_task()
    {
        var session = new SessionId("signalr/framework-stop-lost-callback-cut");
        var reached = NewSignal();
        var release = NewSignal();
        await Journal.OnWrite.FailIf(async representation =>
        {
            if (representation.Payload is not TurnRecorded { SessionId: var id } || id != session) return false;
            reached.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return false;
        });
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        var owner = await OwnerAsync(session);
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        try
        {
            await StartAsync(manager, session);
            await reached.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
            Assert.Equal(2, _executor.Count);
            Assert.Equal(4, _main.Count);
            Sys.Stop(owner);
            await watcher.ExpectTerminatedAsync(owner, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            release.TrySetResult();
        }
        await Journal.OnWrite.Pass();
        JournalEvidence? before = null;
        await AwaitAssertAsync(async () =>
        {
            before = await ReadJournalAsync(session);
            Assert.Single(before.Events.OfType<TurnRecorded>());
        }, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(before);
        Assert.Null(before.Snapshot);
        Assert.Equal(3, before.Events.OfType<ToolBatchStarted>().Count());
        var results = before.Events.OfType<ToolCallRecorded>().ToArray();
        Assert.Equal(3, results.Length);
        Assert.Equal(2, results.Count(result => !result.LoopObservation!.Synthetic));
        Assert.Single(results, result => result.LoopObservation!.Synthetic);
        var terminal = Assert.Single(before.Events.OfType<TurnRecorded>());
        Assert.Equal(ToolCycleMessages.Final, terminal.AssistantReply.Content);
        Assert.All(_executor.Requesters, requester => Assert.Equal("operator-a", requester));

        subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        await manager.Ask<CommandNack>(Restart(session), FaultCeiling, TestContext.Current.CancellationToken);
        Assert.Equal(4, _main.Count);
        Assert.Equal(2, _executor.Count);
        var after = await ReadJournalAsync(session);
        Assert.Equal(terminal, Assert.Single(after.Events.OfType<TurnRecorded>()));
        Assert.Equal(results, after.Events.OfType<ToolCallRecorded>());
        Assert.Equal(3, after.Events.OfType<ToolBatchStarted>().Count());
        Assert.Empty(after.Events.OfType<ToolBatchAbandoned>());
    }

    [Fact]
    public async Task A_failed_compaction_commit_attempts_no_snapshot_and_cold_resumes_the_prior_checkpoint()
    {
        var session = new SessionId("signalr/failed-compaction-commit-cut");
        _main.CompactionRequestNumber = 3;
        var snapshotAttempts = 0;
        await Snapshots.OnSave.FailIf((persistenceId, _) =>
        {
            if (persistenceId == $"session-{session.Value}") Interlocked.Increment(ref snapshotAttempts);
            return false;
        });
        var reached = NewSignal();
        await Journal.OnWrite.FailIf(representation =>
        {
            if (representation.Payload is not SessionCompacted { SessionId: var id } || id != session) return false;
            reached.TrySetResult();
            return true;
        });
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        var owner = await OwnerAsync(session);
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        await EventFilter.Error(contains: "Failed to persist event type").ExpectOneAsync(async () =>
        {
            await StartAsync(manager, session);
            await reached.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
            await watcher.ExpectTerminatedAsync(owner, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        }, cancellationToken: TestContext.Current.CancellationToken);
        var before = await ReadJournalAsync(session);
        Assert.Null(before.Snapshot);
        Assert.Empty(before.Events.OfType<SessionCompacted>());
        Assert.Empty(before.Events.OfType<TurnRecorded>());
        Assert.Equal(0, Volatile.Read(ref snapshotAttempts));
        Assert.Equal(3, before.Events.OfType<ToolBatchStarted>().Count());
        var recorded = before.Events.OfType<ToolCallRecorded>().ToArray();
        Assert.Equal(3, recorded.Length);
        Assert.Equal(2, recorded.Count(result => !result.LoopObservation!.Synthetic));
        Assert.Single(recorded, result => result.LoopObservation!.Synthetic);
        Assert.Equal(2, _executor.Count);
        Assert.Equal(3, _main.Count);
        Assert.Equal(1, _compaction.Count);

        await Journal.OnWrite.Pass();
        subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        await manager.Ask<CommandAck>(Restart(session), FaultCeiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var resumed = _main.Requests[^1];
        foreach (var result in recorded)
        {
            var id = result.ToolResult.ToolCallId!.Value.Value;
            Assert.Single(resumed.SelectMany(message => message.Contents.OfType<FunctionCallContent>()), call => call.CallId == id);
            Assert.Equal(result.ToolResult.Content, Assert.Single(
                resumed.SelectMany(message => message.Contents.OfType<FunctionResultContent>()), pair => pair.CallId == id).Result);
        }
        Assert.Equal(2, _executor.Count);
        Assert.Equal(4, _main.Count);
        Assert.Equal(1, _compaction.Count);
        Assert.Equal(0, Volatile.Read(ref snapshotAttempts));
        Assert.All(_executor.Requesters, requester => Assert.Equal("operator-a", requester));
        var after = await ReadJournalAsync(session);
        Assert.Null(after.Snapshot);
        Assert.Empty(after.Events.OfType<SessionCompacted>());
        Assert.Equal(recorded, after.Events.OfType<ToolCallRecorded>());
        Assert.Equal(3, after.Events.OfType<ToolBatchStarted>().Count());
        Assert.Equal(ToolCycleMessages.Final, Assert.Single(after.Events.OfType<TurnRecorded>()).AssistantReply.Content);
    }

    [Fact]
    public async Task A_fresh_input_committed_after_owner_death_adopts_its_canonical_authority_once_after_the_old_batch()
    {
        var session = new SessionId("signalr/fresh-input-lost-callback-cut");
        _main.HoldRequestNumber = 3;
        var reached = NewSignal();
        var release = NewSignal();
        await Journal.OnWrite.FailIf(async representation =>
        {
            if (representation.Payload is not InputAdmitted { SourceMessageId: "fresh" }) return false;
            reached.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return false;
        });
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        await StartAsync(manager, session);
        await _main.RequestEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        var prefix = await ReadJournalAsync(session);
        Assert.Equal(2, prefix.Events.OfType<ToolCallRecorded>().Count());
        Assert.Equal("original", Assert.Single(prefix.Events.OfType<ToolTaskAdopted>()).TurnContext.TurnId);
        Assert.All(prefix.Events.OfType<ToolCallRecorded>(), result =>
        {
            Assert.False(result.LoopObservation!.Synthetic);
            Assert.Equal((int)ToolInvocationOutcomeCategory.Success, result.LoopObservation.Category);
            Assert.Equal("same", result.ToolResult.Content);
        });
        Assert.Equal(new[] { "operator-a", "operator-a" }, _executor.Requesters);
        var owner = await OwnerAsync(session);
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        var requester = CreateTestProbe();
        try
        {
            manager.Tell(new SendUserMessage
            {
                SessionId = session, Content = "Follow the fresh admitted task.", Source = Source("fresh", "operator-b")
            }, requester.Ref);
            await reached.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
            Sys.Stop(owner);
            await watcher.ExpectTerminatedAsync(owner, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        }
        finally
        {
            release.TrySetResult();
        }
        await Journal.OnWrite.Pass();
        JournalEvidence? committed = null;
        await AwaitAssertAsync(async () =>
        {
            committed = await ReadJournalAsync(session);
            Assert.Single(committed.Events.OfType<InputAdmitted>(), input => input.SourceMessageId == "fresh");
        }, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(committed);
        Assert.Null(committed.Snapshot);
        Assert.Equal(prefix.Events, committed.Events.Take(prefix.Events.Length));
        Assert.Equal(prefix.Events.Length + 1, committed.Events.Length);
        Assert.Empty(committed.Events.OfType<TurnRecorded>());
        var fresh = Assert.Single(committed.Events.OfType<InputAdmitted>(), input => input.SourceMessageId == "fresh");
        Assert.Equal("fresh", fresh.TurnContext.TurnId);
        Assert.Equal("operator-b", fresh.TurnContext.DefaultDeliveryTarget!.DestinationId);
        Assert.Equal("original", Assert.Single(committed.Events.OfType<ToolTaskAdopted>()).TurnContext.TurnId);
        Assert.Equal(2, _executor.Count);
        Assert.Equal(3, _main.Count);

        subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        await manager.Ask<CommandAck>(Restart(session), FaultCeiling, TestContext.Current.CancellationToken);
        await CompletedAsync(subscriber);
        var resumed = _main.Requests[3];
        Assert.DoesNotContain(resumed, message => message.Role == ChatRole.User && message.Text == fresh.UserMessage.Content);
        foreach (var result in prefix.Events.OfType<ToolCallRecorded>())
        {
            var id = result.ToolResult.ToolCallId!.Value.Value;
            Assert.Single(resumed.SelectMany(message => message.Contents.OfType<FunctionCallContent>()), call => call.CallId == id);
            Assert.Equal(result.ToolResult.Content, Assert.Single(
                resumed.SelectMany(message => message.Contents.OfType<FunctionResultContent>()), pair => pair.CallId == id).Result);
        }
        Assert.Single(_main.Requests[4], message => message.Role == ChatRole.User && message.Text == fresh.UserMessage.Content);
        Assert.Equal(new[] { "operator-a", "operator-a", "operator-b", "operator-b" }, _executor.Requesters);
        Assert.Equal(4, _executor.Count);
        Assert.Equal(8, _main.Count);
        var after = await ReadJournalAsync(session);
        var adoptions = after.Events.OfType<ToolTaskAdopted>().ToArray();
        Assert.Equal(new[] { "original", "fresh" }, adoptions.Select(adoption => adoption.TurnContext.TurnId));
        Assert.Equal(fresh.TurnContext, adoptions[1].TurnContext);
        Assert.Equal(fresh.InputId, Assert.Single(adoptions[1].InputIds));
        Assert.Single(after.Events.OfType<InputAdmitted>(), input => input.SourceMessageId == "fresh");
        var batches = after.Events.OfType<ToolBatchStarted>().ToArray();
        Assert.Equal(new[] { "original", "original", "original", "fresh", "fresh", "fresh" },
            batches.Select(batch => batch.LoopAdmission!.TaskId));
        Assert.Single(batches, batch => batch.ConsumedInputIds.Contains(fresh.InputId));
        var originalInput = Assert.Single(after.Events.OfType<InputAdmitted>(), input => input.SourceMessageId == "original");
        Assert.Single(batches, batch => batch.ConsumedInputIds.Contains(originalInput.InputId));
        Assert.Equal(4, after.Events.OfType<ToolCallRecorded>().Count(result => !result.LoopObservation!.Synthetic));
        Assert.Equal(2, after.Events.OfType<ToolCallRecorded>().Count(result => result.LoopObservation!.Synthetic));
        Assert.Equal(ToolCycleMessages.Final, Assert.Single(after.Events.OfType<TurnRecorded>()).AssistantReply.Content);
        Assert.Empty(after.Events.OfType<ToolBatchAbandoned>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unknown_outcome_reaches_the_model_before_an_optional_authorized_retry(bool retry)
    {
        var session = new SessionId($"signalr/unknown-outcome-{retry}");
        _main.CallsByRequest[1] =
        [
            new FunctionCallContent("committed-sibling", "diagnostic_probe", new Dictionary<string, object?>()),
            RepeatedClient.Call("unknown-call")
        ];
        _main.HoldRequestNumber = 2;
        _main.FinalRequestNumber = retry ? 3 : 2;
        _main.CallsByRequest[2] = [RepeatedClient.Call("model-chosen-retry")];
        _executor.ResultByCall["committed-sibling"] = "confirmed diagnostic";
        _executor.ResultByCall["unknown-call"] = "uncommitted actual result";
        var reached = NewSignal();
        await Journal.OnWrite.FailIf(representation =>
        {
            if (representation.Payload is not ToolCallRecorded { ToolResult.ToolCallId: { } id }
                || id.Value != "unknown-call") return false;
            reached.TrySetResult();
            return true;
        });
        var manager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        var owner = await OwnerAsync(session);
        var watcher = CreateTestProbe();
        watcher.Watch(owner);
        await EventFilter.Error(contains: "Failed to persist event type").ExpectOneAsync(async () =>
        {
            await StartAsync(manager, session);
            await reached.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
            await watcher.ExpectTerminatedAsync(owner, FaultCeiling, cancellationToken: TestContext.Current.CancellationToken);
        }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, _executor.Count);
        Assert.Equal(new[] { "committed-sibling", "unknown-call" }, _executor.CallIds);
        Assert.Equal(1, _main.Count);
        var before = await ReadJournalAsync(session);
        var sibling = Assert.Single(before.Events.OfType<ToolCallRecorded>());
        Assert.Equal("committed-sibling", sibling.ToolResult.ToolCallId!.Value.Value);
        Assert.Equal("confirmed diagnostic", sibling.ToolResult.Content);
        Assert.False(sibling.LoopObservation!.Synthetic);
        Assert.False(sibling.LoopObservation.MissingReceipt);
        Assert.Equal((int)ToolInvocationOutcomeCategory.Success, sibling.LoopObservation.Category);
        Assert.Empty(before.Events.OfType<TurnRecorded>());
        var admitted = Assert.Single(before.Events.OfType<ToolBatchStarted>());
        Assert.Equal(new[] { "committed-sibling", "unknown-call" }, admitted.LoopAdmission!.Calls.Select(call => call.CallId));

        await Journal.OnWrite.Pass();
        subscriber = CreateTestProbe();
        await JoinAsync(manager, subscriber, session);
        await manager.Ask<CommandAck>(Restart(session), FaultCeiling, TestContext.Current.CancellationToken);
        await _main.RequestEntered.Task.WaitAsync(FaultCeiling, TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(2, _main.Count);
            Assert.Equal(2, _executor.Count);
            var resumed = _main.Requests[1];
            var calls = resumed.SelectMany(message => message.Contents.OfType<FunctionCallContent>()).ToArray();
            var results = resumed.SelectMany(message => message.Contents.OfType<FunctionResultContent>()).ToArray();
            Assert.Equal(new[] { "committed-sibling", "unknown-call" }, calls.Select(call => call.CallId));
            Assert.Equal(calls.Select(call => call.CallId).Order(), results.Select(result => result.CallId).Order());
            Assert.Equal(sibling.ToolResult.Content, Assert.Single(results, result => result.CallId == "committed-sibling").Result);
            Assert.Equal("Tool call was not completed — the session restarted before the action completed.",
                Assert.Single(results, result => result.CallId == "unknown-call").Result);
            Assert.DoesNotContain(results, result => Equals(result.Result, "uncommitted actual result"));
            var recovered = await ReadJournalAsync(session);
            Assert.Equal(sibling, Assert.Single(recovered.Events.OfType<ToolCallRecorded>()));
            var closure = Assert.Single(Assert.Single(recovered.Events.OfType<ToolBatchAbandoned>()).ToolResults);
            Assert.Equal("unknown-call", closure.ToolCallId!.Value.Value);
            Assert.Empty(recovered.Events.OfType<ToolApprovalRequested>());
        }
        finally
        {
            _main.ReleaseRequest.TrySetResult();
        }
        await CompletedAsync(subscriber);
        Assert.Equal(retry ? 3 : 2, _executor.Count);
        Assert.Equal(retry ? new[] { "committed-sibling", "unknown-call", "model-chosen-retry" }
            : new[] { "committed-sibling", "unknown-call" }, _executor.CallIds);
        Assert.Equal(retry ? 3 : 2, _main.Count);
        Assert.All(_executor.Requesters, requester => Assert.Equal("operator-a", requester));
        var after = await ReadJournalAsync(session);
        Assert.Equal(sibling, Assert.Single(after.Events.OfType<ToolCallRecorded>(), result => result.ToolResult.ToolCallId!.Value.Value == "committed-sibling"));
        Assert.DoesNotContain(after.Events.OfType<ToolCallRecorded>(), result => result.ToolResult.ToolCallId!.Value.Value == "unknown-call");
        Assert.Equal(retry ? 2 : 1, after.Events.OfType<ToolCallRecorded>().Count());
        if (retry)
        {
            var actual = Assert.Single(after.Events.OfType<ToolCallRecorded>(), result => result.ToolResult.ToolCallId!.Value.Value == "model-chosen-retry");
            Assert.False(actual.LoopObservation!.Synthetic);
            Assert.False(actual.LoopObservation.MissingReceipt);
            Assert.Equal((int)ToolInvocationOutcomeCategory.Success, actual.LoopObservation.Category);
            Assert.Equal("same", actual.ToolResult.Content);
            Assert.Equal(actual.ToolResult.Content, Assert.Single(
                _main.Requests[2].SelectMany(message => message.Contents.OfType<FunctionResultContent>()),
                result => result.CallId == "model-chosen-retry").Result);
        }
        Assert.Equal(RepeatedClient.FinalReply, Assert.Single(after.Events.OfType<TurnRecorded>()).AssistantReply.Content);
        Assert.Empty(after.Events.OfType<ToolApprovalRequested>());
        Assert.Equal("original", Assert.Single(after.Events.OfType<ToolTaskAdopted>()).TurnContext.TurnId);
    }

    private Task<JournalEvidence> ReadJournalAsync(SessionId session) => ReadJournalAsync(session, Recovery.Default);

    private async Task<JournalEvidence> ReadJournalAsync(SessionId session, Recovery recovery)
    {
        var completed = new TaskCompletionSource<JournalEvidence>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Sys.ActorOf(Props.Create(() => new EvidenceReader($"session-{session.Value}", completed, recovery)));
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
        manager.Tell(new JoinSession(subscriber) { SessionId = session, Filter = OutputFilter.Full }, subscriber.Ref);
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

    private sealed record JournalEvidence(ISessionEvent[] Events, SessionSnapshot? Snapshot, long SnapshotSequenceNr, long LastSequenceNr);
    private sealed class EvidenceReader : ReceivePersistentActor
    {
        public EvidenceReader(string id, TaskCompletionSource<JournalEvidence> completed, Recovery recovery)
        {
            PersistenceId = id;
            Recovery = recovery;
            long snapshotSequence = 0;
            var events = new List<ISessionEvent>();
            SessionSnapshot? snapshot = null;
            Recover<SnapshotOffer>(offer =>
            {
                snapshot = Assert.IsType<SessionSnapshot>(offer.Snapshot);
                snapshotSequence = offer.Metadata.SequenceNr;
            });
            Recover<ISessionEvent>(events.Add);
            Recover<RecoveryCompleted>(_ => completed.TrySetResult(new JournalEvidence(events.ToArray(), snapshot, snapshotSequence, LastSequenceNr)));
        }
        public override string PersistenceId { get; }
        public override Recovery Recovery { get; }
    }
    private sealed class ReceiptExecutor : IToolExecutor
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public List<string?> Requesters { get; } = [];
        public List<string> CallIds { get; } = [];
        public Dictionary<string, string> ResultByCall { get; } = [];
        public bool EmitReceipt { get; set; } = true;
        public Task<string> ExecuteAsync(FunctionCallContent call, ToolExecutionContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _count);
            CallIds.Add(call.CallId);
            Requesters.Add(context.RunScope.DefaultDeliveryTarget?.DestinationId);
            if (EmitReceipt) context.Outputs.TryComplete(new ToolInvocationReceipt.Succeeded([], null));
            return Task.FromResult(ResultByCall.GetValueOrDefault(call.CallId, "same"));
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
        public const string FinalReply = "I retain the recorded outcome and finish the task.";
        public int FinalRequestNumber { get; set; }
        public int CompactionRequestNumber { get; set; }
        public Dictionary<int, FunctionCallContent[]> CallsByRequest { get; } = [];
        public int HoldRequestNumber { get; set; }
        public TaskCompletionSource RequestEntered { get; } = NewSignal();
        public TaskCompletionSource ReleaseRequest { get; } = NewSignal();
        public List<ChatMessage[]> Requests { get; } = [];
        public static FunctionCallContent Call(string id) => new(id, "recurrence_probe", new Dictionary<string, object?>());
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref _count);
            Requests.Add(messages.ToArray());
            if (count == HoldRequestNumber)
            {
                RequestEntered.TrySetResult();
                await ReleaseRequest.Task.WaitAsync(cancellationToken);
            }
            if (count == FinalRequestNumber)
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, FinalReply));
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, CallsByRequest.TryGetValue(count, out var calls) ? calls : [Call($"call-{count}")]))
            {
                Usage = new UsageDetails { InputTokenCount = count == CompactionRequestNumber ? 800 : 100, OutputTokenCount = 10 }
            };
        }
    }
    private sealed class SummaryClient : ScriptedClient
    {
        private int _count;
        public string ResponseText { get; set; } = "The original task remains active.";
        public Exception? Failure { get; set; }
        public int Count => Volatile.Read(ref _count);
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            if (Failure is { } failure) throw failure;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, ResponseText)));
        }
    }
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally { if (_directory is not null) await _directory.DisposeAsync(); }
    }
}
