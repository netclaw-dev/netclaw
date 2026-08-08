// -----------------------------------------------------------------------
// <copyright file="BackgroundJobDefinitionStore.cs" company="Petabridge, LLC">
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

namespace Netclaw.Actors.Jobs;

/// <summary>
/// File-backed store for background job definitions.
/// New jobs use their source session directory. Existing jobs stay in their current directory.
/// </summary>
public sealed class BackgroundJobDefinitionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _legacyDirectory;
    private readonly string _sessionsDirectory;
    private readonly object _sync = new();
    private readonly Dictionary<string, RejectedLegacyBackgroundJobDefinition> _rejectedLegacyDefinitions =
        new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    public BackgroundJobDefinitionStore(NetclawPaths paths, ILogger<BackgroundJobDefinitionStore>? logger = null)
    {
        _legacyDirectory = paths.JobsDirectory;
        _sessionsDirectory = paths.SessionsDirectory;
        _logger = logger ?? NullLogger<BackgroundJobDefinitionStore>.Instance;
        Directory.CreateDirectory(_legacyDirectory);
        Directory.CreateDirectory(_sessionsDirectory);
    }

    /// <summary>
    /// Returns and clears background job definitions rejected because they
    /// predate the required trust-field schema.
    /// </summary>
    public IReadOnlyList<RejectedLegacyBackgroundJobDefinition> ConsumeRejectedLegacyDefinitions()
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

    public BackgroundJobDefinition? Get(BackgroundJobId id)
    {
        lock (_sync)
            return TryResolveDefinition(id, out var definition, out _) ? definition : null;
    }

    public BackgroundJobDefinition? Get(BackgroundJobId id, SessionId sessionId)
    {
        var definition = Get(id);
        return definition?.SessionId == sessionId ? definition : null;
    }

    public IReadOnlyList<BackgroundJobDefinition> List()
    {
        lock (_sync)
        {
            var definitions = new Dictionary<string, BackgroundJobDefinition>(StringComparer.Ordinal);
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            var duplicates = new HashSet<string>(StringComparer.Ordinal);

            foreach (var path in EnumerateDefinitionFiles())
            {
                var definition = ReadDefinition(path);
                if (definition is null)
                    continue;

                if (!IsValidStoragePath(definition, path, out var reason))
                {
                    _logger.LogError("Background job document {Path} has invalid storage ownership: {Reason}", path, reason);
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

    public void Save(BackgroundJobDefinition definition)
    {
        lock (_sync)
        {
            var existingPaths = FindDefinitionPaths(definition.Id);
            if (existingPaths.Count > 1)
            {
                LogDuplicate(definition.Id, existingPaths[0], existingPaths[1]);
                throw new InvalidOperationException(
                    $"Background job '{definition.Id.Value}' has definitions in more than one storage directory.");
            }

            var path = existingPaths.Count == 1
                ? existingPaths[0]
                : GetDefinitionPath(GetSessionDirectory(definition.SessionId), definition.Id);

            if (!IsLegacyPath(path) && !IsValidSessionStoragePath(definition, path, out var reason))
                throw new InvalidOperationException(
                    $"Background job '{definition.Id.Value}' cannot use its existing storage directory: {reason}");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tempPath = $"{path}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(definition, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
    }

    public bool Delete(BackgroundJobId id)
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

    public bool Delete(BackgroundJobId id, SessionId sessionId) =>
        Get(id, sessionId) is not null && Delete(id);

    /// <summary>
    /// Returns the output log directory for a job and creates it when necessary.
    /// </summary>
    public string GetOutputDirectory(BackgroundJobId id, SessionId sessionId)
    {
        var directory = GetOutputDirectoryPath(GetStorageDirectory(id, sessionId), id);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public string GetOutputLogPath(BackgroundJobId id, SessionId sessionId) =>
        Path.Combine(GetOutputDirectory(id, sessionId), "output.log");

    /// <summary>
    /// Returns the output log path without directory creation.
    /// </summary>
    public string GetOutputLogPathOnly(BackgroundJobId id, SessionId sessionId) =>
        Path.Combine(GetOutputDirectoryPath(GetStorageDirectory(id, sessionId), id), "output.log");

    private string GetStorageDirectory(BackgroundJobId id, SessionId sessionId)
    {
        lock (_sync)
        {
            var paths = FindDefinitionPaths(id);
            if (paths.Count == 0)
                return GetSessionDirectory(sessionId);

            if (paths.Count > 1)
            {
                LogDuplicate(id, paths[0], paths[1]);
                throw new InvalidOperationException(
                    $"Background job '{id.Value}' has definitions in more than one storage directory.");
            }

            if (!TryResolveDefinition(id, out var definition, out var path))
                throw new InvalidOperationException($"Background job '{id.Value}' does not have a valid definition.");
            if (definition.SessionId != sessionId)
                throw new InvalidOperationException(
                    $"Background job '{id.Value}' belongs to session '{definition.SessionId.Value}'.");
            return Path.GetDirectoryName(path)!;
        }
    }

    private bool TryResolveDefinition(
        BackgroundJobId id,
        out BackgroundJobDefinition definition,
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
        var stored = ReadDefinition(path);
        if (stored is null)
        {
            definition = null!;
            path = string.Empty;
            return false;
        }

        if (stored.Id != id)
        {
            _logger.LogError(
                "Background job document {Path} contains id {StoredId}, but its file name identifies {RequestedId}",
                path, stored.Id.Value, id.Value);
            definition = null!;
            path = string.Empty;
            return false;
        }

        if (!IsValidStoragePath(stored, path, out var reason))
        {
            _logger.LogError("Background job document {Path} has invalid storage ownership: {Reason}", path, reason);
            definition = null!;
            path = string.Empty;
            return false;
        }

        definition = stored;
        return true;
    }

    private List<string> FindDefinitionPaths(BackgroundJobId id)
    {
        var paths = new List<string>();
        var fileName = $"{Uri.EscapeDataString(id.Value)}.json";
        AddIfPresent(paths, GetContainedPath(_legacyDirectory, fileName, id));

        foreach (var sessionDirectory in Directory.EnumerateDirectories(_sessionsDirectory))
        {
            var jobDirectory = Path.Combine(sessionDirectory, SessionDirectoryHelper.JobsSubdirectory);
            if (Directory.Exists(jobDirectory))
                AddIfPresent(paths, GetContainedPath(jobDirectory, fileName, id));
        }

        return paths;
    }

    private IEnumerable<string> EnumerateDefinitionFiles()
    {
        foreach (var path in Directory.EnumerateFiles(_legacyDirectory, "*.json", SearchOption.TopDirectoryOnly))
            yield return path;

        foreach (var sessionDirectory in Directory.EnumerateDirectories(_sessionsDirectory))
        {
            var jobDirectory = Path.Combine(sessionDirectory, SessionDirectoryHelper.JobsSubdirectory);
            if (!Directory.Exists(jobDirectory))
                continue;

            foreach (var path in Directory.EnumerateFiles(jobDirectory, "*.json", SearchOption.TopDirectoryOnly))
                yield return path;
        }
    }

    private static void AddIfPresent(List<string> paths, string path)
    {
        if (File.Exists(path))
            paths.Add(path);
    }

    private bool IsValidStoragePath(BackgroundJobDefinition definition, string path, out string reason)
    {
        var expectedPath = GetDefinitionPath(Path.GetDirectoryName(path)!, definition.Id);
        if (!PathUtility.AreEquivalentPaths(path, expectedPath))
        {
            reason = "the file name does not match the stored job id";
            return false;
        }

        if (IsLegacyPath(path))
        {
            reason = string.Empty;
            return true;
        }

        return IsValidSessionStoragePath(definition, path, out reason);
    }

    private bool IsValidSessionStoragePath(BackgroundJobDefinition definition, string path, out string reason)
    {
        var expectedDirectory = GetSessionDirectory(definition.SessionId);
        if (!PathUtility.AreEquivalentPaths(Path.GetDirectoryName(path)!, expectedDirectory))
        {
            reason = $"the stored session owner resolves to '{expectedDirectory}'";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private bool IsLegacyPath(string path) =>
        PathUtility.AreEquivalentPaths(Path.GetDirectoryName(path)!, _legacyDirectory);

    private string GetSessionDirectory(SessionId sessionId) =>
        SessionDirectoryHelper.GetSessionJobsDirectory(sessionId, _sessionsDirectory);

    private static string GetDefinitionPath(string directory, BackgroundJobId id) =>
        GetContainedPath(directory, $"{Uri.EscapeDataString(id.Value)}.json", id);

    private static string GetOutputDirectoryPath(string directory, BackgroundJobId id) =>
        GetContainedPath(directory, Uri.EscapeDataString(id.Value), id);

    private static string GetContainedPath(string directory, string name, BackgroundJobId id)
    {
        var root = Path.GetFullPath(directory);
        var candidate = Path.GetFullPath(Path.Combine(root, name));
        if (!PathUtility.IsWithinRoot(candidate, root))
            throw new ArgumentException(
                $"Background job id '{id.Value}' resolves outside the job directory.", nameof(id));
        return candidate;
    }

    private BackgroundJobDefinition? ReadDefinition(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var definition = Deserialize(text, path);
            return definition is not null && !string.IsNullOrWhiteSpace(definition.Id.Value)
                ? definition
                : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to read background job document {Path}", path);
            return null;
        }
    }

    private BackgroundJobDefinition? Deserialize(string text, string path)
    {
        // A document without trust fields cannot execute safely. Keep it so
        // the operator can repair or remove the file.
        var missing = LegacyTrustFieldGuard.MissingTrustFields(text);
        if (missing.Count > 0)
        {
            RecordRejectedLegacyDefinition(path, $"missing trust field(s): {string.Join(", ", missing)}");
            _logger.LogError(
                "Background job document {Path} predates issue #994 and is missing required "
                + "trust field(s): {MissingFields}. The job will not be loaded. "
                + "Recreate the job or remove the file.",
                path, string.Join(", ", missing));
            return null;
        }

        return JsonSerializer.Deserialize<BackgroundJobDefinition>(text, JsonOptions);
    }

    private void LogDuplicate(BackgroundJobId id, string firstPath, string secondPath) =>
        _logger.LogError(
            "Background job id {JobId} has duplicate definitions at {FirstPath} and {SecondPath}. Neither definition will load.",
            id.Value, firstPath, secondPath);

    private void RecordRejectedLegacyDefinition(string path, string reason)
    {
        var jobId = DecodeJobIdFromPath(path);
        _rejectedLegacyDefinitions[jobId] = new RejectedLegacyBackgroundJobDefinition(jobId, reason);
    }

    private static string DecodeJobIdFromPath(string path)
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
}

public sealed record RejectedLegacyBackgroundJobDefinition(string JobId, string Reason);
