// -----------------------------------------------------------------------
// <copyright file="DisposableTempDir.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using System.Runtime.CompilerServices;

namespace Netclaw.Tests.Utilities;

internal sealed class DisposableTempDir : IDisposable
{
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

    public void Dispose()
    {
        if (!Directory.Exists(Path))
            return;

        // Windows refuses to delete a SQLite file while a pooled connection holds it.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Retry loop for Windows CI where SQLite pooled connections can
        // briefly hold file handles after the test completes.
        for (var i = 0; i < 5; i++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException) when (i < 4) // slopwatch-ignore: SW003 test cleanup retry
            {
                Thread.Sleep(50 * (i + 1));
            }
            catch (UnauthorizedAccessException) when (i < 4) // slopwatch-ignore: SW003 test cleanup retry
            {
                // A test can leave a read-only file. Windows refuses to delete it.
                ClearReadOnlyAttributes(Path);
                Thread.Sleep(50 * (i + 1));
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
