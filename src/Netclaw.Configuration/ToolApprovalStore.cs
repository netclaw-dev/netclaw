// -----------------------------------------------------------------------
// <copyright file="ToolApprovalStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

/// <summary>
/// Reads and writes persistent tool approval entries from
/// <c>~/.netclaw/config/tool-approvals.json</c>. This file is NOT monitored
/// by <see cref="ConfigWatcherService"/> — writes do not trigger daemon restart.
/// Thread-safe for concurrent reads and writes.
///
/// The on-disk schema is version 3: a typed <see cref="ApprovalEntry"/> list
/// per (audience, tool). A version-2 file receives a byte-identical backup
/// before conversion. A version-1 file moves to a quarantine sibling and an
/// empty version-3 file replaces it. Invalid and future files stay untouched,
/// and the store reports that no persistent authority is available.
/// </summary>
public sealed class ToolApprovalStore
{
    /// <summary>
    /// On-disk schema version emitted by writes and required by
    /// <see cref="Load"/> after any supported conversion.
    /// </summary>
    public const int CurrentSchemaVersion = 3;

    private readonly string _filePath;
    private readonly TimeProvider _timeProvider;
    private readonly ApprovalStoreMigrationContext? _migrationContext;
    private readonly TimeSpan _lockTimeout;
    private readonly IApprovalStoreFileAccess _fileAccess;
    private readonly object _lock = new();

    // Cache state is guarded by _lock. The byte snapshot prevents stale
    // authority when another process writes same-size content within a coarse
    // file-system timestamp interval. A cache hit avoids JSON parsing.
    private ToolApprovalData? _cachedData;
    private byte[]? _cachedSourceBytes;

    /// <summary>
    /// Retained compatibility path for an older malformed-file quarantine.
    /// Version 3 leaves malformed input at the active path for manual repair.
    /// </summary>
    public string MalformedQuarantinePath => _filePath + ".invalid";

    /// <summary>
    /// Path to the legacy version-1 quarantine sibling. The original file is
    /// preserved here before an empty version-3 store replaces it.
    /// </summary>
    public string V1QuarantinePath => _filePath + ".v1.bak";

    /// <summary>
    /// Byte-identical version-2 backup created before conversion.
    /// </summary>
    public string V2BackupPath => _filePath + ".v2.bak";

    /// <summary>
    /// Cross-process lock path for approval-store access.
    /// </summary>
    public string LockPath => _filePath + ".lock";

    /// <param name="filePath">Path to <c>tool-approvals.json</c>.</param>
    /// <param name="timeProvider">
    /// Clock used to stamp <see cref="ApprovalEntry.CreatedAt"/> on newly
    /// added grants. Defaults to <see cref="TimeProvider.System"/> in
    /// production; tests pass a fake to assert on timestamps.
    /// </param>
    public ToolApprovalStore(string filePath, TimeProvider? timeProvider = null)
        : this(filePath, timeProvider, migrationContext: null, lockTimeout: null)
    {
    }

    /// <summary>
    /// Creates a version-3 store with the native shell context needed for
    /// version-2 conversion.
    /// </summary>
    public ToolApprovalStore(
        string filePath,
        TimeProvider? timeProvider,
        ApprovalStoreMigrationContext? migrationContext,
        TimeSpan? lockTimeout = null)
        : this(
            filePath,
            timeProvider,
            migrationContext,
            lockTimeout,
            ApprovalStoreFileAccess.Instance)
    {
    }

    internal ToolApprovalStore(
        string filePath,
        TimeProvider? timeProvider,
        ApprovalStoreMigrationContext? migrationContext,
        TimeSpan? lockTimeout,
        IApprovalStoreFileAccess fileAccess)
    {
        _filePath = filePath;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _migrationContext = migrationContext;
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(2);
        _fileAccess = fileAccess ?? throw new ArgumentNullException(nameof(fileAccess));
    }

    /// <summary>
    /// Loads one complete persistent approval snapshot. An absent file is a
    /// ready empty store. Invalid or unsupported files fail closed.
    /// </summary>
    /// <remarks>
    /// The result is cached and reused while the file bytes are unchanged, so
    /// per-tool-call gate evaluations do not pay the JSON parse cost.
    /// Same-process writes use a private copy under the lock. A successful
    /// save replaces the cache, so the next load sees the new authority.
    /// Cross-process writes (CLI <c>netclaw approvals add</c>, TUI revokes)
    /// are detected by exact source-byte comparison.
    ///
    /// The method returns a detached copy. Caller changes cannot alter the
    /// private cache.
    /// </remarks>
    public ToolApprovalData Load()
    {
        lock (_lock)
        {
            using var lease = _fileAccess.AcquireLock(LockPath, _lockTimeout);
            return CloneData(LoadLocked());
        }
    }

    /// <summary>
    /// Loads a typed store status without exposing an exception to the actor.
    /// </summary>
    public ApprovalStoreLoadResult TryLoad()
    {
        try
        {
            return new ApprovalStoreLoadResult.Ready(CreateSnapshot(Load()));
        }
        catch (ApprovalStoreException ex)
        {
            return new ApprovalStoreLoadResult.Unavailable(ex.Failure);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ApprovalStoreLoadResult.Unavailable(ApprovalStoreFailure.IoFailure);
        }
    }

    /// <summary>
    /// Count of unrepresentable entries omitted by the last successful v2
    /// conversion. Grant text is not retained.
    /// </summary>
    public int LastMigrationOmittedEntryCount { get; private set; }

    private ToolApprovalData LoadLocked()
    {
        if (!File.Exists(_filePath))
        {
            _cachedData = null;
            _cachedSourceBytes = null;
            return new ToolApprovalData();
        }

        _fileAccess.EnsureNotLink(_filePath);
        var sourceBytes = _fileAccess.ReadAllBytes(_filePath);
        if (_cachedData is not null &&
            _cachedSourceBytes is not null &&
            sourceBytes.AsSpan().SequenceEqual(_cachedSourceBytes))
        {
            return _cachedData;
        }

        ToolApprovalData data;
        var sourceIsCurrentVersion = false;
        try
        {
            using var document = JsonDocument.Parse(sourceBytes);
            var versionMemberCount = CountVersionMembers(document.RootElement);
            if (versionMemberCount == 0)
            {
                QuarantineV1File(sourceBytes);
                data = new ToolApprovalData();
                SaveLocked(data);
                return data;
            }

            if (versionMemberCount != 1)
            {
                throw new ApprovalStoreException(
                    ApprovalStoreFailure.InvalidData,
                    "The approval store must contain one version member.");
            }

            var version = ApprovalStoreCodec.ReadVersion(document.RootElement);
            sourceIsCurrentVersion = version == CurrentSchemaVersion;
            data = version switch
            {
                CurrentSchemaVersion => ApprovalStoreCodec.ReadVersion3(
                    document.RootElement,
                    _migrationContext?.ShellToolName ?? "shell_execute"),
                2 => ConvertVersion2(document.RootElement, sourceBytes),
                1 => ConvertVersion1(sourceBytes),
                > CurrentSchemaVersion => throw new ApprovalStoreException(
                    ApprovalStoreFailure.UnsupportedVersion,
                    "The approval store uses a newer schema version."),
                _ => throw new ApprovalStoreException(
                    ApprovalStoreFailure.InvalidData,
                    "The approval store version is invalid."),
            };
        }
        catch (ApprovalStoreException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new ApprovalStoreException(
                ApprovalStoreFailure.InvalidData,
                "The approval store contains invalid JSON.",
                ex);
        }

        if (sourceIsCurrentVersion)
        {
            UpdateCache(data, sourceBytes);
        }
        return data;
    }

    private ToolApprovalData ConvertVersion2(JsonElement root, byte[] sourceBytes)
    {
        var data = ApprovalStoreCodec.ConvertVersion2(
            root,
            _migrationContext,
            out var omittedEntries);
        var contents = ApprovalStoreCodec.Serialize(data);
        _fileAccess.ReplaceVersion2(
            _filePath,
            V2BackupPath,
            sourceBytes,
            contents);
        LastMigrationOmittedEntryCount = omittedEntries;
        UpdateCache(data, Encoding.UTF8.GetBytes(contents));
        return data;
    }

    private ToolApprovalData ConvertVersion1(byte[] sourceBytes)
    {
        QuarantineV1File(sourceBytes);
        var data = new ToolApprovalData();
        SaveLocked(data);
        return data;
    }

    private static int CountVersionMembers(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        var count = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals("version"))
            {
                count++;
            }
        }

        return count;
    }

    private void QuarantineV1File(byte[] sourceBytes)
    {
        _fileAccess.EnsureNotLink(V1QuarantinePath);
        if (File.Exists(V1QuarantinePath))
        {
            if (!File.ReadAllBytes(V1QuarantinePath).AsSpan().SequenceEqual(sourceBytes))
            {
                throw new ApprovalStoreException(
                    ApprovalStoreFailure.MigrationFailed,
                    "The version-1 backup differs from the active source.");
            }

            File.Delete(_filePath);
            _cachedData = null;
            _cachedSourceBytes = null;
            return;
        }

        File.Move(_filePath, V1QuarantinePath);
        _cachedData = null;
        _cachedSourceBytes = null;
    }

    /// <summary>
    /// Adds an approved <see cref="ApprovalEntry"/> for a tool in the given
    /// audience. The verb and directory are normalized before storage so the
    /// on-disk file never accumulates whitespace-padded verbs or
    /// trailing-slash directory variants of the same logical entry.
    /// Idempotent: an entry equal under
    /// <see cref="ToolApprovalEntryComparer.Equals(ApprovalEntry, ApprovalEntry)"/>
    /// is left in place and not appended.
    /// </summary>
    /// <returns>
    /// <c>true</c> when a new entry was appended; <c>false</c> when an
    /// equivalent entry was already present. Callers can use this to surface
    /// "new vs already-trusted" feedback without re-implementing the
    /// duplicate check.
    /// </returns>
    public bool AddApproval(TrustAudience audience, string toolName, ApprovalEntry entry)
        => AddApprovals(audience, toolName, [entry]) > 0;

    /// <summary>
    /// Adds one reviewed batch under one lock and one atomic file replace.
    /// </summary>
    public int AddApprovals(
        TrustAudience audience,
        string toolName,
        IReadOnlyList<ApprovalEntry> entriesToAdd)
    {
        ArgumentNullException.ThrowIfNull(entriesToAdd);
        ApprovalStoreCodec.ValidateToolName(toolName);
        var normalizedEntries = entriesToAdd
            .Select(entry => NormalizeForVersion3(toolName, entry))
            .ToArray();

        lock (_lock)
        {
            using var lease = _fileAccess.AcquireLock(LockPath, _lockTimeout);
            var data = CloneData(LoadLocked());
            var audienceKey = audience.ToWireValue();

            if (!data.Audiences.TryGetValue(audienceKey, out var audienceApprovals))
            {
                audienceApprovals = new Dictionary<string, List<ApprovalEntry>>(StringComparer.Ordinal);
                data.Audiences[audienceKey] = audienceApprovals;
            }

            if (!audienceApprovals.TryGetValue(toolName, out var entries))
            {
                entries = [];
                audienceApprovals[toolName] = entries;
            }

            var added = 0;
            foreach (var normalized in normalizedEntries)
            {
                if (entries.Any(existing => ToolApprovalEntryComparer.Equals(existing, normalized)))
                {
                    continue;
                }

                // Stamp creation time on a new grant only. An equivalent grant
                // keeps its original CreatedAt.
                var stamped = normalized.CreatedAt is null
                    ? normalized with { CreatedAt = _timeProvider.GetUtcNow() }
                    : normalized;

                entries.Add(stamped);
                added++;
            }

            if (added > 0)
            {
                SaveLocked(data);
            }

            return added;
        }
    }

    /// <summary>Adds one entry and returns a typed store status.</summary>
    public ApprovalStoreChangeResult TryAddApproval(
        TrustAudience audience,
        string toolName,
        ApprovalEntry entry) => TryChange(
        () => AddApproval(audience, toolName, entry) ? 1 : 0);

    /// <summary>Adds one reviewed batch and returns a typed store status.</summary>
    public ApprovalStoreChangeResult TryAddApprovals(
        TrustAudience audience,
        string toolName,
        IReadOnlyList<ApprovalEntry> entries) => TryChange(
        () => AddApprovals(audience, toolName, entries));

    /// <summary>
    /// Returns the approved entries for a specific tool and audience.
    /// </summary>
    public IReadOnlyList<ApprovalEntry> GetApprovedEntries(TrustAudience audience, string toolName)
    {
        var data = Load();
        var audienceKey = audience.ToWireValue();

        if (!data.Audiences.TryGetValue(audienceKey, out var audienceApprovals))
            return [];

        if (!audienceApprovals.TryGetValue(toolName, out var entries))
            return [];

        return entries;
    }

    /// <summary>
    /// Removes an approved entry for a tool in the given audience. Comparison
    /// uses <see cref="ToolApprovalEntryComparer.Equals(ApprovalEntry, ApprovalEntry)"/>
    /// so the CLI and the daemon agree on what "the same entry" means. Empty
    /// per-tool and per-audience maps are pruned so the file does not retain
    /// hollow sections after a revoke.
    /// </summary>
    /// <returns><c>true</c> if an entry was removed; <c>false</c> otherwise.</returns>
    public bool RemoveApproval(TrustAudience audience, string toolName, ApprovalEntry entry)
    {
        ApprovalStoreCodec.ValidateToolName(toolName);
        var normalized = NormalizeForVersion3(toolName, entry);

        lock (_lock)
        {
            using var lease = _fileAccess.AcquireLock(LockPath, _lockTimeout);
            var data = CloneData(LoadLocked());
            var audienceKey = audience.ToWireValue();

            if (!data.Audiences.TryGetValue(audienceKey, out var audienceApprovals))
                return false;

            if (!audienceApprovals.TryGetValue(toolName, out var entries))
                return false;

            var index = -1;
            for (var i = 0; i < entries.Count; i++)
            {
                if (ToolApprovalEntryComparer.Equals(entries[i], normalized))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
                return false;

            entries.RemoveAt(index);
            CleanupEmptySections(data, audienceKey, toolName);
            SaveLocked(data);
            return true;
        }
    }

    /// <summary>Removes one entry and returns a typed store status.</summary>
    public ApprovalStoreChangeResult TryRemoveApproval(
        TrustAudience audience,
        string toolName,
        ApprovalEntry entry) => TryChange(
        () => RemoveApproval(audience, toolName, entry) ? 1 : 0);

    /// <summary>
    /// Removes every approval entry for a tool in the given audience.
    /// Returns the count removed; zero if the tool had no entries.
    /// </summary>
    public int RemoveAllForTool(TrustAudience audience, string toolName)
    {
        ApprovalStoreCodec.ValidateToolName(toolName);
        lock (_lock)
        {
            using var lease = _fileAccess.AcquireLock(LockPath, _lockTimeout);
            var data = CloneData(LoadLocked());
            var audienceKey = audience.ToWireValue();

            if (!data.Audiences.TryGetValue(audienceKey, out var audienceApprovals))
                return 0;

            if (!audienceApprovals.TryGetValue(toolName, out var entries))
                return 0;

            var removed = entries.Count;
            if (removed == 0)
                return 0;

            entries.Clear();
            CleanupEmptySections(data, audienceKey, toolName);
            SaveLocked(data);
            return removed;
        }
    }

    /// <summary>Removes all entries for one tool and returns a typed store status.</summary>
    public ApprovalStoreChangeResult TryRemoveAllForTool(
        TrustAudience audience,
        string toolName) => TryChange(
        () => RemoveAllForTool(audience, toolName));

    /// <summary>
    /// Returns a read-only snapshot of the current store contents, keyed by
    /// audience wire value then tool name. The snapshot is decoupled from the
    /// underlying file — subsequent mutations are not reflected.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ApprovalEntry>>> Snapshot()
    {
        var data = Load();
        var result = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ApprovalEntry>>>(StringComparer.Ordinal);
        foreach (var (audienceKey, tools) in data.Audiences)
        {
            var clonedTools = new Dictionary<string, IReadOnlyList<ApprovalEntry>>(StringComparer.Ordinal);
            foreach (var (toolName, entries) in tools)
                clonedTools[toolName] = entries.ToArray();
            result[audienceKey] = clonedTools;
        }
        return result;
    }

    private static void CleanupEmptySections(ToolApprovalData data, string audienceKey, string toolName)
    {
        if (!data.Audiences.TryGetValue(audienceKey, out var audienceApprovals))
            return;

        if (audienceApprovals.TryGetValue(toolName, out var entries) && entries.Count == 0)
            audienceApprovals.Remove(toolName);

        if (audienceApprovals.Count == 0)
            data.Audiences.Remove(audienceKey);
    }

    private static ApprovalStoreChangeResult TryChange(Func<int> change)
    {
        try
        {
            return new ApprovalStoreChangeResult.Completed(change());
        }
        catch (ApprovalStoreException ex)
        {
            return new ApprovalStoreChangeResult.Unavailable(ex.Failure);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ApprovalStoreChangeResult.Unavailable(ApprovalStoreFailure.IoFailure);
        }
    }

    private void SaveLocked(ToolApprovalData data)
    {
        data.Version = CurrentSchemaVersion;
        var json = ApprovalStoreCodec.Serialize(data);
        _fileAccess.WriteAtomic(_filePath, json, _cachedSourceBytes);
        UpdateCache(data, Encoding.UTF8.GetBytes(json));
    }

    private void UpdateCache(ToolApprovalData data, byte[] sourceBytes)
    {
        _cachedData = data;
        _cachedSourceBytes = sourceBytes;
    }

    private static ToolApprovalData CloneData(ToolApprovalData source)
    {
        var clone = new ToolApprovalData { Version = source.Version };
        foreach (var (audience, tools) in source.Audiences)
        {
            var toolClone = new Dictionary<string, List<ApprovalEntry>>(StringComparer.Ordinal);
            foreach (var (tool, entries) in tools)
            {
                toolClone[tool] = [.. entries];
            }

            clone.Audiences[audience] = toolClone;
        }

        return clone;
    }

    private static ApprovalStoreSnapshot CreateSnapshot(ToolApprovalData source)
    {
        var audiences = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ApprovalEntry>>>(
            StringComparer.Ordinal);
        foreach (var (audience, tools) in source.Audiences)
        {
            var toolSnapshot = new Dictionary<string, IReadOnlyList<ApprovalEntry>>(StringComparer.Ordinal);
            foreach (var (tool, entries) in tools)
            {
                toolSnapshot[tool] = Array.AsReadOnly(entries.ToArray());
            }

            audiences[audience] = new System.Collections.ObjectModel.ReadOnlyDictionary<
                string,
                IReadOnlyList<ApprovalEntry>>(toolSnapshot);
        }

        return new ApprovalStoreSnapshot(
            new System.Collections.ObjectModel.ReadOnlyDictionary<
                string,
                IReadOnlyDictionary<string, IReadOnlyList<ApprovalEntry>>>(audiences));
    }

    private ApprovalEntry NormalizeForVersion3(string toolName, ApprovalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var isShellTool = string.Equals(
            toolName,
            _migrationContext?.ShellToolName ?? "shell_execute",
            StringComparison.Ordinal);
        if (isShellTool && entry.Shell is null)
        {
            throw new ApprovalStoreException(
                ApprovalStoreFailure.InvalidData,
                "A new shell approval requires a typed token phrase.");
        }
        else if (!isShellTool && entry.Shell is not null)
        {
            throw new ArgumentException(
                "A non-shell tool requires a non-shell approval entry.",
                nameof(entry));
        }

        var directory = NormalizeVersion3Directory(entry.Directory, entry.Shell);
        var normalized = string.Equals(directory, entry.Directory, StringComparison.Ordinal)
            ? entry
            : entry with { Directory = directory };
        ApprovalEntryValidation.ValidateVersion3(normalized);
        return normalized;
    }

    private static string? NormalizeVersion3Directory(
        string? directory,
        ApprovalShell? shell)
    {
        if (directory is null)
        {
            return null;
        }

        ApprovalEntryValidation.ValidatePersistedString(
            directory,
            "directory",
            allowWhitespace: true);
        if (shell == ApprovalShell.PowerShell)
        {
            var windowsDirectory = directory.Replace('/', '\\');
            if (windowsDirectory.Length > 3 &&
                !windowsDirectory.StartsWith("\\\\", StringComparison.Ordinal))
            {
                windowsDirectory = windowsDirectory.TrimEnd('\\');
            }

            if (!ApprovalEntryValidation.IsCanonicalWindowsAbsolutePath(windowsDirectory))
            {
                throw new ArgumentException("The directory must be a canonical Windows path.", nameof(directory));
            }

            return windowsDirectory;
        }

        if (shell == ApprovalShell.Bash)
        {
            var posixDirectory = directory == "/"
                ? directory
                : directory.TrimEnd('/');
            if (!ApprovalEntryValidation.IsCanonicalPosixAbsolutePath(posixDirectory))
            {
                throw new ArgumentException("The directory must be a canonical POSIX path.", nameof(directory));
            }

            return posixDirectory;
        }

        if (directory.Length == 0 || !Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("The directory must be a nonempty absolute path.", nameof(directory));
        }

        var fullPath = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(fullPath);
        if (string.Equals(fullPath, root, ToolApprovalEntryComparer.Comparison))
        {
            return fullPath;
        }

        var normalized = fullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("The directory has no canonical path.", nameof(directory));
        }

        return normalized;
    }
}

/// <summary>
/// Serialization model for <c>tool-approvals.json</c> version 2 and an
/// in-memory model for the version-3 codec.
/// </summary>
public sealed class ToolApprovalData
{
    /// <summary>
    /// On-disk schema version. Set to <see cref="ToolApprovalStore.CurrentSchemaVersion"/>
    /// by the store writer. Files that lack this value are
    /// quarantined as legacy on first read.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = ToolApprovalStore.CurrentSchemaVersion;

    /// <summary>
    /// Per-audience approval sections. Keys are audience wire values
    /// ("personal", "team", "public"). Values are per-tool entry lists.
    /// </summary>
    [JsonPropertyName("audiences")]
    public Dictionary<string, Dictionary<string, List<ApprovalEntry>>> Audiences { get; set; } = new(StringComparer.Ordinal);
}
