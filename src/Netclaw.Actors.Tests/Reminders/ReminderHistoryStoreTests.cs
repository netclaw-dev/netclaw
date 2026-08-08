// -----------------------------------------------------------------------
// <copyright file="ReminderHistoryStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.Reminders;

public class ReminderHistoryStoreTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly ReminderDefinitionStore _definitionStore;
    private readonly ReminderHistoryStore _store;
    private static readonly ReminderId TestId = new("test-reminder");

    public ReminderHistoryStoreTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        Directory.CreateDirectory(_paths.RemindersDirectory);
        _definitionStore = new ReminderDefinitionStore(_paths);
        _definitionStore.Save(CreateDefinition(TestId));
        _store = new ReminderHistoryStore(_definitionStore);
    }

    public void Dispose()
    {
        _dir.Dispose();
    }

    [Fact]
    public async Task Append_creates_file_and_stores_record()
    {
        var record = MakeRecord(true);

        await _store.AppendAsync(TestId, record);

        var records = await _store.ReadAsync(TestId, 10);
        Assert.Single(records);
        Assert.Equal(record.SessionId, records[0].SessionId);
        Assert.True(records[0].Success);
    }

    [Fact]
    public async Task Read_returns_empty_list_when_file_absent()
    {
        var records = await _store.ReadAsync(new ReminderId("does-not-exist"), 10);
        Assert.Empty(records);
    }

    [Fact]
    public async Task Append_multiple_records_preserves_order()
    {
        for (var i = 0; i < 3; i++)
            await _store.AppendAsync(TestId, MakeRecord(true, $"session-{i}"));

        var records = await _store.ReadAsync(TestId, 10);
        Assert.Equal(3, records.Count);
        Assert.Equal("session-0", records[0].SessionId);
        Assert.Equal("session-2", records[2].SessionId);
    }

    [Fact]
    public async Task Delete_removes_history_file()
    {
        await _store.AppendAsync(TestId, MakeRecord(true));

        _store.DeleteHistory(TestId);

        var records = await _store.ReadAsync(TestId, 10);
        Assert.Empty(records);
    }

    [Fact]
    public void Delete_is_idempotent_when_file_absent()
    {
        // Should not throw
        _store.DeleteHistory(new ReminderId("never-existed"));
    }

    [Fact]
    public async Task Read_respects_maxRecords_limit()
    {
        for (var i = 0; i < 5; i++)
            await _store.AppendAsync(TestId, MakeRecord(true, $"session-{i}"));

        var records = await _store.ReadAsync(TestId, 3);
        Assert.Equal(3, records.Count);
        // Should return the 3 newest
        Assert.Equal("session-2", records[0].SessionId);
        Assert.Equal("session-4", records[2].SessionId);
    }

    [Fact]
    public async Task New_current_session_history_is_beside_its_session_definition()
    {
        var id = new ReminderId("session-history");
        var definition = CreateDefinition(id) with
        {
            Delivery = new ReminderDelivery
            {
                Kind = DeliveryKind.CurrentSession,
                SessionId = "C0ABC/1712000000.000001"
            }
        };
        _definitionStore.Save(definition);

        await _store.AppendAsync(id, MakeRecord(true));

        var directory = SessionDirectoryHelper.GetSessionRemindersDirectory(
            new SessionId(definition.Delivery.SessionId!),
            _paths.SessionsDirectory);
        Assert.True(File.Exists(Path.Combine(directory, "session-history.json")));
        Assert.True(File.Exists(Path.Combine(directory, "session-history.history.jsonl")));
        Assert.False(File.Exists(Path.Combine(_paths.RemindersDirectory, "session-history.history.jsonl")));
    }

    [Fact]
    public async Task Existing_current_session_history_stays_beside_its_daemon_definition()
    {
        var id = new ReminderId("existing-session-history");
        var definition = CreateDefinition(id) with
        {
            Delivery = new ReminderDelivery
            {
                Kind = DeliveryKind.CurrentSession,
                SessionId = "C0ABC/1712000000.000001"
            }
        };
        var definitionPath = Path.Combine(_paths.RemindersDirectory, "existing-session-history.json");
        WriteDefinition(definitionPath, definition);
        var store = new ReminderHistoryStore(new ReminderDefinitionStore(_paths));

        await store.AppendAsync(id, MakeRecord(true));

        var daemonHistoryPath = Path.Combine(
            _paths.RemindersDirectory,
            "existing-session-history.history.jsonl");
        var sessionDirectory = SessionDirectoryHelper.GetSessionRemindersDirectory(
            new SessionId(definition.Delivery.SessionId!),
            _paths.SessionsDirectory);
        Assert.True(File.Exists(definitionPath));
        Assert.True(File.Exists(daemonHistoryPath));
        Assert.False(File.Exists(Path.Combine(sessionDirectory, "existing-session-history.history.jsonl")));
        Assert.Single(await store.ReadAsync(id, 10));
    }

    private static HistoryRecord MakeRecord(bool success, string? sessionId = null) =>
        new(
            FiredAt: DateTimeOffset.UtcNow,
            Success: success,
            DurationMs: 1234,
            SessionId: sessionId ?? "reminder/test-reminder/1234567890",
            ErrorMessage: success ? null : "test error");

    private static ReminderDefinition CreateDefinition(ReminderId id)
    {
        var now = TimeProvider.System.GetUtcNow();
        return new ReminderDefinition
        {
            Id = id,
            Title = id.Value,
            Instructions = "Run the test reminder.",
            Delivery = new ReminderDelivery { Kind = DeliveryKind.None },
            Schedule = new ReminderSchedule { Type = ReminderScheduleType.OneShot, FireAt = now.AddHours(1) },
            Audience = TrustAudience.Personal,
            Boundary = TrustBoundary.Personal,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static void WriteDefinition(string path, ReminderDefinition definition)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(definition, options));
    }
}
