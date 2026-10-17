// -----------------------------------------------------------------------
// <copyright file="OneShotReminderTestBase.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Text.Json;
using Akka.Actor;
using Akka.Event;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Akka.Reminders;
using Akka.Reminders.Sharding;
using Akka.Streams;
using Akka.TestKit;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Tests.Hosting;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Reminders.ReminderProtocol;
using static Netclaw.Actors.Sessions.SessionProtocol;
using TestKit = Akka.Hosting.TestKit.TestKit;

namespace Netclaw.Actors.Tests.Reminders;

/// <summary>
/// Shared harness for the one-shot lifecycle tests of change <c>oneshot-reminder-autodelete</c>.
/// It runs the real reminder manager against the real Akka.Reminders scheduler with the
/// production acknowledgement timeout. The actor system uses <see cref="TestScheduler"/>, and the
/// manager uses a <see cref="FakeTimeProvider"/>. Both clocks move only when a test calls
/// <see cref="Advance"/>, so no test waits on wall-clock time.
/// <para>
/// A test observes on-disk state (the definition, the history, and the tombstone file) and the
/// durable Akka.Reminders state. The tombstone file layout is in design.md, decisions D1 and D1a.
/// </para>
/// </summary>
[Collection(ReminderActorTestCollection.Name)]
public abstract class OneShotReminderTestBase : TestKit, IAsyncDisposable
{
    protected const string DefaultPrompt = "Check PR 42 and tell me when CI is green.";
    protected const string FailureText = "model endpoint is not available";
    protected const string DeniedText =
        "Tool access denied: shell_execute needs approval, and nobody can answer a prompt in an unattended run.";
    protected static readonly TimeSpan FirstFireDelay = TimeSpan.FromSeconds(10);
    protected static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(5);

    protected static readonly ReminderAudienceAuthorizationContext OperatorCaller =
        new(TrustAudience.Personal, "Operator/test");
    protected static readonly ReminderAudienceAuthorizationContext TeamCaller =
        new(TrustAudience.Team, "test");
    protected static readonly ReminderAudienceAuthorizationContext PublicCaller =
        new(TrustAudience.Public, "test");

    private readonly TestSessionTempDirectory _tempDir =
        TestSessionTempDirectory.Create(prefix: "netclaw-oneshot-tests-", createDirectoryTree: true);
    private readonly TestShardRegionResolver _resolver = new();
    private readonly ConcurrentQueue<LogEvent> _logs = new();
    private int _dispatchesTaken;
    private int _managerSequence;
    private IActorRef _tap = ActorRefs.Nobody;

    protected OneShotReminderTestBase(ITestOutputHelper output) : base(output: output) { }

    protected FakeTimeProvider Clock { get; } = new();
    private protected ReminderManagerActorTests.TestNotificationSink Alerts { get; } = new();
    protected RunModePipeline Pipeline { get; } = new();
    protected EnvelopeTapProbe Envelopes { get; } = new();
    protected ReminderDefinitionStore Definitions { get; private set; } = null!;
    protected ReminderHistoryStore History { get; private set; } = null!;
    protected NetclawPaths Paths => _tempDir.Paths;
    protected IActorRef Manager { get; private set; } = ActorRefs.Nobody;

    /// <summary>Akka.Reminders attempts before it replies <c>Failed</c> to a negative acknowledgement.</summary>
    protected virtual int MaxDeliveryAttempts => 10;

    // TestKit stops the actor system only after AfterAllAsync returns. Delete the directory
    // after TestKit has disposed, and not in AfterAllAsync.
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            Pipeline.ReleaseHeldRuns();
            await _tempDir.DisposeAsync();
        }
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.AddHocon(
            "akka.scheduler.implementation = \"Akka.TestKit.TestScheduler, Akka.TestKit\"",
            HoconAddMode.Prepend);

        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawSerialization()
            .WithSerializationVerification();

        builder.WithLocalReminders(reminders =>
        {
            reminders.WithInMemoryStorage();
            reminders.WithResolver(_ => _resolver);
            reminders.WithSettings(new ReminderSettings
            {
                AckTimeout = NetclawAkkaHostingExtensions.ReminderAckTimeout,
                MaxDeliveryAttempts = MaxDeliveryAttempts
            });
        });

        builder.StartActors((system, registry, _) =>
        {
            registry.Register<SessionManagerActorKey>(system.DeadLetters);

            // The manager clock and the scheduler clock start at the same instant.
            Clock.SetUtcNow(system.Scheduler.Now);

            var sink = system.ActorOf(Props.Create(() => new LogSink(_logs)), "log-sink");
            system.EventStream.Subscribe(sink, typeof(LogEvent));
            _tap = system.ActorOf(Props.Create(() => new EnvelopeTap(Envelopes)), "envelope-tap");
            _resolver.RegisterShardRegion(ReminderManagerActor.ShardRegionName, _tap);

            Manager = StartManager(system);
            registry.Register<ReminderManagerActorKey>(Manager);
        });
    }

    private IActorRef StartManager(ActorSystem system)
    {
        Definitions = new ReminderDefinitionStore(Paths);
        History = new ReminderHistoryStore(Paths);
        var defaults = new EffectivePolicyDefaults(
            DeploymentPosture.Team, TrustAudience.Team, ShellExecutionMode.Off, false);
        var manager = system.ActorOf(
            Props.Create(() => new ReminderManagerActor(
                Pipeline,
                defaults,
                new SchedulingConfig(),
                Clock,
                Definitions,
                History,
                Alerts,
                NullReminderChannelNotifier.Instance)),
            $"reminder-manager-oneshot-{++_managerSequence}");
        _tap.Tell(new EnvelopeTap.SetTarget(manager));
        return manager;
    }

    /// <summary>
    /// Stops the manager and starts a new one over the same folders and the same Akka.Reminders
    /// storage, as a daemon restart does. Files that a test writes in <paramref name="beforeStart"/>
    /// are what the new manager finds at startup.
    /// </summary>
    protected async Task RestartManagerAsync(Action? beforeStart = null)
    {
        await StopManagerAsync();
        beforeStart?.Invoke();
        await StartManagerAsync();
    }

    /// <summary>Stops the manager. The scheduler then delivers envelopes to nobody, unless a test routes them elsewhere.</summary>
    protected async Task StopManagerAsync()
    {
        Watch(Manager);
        Sys.Stop(Manager);
        await ExpectTerminatedAsync(Manager, AskTimeout, cancellationToken: TestContext.Current.CancellationToken);
    }

    protected async Task StartManagerAsync()
    {
        Manager = StartManager(Sys);
        ActorRegistry.For(Sys).Register<ReminderManagerActorKey>(Manager, overwrite: true);
        await DrainStartupAsync();
    }

    /// <summary>
    /// Removes the shard region from the resolver. Akka.Reminders then replies <c>ShardRegionNotFound</c> to every
    /// schedule request, which is a deterministic scheduling failure for any schedule type.
    /// </summary>
    protected void MakeShardRegionUnavailable()
        => Assert.True(
            _resolver.UnregisterShardRegion(ReminderManagerActor.ShardRegionName),
            "The shard region was not registered.");

    /// <summary>Routes the envelopes that the scheduler delivers to <paramref name="target"/> until a manager starts.</summary>
    protected void RouteEnvelopesTo(IActorRef target) => _tap.Tell(new EnvelopeTap.SetTarget(target));

    /// <summary>
    /// ActorOf can return before PreStart queues its reconcile. Two ordered barriers drain both orders.
    /// </summary>
    protected async Task DrainStartupAsync()
    {
        await ReconcileAsync();
        await ReconcileAsync();
    }

    private protected Task<ReminderManagerActor.ReconcileCompleted> ReconcileAsync() =>
        Manager.Ask<ReminderManagerActor.ReconcileCompleted>(
            ReminderManagerActor.ReconcileReminders.Instance, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    /// <summary>
    /// Runs the 12-hour sweep. The manager handles one message at a time, so the health reply
    /// proves that the sweep finished.
    /// </summary>
    protected async Task SweepAsync()
    {
        Manager.Tell(ReminderManagerActor.PruneTerminalReminders.Instance);
        await BarrierAsync();
    }

    protected Task<ReminderHealthResponse> BarrierAsync() =>
        Manager.Ask<ReminderHealthResponse>(
            GetReminderHealthQuery.Instance, AskTimeout, TestContext.Current.CancellationToken);

    protected void Advance(TimeSpan offset)
    {
        // The manager clock moves first. The scheduler delivers the due occurrences during its own advance.
        Clock.Advance(offset);
        ((TestScheduler)Sys.Scheduler).Advance(offset);
    }

    // ── Reminder definitions ──

    protected ReminderDefinition NewOneShot(
        string id,
        TimeSpan? fireIn = null,
        TrustAudience audience = TrustAudience.Team,
        string instructions = DefaultPrompt)
    {
        var now = Clock.GetUtcNow();
        return new ReminderDefinition
        {
            Id = new ReminderId(id),
            Title = $"Title of {id}",
            Instructions = instructions,
            // The transport and address are on the definition so a test can prove that no tombstone keeps them.
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None, Transport = "slack", Address = "#secret-room" },
            DeliveryInstructions = "secret delivery note",
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = now + (fireIn ?? FirstFireDelay)
            },
            Audience = audience,
            Boundary = audience == TrustAudience.Personal ? TrustBoundary.Personal
                : audience == TrustAudience.Team ? TrustBoundary.Team
                : TrustBoundary.Public,
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    protected ReminderDefinition NewInterval(string id, TimeSpan interval)
    {
        var now = Clock.GetUtcNow();
        return NewOneShot(id) with
        {
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.Interval,
                Interval = interval,
                FireAt = now + FirstFireDelay
            }
        };
    }

    protected static ReminderAudienceAuthorizationContext CallerFor(TrustAudience audience) => audience switch
    {
        TrustAudience.Personal => OperatorCaller,
        TrustAudience.Team => TeamCaller,
        _ => PublicCaller
    };

    protected async Task<ReminderSavedResponse> SaveAsync(
        ReminderDefinition definition,
        ReminderWriteMode mode = ReminderWriteMode.Upsert,
        ReminderAudienceAuthorizationContext? caller = null)
        => await Manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(definition, mode, caller ?? CallerFor(definition.Audience)),
            AskTimeout, TestContext.Current.CancellationToken);

    protected async Task ScheduleAsync(ReminderDefinition definition)
    {
        var saved = await SaveAsync(definition);
        Assert.True(saved.Success, saved.ErrorMessage);
    }

    // ── Queries ──

    protected Task<ReminderCancelledResponse> CancelAsync(ReminderId id, ReminderAudienceAuthorizationContext caller)
        => Manager.Ask<ReminderCancelledResponse>(
            new CancelReminderCommand(id, caller), AskTimeout, TestContext.Current.CancellationToken);

    protected Task<ReminderHistoryResponse> HistoryAsync(ReminderId id, ReminderAudienceAuthorizationContext caller)
        => Manager.Ask<ReminderHistoryResponse>(
            new GetReminderHistoryQuery(id, 50, caller), AskTimeout, TestContext.Current.CancellationToken);

    protected Task<ReminderStatusResponse> StatusAsync(ReminderId id, ReminderAudienceAuthorizationContext caller)
        => Manager.Ask<ReminderStatusResponse>(
            new GetReminderStatusQuery(id, caller), AskTimeout, TestContext.Current.CancellationToken);

    protected Task<GetReminderResponse> GetAsync(ReminderId id, ReminderAudienceAuthorizationContext caller)
        => Manager.Ask<GetReminderResponse>(
            new GetReminderCommand(id, caller), AskTimeout, TestContext.Current.CancellationToken);

    protected Task<ReminderListResponse> ListAsync(ReminderAudienceAuthorizationContext caller)
        => Manager.Ask<ReminderListResponse>(
            new ListRemindersCommand(caller), AskTimeout, TestContext.Current.CancellationToken);

    // ── Akka.Reminders (durable state) ──

    private IReminderClient Client => ReminderClientExtension.Get(Sys).CreateClient(
        new ReminderEntity(ReminderManagerActor.ShardRegionName, ReminderManagerActor.EntityId));

    protected async Task<bool> IsScheduledAsync(ReminderId id)
    {
        var all = await Client.ListRemindersAsync();
        return all.Reminders.Any(r => r.Message is ReminderPayload p && p.Id == id);
    }

    /// <summary>The durable completion status of the occurrence, or null when none is stored.</summary>
    protected async Task<string?> OccurrenceStatusAsync(ReminderId id, DateTimeOffset due)
    {
        var response = await Client.GetOccurrenceStatusAsync(new ReminderKey(id.Value), due);
        return response.ResponseCode == ReminderOccurrenceStatusResponseCode.Success
            ? response.Status!.CompletionStatus.ToString()
            : null;
    }

    protected Task AckAsync(ReminderEnvelope<ReminderPayload> envelope) => Client.AckAsync(envelope);

    protected Task CancelScheduleAsync(ReminderId id) => Client.CancelReminderAsync(new ReminderKey(id.Value));

    protected Task ScheduleDirectlyAsync(ReminderId id, DateTimeOffset when)
        => Client.ScheduleSingleReminderAsync(new ReminderKey(id.Value), when, new ReminderPayload { Id = id });

    // ── Runs ──

    /// <summary>
    /// Waits for <paramref name="count"/> history rows, and then for the manager to finish the settlement
    /// that follows the append. The manager appends the row first and settles in the same message handler.
    /// </summary>
    protected async Task AwaitSettledRunAsync(ReminderId id, int count)
    {
        await AwaitAssertAsync(
            async () => Assert.Equal(count, (await History.ReadAsync(id, 50)).Count),
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        await BarrierAsync();
    }

    /// <summary>Waits until the scheduler has delivered <paramref name="count"/> envelopes, and the manager has handled them.</summary>
    protected async Task AwaitEnvelopesHandledAsync(int count)
    {
        await AwaitAssertAsync(
            () => Assert.Equal(count, Envelopes.Count),
            TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        await BarrierAsync();
    }

    /// <summary>
    /// Returns the execution id of the next run that the manager started. Use it with
    /// <see cref="RunMode.Hold"/>: the run stays active until the test completes it.
    /// </summary>
    protected async Task<Guid> AwaitDispatchAsync()
    {
        const string prefix = "ReminderExecution Dispatched: execution_id=";
        Guid found = Guid.Empty;
        await AwaitAssertAsync(() =>
        {
            var dispatches = _logs
                .Where(e => e.LogClass == typeof(ReminderExecutionActor)
                            && e.Message?.ToString() is { } text
                            && text.StartsWith(prefix, StringComparison.Ordinal))
                .Select(e => Guid.Parse(e.Message!.ToString()!.AsSpan(prefix.Length, 36)))
                .ToArray();
            Assert.True(dispatches.Length > _dispatchesTaken, "No execution has started.");
            found = dispatches[_dispatchesTaken++];
        }, TimeSpan.FromSeconds(10), cancellationToken: TestContext.Current.CancellationToken);
        return found;
    }

    /// <summary>
    /// Sends the outcome of an active run to the manager, as the execution actor does, and waits for
    /// the manager to settle it.
    /// </summary>
    protected async Task CompleteRunAsync(Guid executionId, ReminderId id, RunOutcome outcome)
    {
        var history = new HistoryRecord(
            Clock.GetUtcNow(),
            Success: outcome is RunOutcome.Success or RunOutcome.Denied,
            DurationMs: 10,
            SessionId: $"reminder/{executionId:N}",
            ErrorMessage: outcome switch
            {
                RunOutcome.Failure => FailureText,
                RunOutcome.Denied => DeniedText,
                _ => null
            },
            ToolDenied: outcome is RunOutcome.Denied);
        var accepted = await Manager.Ask<ReminderExecutionAccepted>(
            new ReminderExecutionCompleted(
                executionId, id, history.Success, history, history.ErrorMessage),
            AskTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(executionId, accepted.ExecutionId);
    }

    public enum RunOutcome
    {
        Success,
        Failure,
        Denied
    }

    // ── Tombstone files (design D1) ──

    protected string EndedDirectory => Path.Combine(Paths.RemindersDirectory, "ended");

    protected string TombstonePath(ReminderId id)
        => Path.Combine(EndedDirectory, $"{Uri.EscapeDataString(id.Value)}.json");

    protected bool TombstoneExists(ReminderId id) => File.Exists(TombstonePath(id));

    protected string[] TombstoneFiles()
        => Directory.Exists(EndedDirectory)
            ? Directory.EnumerateFiles(EndedDirectory, "*", SearchOption.AllDirectories).Order().ToArray()
            : [];

    /// <summary>Reads the tombstone file as JSON, or null when it does not exist.</summary>
    protected JsonElement? ReadTombstone(ReminderId id)
    {
        var path = TombstonePath(id);
        return File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone() : null;
    }

    protected JsonElement RequireTombstone(ReminderId id)
        => ReadTombstone(id) ?? throw new Xunit.Sdk.XunitException(
            $"No tombstone file for '{id.Value}'. Expected {TombstonePath(id)}. Files in ended/: [{string.Join(", ", TombstoneFiles().Select(Path.GetFileName))}].");

    protected static string? Text(JsonElement element, string name)
        => TryGet(element, name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

    protected static long? Number(JsonElement element, string name)
        => TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    protected static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    protected static string OutcomeOf(JsonElement tombstone) => Text(tombstone, "outcome")!;

    protected void WriteTombstone(
        ReminderDefinition definition,
        string outcome,
        DateTimeOffset endedAt,
        string? lastError = null,
        DateTimeOffset? fireAt = null,
        TrustAudience? audience = null,
        bool withInstructions = false)
    {
        var fields = new Dictionary<string, object?>
        {
            ["id"] = definition.Id.Value,
            ["title"] = definition.Title,
            ["audience"] = (audience ?? definition.Audience).ToString(),
            ["fireAtMs"] = (fireAt ?? definition.Schedule.FireAt!.Value).ToUnixTimeMilliseconds(),
            ["outcome"] = outcome,
            ["endedAtMs"] = endedAt.ToUnixTimeMilliseconds(),
            ["lastError"] = lastError
        };
        if (withInstructions)
            fields["instructions"] = definition.Instructions;
        Directory.CreateDirectory(EndedDirectory);
        File.WriteAllText(TombstonePath(definition.Id), JsonSerializer.Serialize(fields));
    }

    /// <summary>
    /// Seeds the on-disk state of an ended one-shot that another daemon run produced: a tombstone and a
    /// history file, and no definition.
    /// </summary>
    protected async Task SeedEndedAsync(
        ReminderDefinition definition,
        string outcome,
        DateTimeOffset? endedAt = null,
        string? lastError = null)
    {
        WriteTombstone(
            definition,
            outcome,
            endedAt ?? Clock.GetUtcNow(),
            lastError ?? (outcome == "Failed" ? FailureText : null),
            withInstructions: outcome == "Failed");
        await History.AppendAsync(
            definition.Id,
            new HistoryRecord(
                Clock.GetUtcNow(),
                outcome == "Completed",
                100,
                $"reminder/{definition.Id.Value}/1",
                outcome == "Failed" ? FailureText : null));
    }

    // ── Log events ──

    protected IReadOnlyList<LogEvent> LogsAbout(string text)
        => _logs.Where(e => e.Message?.ToString()?.Contains(text, StringComparison.Ordinal) == true).ToArray();

    protected IReadOnlyList<LogEvent> LogsOfLevel(Akka.Event.LogLevel level, string text)
        => LogsAbout(text).Where(e => e.LogLevel() == level).ToArray();

    // ── Test doubles ──

    public enum RunMode
    {
        Succeed,
        Fail,
        Deny,
        Hold
    }

    /// <summary>
    /// A session pipeline whose next runs succeed, fail, or hit a denied tool call. In <see cref="RunMode.Hold"/>
    /// a run stays active until the test releases it, so a test can change the reminder during the run.
    /// </summary>
    protected sealed class RunModePipeline : ISessionPipeline
    {
        private readonly TaskCompletionSource _hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invocationCount;

        public volatile RunMode Mode = RunMode.Succeed;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public void ReleaseHeldRuns() => _hold.TrySetCanceled();

        public async Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            switch (Mode)
            {
                case RunMode.Fail:
                    throw new InvalidOperationException(FailureText);
                case RunMode.Hold:
                    await _hold.Task.WaitAsync(cancellationToken);
                    throw new InvalidOperationException("The held run was released.");
                case RunMode.Deny:
                    return await new ReminderExecutionActorTests.ScriptedSessionPipeline(id =>
                    [
                        new ToolResultOutput
                        {
                            SessionId = id,
                            CallId = new ToolCallId("call-d"),
                            ToolName = new ToolName("shell_execute"),
                            Result = DeniedText,
                            FailureCode = ToolResultOutput.AccessDeniedFailureCode
                        },
                        new TextOutput("Done another way.") { SessionId = id },
                        new TurnCompleted { SessionId = id, TurnNumber = new TurnNumber(1) }
                    ]).CreateAsync(sessionId, options, materializer, cancellationToken);
                default:
                    return await new ReminderExecutionActorTests.ScriptedSessionPipeline(id =>
                    [
                        new TextOutput("Done.") { SessionId = id },
                        new TurnCompleted { SessionId = id, TurnNumber = new TurnNumber(1) }
                    ]).CreateAsync(sessionId, options, materializer, cancellationToken);
            }
        }

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            Task.FromResult<ISessionResponse>(CommandAck.For(feedback.SessionId));
    }

    /// <summary>The envelopes that the scheduler delivered, in order.</summary>
    protected sealed class EnvelopeTapProbe
    {
        private readonly object _sync = new();
        private readonly List<ReminderEnvelope<ReminderPayload>> _items = [];

        public int Count
        {
            get
            {
                lock (_sync)
                    return _items.Count;
            }
        }

        public ReminderEnvelope<ReminderPayload> Last
        {
            get
            {
                lock (_sync)
                    return _items[^1];
            }
        }

        internal void Add(ReminderEnvelope<ReminderPayload> envelope)
        {
            lock (_sync)
                _items.Add(envelope);
        }
    }

    /// <summary>
    /// Stands in for the shard region. It passes every message to its target, and it counts the
    /// envelopes after it passes them. The count therefore proves that the target already has the
    /// envelope in its mailbox.
    /// </summary>
    private sealed class EnvelopeTap : ReceiveActor
    {
        private IActorRef _target = ActorRefs.Nobody;

        public EnvelopeTap(EnvelopeTapProbe probe)
        {
            Receive<SetTarget>(set => _target = set.Target);
            ReceiveAny(message =>
            {
                _target.Forward(message);
                if (message is ReminderEnvelope<ReminderPayload> envelope)
                    probe.Add(envelope);
            });
        }

        public sealed record SetTarget(IActorRef Target) : INoSerializationVerificationNeeded;
    }

    private sealed class LogSink : ReceiveActor
    {
        public LogSink(ConcurrentQueue<LogEvent> queue) => Receive<LogEvent>(queue.Enqueue);
    }
}
