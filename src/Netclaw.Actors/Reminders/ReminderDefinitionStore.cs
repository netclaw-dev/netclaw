// -----------------------------------------------------------------------
// <copyright file="ReminderDefinitionStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Configuration;

namespace Netclaw.Actors.Reminders;

/// <summary>
/// File-backed reminder definition store.
///
/// Reminder schedules are durable in Akka.Reminders (SQLite), but the execution
/// contract and mutable task content are sourced from these JSON files.
/// Scheduled messages only carry a pointer (reminder ID).
/// </summary>
public sealed class ReminderDefinitionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly object _sync = new();
    private readonly List<DroppedInvalidReminderDefinition> _droppedInvalidDefinitions = [];

    public ReminderDefinitionStore(NetclawPaths paths)
    {
        _directory = paths.RemindersDirectory;
        Directory.CreateDirectory(_directory);
        PruneInvalidDefinitions();
    }

    /// <summary>
    /// Returns and clears reminder definitions dropped due to invalid schema.
    /// </summary>
    public IReadOnlyList<DroppedInvalidReminderDefinition> ConsumeDroppedInvalidDefinitions()
    {
        lock (_sync)
        {
            if (_droppedInvalidDefinitions.Count == 0)
                return [];

            var snapshot = _droppedInvalidDefinitions.ToArray();
            _droppedInvalidDefinitions.Clear();
            return snapshot;
        }
    }

    public bool Exists(ReminderId id)
    {
        lock (_sync)
            return File.Exists(GetPath(id));
    }

    public ReminderDefinition? Get(ReminderId id)
    {
        lock (_sync)
        {
            var path = GetPath(id);
            if (!File.Exists(path))
                return null;

            var result = TryReadDefinition(path);
            if (result.Definition is not null)
                return result.Definition;

            if (result.ShouldDelete)
                DeleteInvalidDefinition(path, result.ErrorMessage ?? "invalid reminder definition");

            return null;
        }
    }

    public IReadOnlyList<ReminderDefinition> List()
    {
        lock (_sync)
        {
            var list = new List<ReminderDefinition>();
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                var result = TryReadDefinition(file);
                if (result.Definition is not null)
                {
                    list.Add(result.Definition);
                    continue;
                }

                if (result.ShouldDelete)
                    DeleteInvalidDefinition(file, result.ErrorMessage ?? "invalid reminder definition");
            }

            return list;
        }
    }

    public void Save(ReminderDefinition definition)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(_directory);

            var path = GetPath(new ReminderId(definition.Id));
            var tempPath = $"{path}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(definition, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
    }

    public bool Delete(ReminderId id)
    {
        lock (_sync)
        {
            var path = GetPath(id);
            if (!File.Exists(path))
                return false;

            File.Delete(path);
            return true;
        }
    }

    private string GetPath(ReminderId id)
    {
        var encoded = Uri.EscapeDataString(id.Value);
        return Path.Combine(_directory, $"{encoded}.json");
    }

    private void PruneInvalidDefinitions()
    {
        lock (_sync)
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                var result = TryReadDefinition(file);
                if (result.Definition is not null || !result.ShouldDelete)
                    continue;

                DeleteInvalidDefinition(file, result.ErrorMessage ?? "invalid reminder definition");
            }
        }
    }

    private void DeleteInvalidDefinition(string path, string reason)
    {
        var reminderId = DecodeReminderIdFromPath(path);
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            reason = $"{reason}; failed to delete file: {ex.Message}";
        }

        _droppedInvalidDefinitions.Add(new DroppedInvalidReminderDefinition(reminderId, reason));
    }

    private static string DecodeReminderIdFromPath(string path)
    {
        var encoded = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(encoded))
            return "unknown";

        try
        {
            return Uri.UnescapeDataString(encoded);
        }
        catch
        {
            return encoded;
        }
    }

    private static ReadResult TryReadDefinition(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var definition = JsonSerializer.Deserialize<ReminderDefinition>(text, JsonOptions);
            if (definition is null || string.IsNullOrWhiteSpace(definition.Id))
            {
                return new ReadResult(
                    null,
                    "reminder definition is missing required field 'id'",
                    ShouldDelete: true);
            }

            return new ReadResult(definition, null, ShouldDelete: false);
        }
        catch (JsonException ex)
        {
            return new ReadResult(null, ex.Message, ShouldDelete: true);
        }
        catch (NotSupportedException ex)
        {
            return new ReadResult(null, ex.Message, ShouldDelete: true);
        }
        catch
        {
            return new ReadResult(null, null, ShouldDelete: false);
        }
    }

    private sealed record ReadResult(ReminderDefinition? Definition, string? ErrorMessage, bool ShouldDelete);
}

public sealed record DroppedInvalidReminderDefinition(string ReminderId, string Reason);
