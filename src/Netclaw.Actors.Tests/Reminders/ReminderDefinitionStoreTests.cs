// -----------------------------------------------------------------------
// <copyright file="ReminderDefinitionStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Reminders;

public sealed class ReminderDefinitionStoreTests : IDisposable
{
    private readonly string _basePath = Path.Combine(Path.GetTempPath(), $"netclaw-reminder-store-tests-{Guid.NewGuid():N}");
    private readonly NetclawPaths _paths;

    public ReminderDefinitionStoreTests()
    {
        _paths = new NetclawPaths(_basePath);
        _paths.EnsureDirectoriesExist();
    }

    [Fact]
    public void Constructor_prunes_invalid_json_and_records_dropped_definition()
    {
        var reminderId = "legacy-reminder";
        var filePath = Path.Combine(_paths.RemindersDirectory, $"{Uri.EscapeDataString(reminderId)}.json");
        File.WriteAllText(filePath, "{ this is invalid json }");

        var store = new ReminderDefinitionStore(_paths);

        Assert.False(File.Exists(filePath));

        var dropped = store.ConsumeDroppedInvalidDefinitions();
        var entry = Assert.Single(dropped);
        Assert.Equal(reminderId, entry.ReminderId);
        Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
        Assert.Empty(store.ConsumeDroppedInvalidDefinitions());
    }

    [Fact]
    public void Constructor_keeps_valid_definitions_while_pruning_invalid_files()
    {
        var seededStore = new ReminderDefinitionStore(_paths);
        var now = TimeProvider.System.GetUtcNow();

        seededStore.Save(new ReminderDefinition
        {
            Id = "valid-reminder",
            Title = "valid-reminder",
            Instructions = "check status",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule
            {
                Type = ReminderScheduleType.OneShot,
                FireAt = now.AddMinutes(30)
            },
            Enabled = true,
            CreatedBy = "test",
            CreatedAt = now,
            UpdatedAt = now
        });

        var invalidPath = Path.Combine(_paths.RemindersDirectory, $"{Uri.EscapeDataString("bad-reminder")}.json");
        File.WriteAllText(invalidPath, "not json");

        var reloadedStore = new ReminderDefinitionStore(_paths);
        var reminders = reloadedStore.List();

        Assert.Single(reminders);
        Assert.Equal("valid-reminder", reminders[0].Id);
        Assert.False(File.Exists(invalidPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_basePath))
            Directory.Delete(_basePath, recursive: true);
    }
}
