// -----------------------------------------------------------------------
// <copyright file="BootstrapStateStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netclaw.Configuration;

/// <summary>
/// Tracks whether daemon-owned first-launch bootstrap seeding has already been
/// consumed by a successful non-local daemon start.
/// </summary>
public sealed class BootstrapStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public BootstrapStateStore(NetclawPaths paths)
    {
        _path = paths.BootstrapStatePath;
    }

    public bool HasCompletedNonLocalBootstrap()
    {
        if (!File.Exists(_path))
            return false;

        try
        {
            var json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize<BootstrapStateRecord>(json, JsonOptions);
            return state?.HasCompletedFirstSuccessfulNonLocalStart == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void MarkCompleted(TimeProvider timeProvider)
    {
        var dir = Path.GetDirectoryName(_path);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        var state = new BootstrapStateRecord
        {
            HasCompletedFirstSuccessfulNonLocalStart = true,
            CompletedAt = timeProvider.GetUtcNow()
        };

        File.WriteAllText(_path, JsonSerializer.Serialize(state, JsonOptions));
    }

    private sealed class BootstrapStateRecord
    {
        public bool HasCompletedFirstSuccessfulNonLocalStart { get; init; }

        public DateTimeOffset? CompletedAt { get; init; }
    }
}
