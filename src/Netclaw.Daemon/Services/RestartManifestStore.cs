// -----------------------------------------------------------------------
// <copyright file="RestartManifestStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Reminders;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

public sealed record RestartManifest
{
    public List<ReminderDefinition> RestartReminders { get; init; } = [];
}

/// <summary>
/// Persists short-lived reminders across a coordinated daemon restart.
/// </summary>
public sealed class RestartManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly NetclawPaths _paths;

    public RestartManifestStore(NetclawPaths paths)
    {
        _paths = paths;
    }

    public async Task WriteAsync(RestartManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _paths.EnsureDirectoriesExist();

        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        await AtomicFile.WriteAllTextAsync(
            _paths.RestartManifestPath,
            json,
            static path =>
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            },
            cancellationToken);
    }

    public async Task<RestartManifest?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.RestartManifestPath))
            return null;

        await using var stream = File.OpenRead(_paths.RestartManifestPath);
        return await JsonSerializer.DeserializeAsync<RestartManifest>(stream, JsonOptions, cancellationToken);
    }

    public Task DeleteAsync()
    {
        if (File.Exists(_paths.RestartManifestPath))
            File.Delete(_paths.RestartManifestPath);

        return Task.CompletedTask;
    }
}
