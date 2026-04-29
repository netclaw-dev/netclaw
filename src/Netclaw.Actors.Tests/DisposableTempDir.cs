// -----------------------------------------------------------------------
// <copyright file="DisposableTempDir.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Actors.Tests;

internal sealed class DisposableTempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"netclaw-test-{Guid.NewGuid():N}");

    public DisposableTempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
