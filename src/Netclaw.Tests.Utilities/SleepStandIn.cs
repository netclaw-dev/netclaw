// -----------------------------------------------------------------------
// <copyright file="SleepStandIn.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Tests.Utilities;

/// <summary>
/// Creates the executable that a test starts as a stand-in for <c>netclawd</c>.
/// </summary>
internal static class SleepStandIn
{
    private const string SleepPath = "/bin/sleep";

    /// <summary>
    /// Creates an executable at <paramref name="path"/> that behaves as <c>sleep</c>.
    /// </summary>
    /// <remarks>
    /// On Linux, <c>execve</c> fails with ETXTBSY while any process holds a write
    /// descriptor to the file. A test that copies a file and starts it at once races with
    /// every other thread that forks: the child of that fork inherits the write descriptor
    /// until it calls <c>execve</c>. A rename does not help, because the inode stays the
    /// same. A symbolic link has no write descriptor to race with, because nothing opens
    /// the real <c>sleep</c> for write. The kernel sets the process name from the link
    /// name, so the stand-in still shows as <c>netclawd</c>. Other platforms copy the file.
    /// </remarks>
    public static void Create(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            // Ubuntu uutils rejects the alternate executable name that this fixture requires.
            File.CreateSymbolicLink(path, File.Exists("/usr/bin/gnusleep") ? "/usr/bin/gnusleep" : SleepPath);
            return;
        }

        File.Copy(SleepPath, path, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
