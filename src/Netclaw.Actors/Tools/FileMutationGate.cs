// -----------------------------------------------------------------------
// <copyright file="FileMutationGate.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
/// A fixed array of stripes is used instead of a per-path dictionary so memory
/// stays bounded in the long-running daemon. Distinct files may share a stripe
/// (benign false contention); the same file always maps to the same stripe.
/// This gate is process-local: it does not guard against other processes
/// writing the same file.
/// </remarks>
internal static class FileMutationGate
{
    private const int StripeCount = 64;

    // Process-lifetime striped locks, allocated once. Intentionally never
    // disposed: they hold no unmanaged handle (AvailableWaitHandle is unused)
    // and the OS reclaims them at process exit. No ProcessExit hook -- .NET 10
    // no longer raises that event on SIGTERM, and there is nothing to release.
    private static readonly SemaphoreSlim[] Stripes = CreateStripes();

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>
    /// Runs <paramref name="action"/> while holding the exclusive lock for
    /// <paramref name="path"/>, so concurrent mutations of the same file run
    /// one at a time.
    /// </summary>
    public static async Task<T> RunExclusiveAsync<T>(string path, Func<Task<T>> action, CancellationToken ct)
    {
        var stripe = StripeFor(path);
        await stripe.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            stripe.Release();
        }
    }

    private static SemaphoreSlim StripeFor(string path)
    {
        var index = (int)((uint)PathComparer.GetHashCode(PathUtility.Normalize(path)) % StripeCount);
        return Stripes[index];
    }

    private static SemaphoreSlim[] CreateStripes()
    {
        var stripes = new SemaphoreSlim[StripeCount];
        for (var i = 0; i < StripeCount; i++)
            stripes[i] = new SemaphoreSlim(1, 1);
        return stripes;
    }
}
