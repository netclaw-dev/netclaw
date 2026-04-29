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
        try { Directory.Delete(Path, recursive: true); }
        catch (DirectoryNotFoundException) { } // slopwatch-ignore: SW003 test cleanup — directory may already be gone
    }
}
