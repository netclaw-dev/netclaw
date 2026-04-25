using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.Persistence.Hosting;
using Netclaw.Actors.Reminders;
using Xunit;

namespace Netclaw.Actors.Tests.Reminders;

public class CompleteReminderToolTests : TestKit
{
    public CompleteReminderToolTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder
            .WithInMemoryJournal()
            .WithInMemorySnapshotStore();
    }

    [Fact]
    public async Task Complete_sends_disable_command_and_returns_success()
    {
        var probe = CreateTestProbe();
        var tool = new CompleteReminderTool(probe);

        var execution = Task.Run(async () =>
        {
            return await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["ReminderId"] = "daily-check"
            });
        });

        var cmd = await probe.ExpectMsgAsync<DisableReminderCommand>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("daily-check", cmd.Id.Value);

        probe.Reply(new ReminderStateResponse(
            new ReminderId("daily-check"), Found: true, Enabled: false));

        var result = await execution;
        Assert.Contains("marked as completed", result);
    }

    [Fact]
    public async Task Complete_returns_not_found_for_missing_id()
    {
        var probe = CreateTestProbe();
        var tool = new CompleteReminderTool(probe);

        var execution = Task.Run(async () =>
        {
            return await tool.ExecuteAsync(new Dictionary<string, object?>
            {
                ["ReminderId"] = "does-not-exist"
            });
        });

        var cmd = await probe.ExpectMsgAsync<DisableReminderCommand>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);

        probe.Reply(new ReminderStateResponse(
            new ReminderId("does-not-exist"), Found: false, Enabled: false));

        var result = await execution;
        Assert.Contains("not found", result);
    }

    [Fact]
    public async Task Complete_rejects_empty_reminder_id()
    {
        var probe = CreateTestProbe();
        var tool = new CompleteReminderTool(probe);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["ReminderId"] = ""
        }, TestContext.Current.CancellationToken);

        Assert.Contains("Error:", result);
        await probe.ExpectNoMsgAsync(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
    }
}
