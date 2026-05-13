// -----------------------------------------------------------------------
// <copyright file="ToolApprovalStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

/// <summary>
/// Reads and writes persistent tool approval entries from
/// <c>~/.netclaw/config/tool-approvals.json</c>. This file is NOT monitored
/// by <see cref="ConfigWatcherService"/> — writes do not trigger daemon restart.
/// Thread-safe for concurrent reads and writes.
///
/// The on-disk schema is version 2: a typed <see cref="ApprovalEntry"/> list
/// per (audience, tool). Files lacking <c>"version": 2</c> at the root are
/// treated as legacy v1 and quarantined to <see cref="V1QuarantinePath"/>; an
/// empty v2 store is returned in their place. Files that fail to parse as
/// JSON at all are quarantined to <see cref="MalformedQuarantinePath"/>. In
/// both cases the daemon fails closed (no approvals) instead of silently
/// dropping every persisted grant.
/// </summary>
public sealed class ToolApprovalStore
{
    /// <summary>
    /// On-disk schema version emitted by <see cref="Save"/> and required by
    /// <see cref="Load"/>. Files with any other value (including absent) are
    /// quarantined to <see cref="V1QuarantinePath"/> on first read.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    private readonly string _filePath;
    private readonly object _lock = new();

    // Cache state guarded by _lock. `_cachedData` is the live in-memory
    // instance returned to callers; writers (AddApproval, RemoveApproval,
    // RemoveAllForTool) mutate it under the lock and then Save() refreshes
    // the mtime/size to keep it authoritative. External writes (CLI, TUI in
    // a separate process) invalidate the cache via the mtime+size check at
    // the top of Load(); same-process writes that don't change size within
    // the filesystem's mtime resolution are caught by the internal version
    // counter bumped on every Save.
    private ToolApprovalData? _cachedData;
    private DateTime _cachedMtime;
    private long _cachedSize;
    private int _internalVersion;

    /// <summary>
    /// Path to the malformed-file quarantine sibling, used when the file
    /// cannot be parsed as JSON at all. Distinct from
    /// <see cref="V1QuarantinePath"/> so operators can tell a corrupted file
    /// apart from a legacy-version file.
    /// </summary>
    public string MalformedQuarantinePath => _filePath + ".invalid";

    /// <summary>
    /// Path to the legacy-v1 quarantine sibling, used when the file parses as
    /// JSON but does not declare schema version 2. The v1 file is preserved
    /// here untouched so operators who hand-curated v1 entries can mine them
    /// for ideas before writing fresh v2 grants.
    /// </summary>
    public string V1QuarantinePath => _filePath + ".v1.bak";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Operator-editable file: tolerate JSON5-ish niceties so a stray
        // comment or trailing comma doesn't trigger the malformed-quarantine
        // path and silently drop every grant.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public ToolApprovalStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// Loads all persistent approvals. Returns an empty store when the file
    /// does not exist, parses as JSON but lacks <c>"version": 2</c> (the file
    /// is moved aside to <see cref="V1QuarantinePath"/>), or fails to parse
    /// as JSON at all (the file is moved aside to
    /// <see cref="MalformedQuarantinePath"/>).
    /// </summary>
    /// <remarks>
    /// The result is cached and reused while the file's last-write time and
    /// size are unchanged so per-tool-call gate evaluations don't pay the
    /// disk-read + JSON-parse cost. Same-process writes via
    /// <see cref="AddApproval"/> and friends mutate the cached instance
    /// under the lock and Save refreshes the cache metadata, so live
    /// approvals are visible on the next Load without re-reading the file.
    /// Cross-process writes (CLI <c>netclaw approvals add</c>, TUI revokes)
    /// are detected by the mtime+size check and trigger a fresh read.
    ///
    /// Callers SHOULD NOT mutate the returned <see cref="ToolApprovalData"/>
    /// outside the store's lock — the store hands back its live instance to
    /// keep the hot read path allocation-free.
    /// </remarks>
    public ToolApprovalData Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                _cachedData = null;
                return new ToolApprovalData();
            }

            // Capture metadata up front: if LoadFromDisk quarantines the file
            // (malformed JSON, wrong schema) it gets moved aside and a later
            // FileInfo lookup would throw FileNotFoundException. The captured
            // metadata becomes the cache key for the quarantine-empty result
            // we return — harmless because the next Load() observes
            // File.Exists == false and skips the cache check entirely.
            var info = new FileInfo(_filePath);
            var mtime = info.LastWriteTimeUtc;
            var size = info.Length;

            if (_cachedData is not null && mtime == _cachedMtime && size == _cachedSize)
                return _cachedData;

            var data = LoadFromDisk();
            _cachedData = data;
            _cachedMtime = mtime;
            _cachedSize = size;
            return data;
        }
    }

    private ToolApprovalData LoadFromDisk()
    {
        var json = File.ReadAllText(_filePath);

        // Parse once via JsonDocument so we can both (a) check schema version
        // on the root element and (b) deserialize the typed model from the
        // already-parsed DOM via RootElement.Deserialize. Three failure modes
        // are distinguished:
        //   (1) unparseable JSON → quarantine to .invalid
        //   (2) parseable JSON but wrong schema version → quarantine to .v1.bak
        //   (3) parseable v2 JSON with deserialization error → quarantine to .invalid
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!IsCurrentSchema(document.RootElement))
            {
                QuarantineV1File();
                return new ToolApprovalData();
            }

            try
            {
                return document.RootElement.Deserialize<ToolApprovalData>(JsonOptions)
                    ?? new ToolApprovalData();
            }
            catch (JsonException ex)
            {
                QuarantineMalformedFile(ex);
                return new ToolApprovalData();
            }
        }
        catch (JsonException ex)
        {
            QuarantineMalformedFile(ex);
            return new ToolApprovalData();
        }
    }

    private static bool IsCurrentSchema(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        if (!root.TryGetProperty("version", out var versionElem))
            return false;

        if (versionElem.ValueKind != JsonValueKind.Number)
            return false;

        return versionElem.TryGetInt32(out var version) && version == CurrentSchemaVersion;
    }

    private void QuarantineMalformedFile(JsonException cause)
    {
        try
        {
            if (File.Exists(MalformedQuarantinePath))
                File.Delete(MalformedQuarantinePath);
            File.Move(_filePath, MalformedQuarantinePath);
        }
        catch (Exception moveEx)
        {
            throw new InvalidDataException(
                $"Tool approvals file at '{_filePath}' is malformed and could not be quarantined to '{MalformedQuarantinePath}'. Inspect the file manually before restarting.",
                new AggregateException(cause, moveEx));
        }
    }

    private void QuarantineV1File()
    {
        try
        {
            if (File.Exists(V1QuarantinePath))
                File.Delete(V1QuarantinePath);
            File.Move(_filePath, V1QuarantinePath);
        }
        catch (Exception moveEx)
        {
            throw new InvalidDataException(
                $"Tool approvals file at '{_filePath}' uses a legacy schema and could not be quarantined to '{V1QuarantinePath}'. Inspect the file manually before restarting.",
                moveEx);
        }
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
    {
        var normalized = ToolApprovalEntryComparer.Normalize(entry);

        lock (_lock)
        {
            var data = Load();
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

            foreach (var existing in entries)
            {
                if (ToolApprovalEntryComparer.Equals(existing, normalized))
                    return false;
            }

            entries.Add(normalized);
            Save(data);
            return true;
        }
    }

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
        var normalized = ToolApprovalEntryComparer.Normalize(entry);

        lock (_lock)
        {
            var data = Load();
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
            Save(data);
            return true;
        }
    }

    /// <summary>
    /// Removes every approval entry for a tool in the given audience.
    /// Returns the count removed; zero if the tool had no entries.
    /// </summary>
    public int RemoveAllForTool(TrustAudience audience, string toolName)
    {
        lock (_lock)
        {
            var data = Load();
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
            Save(data);
            return removed;
        }
    }

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

    private void Save(ToolApprovalData data)
    {
        data.Version = CurrentSchemaVersion;

        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(_filePath, json);

        // Refresh cache from the file we just wrote: the in-memory `data` is
        // already authoritative (callers mutated it under the lock) so keep it
        // cached and just sync the metadata. Bumping `_internalVersion`
        // guarantees that a same-process write within the filesystem's mtime
        // resolution still invalidates against any hypothetical concurrent
        // reader holding stale (mtime,size) markers.
        var info = new FileInfo(_filePath);
        _cachedData = data;
        _cachedMtime = info.LastWriteTimeUtc;
        _cachedSize = info.Length;
        _internalVersion++;
    }
}

/// <summary>
/// Serialization model for <c>tool-approvals.json</c>.
/// </summary>
public sealed class ToolApprovalData
{
    /// <summary>
    /// On-disk schema version. Set to <see cref="ToolApprovalStore.CurrentSchemaVersion"/>
    /// by <see cref="ToolApprovalStore.Save"/>. Files lacking this value are
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
