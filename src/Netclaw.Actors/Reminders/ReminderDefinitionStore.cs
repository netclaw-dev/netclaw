// -----------------------------------------------------------------------
// <copyright file="ReminderDefinitionStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Persistence;
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
    private readonly Dictionary<string, RejectedLegacyReminderDefinition> _rejectedLegacyDefinitions =
        new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    public ReminderDefinitionStore(NetclawPaths paths, ILogger<ReminderDefinitionStore>? logger = null)
    {
        _directory = paths.RemindersDirectory;
        _logger = logger ?? NullLogger<ReminderDefinitionStore>.Instance;
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

    /// <summary>
    /// Returns and clears reminder definitions rejected because they predate the
    /// required trust-field schema.
    /// </summary>
    public IReadOnlyList<RejectedLegacyReminderDefinition> ConsumeRejectedLegacyDefinitions()
    {
        lock (_sync)
        {
            if (_rejectedLegacyDefinitions.Count == 0)
                return [];

            var snapshot = _rejectedLegacyDefinitions.Values.ToArray();
            _rejectedLegacyDefinitions.Clear();
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

            var path = GetPath(definition.Id);
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
        // Uri.EscapeDataString escapes path separators and absolute-path
        // markers (/, \, :, control chars), so the encoded value cannot encode a
        // traversal. The post-canonicalization containment check below is the
        // belt-and-suspenders that CodeQL also recognizes as a path sanitizer.
        // (cs/path-injection)
        var encoded = Uri.EscapeDataString(id.Value);
        var baseDir = Path.GetFullPath(_directory);
        var candidate = Path.GetFullPath(Path.Combine(baseDir, $"{encoded}.json"));
        var baseWithSep = baseDir.EndsWith(Path.DirectorySeparatorChar)
            ? baseDir
            : baseDir + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(baseWithSep, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Reminder id '{id.Value}' resolves outside the reminders directory.",
                nameof(id));
        return candidate;
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

    private void RecordRejectedLegacyDefinition(string path, string reason)
    {
        var reminderId = DecodeReminderIdFromPath(path);
        _rejectedLegacyDefinitions[reminderId] = new RejectedLegacyReminderDefinition(reminderId, reason);
    }

    private ReadResult TryReadDefinition(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            // A pre-#994 reminder document with no persisted trust context cannot
            // be run safely. Reject it loudly without coercing a substitute
            // audience, and keep the file — it is operator-authored data, not
            // corrupt JSON, so the operator can repair or remove it.
            var missingTrustFields = LegacyTrustFieldGuard.MissingTrustFields(text);
            if (missingTrustFields.Count > 0)
            {
                var fields = string.Join(", ", missingTrustFields);
                _logger.LogError(
                    "Reminder document {Path} predates issue #994 and is missing required "
                    + "trust field(s): {MissingFields}. The reminder will not be loaded or "
                    + "scheduled — a reminder with no persisted audience cannot be run safely. "
                    + "Recreate the reminder or remove the file.",
                    path, fields);
                RecordRejectedLegacyDefinition(path, $"missing trust field(s): {fields}");
                return new ReadResult(null, $"missing trust field(s): {fields}", ShouldDelete: false);
            }

            var definition = JsonSerializer.Deserialize<ReminderDefinition>(text, JsonOptions);
            if (definition is null || string.IsNullOrWhiteSpace(definition.Id.Value))
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
public sealed record RejectedLegacyReminderDefinition(string ReminderId, string Reason);
