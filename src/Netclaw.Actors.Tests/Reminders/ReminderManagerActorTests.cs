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
    public ReminderManagerActorTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore();

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
                    new ReminderConfig(),
                    pipeline,
                    TimeProvider.System)),
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

        var payload = CreatePayload("test-list", "once", "Check status");

        var scheduled = await manager.Ask<ReminderScheduledResponse>(
            new ScheduleReminderCommand(payload), TimeSpan.FromSeconds(5));

        Assert.Equal("test-list", scheduled.Name);
        Assert.NotNull(scheduled.NextFire);

        var list = await manager.Ask<ReminderListResponse>(
            new ListRemindersCommand(), TimeSpan.FromSeconds(5));

        Assert.Single(list.Reminders);
        Assert.Equal("test-list", list.Reminders[0].Name);
        Assert.Equal("Check status", list.Reminders[0].Prompt);
    }

    [Fact]
    public async Task Cancel_existing_reminder_returns_found()
    {
        var manager = await GetManagerAsync();

        var payload = CreatePayload("test-cancel", "once", "Check it");

        await manager.Ask<ReminderScheduledResponse>(
            new ScheduleReminderCommand(payload), TimeSpan.FromSeconds(5));

        var cancelled = await manager.Ask<ReminderCancelledResponse>(
            new CancelReminderCommand(payload.Id), TimeSpan.FromSeconds(5));

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

    private static ReminderPayload CreatePayload(string name, string scheduleType, string prompt)
    {
        var id = new ReminderId($"{name}-{Guid.NewGuid():N}"[..20]);
        var now = TimeProvider.System.GetUtcNow();

        return new ReminderPayload
        {
            Id = id,
            Name = name,
            Prompt = prompt,
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = now.AddHours(1)
            },
            CreatedAt = now
        };
    }
}
