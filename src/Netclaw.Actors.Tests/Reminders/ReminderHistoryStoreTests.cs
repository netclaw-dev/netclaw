// -----------------------------------------------------------------------
// <copyright file="ReminderHistoryStoreTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.Tests.Reminders;

public class ReminderHistoryStoreTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly ReminderHistoryStore _store;
    private static readonly ReminderId TestId = new("test-reminder");

    public ReminderHistoryStoreTests()
    {
        var paths = new NetclawPaths(_dir.Path);
        Directory.CreateDirectory(paths.RemindersDirectory);
        _store = new ReminderHistoryStore(paths);
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

    private static HistoryRecord MakeRecord(bool success, string? sessionId = null) =>
        new(
            FiredAt: DateTimeOffset.UtcNow,
            Success: success,
            DurationMs: 1234,
            SessionId: sessionId ?? "reminder/test-reminder/1234567890",
            ErrorMessage: success ? null : "test error");
}
