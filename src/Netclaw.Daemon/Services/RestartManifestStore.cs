// -----------------------------------------------------------------------
// <copyright file="RestartManifestStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration;
using Netclaw.Actors.Protocol;

namespace Netclaw.Daemon.Services;

public sealed record RestartManifest
{
    public Guid GenerationId { get; init; }

    public required string Reason { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }

    public required List<string> SessionIds { get; init; }

    public List<string> TimedOutSessionIds { get; init; } = [];

    public List<RestartResumeCandidate> ResumeCandidates { get; init; } = [];
}

/// <summary>
/// Persists short-lived restart recovery state across a coordinated in-process host restart.
/// </summary>
public sealed class RestartManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly NetclawPaths _paths;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public RestartManifestStore(NetclawPaths paths)
    {
        _paths = paths;
    }

    public async Task WriteAsync(RestartManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await WriteCoreAsync(manifest, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> TryUpdateCandidatesAsync(
        Guid generationId,
        IReadOnlyList<RestartResumeCandidate> remaining,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var current = await ReadCoreAsync(cancellationToken);
            if (current is null || current.GenerationId != generationId)
                return false;

            if (remaining.Count == 0)
                File.Delete(_paths.RestartManifestPath);
            else
                await WriteCoreAsync(current with { ResumeCandidates = [.. remaining] }, cancellationToken);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task WriteCoreAsync(RestartManifest manifest, CancellationToken cancellationToken)
    {
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
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            return await ReadCoreAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<RestartManifest?> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.RestartManifestPath))
            return null;

        await using var stream = File.OpenRead(_paths.RestartManifestPath);
        return await JsonSerializer.DeserializeAsync<RestartManifest>(stream, JsonOptions, cancellationToken);
    }

    public async Task DeleteAsync()
    {
        await _writeGate.WaitAsync();
        try
        {
            if (File.Exists(_paths.RestartManifestPath))
                File.Delete(_paths.RestartManifestPath);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
