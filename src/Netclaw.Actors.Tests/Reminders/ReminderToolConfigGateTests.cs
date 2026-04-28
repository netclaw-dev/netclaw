// -----------------------------------------------------------------------
// <copyright file="ReminderToolConfigGateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Reminders;

/// <summary>
/// Tests that all four reminder tools respect the SchedulingConfig.Enabled gate.
/// When disabled, each tool must return a config-disabled error without touching the actor system.
/// </summary>
public class ReminderToolConfigGateTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-reminder-gate-{Guid.NewGuid():N}");
    private readonly SchedulingConfig _disabledConfig = new() { Enabled = false };

    public ReminderToolConfigGateTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task SetReminderTool_ReturnsErrorWhenSchedulingDisabled()
    {
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 3, 5, 12, 0, 0, TimeSpan.Zero));
        // Pass null! for reminderManager — the tool must return before touching the actor
        var tool = new SetReminderTool(reminderManager: null!, timeProvider, _disabledConfig);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Id"] = "test-reminder",
            ["Name"] = "test-reminder",
            ["Prompt"] = "Check the server",
            ["ScheduleType"] = "once",
            ["Schedule"] = "30m",
            ["DeliveryKind"] = "none"
        }, TestContext.Current.CancellationToken);

        Assert.Contains("Scheduling is disabled", result);
    }

    [Fact]
    public async Task CancelReminderTool_ReturnsErrorWhenSchedulingDisabled()
    {
        var tool = new CancelReminderTool(reminderManager: null!, _disabledConfig);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["ReminderId"] = "test-reminder" },
            TestContext.Current.CancellationToken);

        Assert.Contains("Scheduling is disabled", result);
    }

    [Fact]
    public async Task ListRemindersTool_ReturnsErrorWhenSchedulingDisabled()
    {
        var tool = new ListRemindersTool(reminderManager: null!, _disabledConfig);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Filter"] = "active" },
            TestContext.Current.CancellationToken);

        Assert.Contains("Scheduling is disabled", result);
    }

    [Fact]
    public async Task GetReminderHistoryTool_ReturnsErrorWhenSchedulingDisabled()
    {
        var paths = new NetclawPaths(_tempDir);
        Directory.CreateDirectory(paths.RemindersDirectory);
        var store = new ReminderHistoryStore(paths);
        var tool = new GetReminderHistoryTool(store, _disabledConfig);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["ReminderId"] = "test-reminder" },
            TestContext.Current.CancellationToken);

        Assert.Contains("Scheduling is disabled", result);
    }
}
