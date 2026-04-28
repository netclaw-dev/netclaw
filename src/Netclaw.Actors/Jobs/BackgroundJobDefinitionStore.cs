// -----------------------------------------------------------------------
// <copyright file="BackgroundJobDefinitionStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;
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

    public BackgroundJobDefinitionStore(NetclawPaths paths)
    {
        _directory = paths.JobsDirectory;
        Directory.CreateDirectory(_directory);
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
                return JsonSerializer.Deserialize<BackgroundJobDefinition>(text, JsonOptions);
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
                    var def = JsonSerializer.Deserialize<BackgroundJobDefinition>(text, JsonOptions);
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
}
