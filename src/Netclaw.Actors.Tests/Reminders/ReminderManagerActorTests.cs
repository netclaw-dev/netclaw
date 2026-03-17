using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Akka.Reminders;
using Akka.Reminders.Sharding;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Netclaw.Actors.Tests.Reminders;

public class ReminderManagerActorTests : TestKit
{
    private readonly string _basePath = Path.Combine(Path.GetTempPath(), $"netclaw-reminder-tests-{Guid.NewGuid():N}");

    public ReminderManagerActorTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore();

        var paths = new NetclawPaths(_basePath);
        paths.EnsureDirectoriesExist();
        var reminderConfig = new ReminderConfig();
        var definitionStore = new ReminderDefinitionStore(paths);
        var historyStore = new ReminderHistoryStore(paths, reminderConfig);

        // Wire local reminders with in-memory storage
        var sharedResolver = new TestShardRegionResolver();
        builder.WithLocalReminders(reminders =>
        {
            reminders.WithInMemoryStorage();
            reminders.WithResolver(_ => sharedResolver);
        });

        builder.StartActors((system, registry, _) =>
        {
            // Create a minimal SessionPipeline stub — manager needs it but
            // we won't actually execute reminders in these tests.
            registry.Register<SessionManagerActorKey>(system.DeadLetters);

            var pipeline = new SessionPipeline(
                system,
                new RequiredActor<SessionManagerActorKey>(ActorRegistry.For(system)));

            var reminderManager = system.ActorOf(
                Props.Create(() => new ReminderManagerActor(
                    reminderConfig,
                    pipeline,
                    TimeProvider.System,
                    definitionStore,
                    historyStore)),
                "reminder-manager-test");

            registry.Register<ReminderManagerActorKey>(reminderManager);
            sharedResolver.RegisterShardRegion(ReminderManagerActor.ShardRegionName, reminderManager);
        });
    }

    private async Task<IActorRef> GetManagerAsync()
    {
        var registry = ActorRegistry.For(Sys);
        return registry.Get<ReminderManagerActorKey>();
    }

    [Fact]
    public async Task Schedule_and_list_returns_reminder()
    {
        var manager = await GetManagerAsync();

        var definition = CreateDefinition("test-list", "Check status");

        var scheduled = await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(definition), TimeSpan.FromSeconds(5));

        Assert.Equal("test-list", scheduled.Title);
        Assert.NotNull(scheduled.NextFire);

        var list = await manager.Ask<ReminderListResponse>(
            new ListRemindersCommand(), TimeSpan.FromSeconds(5));

        Assert.Single(list.Reminders);
        Assert.Equal("test-list", list.Reminders[0].Title);
        Assert.Equal("Check status", list.Reminders[0].Instructions);
    }

    [Fact]
    public async Task Cancel_existing_reminder_returns_found()
    {
        var manager = await GetManagerAsync();

        var definition = CreateDefinition("test-cancel", "Check it");

        await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(definition), TimeSpan.FromSeconds(5));

        var cancelled = await manager.Ask<ReminderCancelledResponse>(
            new CancelReminderCommand(new ReminderId(definition.Id)), TimeSpan.FromSeconds(5));

        Assert.True(cancelled.Found);
    }

    [Fact]
    public async Task Cancel_nonexistent_returns_not_found()
    {
        var manager = await GetManagerAsync();

        var cancelled = await manager.Ask<ReminderCancelledResponse>(
            new CancelReminderCommand(new ReminderId("does-not-exist")),
            TimeSpan.FromSeconds(5));

        Assert.False(cancelled.Found);
    }

    [Fact]
    public async Task Health_query_returns_scheduled_count()
    {
        var manager = await GetManagerAsync();

        var definition = CreateDefinition("test-health", "Check health");

        await manager.Ask<ReminderSavedResponse>(
            new SaveReminderCommand(definition), TimeSpan.FromSeconds(5));

        var health = await manager.Ask<ReminderHealthResponse>(
            GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(5));

        Assert.Equal(1, health.ScheduledCount);
        Assert.Equal(0, health.ActiveExecutions);
        Assert.Equal(0, health.FailedCount);
    }

    [Fact]
    public async Task Health_query_on_empty_manager_returns_zeros()
    {
        var manager = await GetManagerAsync();

        var health = await manager.Ask<ReminderHealthResponse>(
            GetReminderHealthQuery.Instance, TimeSpan.FromSeconds(5));

        Assert.Equal(0, health.ScheduledCount);
        Assert.Equal(0, health.ActiveExecutions);
        Assert.Equal(0, health.FailedCount);
    }

    private static ReminderDefinition CreateDefinition(string name, string instructions)
    {
        var id = new ReminderId($"{name}-{Guid.NewGuid():N}"[..20]);
        var now = TimeProvider.System.GetUtcNow();

        return new ReminderDefinition
        {
            Id = id.Value,
            Title = name,
            Instructions = instructions,
            NotifyInstructions = "Reply in-thread with concise status.",
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = now.AddHours(1)
            },
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        };
    }
}
