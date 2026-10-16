// -----------------------------------------------------------------------
// <copyright file="ReminderDefinitionStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
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
/// <para>
/// The store also owns the <see cref="ReminderTombstone"/> files of ended one-shots, in the
/// <c>ended/</c> subfolder. The subfolder is a constraint: <see cref="List"/> reads every top-level
/// <c>*.json</c> file, so a tombstone at the top level would be parsed as a definition and then
/// deleted as invalid. An older binary also reads only the top level, so a downgrade ignores the folder.
/// </para>
/// </summary>
public sealed class ReminderDefinitionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private const string EndedFolderName = "ended";

    private readonly string _directory;
    private readonly string _endedDirectory;
    private readonly object _sync = new();
    private readonly List<DroppedInvalidReminderDefinition> _droppedInvalidDefinitions = [];
    private readonly Dictionary<string, RejectedLegacyReminderDefinition> _rejectedLegacyDefinitions =
        new(StringComparer.Ordinal);
    private readonly List<InvalidReminderTombstone> _invalidTombstones = [];
    private readonly ILogger _logger;

    public ReminderDefinitionStore(NetclawPaths paths, ILogger<ReminderDefinitionStore>? logger = null)
    {
        _directory = paths.RemindersDirectory;
        _endedDirectory = Path.Combine(_directory, EndedFolderName);
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

    /// <summary>
    /// Returns and clears the tombstone files that could not be read. A tombstone that Netclaw cannot read
    /// counts as absent. The file stays on disk for the operator, and the manager raises an alert.
    /// </summary>
    public IReadOnlyList<InvalidReminderTombstone> ConsumeInvalidTombstones()
    {
        lock (_sync)
        {
            if (_invalidTombstones.Count == 0)
                return [];

            var snapshot = _invalidTombstones.ToArray();
            _invalidTombstones.Clear();
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

    // ── Tombstones of ended one-shots ──

    /// <summary>
    /// Writes the tombstone with the same atomic write as a definition (temp file, then rename).
    /// An I/O error propagates: the tombstone is the commit point of an end, so the caller must know.
    /// </summary>
    public void SaveTombstone(ReminderTombstone tombstone)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(_endedDirectory);

            var path = GetTombstonePath(tombstone.Id);
            var tempPath = $"{path}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(tombstone, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
    }

    /// <summary>
    /// Reads the tombstone of <paramref name="id"/>. Returns null when none exists and when the file
    /// cannot be read. An unreadable file is recorded for <see cref="ConsumeInvalidTombstones"/> and stays on disk.
    /// </summary>
    public ReminderTombstone? GetTombstone(ReminderId id)
    {
        lock (_sync)
            return TryReadTombstone(GetTombstonePath(id), id);
    }

    public IReadOnlyList<ReminderTombstone> ListTombstones()
    {
        lock (_sync)
        {
            var list = new List<ReminderTombstone>();
            if (!Directory.Exists(_endedDirectory))
                return list;

            foreach (var file in Directory.EnumerateFiles(_endedDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                var tombstone = TryReadTombstone(file, new ReminderId(DecodeReminderIdFromPath(file)));
                if (tombstone is not null)
                    list.Add(tombstone);
            }

            return list;
        }
    }

    /// <summary>True when a tombstone file exists, whether or not Netclaw can read it.</summary>
    public bool TombstoneExists(ReminderId id)
    {
        lock (_sync)
            return File.Exists(GetTombstonePath(id));
    }

    public bool DeleteTombstone(ReminderId id)
    {
        lock (_sync)
        {
            var path = GetTombstonePath(id);
            if (!File.Exists(path))
                return false;

            File.Delete(path);
            return true;
        }
    }

    private ReminderTombstone? TryReadTombstone(string path, ReminderId expectedId)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var tombstone = JsonSerializer.Deserialize<ReminderTombstone>(File.ReadAllText(path), JsonOptions);
            if (tombstone is null)
                throw new JsonException("the tombstone file is empty");
            if (tombstone.Id != expectedId)
                throw new JsonException($"the tombstone names id '{tombstone.Id.Value}', but its file is for '{expectedId.Value}'");

            return tombstone;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            _invalidTombstones.Add(new InvalidReminderTombstone(expectedId.Value, ex.Message));
            return null;
        }
    }

    private string GetTombstonePath(ReminderId id) => ResolveContained(_endedDirectory, id);

    private string GetPath(ReminderId id) => ResolveContained(_directory, id);

    private static string ResolveContained(string directory, ReminderId id)
    {
        // Uri.EscapeDataString escapes path separators and absolute-path
        // markers (/, \, :, control chars), so the encoded value cannot encode a
        // traversal. The post-canonicalization containment check below is the
        // belt-and-suspenders that CodeQL also recognizes as a path sanitizer.
        // (cs/path-injection)
        var encoded = Uri.EscapeDataString(id.Value);
        var baseDir = Path.GetFullPath(directory);
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
public sealed record InvalidReminderTombstone(string ReminderId, string Reason);
