// -----------------------------------------------------------------------
// <copyright file="ToolApprovalStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

/// <summary>
/// Reads and writes persistent tool approval patterns from
/// <c>~/.netclaw/config/tool-approvals.json</c>. This file is NOT monitored
/// by <see cref="ConfigWatcherService"/> — writes do not trigger daemon restart.
/// Thread-safe for concurrent reads and writes.
/// </summary>
public sealed class ToolApprovalStore
{
    private readonly string _filePath;
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ToolApprovalStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>
    /// Loads all persistent approvals from disk. Returns an empty store if the
    /// file does not exist. If the file is malformed, the corrupt file is
    /// quarantined to <c>tool-approvals.json.invalid</c> and an empty store is
    /// returned — operators can inspect or restore the quarantined copy and
    /// the system fails closed (no approvals) instead of silently dropping
    /// every persisted grant.
    /// </summary>
    public ToolApprovalData Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
                return new ToolApprovalData();

            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<ToolApprovalData>(json, JsonOptions)
                    ?? new ToolApprovalData();
            }
            catch (JsonException ex)
            {
                QuarantineCorruptFile(ex);
                return new ToolApprovalData();
            }
        }
    }

    private void QuarantineCorruptFile(JsonException cause)
    {
        var quarantinePath = _filePath + ".invalid";
        try
        {
            if (File.Exists(quarantinePath))
                File.Delete(quarantinePath);
            File.Move(_filePath, quarantinePath);
        }
        catch (Exception moveEx)
        {
            throw new InvalidDataException(
                $"Tool approvals file at '{_filePath}' is malformed and could not be quarantined to '{quarantinePath}'. Inspect the file manually before restarting.",
                new AggregateException(cause, moveEx));
        }
    }

    /// <summary>
    /// Adds an approved pattern for a tool in the given audience.
    /// For shell_execute, the pattern is a verb chain (e.g., "git push").
    /// For other tools, pass the tool name to approve tool-level access.
    /// </summary>
    public void AddApproval(TrustAudience audience, string toolName, string pattern)
    {
        lock (_lock)
        {
            var data = Load();
            var audienceKey = audience.ToWireValue();

            if (!data.Audiences.TryGetValue(audienceKey, out var audienceApprovals))
            {
                audienceApprovals = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                data.Audiences[audienceKey] = audienceApprovals;
            }

            if (!audienceApprovals.TryGetValue(toolName, out var patterns))
            {
                patterns = [];
                audienceApprovals[toolName] = patterns;
            }

            if (!patterns.Contains(pattern, StringComparer.Ordinal))
            {
                patterns.Add(pattern);
                Save(data);
            }
        }
    }

    /// <summary>
    /// Returns the approved patterns for a specific tool and audience.
    /// </summary>
    public IReadOnlyList<string> GetApprovedPatterns(TrustAudience audience, string toolName)
    {
        var data = Load();
        var audienceKey = audience.ToWireValue();

        if (!data.Audiences.TryGetValue(audienceKey, out var audienceApprovals))
            return [];

        if (!audienceApprovals.TryGetValue(toolName, out var patterns))
            return [];

        return patterns;
    }

    /// <summary>
    /// Removes an approved pattern for a tool in the given audience. Comparison
    /// uses <see cref="ToolApprovalEntryComparer.Comparison"/> so the CLI and
    /// the daemon agree on what "the same entry" means. Empty per-tool and
    /// per-audience maps are pruned so the file does not retain hollow
    /// sections after a revoke.
    /// </summary>
    /// <returns><c>true</c> if an entry was removed; <c>false</c> otherwise.</returns>
    public bool RemoveApproval(TrustAudience audience, string toolName, string pattern)
    {
        lock (_lock)
        {
            var data = Load();
            var audienceKey = audience.ToWireValue();

            if (!data.Audiences.TryGetValue(audienceKey, out var audienceApprovals))
                return false;

            if (!audienceApprovals.TryGetValue(toolName, out var patterns))
                return false;

            var index = -1;
            for (var i = 0; i < patterns.Count; i++)
            {
                if (ToolApprovalEntryComparer.Equals(patterns[i], pattern))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
                return false;

            patterns.RemoveAt(index);
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

            if (!audienceApprovals.TryGetValue(toolName, out var patterns))
                return 0;

            var removed = patterns.Count;
            if (removed == 0)
                return 0;

            patterns.Clear();
            CleanupEmptySections(data, audienceKey, toolName);
            Save(data);
            return removed;
        }
    }

    /// <summary>
    /// Returns a deep clone of the current store contents. The returned object
    /// is a snapshot — subsequent mutations to the underlying file are not
    /// reflected. Suitable for read-only iteration by the operator CLI.
    /// </summary>
    public ToolApprovalData Snapshot()
    {
        var data = Load();
        var clone = new ToolApprovalData();
        foreach (var (audienceKey, tools) in data.Audiences)
        {
            var clonedTools = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (toolName, patterns) in tools)
                clonedTools[toolName] = [.. patterns];
            clone.Audiences[audienceKey] = clonedTools;
        }
        return clone;
    }

    private static void CleanupEmptySections(ToolApprovalData data, string audienceKey, string toolName)
    {
        if (!data.Audiences.TryGetValue(audienceKey, out var audienceApprovals))
            return;

        if (audienceApprovals.TryGetValue(toolName, out var patterns) && patterns.Count == 0)
            audienceApprovals.Remove(toolName);

        if (audienceApprovals.Count == 0)
            data.Audiences.Remove(audienceKey);
    }

    private void Save(ToolApprovalData data)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}

/// <summary>
/// Serialization model for <c>tool-approvals.json</c>.
/// </summary>
public sealed class ToolApprovalData
{
    /// <summary>
    /// Per-audience approval sections. Keys are audience wire values
    /// ("personal", "team", "public"). Values are per-tool pattern lists.
    /// </summary>
    [JsonPropertyName("audiences")]
    public Dictionary<string, Dictionary<string, List<string>>> Audiences { get; set; } = new(StringComparer.Ordinal);
}
