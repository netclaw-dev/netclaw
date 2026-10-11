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
        // still holds. A killed process tree and a pooled SQLite connection can hold
        // one for a short time after the test ends. Retry for a few seconds.
        // The delete succeeds on the first try on Linux and macOS.
        for (var i = 0; i < MaxAttempts; i++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (i < MaxAttempts - 1) // slopwatch-ignore: SW003 test cleanup retry
            {
                // Clear the pools only after a failure. The call closes the idle
                // connections of every database in the process, which other tests
                // can observe. The first call in a process also loads the SQLite
                // native library, which a test without SQLite does not need.
                if (i == 0)
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

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
