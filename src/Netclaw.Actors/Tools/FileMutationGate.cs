// -----------------------------------------------------------------------
// <copyright file="FileMutationGate.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Netclaw.Security;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Serializes file-mutating read-modify-write operations per target path.
/// </summary>
/// <remarks>
/// Tool calls within a single assistant turn run concurrently (see
/// <c>SessionToolExecutionPipeline</c>). Without serialization, two
/// <c>file_edit</c> calls against the same file each read the same original
/// content and write back only their own change — last writer wins and the
/// other edits are silently lost while every call still reports success.
/// Routing the read-modify-write through this gate forces same-file callers to
/// run one at a time: a later edit re-reads the file after the earlier write,
/// so disjoint block edits compose without retries, and a genuine conflict
/// (target text removed by the earlier write) surfaces loudly via the tool's
/// existing not-found / ambiguous-match errors.
///
/// One <see cref="SemaphoreSlim"/> is kept per normalized path, so distinct
/// files never contend. The map grows with the set of distinct files mutated
/// over the process lifetime — a small, bounded set in practice — and the
/// semaphores are intentionally never disposed: they are process-lifetime
/// singletons that hold no unmanaged handle. This gate is process-local; it
/// does not guard against other processes writing the same file.
/// </remarks>
internal static class FileMutationGate
{
    // Path key comparison follows the host filesystem so two spellings of the
    // same file (case differences on Windows/macOS) map to the same lock.
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(PathComparer);

    /// <summary>
    /// Runs <paramref name="action"/> while holding the exclusive lock for
    /// <paramref name="path"/>, so concurrent mutations of the same file run
    /// one at a time. Distinct files never contend.
    /// </summary>
    public static async Task<T> RunExclusiveAsync<T>(string path, Func<Task<T>> action, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(PathUtility.Normalize(path), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }
}
