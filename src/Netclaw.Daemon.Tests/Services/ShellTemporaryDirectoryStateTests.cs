// -----------------------------------------------------------------------
// <copyright file="ShellTemporaryDirectoryStateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Daemon.Services;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class ShellTemporaryDirectoryStateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netclaw-shell-temp-state-{Guid.NewGuid():N}");

    public ShellTemporaryDirectoryStateTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ManagedTemporaryLayout Layout(string folder)
        => new ManagedTemporaryLayout.PrivateBase(new ManagedTemporaryBase(folder));

    [Fact]
    public void Writable_base_reports_no_problem_and_leaves_no_probe_file()
    {
        var folder = Path.Combine(_root, "netclaw-1000");

        var state = ShellTemporaryDirectoryState.Probe(Layout(folder));

        Assert.Null(state.Problem);
        Assert.Empty(Directory.GetFileSystemEntries(folder));
    }

    [Fact]
    public void Unwritable_base_names_the_path_and_the_fix()
    {
        // A regular file in the way makes directory creation fail on any filesystem, including as root.
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, string.Empty);
        var folder = Path.Combine(blocker, "netclaw-1000");

        var state = ShellTemporaryDirectoryState.Probe(Layout(folder));

        Assert.NotNull(state.Problem);
        Assert.Contains(folder, state.Problem);
        Assert.Contains("tmpfs", state.Problem);
        Assert.Contains("TMPDIR", state.Problem);
    }

    [Fact]
    public void Layout_inside_session_storage_has_nothing_to_probe()
    {
        var state = ShellTemporaryDirectoryState.Probe(ManagedTemporaryLayout.InsideSessionStorage.Instance);

        Assert.Null(state.Problem);
    }
}
