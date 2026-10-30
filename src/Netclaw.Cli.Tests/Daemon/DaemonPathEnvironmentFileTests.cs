// -----------------------------------------------------------------------
// <copyright file="DaemonPathEnvironmentFileTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Daemon;

/// <summary>
/// Unit tests for the daemon PATH provisioning contract shared by install
/// (producer), doctor --fix (rehydrator), and the systemd PATH doctor check
/// (validator). <c>InstallAsync</c>/<c>UninstallAsync</c> themselves are not
/// driven here: they invoke real <c>systemctl</c>/<c>loginctl</c> against the
/// live <c>netclaw.service</c> and the real <c>~/.config</c> unit path, so
/// exercising them in-process would mutate the developer's own service. The
/// pure builders below are exactly the content those methods write and read.
/// </summary>
public sealed class DaemonPathEnvironmentFileTests : IDisposable
{
    private readonly DisposableTempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ComposePathValue_InstallDirFirst_ThenCapture_ThenDedupedFloor()
        => Assert.Equal(
            "/opt/netclaw:/home/u/.dotnet:/usr/bin:/usr/local/bin:/bin:/usr/sbin:/sbin",
            DaemonPathEnvironmentFile.ComposePathValue("/opt/netclaw", "/home/u/.dotnet:/usr/bin"));

    [Fact]
    public void ComposePathValue_EmptyCapture_StillHasFunctionalFloor()
    {
        // An empty PATH on the installing shell must NOT leave the daemon with installDir
        // alone — the system floor is always guaranteed so /bin/sh etc. still resolve.
        const string expected = "/opt/netclaw:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin";
        Assert.Equal(expected, DaemonPathEnvironmentFile.ComposePathValue("/opt/netclaw", null));
        Assert.Equal(expected, DaemonPathEnvironmentFile.ComposePathValue("/opt/netclaw", ""));
    }

    [Fact]
    public void ComposePathValue_DropsEmptyElements()
    {
        // POSIX treats an empty PATH element as the current directory — an exec-hijack
        // vector for a daemon running `bash -c` in an agent-controlled workspace.
        var value = DaemonPathEnvironmentFile.ComposePathValue("/opt/netclaw", "/a::/b:");

        Assert.DoesNotContain("::", value, StringComparison.Ordinal);
        Assert.All(value.Split(':'), Assert.NotEmpty);
        Assert.StartsWith("/opt/netclaw:/a:/b:", value, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_ThenReadPathValue_RoundTrips()
    {
        var content = DaemonPathEnvironmentFile.Merge(null, "/opt/netclaw", "/usr/bin");

        Assert.Equal("PATH=/opt/netclaw:/usr/bin:/usr/local/bin:/bin:/usr/sbin:/sbin\n", content);
        Assert.Equal(
            "/opt/netclaw:/usr/bin:/usr/local/bin:/bin:/usr/sbin:/sbin",
            DaemonPathEnvironmentFile.ReadPathValue(content));
    }

    [Fact]
    public void ReadPathValue_NoPathAssignment_ReturnsNull()
        => Assert.Null(DaemonPathEnvironmentFile.ReadPathValue("FOO=bar\nBAZ=qux\n"));

    [Theory]
    [InlineData("/opt/netclaw:/usr/bin", "/opt/netclaw", true)]
    [InlineData("/usr/local/bin:/usr/bin", "/opt/netclaw", false)]
    public void PathContainsDirectory_MatchesExactEntry(string pathValue, string dir, bool expected)
        => Assert.Equal(expected, DaemonPathEnvironmentFile.PathContainsDirectory(pathValue, dir));

    [Fact]
    public void TryGetInstallDir_FromExecStart_StripsBinaryAndArgs()
    {
        var lines = new[] { "[Service]", "ExecStart=/opt/netclaw/netclawd --foreground" };

        Assert.True(DaemonPathEnvironmentFile.TryGetInstallDir(lines, out var dir));
        Assert.Equal("/opt/netclaw", dir);
    }

    [Fact]
    public void TryGetInstallDir_NoExecStart_ReturnsFalse()
        => Assert.False(DaemonPathEnvironmentFile.TryGetInstallDir(new[] { "[Service]" }, out _));

    [Fact]
    public void TryGetEnvironmentFilePath_StripsTolerantDashPrefix()
    {
        var lines = new[] { "EnvironmentFile=-/home/u/.netclaw/config/daemon.env" };

        Assert.True(DaemonPathEnvironmentFile.TryGetEnvironmentFilePath(lines, out var path));
        Assert.Equal("/home/u/.netclaw/config/daemon.env", path);
    }

    [Fact]
    public void TryGetEnvironmentFilePath_Absent_ReturnsFalse()
        => Assert.False(DaemonPathEnvironmentFile.TryGetEnvironmentFilePath(
            new[] { "ExecStart=/opt/netclaw/netclawd" }, out _));

    [Fact]
    public void BuildDaemonUnitContent_WiresEnvironmentFile_AndOmitsInlinePath()
    {
        var unit = DaemonManager.BuildDaemonUnitContent(
            "/opt/netclaw/netclawd",
            "/opt/netclaw/netclaw",
            "/home/u/.netclaw/config/daemon.env",
            "/home/u/.netclaw");

        Assert.Contains("EnvironmentFile=-/home/u/.netclaw/config/daemon.env", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment=PATH=", unit, StringComparison.Ordinal);
        Assert.Contains("ExecStart=/opt/netclaw/netclawd", unit, StringComparison.Ordinal);
        Assert.Contains("ExecStop=/opt/netclaw/netclaw daemon stop", unit, StringComparison.Ordinal);

        // Producer/consumer contract (netclaw-dev/netclaw#1665): the generated unit's
        // TimeoutStopSec= must track DaemonConfig.SystemdTimeoutStopSec, not a stale literal,
        // so systemd never SIGKILLs the cgroup out from under a still-legitimately-waiting
        // `netclaw daemon stop` (ExecStop=).
        Assert.Contains(
            $"TimeoutStopSec={(int)DaemonConfig.SystemdTimeoutStopSec.TotalSeconds}",
            unit,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDaemonUnitContent_NamesTheHome()
    {
        var unit = DaemonManager.BuildDaemonUnitContent("/opt/netclaw/netclawd", "/opt/netclaw/netclaw", "/x/daemon.env", "/srv/netclaw");

        Assert.Contains($"Environment=NETCLAW_HOME={Path.GetFullPath("/srv/netclaw")}", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveDaemonEnvironmentFile_DeletesFile_AndIsIdempotent()
    {
        // Covers the uninstall env-file-removal contract without driving the
        // systemctl-coupled UninstallAsync.
        var basePath = Path.Combine(_temp.Path, Guid.NewGuid().ToString("N"));
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();
        File.WriteAllText(paths.DaemonEnvironmentFilePath, "PATH=/opt/netclaw:/usr/bin\n");
        var manager = new DaemonManager(paths, TimeProvider.System);

        manager.RemoveDaemonEnvironmentFile();
        Assert.False(File.Exists(paths.DaemonEnvironmentFilePath));

        // Idempotent — a second call on an already-removed file must not throw.
        manager.RemoveDaemonEnvironmentFile();
    }

    // ── Merge: operator lines survive a rewrite ──

    private const string ExpectedPath = "PATH=/opt/netclaw:/usr/bin:/usr/local/bin:/bin:/usr/sbin:/sbin";

    private static string Merge(string? existing)
        => DaemonPathEnvironmentFile.Merge(existing, "/opt/netclaw", "/usr/bin");

    [Fact]
    public void Merge_without_existing_text_writes_one_path_line()
    {
        Assert.Equal($"{ExpectedPath}\n", Merge(null));
        Assert.Equal($"{ExpectedPath}\n", Merge(string.Empty));
    }

    [Fact]
    public void Merge_keeps_operator_lines_comments_and_blank_lines_and_replaces_path_in_place()
    {
        const string existing = "NETCLAW_HOME=/x/home\n# proxy\nHTTP_PROXY=http://proxy.example:3128\n\nPATH=/old\nFOO=bar\n";

        Assert.Equal(
            $"NETCLAW_HOME=/x/home\n# proxy\nHTTP_PROXY=http://proxy.example:3128\n\n{ExpectedPath}\nFOO=bar\n",
            Merge(existing));
    }

    [Fact]
    public void Merge_drops_a_later_path_line()
        => Assert.Equal(
            $"A=1\n{ExpectedPath}\nB=2\n",
            Merge("A=1\nPATH=/old\nB=2\n  PATH=/older\n"));

    [Fact]
    public void Merge_adds_path_at_the_end_when_none_exists()
        => Assert.Equal($"A=1\n# c\n{ExpectedPath}\n", Merge("A=1\n# c\n"));

    [Fact]
    public void Merge_keeps_a_line_that_only_contains_path_text()
    {
        // A key that merely ends in PATH, and an export line, are operator lines.
        const string existing = "MANPATH=/m\nexport PATH=/x\n";

        Assert.Equal($"MANPATH=/m\nexport PATH=/x\n{ExpectedPath}\n", Merge(existing));
    }

    // systemd trims the key before the first '=', so these lines assign PATH.
    [Fact]
    public void Merge_replaces_a_path_line_with_whitespace_around_the_key_in_place()
    {
        Assert.Equal($"A=1\n{ExpectedPath}\nB=2\n", Merge("A=1\nPATH = /old\nB=2\n"));
        Assert.Equal($"A=1\n{ExpectedPath}\nB=2\n", Merge("A=1\nPATH\t=/old\nB=2\n"));
        Assert.Equal($"A=1\n{ExpectedPath}\nB=2\n", Merge("A=1\n  PATH=/old\nB=2\n"));
    }

    [Fact]
    public void Merge_drops_a_later_path_line_with_whitespace_around_the_key()
        => Assert.Equal(
            $"{ExpectedPath}\nB=2\n",
            Merge("PATH=/old\nB=2\nPATH = /older\nPATH\t=/oldest\n"));

    [Fact]
    public void ReadPathValue_reads_a_path_line_with_whitespace_around_the_key()
    {
        Assert.Equal("/x", DaemonPathEnvironmentFile.ReadPathValue("A=1\nPATH = /x\n"));
        Assert.Equal("/x", DaemonPathEnvironmentFile.ReadPathValue("PATH\t=/x\r\n"));
        Assert.Null(DaemonPathEnvironmentFile.ReadPathValue("export PATH=/x\nMANPATH=/m\n"));
    }

    [Fact]
    public void Merge_adds_a_missing_final_newline()
        => Assert.Equal($"A=1\n{ExpectedPath}\n", Merge("A=1\nPATH=/old"));

    [Fact]
    public void Merge_keeps_crlf_endings_and_is_idempotent()
    {
        var crlf = Merge("A=1\r\nPATH=/old\r\nB=2\r\n");
        var appended = Merge("A=1\r\n");

        Assert.Equal($"A=1\r\n{ExpectedPath}\r\nB=2\r\n", crlf);
        Assert.Equal($"A=1\r\n{ExpectedPath}\r\n", appended);
        Assert.Equal(crlf, Merge(crlf));
    }

    [Fact]
    public void Merge_is_idempotent()
    {
        var once = Merge("A=1\nPATH=/old\nPATH=/older");

        Assert.Equal(once, Merge(once));
    }
}
