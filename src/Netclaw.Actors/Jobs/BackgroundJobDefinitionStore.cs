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
using Netclaw.Configuration;

namespace Netclaw.Actors.Jobs;

/// <summary>
/// File-backed store for background job definitions.
/// Each job is persisted at <c>~/.netclaw/jobs/{id}.json</c>.
/// </summary>
public sealed class BackgroundJobDefinitionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly object _sync = new();
    private readonly Dictionary<string, RejectedLegacyBackgroundJobDefinition> _rejectedLegacyDefinitions =
        new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    public BackgroundJobDefinitionStore(NetclawPaths paths, ILogger<BackgroundJobDefinitionStore>? logger = null)
    {
        _directory = paths.JobsDirectory;
        _logger = logger ?? NullLogger<BackgroundJobDefinitionStore>.Instance;
        Directory.CreateDirectory(_directory);
    }

    private BackgroundJobDefinition? Deserialize(string text, string path)
    {
        // A pre-#994 job document with no persisted trust context cannot be run
        // safely — its trust tier is unknown. Reject it loudly rather than
        // coerce a substitute audience.
        var missing = LegacyTrustFieldGuard.MissingTrustFields(text);
        if (missing.Count > 0)
        {
            RecordRejectedLegacyDefinition(path, $"missing trust field(s): {string.Join(", ", missing)}");
            _logger.LogError(
                "Background job document {Path} predates issue #994 and is missing required "
                + "trust field(s): {MissingFields}. The job will not be loaded — a job with no "
                + "persisted audience cannot be run safely. Recreate the job or remove the file.",
                path, string.Join(", ", missing));
            return null;
        }

        return JsonSerializer.Deserialize<BackgroundJobDefinition>(text, JsonOptions);
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
        {
            var path = GetPath(id);
            if (!File.Exists(path))
                return null;

            try
            {
                var text = File.ReadAllText(path);
                return Deserialize(text, path);
            }
            catch
            {
                return null;
            }
        }
    }

    public IReadOnlyList<BackgroundJobDefinition> List()
    {
        lock (_sync)
        {
            var list = new List<BackgroundJobDefinition>();
            if (!Directory.Exists(_directory))
                return list;

            foreach (var file in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var def = Deserialize(text, file);
                    if (def is not null && !string.IsNullOrWhiteSpace(def.Id))
                        list.Add(def);
                }
                catch // slopwatch-ignore: SW003 corrupt job JSON is benign — skip and continue listing
                {
                }
            }

            return list;
        }
    }

    public void Save(BackgroundJobDefinition definition)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(_directory);
            var path = GetPath(new BackgroundJobId(definition.Id));
            var tempPath = $"{path}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(definition, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
    }

    public bool Delete(BackgroundJobId id)
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

    /// <summary>
    /// Returns the output log directory for a job, creating it if needed.
    /// </summary>
    public string GetOutputDirectory(BackgroundJobId id)
    {
        var dir = Path.Combine(_directory, Uri.EscapeDataString(id.Value));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string GetOutputLogPath(BackgroundJobId id) =>
        Path.Combine(GetOutputDirectory(id), "output.log");

    /// <summary>
    /// Returns the output log path without creating any directories.
    /// </summary>
    public string GetOutputLogPathOnly(BackgroundJobId id) =>
        Path.Combine(_directory, Uri.EscapeDataString(id.Value), "output.log");

    private string GetPath(BackgroundJobId id)
    {
        var encoded = Uri.EscapeDataString(id.Value);
        return Path.Combine(_directory, $"{encoded}.json");
    }

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
