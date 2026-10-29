// -----------------------------------------------------------------------
// <copyright file="ShellTemporaryDirectoryStateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
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
        // One sentence for the reason, then the hint; the path appears once.
        Assert.Contains(". Mount a writable tmpfs", state.Problem);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(state.Problem, System.Text.RegularExpressions.Regex.Escape(folder)));
    }

    [Fact]
    public void Layout_inside_session_storage_has_nothing_to_probe()
    {
        var state = ShellTemporaryDirectoryState.Probe(ManagedTemporaryLayout.InsideSessionStorage.Instance);

        Assert.Null(state.Problem);
    }

    private static bool ShellAccepts(string folder)
        => ManagedTemporaryEnvironment.EnsureFolders(
            ManagedTemporaryLocation.CreatePrivateParent(new ManagedTemporaryBase(folder), "parity-key")) is null;

    private void AssertParity(string folder, bool expected)
    {
        var shell = ShellAccepts(folder);
        var probe = ShellTemporaryDirectoryState.Probe(Layout(folder)).Problem is null;
        Assert.Equal(expected, shell);
        Assert.Equal(shell, probe);
    }

    [Fact]
    public void Probe_agrees_with_the_shell_checks_for_a_writable_base()
    {
        if (OperatingSystem.IsWindows())
            return;

        AssertParity(Path.Combine(_root, "netclaw-1000"), expected: true);
    }

    [Fact]
    public void Probe_agrees_with_the_shell_checks_when_the_base_is_blocked_by_a_file()
    {
        if (OperatingSystem.IsWindows())
            return;

        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, string.Empty);
        AssertParity(Path.Combine(blocker, "netclaw-1000"), expected: false);
    }

    [Fact]
    public void Probe_agrees_with_the_shell_checks_for_an_existing_world_writable_base()
    {
        if (OperatingSystem.IsWindows())
            return;

        var folder = Path.Combine(_root, "netclaw-1000");
        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(folder, (UnixFileMode)0x1FF);
        AssertParity(folder, expected: false);

        var problem = ShellTemporaryDirectoryState.Probe(Layout(folder)).Problem;
        Assert.Contains("group or other access", problem);
        Assert.DoesNotContain("tmpfs", problem);
    }

    [Fact]
    public void Probe_agrees_with_the_shell_checks_for_a_symlinked_base()
    {
        if (OperatingSystem.IsWindows())
            return;

        var real = Path.Combine(_root, "real");
        Directory.CreateDirectory(real, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var link = Path.Combine(_root, "netclaw-1000");
        Directory.CreateSymbolicLink(link, real);
        AssertParity(link, expected: false);
    }

    [Fact]
    public void Probe_refuses_a_world_writable_base_so_a_planted_link_inside_it_is_never_reached()
    {
        if (OperatingSystem.IsWindows())
            return;

        var folder = Path.Combine(_root, "netclaw-1000");
        Directory.CreateDirectory(folder);
        File.SetUnixFileMode(folder, (UnixFileMode)0x1FF);
        var victim = Path.Combine(_root, "victim.txt");
        File.WriteAllText(victim, "precious");
        File.CreateSymbolicLink(Path.Combine(folder, $".probe-{Environment.ProcessId}"), victim);

        // The shell refuses a base that others can write to, so the probe stops at the base check
        // before it creates anything; the link planted in it is not followed.
        AssertParity(folder, expected: false);

        Assert.Equal("precious", File.ReadAllText(victim));
    }
}
