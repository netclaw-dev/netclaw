// -----------------------------------------------------------------------
// <copyright file="DisposableTempDir.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Tests.Utilities;

internal sealed class DisposableTempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"netclaw-test-{Guid.NewGuid():N}");

    public DisposableTempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        if (!Directory.Exists(Path))
            return;

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
                Thread.Sleep(50 * (i + 1));
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
        }
    }
}
