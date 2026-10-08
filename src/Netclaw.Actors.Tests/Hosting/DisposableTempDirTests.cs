// -----------------------------------------------------------------------
// <copyright file="DisposableTempDirTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;

namespace Netclaw.Actors.Tests.Hosting;

public sealed class DisposableTempDirTests
{
    // Git marks its object files read-only. Windows refuses to delete a
    // read-only file. Linux and macOS refuse when the folder is not writable.
    [Fact]
    public void Delete_removes_a_tree_with_read_only_entries()
    {
        var dir = new DisposableTempDir();
        var objects = Directory.CreateDirectory(Path.Combine(dir.Path, ".git", "objects", "d7")).FullName;
        var file = Path.Combine(objects, "4d258442c7c65512eafab474568dd706c430");
        File.WriteAllText(file, "blob");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(objects, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Assert.ThrowsAny<UnauthorizedAccessException>(() => Directory.Delete(dir.Path, recursive: true));

        DisposableTempDir.Delete(dir.Path);

        Assert.False(Directory.Exists(dir.Path));
    }
}
