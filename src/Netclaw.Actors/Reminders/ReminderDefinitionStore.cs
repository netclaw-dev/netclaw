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
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Security;

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

    private readonly string _daemonDirectory;
    private readonly string _sessionsDirectory;
    private readonly object _sync = new();
    private readonly List<DroppedInvalidReminderDefinition> _droppedInvalidDefinitions = [];
    private readonly Dictionary<string, RejectedLegacyReminderDefinition> _rejectedLegacyDefinitions =
        new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    public ReminderDefinitionStore(NetclawPaths paths, ILogger<ReminderDefinitionStore>? logger = null)
    {
        _daemonDirectory = paths.RemindersDirectory;
        _sessionsDirectory = paths.SessionsDirectory;
        _logger = logger ?? NullLogger<ReminderDefinitionStore>.Instance;
        Directory.CreateDirectory(_daemonDirectory);
        Directory.CreateDirectory(_sessionsDirectory);
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
            return TryResolveDefinition(id, out _, out _);
    }

    public ReminderDefinition? Get(ReminderId id)
    {
        lock (_sync)
            return TryResolveDefinition(id, out var definition, out _) ? definition : null;
    }

    public IReadOnlyList<ReminderDefinition> List()
    {
        lock (_sync)
        {
            var definitions = new Dictionary<string, ReminderDefinition>(StringComparer.Ordinal);
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            var duplicates = new HashSet<string>(StringComparer.Ordinal);

            foreach (var path in EnumerateDefinitionFiles())
            {
                var result = TryReadDefinition(path);
                if (result.Definition is null)
                {
                    if (result.ShouldDelete)
                        DeleteInvalidDefinition(path, result.ErrorMessage ?? "invalid reminder definition");
                    continue;
                }

                var definition = result.Definition;
                if (!IsValidStoragePath(definition, path, out var reason))
                {
                    _logger.LogError("Reminder document {Path} has invalid storage ownership: {Reason}", path, reason);
                    continue;
                }

                if (duplicates.Contains(definition.Id.Value))
                    continue;

                if (paths.TryGetValue(definition.Id.Value, out var priorPath))
                {
                    paths.Remove(definition.Id.Value);
                    definitions.Remove(definition.Id.Value);
                    duplicates.Add(definition.Id.Value);
                    LogDuplicate(definition.Id, priorPath, path);
                    continue;
                }

                paths.Add(definition.Id.Value, path);
                definitions.Add(definition.Id.Value, definition);
            }

            return definitions.Values.ToArray();
        }
    }

    public void Save(ReminderDefinition definition)
    {
        lock (_sync)
        {
            var existingPaths = FindDefinitionPaths(definition.Id);
            if (existingPaths.Count > 1)
            {
                LogDuplicate(definition.Id, existingPaths[0], existingPaths[1]);
                throw new InvalidOperationException(
                    $"Reminder '{definition.Id.Value}' has definitions in more than one storage directory.");
            }

            var path = existingPaths.Count == 1
                ? existingPaths[0]
                : GetDefinitionPath(GetDirectoryForNewDefinition(definition), definition.Id);

            if (!IsDaemonPath(path) && !IsValidSessionStoragePath(definition, path, out var reason))
                throw new InvalidOperationException(
                    $"Reminder '{definition.Id.Value}' cannot use its existing storage directory: {reason}");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tempPath = $"{path}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(definition, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
    }

    public bool Delete(ReminderId id)
    {
        lock (_sync)
        {
            var paths = FindDefinitionPaths(id);
            if (paths.Count == 0)
                return false;
            if (paths.Count > 1)
            {
                LogDuplicate(id, paths[0], paths[1]);
                return false;
            }

            File.Delete(paths[0]);
            return true;
        }
    }

    internal bool TryGetStorageDirectory(ReminderId id, out string directory)
    {
        lock (_sync)
        {
            if (!TryResolveDefinition(id, out _, out var path))
            {
                directory = string.Empty;
                return false;
            }

            directory = Path.GetDirectoryName(path)!;
            return true;
        }
    }

    private bool TryResolveDefinition(
        ReminderId id,
        out ReminderDefinition definition,
        out string path)
    {
        var paths = FindDefinitionPaths(id);
        if (paths.Count == 0)
        {
            definition = null!;
            path = string.Empty;
            return false;
        }

        if (paths.Count > 1)
        {
            LogDuplicate(id, paths[0], paths[1]);
            definition = null!;
            path = string.Empty;
            return false;
        }

        path = paths[0];
        var result = TryReadDefinition(path);
        if (result.Definition is null)
        {
            if (result.ShouldDelete)
                DeleteInvalidDefinition(path, result.ErrorMessage ?? "invalid reminder definition");
            definition = null!;
            path = string.Empty;
            return false;
        }

        if (result.Definition.Id != id)
        {
            _logger.LogError(
                "Reminder document {Path} contains id {StoredId}, but its file name identifies {RequestedId}",
                path, result.Definition.Id.Value, id.Value);
            definition = null!;
            path = string.Empty;
            return false;
        }

        if (!IsValidStoragePath(result.Definition, path, out var reason))
        {
            _logger.LogError("Reminder document {Path} has invalid storage ownership: {Reason}", path, reason);
            definition = null!;
            path = string.Empty;
            return false;
        }

        definition = result.Definition;
        return true;
    }

    private List<string> FindDefinitionPaths(ReminderId id)
    {
        var paths = new List<string>();
        var fileName = $"{Uri.EscapeDataString(id.Value)}.json";
        AddIfPresent(paths, GetContainedPath(_daemonDirectory, fileName, id));

        foreach (var sessionDirectory in Directory.EnumerateDirectories(_sessionsDirectory))
        {
            var reminderDirectory = Path.Combine(sessionDirectory, SessionDirectoryHelper.RemindersSubdirectory);
            if (Directory.Exists(reminderDirectory))
                AddIfPresent(paths, GetContainedPath(reminderDirectory, fileName, id));
        }

        return paths;
    }

    private IEnumerable<string> EnumerateDefinitionFiles()
    {
        foreach (var path in Directory.EnumerateFiles(_daemonDirectory, "*.json", SearchOption.TopDirectoryOnly))
            yield return path;

        foreach (var sessionDirectory in Directory.EnumerateDirectories(_sessionsDirectory))
        {
            var reminderDirectory = Path.Combine(sessionDirectory, SessionDirectoryHelper.RemindersSubdirectory);
            if (!Directory.Exists(reminderDirectory))
                continue;

            foreach (var path in Directory.EnumerateFiles(reminderDirectory, "*.json", SearchOption.TopDirectoryOnly))
                yield return path;
        }
    }

    private static void AddIfPresent(List<string> paths, string path)
    {
        if (File.Exists(path))
            paths.Add(path);
    }

    private bool IsValidStoragePath(ReminderDefinition definition, string path, out string reason)
    {
        var expectedPath = GetDefinitionPath(Path.GetDirectoryName(path)!, definition.Id);
        if (!PathUtility.AreEquivalentPaths(path, expectedPath))
        {
            reason = "the file name does not match the stored reminder id";
            return false;
        }

        if (IsDaemonPath(path))
        {
            reason = string.Empty;
            return true;
        }

        return IsValidSessionStoragePath(definition, path, out reason);
    }

    private bool IsValidSessionStoragePath(ReminderDefinition definition, string path, out string reason)
    {
        if (definition.Delivery.Kind != DeliveryKind.CurrentSession)
        {
            reason = $"delivery kind {definition.Delivery.Kind} requires the daemon reminder directory";
            return false;
        }

        if (string.IsNullOrWhiteSpace(definition.Delivery.SessionId))
        {
            reason = "a CurrentSession reminder requires a session id";
            return false;
        }

        var expectedDirectory = SessionDirectoryHelper.GetSessionRemindersDirectory(
            new SessionId(definition.Delivery.SessionId), _sessionsDirectory);
        if (!PathUtility.AreEquivalentPaths(Path.GetDirectoryName(path)!, expectedDirectory))
        {
            reason = $"the stored session owner resolves to '{expectedDirectory}'";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool IsDaemonPath(string path) =>
        PathUtility.AreEquivalentPaths(Path.GetDirectoryName(path)!, _daemonDirectory);

    private string GetDirectoryForNewDefinition(ReminderDefinition definition) =>
        definition.Delivery.Kind switch
        {
            DeliveryKind.CurrentSession when !string.IsNullOrWhiteSpace(definition.Delivery.SessionId) =>
                SessionDirectoryHelper.GetSessionRemindersDirectory(
                    new SessionId(definition.Delivery.SessionId), _sessionsDirectory),
            DeliveryKind.CurrentSession => throw new InvalidDataException(
                $"CurrentSession reminder '{definition.Id.Value}' does not have a session id."),
            DeliveryKind.Channel or DeliveryKind.None => _daemonDirectory,
            _ => throw new InvalidDataException(
                $"Reminder '{definition.Id.Value}' has unsupported delivery kind '{definition.Delivery.Kind}'.")
        };

    private static string GetDefinitionPath(string directory, ReminderId id) =>
        GetContainedPath(directory, $"{Uri.EscapeDataString(id.Value)}.json", id);

    private static string GetContainedPath(string directory, string fileName, ReminderId id)
    {
        var root = Path.GetFullPath(directory);
        var candidate = Path.GetFullPath(Path.Combine(root, fileName));
        if (!PathUtility.IsWithinRoot(candidate, root))
            throw new ArgumentException(
                $"Reminder id '{id.Value}' resolves outside the reminder directory.", nameof(id));
        return candidate;
    }

    private void PruneInvalidDefinitions()
    {
        lock (_sync)
        {
            foreach (var path in EnumerateDefinitionFiles().ToArray())
            {
                var result = TryReadDefinition(path);
                if (result.Definition is not null || !result.ShouldDelete)
                    continue;

                DeleteInvalidDefinition(path, result.ErrorMessage ?? "invalid reminder definition");
            }
        }
    }

    private void LogDuplicate(ReminderId id, string firstPath, string secondPath) =>
        _logger.LogError(
            "Reminder id {ReminderId} has duplicate definitions at {FirstPath} and {SecondPath}. Neither definition will run.",
            id.Value, firstPath, secondPath);

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
            // A document without trust fields cannot execute safely. Keep it so
            // the operator can repair or remove the file.
            var missingTrustFields = LegacyTrustFieldGuard.MissingTrustFields(text);
            if (missingTrustFields.Count > 0)
            {
                var fields = string.Join(", ", missingTrustFields);
                _logger.LogError(
                    "Reminder document {Path} predates issue #994 and is missing required "
                    + "trust field(s): {MissingFields}. The reminder will not be loaded or "
                    + "scheduled. Recreate the reminder or remove the file.",
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
