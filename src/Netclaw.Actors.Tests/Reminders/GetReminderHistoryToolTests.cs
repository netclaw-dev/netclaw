using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Reminders;

public class GetReminderHistoryToolTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-history-tool-tests-{Guid.NewGuid():N}");
    private readonly ReminderHistoryStore _store;
    private readonly GetReminderHistoryTool _tool;

    public GetReminderHistoryToolTests()
    {
        Directory.CreateDirectory(_tempDir);
        var paths = new NetclawPaths(_tempDir);
        Directory.CreateDirectory(paths.RemindersDirectory);
        _store = new ReminderHistoryStore(paths);
        _tool = new GetReminderHistoryTool(_store);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task Returns_empty_message_for_unknown_reminder_id()
    {
        var result = await _tool.ExecuteAsync(
            new Dictionary<string, object?> { ["ReminderId"] = "no-such-reminder" }, TestContext.Current.CancellationToken);

        Assert.Contains("No execution history found", result);
        Assert.Contains("no-such-reminder", result);
    }

    [Fact]
    public async Task Returns_formatted_history_for_existing_reminder()
    {
        var id = new ReminderId("daily-summary");
        await _store.AppendAsync(id, new HistoryRecord(
            FiredAt: DateTimeOffset.UtcNow,
            Success: true,
            DurationMs: 4200,
            SessionId: "reminder/daily-summary/1741993200000",
            ErrorMessage: null));

        var result = await _tool.ExecuteAsync(
            new Dictionary<string, object?> { ["ReminderId"] = "daily-summary" }, TestContext.Current.CancellationToken);

        Assert.Contains("daily-summary", result);
        Assert.Contains("True", result);
        Assert.Contains("4200", result);
        Assert.Contains("reminder/daily-summary/1741993200000", result);
    }

    [Fact]
    public async Task Last_param_is_capped_at_100()
    {
        var id = new ReminderId("busy-job");
        // Store uses max 500, so add 150 records normally
        for (var i = 0; i < 150; i++)
            await _store.AppendAsync(id, new HistoryRecord(
                FiredAt: DateTimeOffset.UtcNow,
                Success: true,
                DurationMs: i,
                SessionId: $"reminder/busy-job/{i}",
                ErrorMessage: null));

        // Request 200 — tool caps at 100
        var result = await _tool.ExecuteAsync(
            new Dictionary<string, object?> { ["ReminderId"] = "busy-job", ["Last"] = 200 }, TestContext.Current.CancellationToken);

        // Verify by counting "fired_at:" occurrences
        var lineCount = result.Split("fired_at:").Length - 1;
        Assert.True(lineCount <= 100, $"Expected at most 100 records, got {lineCount}");
    }

    [Fact]
    public async Task Error_message_included_for_failed_run()
    {
        var id = new ReminderId("failing-job");
        await _store.AppendAsync(id, new HistoryRecord(
            FiredAt: DateTimeOffset.UtcNow,
            Success: false,
            DurationMs: 999,
            SessionId: "reminder/failing-job/999",
            ErrorMessage: "Notification tool returned an unspecified error."));

        var result = await _tool.ExecuteAsync(
            new Dictionary<string, object?> { ["ReminderId"] = "failing-job" }, TestContext.Current.CancellationToken);

        Assert.Contains("False", result);
        Assert.Contains("Notification tool returned an unspecified error.", result);
    }
}
