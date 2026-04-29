// -----------------------------------------------------------------------
// <copyright file="ReminderHistoryStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Configuration;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// File-backed per-reminder execution history store.
///
/// Each reminder gets an append-only <c>{id}.history.jsonl</c> file alongside
/// its definition file. Entries are newline-delimited JSON objects, one per line.
/// When the record count would exceed <see cref="MaxRecords"/>, the oldest
/// entries are trimmed via an atomic tmp-file rename.
///
/// Single-writer per reminder ID is assumed — enforced by the concurrency gate in
/// <see cref="ReminderManagerActor"/> (at most one execution per reminder at a time).
/// </summary>
public sealed class ReminderHistoryStore
{
    /// <summary>
    /// Maximum number of execution history records retained per reminder.
    /// Not operator-configurable — if we ever need to tune this, add a knob then.
    /// </summary>
    internal const int MaxRecords = 500;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;

    public ReminderHistoryStore(NetclawPaths paths)
    {
        _directory = paths.RemindersDirectory;
        Directory.CreateDirectory(_directory);
    }

    /// <summary>
    /// Appends <paramref name="record"/> to <c>{id}.history.jsonl</c>.
    /// If the resulting count would exceed the cap, the oldest entries are trimmed atomically.
    /// Throws on I/O failure — callers should catch and log without propagating.
    /// </summary>
    public async Task AppendAsync(ReminderId id, HistoryRecord record)
    {
        var path = GetHistoryPath(id);
        var line = JsonSerializer.Serialize(record, JsonOptions);

        if (!File.Exists(path))
        {
            await File.WriteAllTextAsync(path, line + '\n');
            return;
        }

        var existingLines = await File.ReadAllLinesAsync(path);
        // Filter empty lines that may result from previous writes
        var nonEmpty = existingLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

        if (nonEmpty.Length < MaxRecords)
        {
            await File.AppendAllTextAsync(path, line + '\n');
        }
        else
        {
            // Trim: keep last (max - 1) existing entries, then append new one
            var kept = nonEmpty.Length > MaxRecords - 1
                ? nonEmpty[^(MaxRecords - 1)..]
                : nonEmpty;

            var tmpPath = $"{path}.tmp";
            var allLines = new string[kept.Length + 1];
            kept.CopyTo(allLines, 0);
            allLines[^1] = line;

            await File.WriteAllLinesAsync(tmpPath, allLines);
            File.Move(tmpPath, path, overwrite: true);
        }
    }

    /// <summary>
    /// Returns up to <paramref name="maxRecords"/> most recent history entries for
    /// <paramref name="id"/>. Returns an empty list if no history file exists.
    /// </summary>
    public async Task<IReadOnlyList<HistoryRecord>> ReadAsync(ReminderId id, int maxRecords)
    {
        var path = GetHistoryPath(id);
        if (!File.Exists(path))
            return [];

        try
        {
            var lines = await File.ReadAllLinesAsync(path);
            var slice = lines.Length > maxRecords ? lines[^maxRecords..] : lines;

            var records = new List<HistoryRecord>(slice.Length);
            foreach (var line in slice)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var record = JsonSerializer.Deserialize<HistoryRecord>(line, JsonOptions);
                if (record is not null)
                    records.Add(record);
            }

            return records;
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Deletes the history file for <paramref name="id"/> if it exists.
    /// Silently succeeds if the file is absent.
    /// </summary>
    public void DeleteHistory(ReminderId id)
    {
        var path = GetHistoryPath(id);
        if (File.Exists(path))
            File.Delete(path);
    }

    private string GetHistoryPath(ReminderId id)
    {
        var encoded = Uri.EscapeDataString(id.Value);
        return Path.Combine(_directory, $"{encoded}.history.jsonl");
    }
}
