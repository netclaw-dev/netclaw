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

        var definition = JsonSerializer.Deserialize<BackgroundJobDefinition>(text, JsonOptions);
        if (definition is null)
            return null;

        // Reject ids that resolve to special directory entries ("." / "..") —
        // Uri.EscapeDataString does NOT escape dots, so such an id would make
        // DeleteJobArtifacts target the jobs directory itself or its parent
        // (see DeleteJobArtifacts containment check). The manager only ever
        // generates 12-hex ids; anything else on disk is corrupt or hostile.
        if (!IsSafeJobId(definition.Id.Value))
        {
            _logger.LogError(
                "Background job document {Path} has an unsafe id '{JobId}'. The job will not be loaded.",
                path, definition.Id.Value);
            return null;
        }

        var expectedFileName = $"{Uri.EscapeDataString(definition.Id.Value)}.json";
        var actualFileName = Path.GetFileName(path);
        if (!string.Equals(actualFileName, expectedFileName, StringComparison.Ordinal))
        {
            _logger.LogError(
                "Background job document {Path} has id '{JobId}', which does not match its canonical file name {ExpectedFileName}. "
                + "The job will not be loaded.",
                path, definition.Id.Value, expectedFileName);
            return null;
        }

        return definition;
    }

    /// <summary>
    /// True when the id is a plain file-name token that cannot traverse out of
    /// the jobs directory. Dots pass through <c>Uri.EscapeDataString</c>
    /// unescaped, so "." / ".." (and any id containing a path separator) are
    /// rejected outright.
    /// </summary>
    private static bool IsSafeJobId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        if (id is "." or "..")
            return false;

        if (id.IndexOfAny(['/', '\\']) >= 0)
            return false;

        return true;
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
                    if (def is not null && !string.IsNullOrWhiteSpace(def.Id.Value))
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
            var path = GetPath(definition.Id);
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
    /// Deletes the job definition file AND its output-log directory. Used by
    /// terminal-job cleanup once a job's retention window has elapsed. Returns
    /// true when anything was removed.
    /// </summary>
    public bool DeleteJobArtifacts(BackgroundJobId id)
    {
        lock (_sync)
        {
            var removed = false;

            // Reuse the canonical output-log path (same encoding as
            // GetOutputLogPathOnly) so the artifact directory matches exactly
            // what the execution actor wrote.
            var outputLogPath = GetOutputLogPathOnly(id);
            var outputDir = Path.GetDirectoryName(outputLogPath);
            if (outputDir is not null)
            {
                // Containment guard: Uri.EscapeDataString does not escape dots,
                // so an id like ".." would otherwise resolve to the jobs
                // directory's parent and Directory.Delete(recursive) would wipe
                // it. Never touch anything outside the jobs directory.
                var root = Path.GetFullPath(_directory);
                var fullDir = Path.GetFullPath(outputDir);
                var prefix = root.EndsWith(Path.DirectorySeparatorChar)
                    ? root
                    : root + Path.DirectorySeparatorChar;
                if (!fullDir.StartsWith(prefix, StringComparison.Ordinal))
                    return false;

                if (File.Exists(fullDir))
                    throw new IOException($"Background job artifact path '{fullDir}' is not a directory.");

                if (Directory.Exists(fullDir))
                {
                    Directory.Delete(fullDir, recursive: true);
                    removed = true;
                }
            }

            // Keep the definition until every output artifact is gone. A later
            // sweep can retry if the directory delete fails because of a lock,
            // permissions, or another transient filesystem error.
            var path = GetPath(id);
            if (File.Exists(path))
            {
                File.Delete(path);
                removed = true;
            }

            return removed;
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
