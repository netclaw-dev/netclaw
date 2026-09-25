// -----------------------------------------------------------------------
// <copyright file="GetReminderHistoryToolTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Akka.Reminders;
using Akka.Reminders.Sharding;
using Akka.Streams;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Tests.Hosting;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using static Netclaw.Actors.Reminders.ReminderProtocol;

namespace Netclaw.Actors.Tests.Reminders;

/// <summary>
/// Exercises <see cref="GetReminderHistoryTool"/> against a real
/// <see cref="ReminderManagerActor"/>, the same round trip production uses, so
/// the manager's audience check applies here too. See
/// <see cref="ReminderManagerActorTests"/> for actor-level coverage of that check.
/// </summary>
[Collection(ReminderActorTestCollection.Name)]
public class GetReminderHistoryToolTests : TestKit, IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly TestShardRegionResolver _sharedResolver = new();
    private ReminderDefinitionStore _definitionStore = null!;
    private ReminderHistoryStore _historyStore = null!;

    public GetReminderHistoryToolTests(ITestOutputHelper output) : base(output: output) { }

    void IDisposable.Dispose()
    {
        _dir.Dispose();
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore()
            .WithNetclawSerialization()
            .WithSerializationVerification();

        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        _definitionStore = new ReminderDefinitionStore(paths);
        _historyStore = new ReminderHistoryStore(paths);
        var definitionStore = _definitionStore;
        var historyStore = _historyStore;

        builder.WithLocalReminders(reminders =>
        {
            reminders.WithInMemoryStorage();
            reminders.WithResolver(_ => _sharedResolver);
            reminders.WithSettings(new ReminderSettings
            {
                AckTimeout = TimeSpan.FromMinutes(70),
                RetryBackoffBase = TimeSpan.FromMilliseconds(25),
                MaxRetryBackoff = TimeSpan.FromMilliseconds(25),
                MaxDeliveryAttempts = 10
            });
        });

        builder.StartActors((system, registry, _) =>
        {
            var defaults = new EffectivePolicyDefaults(
                DeploymentPosture.Team, TrustAudience.Team, ShellExecutionMode.Off, false);

            var reminderManager = system.ActorOf(
                Props.Create(() => new ReminderManagerActor(
                    new NotSupportedSessionPipeline(),
                    defaults,
                    new SchedulingConfig(),
                    TimeProvider.System,
                    definitionStore,
                    historyStore,
                    NullNotificationSink.Instance,
                    NullReminderChannelNotifier.Instance)),
                "reminder-manager-history-test");

            registry.Register<ReminderManagerActorKey>(reminderManager);
            _sharedResolver.RegisterShardRegion(ReminderManagerActor.ShardRegionName, reminderManager);
        });
    }

    private async Task<GetReminderHistoryTool> GetToolAsync()
    {
        var registry = ActorRegistry.For(Sys);
        var manager = registry.Get<ReminderManagerActorKey>();
        return new GetReminderHistoryTool(new SchedulingConfig(), manager);
    }

    /// <summary>
    /// Saves a definition directly to the store the manager reads from, so the
    /// history query below finds a real reminder to authorize against — a bare
    /// history record with no matching definition is always "not found."
    /// </summary>
    private void SaveDefinition(string idValue)
    {
        var now = TimeProvider.System.GetUtcNow();
        _definitionStore.Save(new ReminderDefinition
        {
            Id = new ReminderId(idValue),
            Title = idValue,
            Instructions = "test",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = now.AddHours(1)
            },
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    private static ToolExecutionContext CreateContext() =>
        TestToolExecutionContext.CreateUnbound(
            new TestToolExecutionContextOptions { Audience = TrustAudience.Personal });

    [Fact]
    public async Task Returns_empty_message_for_unknown_reminder_id()
    {
        var tool = await GetToolAsync();

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["ReminderId"] = "no-such-reminder" }, CreateContext(), TestContext.Current.CancellationToken);

        Assert.Contains("No execution history found", result);
        Assert.Contains("no-such-reminder", result);
    }

    [Fact]
    public async Task Returns_formatted_history_for_existing_reminder()
    {
        var tool = await GetToolAsync();
        var id = new ReminderId("daily-summary");
        SaveDefinition("daily-summary");
        await _historyStore.AppendAsync(id, new HistoryRecord(
            FiredAt: DateTimeOffset.UtcNow,
            Success: true,
            DurationMs: 4200,
            SessionId: "reminder/daily-summary/1741993200000",
            ErrorMessage: null));

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["ReminderId"] = "daily-summary" }, CreateContext(), TestContext.Current.CancellationToken);

        Assert.Contains("daily-summary", result);
        Assert.Contains("True", result);
        Assert.Contains("4200", result);
        Assert.Contains("reminder/daily-summary/1741993200000", result);
    }

    [Fact]
    public async Task Last_param_is_capped_at_100()
    {
        var tool = await GetToolAsync();
        var id = new ReminderId("busy-job");
        SaveDefinition("busy-job");
        // Store uses max 500, so add 150 records normally
        for (var i = 0; i < 150; i++)
            await _historyStore.AppendAsync(id, new HistoryRecord(
                FiredAt: DateTimeOffset.UtcNow,
                Success: true,
                DurationMs: i,
                SessionId: $"reminder/busy-job/{i}",
                ErrorMessage: null));

        // Request 200 — tool caps at 100
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["ReminderId"] = "busy-job", ["Last"] = 200 }, CreateContext(), TestContext.Current.CancellationToken);

        // Verify by counting "fired_at:" occurrences
        var lineCount = result.Split("fired_at:").Length - 1;
        Assert.True(lineCount <= 100, $"Expected at most 100 records, got {lineCount}");
    }

    [Fact]
    public async Task Error_message_included_for_failed_run()
    {
        var tool = await GetToolAsync();
        var id = new ReminderId("failing-job");
        SaveDefinition("failing-job");
        await _historyStore.AppendAsync(id, new HistoryRecord(
            FiredAt: DateTimeOffset.UtcNow,
            Success: false,
            DurationMs: 999,
            SessionId: "reminder/failing-job/999",
            ErrorMessage: "Notification tool returned an unspecified error."));

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["ReminderId"] = "failing-job" }, CreateContext(), TestContext.Current.CancellationToken);

        Assert.Contains("False", result);
        Assert.Contains("Notification tool returned an unspecified error.", result);
    }

    /// <summary>
    /// Fails any call — this suite never re-enters a session, so the pipeline
    /// must stay untouched. A real call here would signal a test setup bug.
    /// </summary>
    private sealed class NotSupportedSessionPipeline : ISessionPipeline
    {
        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            IMaterializer? materializer = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This test does not re-enter a session.");

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default) =>
            throw new NotSupportedException("This test does not send session feedback.");

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(
            IWithSessionId feedback,
            CancellationToken ct = default) =>
            throw new NotSupportedException("This test does not send session feedback.");
    }
}
