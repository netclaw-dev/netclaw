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
