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

    private readonly NetclawPaths _paths;
    private readonly object _sync = new();
    private readonly List<DroppedInvalidReminderDefinition> _droppedInvalidDefinitions = [];
    private readonly Dictionary<string, RejectedLegacyReminderDefinition> _rejectedLegacyDefinitions =
        new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    public ReminderDefinitionStore(NetclawPaths paths, ILogger<ReminderDefinitionStore>? logger = null)
    {
        _paths = paths;
        _logger = logger ?? NullLogger<ReminderDefinitionStore>.Instance;
        Directory.CreateDirectory(_paths.RemindersDirectory);
        Directory.CreateDirectory(_paths.SessionsDirectory);
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
                if (TryReadValidDefinition(path) is not { } stored)
                    continue;

                var definition = stored.Definition;

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
            if (!TryGetSavePath(definition, out var path, out var error))
                throw new InvalidOperationException(error);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tempPath = $"{path}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(definition, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
    }

    internal bool TryValidateSave(ReminderDefinition definition, out string error)
    {
        lock (_sync)
            return TryGetSavePath(definition, out _, out error);
    }

    public bool Delete(ReminderId id)
    {
        lock (_sync)
        {
            var definitions = FindValidDefinitions(id);
            if (definitions.Count == 0)
                return false;
            if (definitions.Count > 1)
            {
                LogDuplicate(id, definitions[0].Path, definitions[1].Path);
                return false;
            }

            File.Delete(definitions[0].Path);
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
        var definitions = FindValidDefinitions(id);
        if (definitions.Count == 0)
        {
            definition = null!;
            path = string.Empty;
            return false;
        }

        if (definitions.Count > 1)
        {
            LogDuplicate(id, definitions[0].Path, definitions[1].Path);
            definition = null!;
            path = string.Empty;
            return false;
        }

        definition = definitions[0].Definition;
        path = definitions[0].Path;
        return true;
    }

    private bool TryGetSavePath(ReminderDefinition definition, out string path, out string error)
    {
        var definitions = FindValidDefinitions(definition.Id);
        if (definitions.Count > 1)
        {
            LogDuplicate(definition.Id, definitions[0].Path, definitions[1].Path);
            path = string.Empty;
            error = $"Reminder '{definition.Id.Value}' has definitions in more than one storage directory.";
            return false;
        }

        path = definitions.Count == 1
            ? definitions[0].Path
            : GetDefinitionPath(GetDirectoryForNewDefinition(definition), definition.Id);

        if (!IsDaemonPath(path) && !IsValidSessionStoragePath(definition, path, out var reason))
        {
            error = $"Reminder '{definition.Id.Value}' cannot use its existing storage directory: {reason}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private List<(ReminderDefinition Definition, string Path)> FindValidDefinitions(ReminderId id)
    {
        var definitions = new List<(ReminderDefinition, string)>();
        foreach (var path in FindDefinitionPaths(id))
        {
            if (TryReadValidDefinition(path) is { } stored && stored.Definition.Id == id)
                definitions.Add(stored);
        }

        return definitions;
    }

    private List<string> FindDefinitionPaths(ReminderId id)
    {
        var paths = new List<string>();
        var fileName = $"{Uri.EscapeDataString(id.Value)}.json";
        AddIfPresent(paths, GetContainedPath(_paths.RemindersDirectory, fileName, id));

        foreach (var reminderDirectory in EnumerateSessionReminderDirectories())
        {
            var path = GetContainedPath(reminderDirectory, fileName, id);
            if (IsSafeSessionPath(path, out var reason))
                AddIfPresent(paths, path);
            else
                _logger.LogError("Session reminder definition {Path} is unsafe: {Reason}", path, reason);
        }

        return paths;
    }

    private IEnumerable<string> EnumerateDefinitionFiles()
    {
        foreach (var path in Directory.EnumerateFiles(_paths.RemindersDirectory, "*.json", SearchOption.TopDirectoryOnly))
            yield return path;

        foreach (var reminderDirectory in EnumerateSessionReminderDirectories())
        {
            foreach (var path in Directory.EnumerateFiles(reminderDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (IsSafeSessionPath(path, out var reason))
                    yield return path;
                else
                    _logger.LogError("Session reminder definition {Path} is unsafe: {Reason}", path, reason);
            }
        }
    }

    private IEnumerable<string> EnumerateSessionReminderDirectories()
    {
        if (!IsSafeSessionPath(_paths.SessionsDirectory, out var rootReason))
        {
            _logger.LogError("Session reminder root {Path} is unsafe: {Reason}", _paths.SessionsDirectory, rootReason);
            yield break;
        }

        foreach (var sessionDirectory in Directory.EnumerateDirectories(_paths.SessionsDirectory))
        {
            var reminderDirectory = Path.Combine(sessionDirectory, SessionDirectoryHelper.RemindersSubdirectory);
            if (!IsSafeSessionPath(reminderDirectory, out var reason))
            {
                _logger.LogError("Session reminder directory {Path} is unsafe: {Reason}", reminderDirectory, reason);
                continue;
            }

            if (Directory.Exists(reminderDirectory))
                yield return reminderDirectory;
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
        if (!IsSafeSessionPath(path, out reason))
            return false;

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
            new SessionId(definition.Delivery.SessionId), _paths.SessionsDirectory);
        if (!PathUtility.AreEquivalentPaths(Path.GetDirectoryName(path)!, expectedDirectory))
        {
            reason = $"the stored session owner resolves to '{expectedDirectory}'";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool IsSafeSessionPath(string path, out string reason)
    {
        if (!PathUtility.IsWithinRoot(path, _paths.SessionsDirectory))
        {
            reason = "the path is outside the session directory";
            return false;
        }

        if (PathUtility.ContainsSymlinkSegment(Path.GetDirectoryName(_paths.SessionsDirectory)!, path))
        {
            reason = "the path contains a symbolic link or reparse point";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool IsDaemonPath(string path) =>
        PathUtility.AreEquivalentPaths(Path.GetDirectoryName(path)!, _paths.RemindersDirectory);

    private string GetDirectoryForNewDefinition(ReminderDefinition definition) =>
        definition.Delivery.Kind switch
        {
            DeliveryKind.CurrentSession when !string.IsNullOrWhiteSpace(definition.Delivery.SessionId) =>
                SessionDirectoryHelper.GetSessionRemindersDirectory(
                    new SessionId(definition.Delivery.SessionId), _paths.SessionsDirectory),
            DeliveryKind.CurrentSession => throw new InvalidDataException(
                $"CurrentSession reminder '{definition.Id.Value}' does not have a session id."),
            DeliveryKind.Channel or DeliveryKind.None => _paths.RemindersDirectory,
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

    private (ReminderDefinition Definition, string Path)? TryReadValidDefinition(string path)
    {
        var result = TryReadDefinition(path);
        if (result.Definition is null)
        {
            if (result.ShouldDelete)
                DeleteInvalidDefinition(path, result.ErrorMessage ?? "invalid reminder definition");
            return null;
        }

        if (IsValidStoragePath(result.Definition, path, out var reason))
            return (result.Definition, path);

        _logger.LogError("Reminder document {Path} has invalid storage ownership: {Reason}", path, reason);
        return null;
    }

    private sealed record ReadResult(ReminderDefinition? Definition, string? ErrorMessage, bool ShouldDelete);
}

public sealed record DroppedInvalidReminderDefinition(string ReminderId, string Reason);
public sealed record RejectedLegacyReminderDefinition(string ReminderId, string Reason);
