// -----------------------------------------------------------------------
// <copyright file="DisposableTempDir.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System.Runtime.CompilerServices;

namespace Netclaw.Tests.Utilities;

internal sealed class DisposableTempDir : IDisposable
{
    private const int MaxAttempts = 8;

    public string Path { get; }

    /// <summary>
    /// The folder name carries the name of the test file that made it. When a
    /// test leaks the folder, the leak report names the file.
    /// </summary>
    public DisposableTempDir([CallerFilePath] string callerFile = "")
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"netclaw-test-{System.IO.Path.GetFileNameWithoutExtension(callerFile)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public void Dispose() => Delete(Path);

    /// <summary>
    /// Deletes a folder that a test owns. <see cref="TestSessionTempDirectory"/>
    /// deletes its folder through this method too.
    /// </summary>
    internal static void Delete(string path)
    {
        if (!Directory.Exists(path))
            return;

        // Windows refuses to delete a file or a working directory that a process
        // still holds. A killed process tree can hold one for a short time
        // after the test ends. SQLite owners must clear their own pools.
        // Retry for a few seconds.
        // The delete succeeds on the first try on Linux and macOS.
        for (var i = 0; i < MaxAttempts; i++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                // On Windows, a recursive delete ignores ERROR_DIR_NOT_EMPTY and returns
                // without an error (dotnet/runtime FileSystem.Windows.cs, RemoveDirectoryRecursive).
                // A file that a writer adds during the delete, or a file that stays
                // delete-pending, then leaves the tree in place. Linux and macOS throw
                // IOException for the same state. Throw it here too, so the loop below
                // handles both platforms the same way and a real leak fails its own test.
                if (Directory.Exists(path))
                    throw new IOException($"The folder '{path}' still exists after a recursive delete. "
                        + $"Entries left: {DescribeEntries(path)}");
                return;
            }
            catch (IOException) when (i < MaxAttempts - 1) // slopwatch-ignore: SW003 test cleanup retry
            {
                Thread.Sleep(100 * (i + 1));
            }
            catch (UnauthorizedAccessException) when (i < MaxAttempts - 1) // slopwatch-ignore: SW003 test cleanup retry
            {
                // A test can leave a read-only file, and git marks its object files
                // read-only. Windows refuses to delete such a file.
                ClearReadOnlyAttributes(path);
                Thread.Sleep(100 * (i + 1));
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
        }
    }

    private static string DescribeEntries(string root)
    {
        var entries = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(entry => System.IO.Path.GetRelativePath(root, entry))
            .Take(10)
            .ToArray();
        return entries.Length == 0 ? "(none)" : string.Join(", ", entries);
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
        }
    }
}
