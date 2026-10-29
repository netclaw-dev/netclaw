// -----------------------------------------------------------------------
// <copyright file="ShellTemporaryDirectoryState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Tools;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Startup result of probing the directory that shell commands use for temporary files.
/// A read-only root filesystem without a tmpfs on <c>/tmp</c> leaves the daemon healthy while
/// every shell call fails, so the status endpoint reports it as degraded.
/// </summary>
/// <param name="Problem">Why the directory is unusable, or null when it is writable.</param>
internal sealed record ShellTemporaryDirectoryState(string? Problem)
{
    /// <summary>Probes the base folder of <paramref name="layout"/>; other layouts have nothing outside the Netclaw home to probe.</summary>
    public static ShellTemporaryDirectoryState Probe(ManagedTemporaryLayout layout)
    {
        if (OperatingSystem.IsWindows() || layout is not ManagedTemporaryLayout.PrivateBase privateBase)
            return new ShellTemporaryDirectoryState((string?)null);

        var folder = privateBase.Base.Value;

        // The shell launch's own folder checks first (link, owner, group/other bits), so the probe
        // passes only where the shell would.
        if (ManagedTemporaryEnvironment.CheckBase(privateBase.Base) is { } refusal)
        {
            // Only a failed create is a filesystem problem a tmpfs fixes; a link, owner or mode refusal is not.
            var hint = refusal.StartsWith("Error preparing", StringComparison.Ordinal)
                ? " Mount a writable tmpfs at /tmp (docker run --tmpfs /tmp)."
                : string.Empty;
            return new ShellTemporaryDirectoryState($"Shell commands cannot use their temporary folder '{folder}'. {refusal}{hint}");
        }

        try
        {
            // CreateNew refuses an existing name or link, so the probe cannot write through a planted link.
            using var probe = new FileStream(
                Path.Combine(folder, $".probe-{Guid.NewGuid():N}"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose);
            return new ShellTemporaryDirectoryState((string?)null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ShellTemporaryDirectoryState(
                $"Shell commands cannot create temporary files in '{folder}': {ex.Message}. "
                + "Mount a writable tmpfs at /tmp (docker run --tmpfs /tmp).");
        }
    }
}
