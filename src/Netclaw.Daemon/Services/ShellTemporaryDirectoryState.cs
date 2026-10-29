// -----------------------------------------------------------------------
// <copyright file="ShellTemporaryDirectoryState.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
        try
        {
            // Same mode the shell launch uses, so the probe cannot widen access to the folder.
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var probeFile = Path.Combine(folder, $".probe-{Environment.ProcessId}");
            File.WriteAllText(probeFile, string.Empty);
            File.Delete(probeFile);
            return new ShellTemporaryDirectoryState((string?)null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ShellTemporaryDirectoryState(
                $"Shell commands cannot create temporary files in '{folder}': {ex.Message}. "
                + "Mount a tmpfs at /tmp (docker run --tmpfs /tmp) or point TMPDIR at a writable path.");
        }
    }
}
