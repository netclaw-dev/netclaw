// -----------------------------------------------------------------------
// <copyright file="WindowsJunction.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;

namespace Netclaw.Tests.Utilities;

/// <summary>
/// Creates a Windows directory junction with <c>mklink /J</c>. A junction needs
/// no administrator rights or developer mode, so Windows CI can run link-escape
/// regressions that a symbolic link cannot run there.
/// </summary>
public static class WindowsJunction
{
    public static async Task CreateAsync(string link, string target, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("cmd.exe did not start.");
        // Read both streams while the process runs. A read after the exit can deadlock
        // when the child fills a pipe buffer before it exits.
        var standardOutputTask = process.StandardOutput.ReadToEndAsync(ct);
        var standardErrorTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"mklink failed: {standardOutput}{standardError}");

        if ((File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
            throw new InvalidOperationException("mklink did not create a reparse point.");
    }

    /// <summary>
    /// Removes each directory junction or directory link below <paramref name="root"/>
    /// without a visit to its target. A recursive <see cref="Directory.Delete(string, bool)"/>
    /// treats a junction as a volume mount point and fails with "The parameter is
    /// incorrect". A non-recursive delete removes only the link entry. Call this
    /// before the recursive delete of a test root. The method does nothing on POSIX.
    /// </summary>
    public static void RemoveJunctionsUnder(string root)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(root))
            return;

        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            RecurseSubdirectories = false
        };
        foreach (var directory in Directory.EnumerateDirectories(root, "*", options))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(directory, recursive: false);
            else
                RemoveJunctionsUnder(directory);
        }
    }
}
