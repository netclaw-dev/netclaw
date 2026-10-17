// -----------------------------------------------------------------------
// <copyright file="DaemonManagerInstallTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Pins <c>daemon install</c> / <c>daemon uninstall</c> on hosts with and without a user systemd.
/// HOME points at a temp dir so the unit lands there, never in the developer's real
/// <c>~/.config/systemd/user</c>.
/// </summary>
[Collection(LegacyModelEnvironmentCollection.Name)]
public sealed class DaemonManagerInstallTests : IDisposable
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly string? _originalHome;
    private readonly string? _originalDaemonPath;
    private readonly string _unitPath;

    public DaemonManagerInstallTests()
    {
        _originalHome = Environment.GetEnvironmentVariable("HOME");
        _originalDaemonPath = Environment.GetEnvironmentVariable("NETCLAW_DAEMON_PATH");

        var home = Path.Combine(_dir.Path, "home");
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("HOME", home);

        var daemonBinary = Path.Combine(_dir.Path, "daemon-binary");
        File.WriteAllText(daemonBinary, string.Empty);
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_PATH", daemonBinary);

        _paths = new NetclawPaths(Path.Combine(_dir.Path, "netclaw-home"));
        _paths.EnsureDirectoriesExist();
        _unitPath = DaemonManager.SystemdUserUnitFilePath;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_PATH", _originalDaemonPath);
        _dir.Dispose();
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Install_WithoutUserSystemd_ExplainsAndWritesNothing()
    {
        var runner = new FakeSystemCommandRunner(
            new SystemCommandResult(1, "Failed to connect to bus: No medium found"));
        var sut = CreateManager(runner);

        var result = await sut.InstallAsync([]);

        Assert.False(result.Success);
        Assert.Contains("systemd user services are not available on this host", result.Message);
        Assert.Contains("Failed to connect to bus", result.Message);
        Assert.Contains("container supervisor", result.Message);
        Assert.Contains("netclaw daemon start", result.Message);
        Assert.Equal([("systemctl", "--user show-environment")], runner.Commands);
        Assert.False(Directory.Exists(DaemonManager.SystemdUserUnitDirectory));
        Assert.False(File.Exists(_unitPath));
        Assert.False(File.Exists(_paths.DaemonEnvironmentFilePath));
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Install_WithMissingSystemctl_ExplainsAndWritesNothing()
    {
        var runner = new FakeSystemCommandRunner(
            new SystemCommandResult(-1, string.Empty, "No such file or directory"));
        var sut = CreateManager(runner);

        var result = await sut.InstallAsync([]);

        Assert.False(result.Success);
        Assert.Contains("systemd user services are not available on this host", result.Message);
        Assert.False(File.Exists(_unitPath));
        Assert.False(File.Exists(_paths.DaemonEnvironmentFilePath));
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Install_WithUserSystemd_WritesUnitAndEnablesIt()
    {
        var runner = new FakeSystemCommandRunner();
        var sut = CreateManager(runner);

        var result = await sut.InstallAsync([]);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(_unitPath));
        Assert.True(File.Exists(_paths.DaemonEnvironmentFilePath));
        Assert.Equal(
            [
                "systemctl --user show-environment",
                "systemctl --user daemon-reload",
                "systemctl --user enable netclaw.service",
                $"loginctl enable-linger {Environment.UserName}"
            ],
            runner.Commands.Select(c => $"{c.Command} {c.Arguments}").ToArray());
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Uninstall_RemovesUnitLeftByAFailedInstall_WhenSystemdIsMissing()
    {
        Directory.CreateDirectory(DaemonManager.SystemdUserUnitDirectory);
        File.WriteAllText(_unitPath, "[Service]\nExecStart=/opt/netclaw/netclawd\n");
        File.WriteAllText(_paths.DaemonEnvironmentFilePath, "PATH=/usr/bin\n");
        var runner = new FakeSystemCommandRunner(
            new SystemCommandResult(-1, string.Empty, "No such file or directory"));
        var sut = CreateManager(runner);

        var result = await sut.UninstallAsync();

        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(_unitPath));
        Assert.False(File.Exists(_paths.DaemonEnvironmentFilePath));
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Install_keeps_operator_lines_in_daemon_env()
    {
        File.WriteAllText(
            _paths.DaemonEnvironmentFilePath,
            "NETCLAW_HOME=/x/home\n# proxy for the shell tool\nHTTP_PROXY=http://proxy.example:3128\nPATH=/old\n");
        var sut = CreateManager(new FakeSystemCommandRunner());

        var first = await sut.InstallAsync([]);
        var afterFirst = File.ReadAllText(_paths.DaemonEnvironmentFilePath);
        var second = await sut.InstallAsync([]);

        Assert.True(first.Success, first.Message);
        Assert.True(second.Success, second.Message);
        var lines = afterFirst.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("NETCLAW_HOME=/x/home", lines[0]);
        Assert.Equal("# proxy for the shell tool", lines[1]);
        Assert.Equal("HTTP_PROXY=http://proxy.example:3128", lines[2]);
        Assert.StartsWith("PATH=", lines[3], StringComparison.Ordinal);
        Assert.DoesNotContain("PATH=/old", afterFirst, StringComparison.Ordinal);
        Assert.Single(lines, line => line.StartsWith("PATH=", StringComparison.Ordinal));
        Assert.Equal(afterFirst, File.ReadAllText(_paths.DaemonEnvironmentFilePath));
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Install_with_env_writes_netclaw_lines_once_and_is_idempotent()
    {
        var sut = CreateManager(new FakeSystemCommandRunner());
        string[] entries = ["NETCLAW_DAEMON__PORT=56114", "NETCLAW_HOME=/x/home"];

        var first = await sut.InstallAsync(entries);
        var afterFirst = File.ReadAllText(_paths.DaemonEnvironmentFilePath);
        var second = await sut.InstallAsync(entries);

        Assert.True(first.Success, first.Message);
        Assert.True(second.Success, second.Message);
        var lines = afterFirst.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines, line => line == "NETCLAW_DAEMON__PORT=56114");
        Assert.Single(lines, line => line == "NETCLAW_HOME=/x/home");
        Assert.Single(lines, line => line.StartsWith("PATH=", StringComparison.Ordinal));
        Assert.Equal(afterFirst, File.ReadAllText(_paths.DaemonEnvironmentFilePath));
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Theory(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    [InlineData("NETCLAW_Providers__main__ApiKey=sk-test-value")]
    [InlineData("NETCLAW_Mattermost__ServerUrl=https://user:pw-test-value@chat.example")]
    public async Task Install_with_env_refuses_credential_keys_and_writes_nothing(string entry)
    {
        const string seeded = "HTTP_PROXY=http://proxy.example:3128\nPATH=/old\n";
        File.WriteAllText(_paths.DaemonEnvironmentFilePath, seeded);
        var runner = new FakeSystemCommandRunner();
        var sut = CreateManager(runner);

        var result = await sut.InstallAsync([entry]);

        Assert.False(result.Success);
        Assert.Contains("netclaw secrets set", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-value", result.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
        Assert.Equal(seeded, File.ReadAllText(_paths.DaemonEnvironmentFilePath));
        Assert.False(File.Exists(_unitPath));
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Theory(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    [InlineData("HTTP_PROXY=http://proxy.example:3128")]
    [InlineData("NOEQUALS")]
    [InlineData("NETCLAW_HOME=/x\ny")]
    public async Task Install_with_env_refuses_bad_entries(string entry)
    {
        var runner = new FakeSystemCommandRunner();
        var sut = CreateManager(runner);

        var result = await sut.InstallAsync([entry]);

        Assert.False(result.Success);
        Assert.DoesNotContain("proxy.example", result.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Commands);
        Assert.False(File.Exists(_paths.DaemonEnvironmentFilePath));
        Assert.False(File.Exists(_unitPath));
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Theory(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Install_writes_daemon_env_with_mode_0600(bool seedLooseFile)
    {
        if (OperatingSystem.IsWindows())
            return;

        if (seedLooseFile)
        {
            File.WriteAllText(_paths.DaemonEnvironmentFilePath, "HTTP_PROXY=http://proxy.example:3128\n");
            File.SetUnixFileMode(
                _paths.DaemonEnvironmentFilePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        var result = await CreateManager(new FakeSystemCommandRunner()).InstallAsync([]);

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(_paths.DaemonEnvironmentFilePath));
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Uninstall_keeps_daemon_env_with_operator_lines_and_names_the_path()
    {
        const string content = "NETCLAW_HOME=/x/home\nPATH=/usr/bin\n";
        File.WriteAllText(_paths.DaemonEnvironmentFilePath, content);
        var sut = CreateManager(new FakeSystemCommandRunner());

        var result = await sut.UninstallAsync();

        Assert.True(result.Success, result.Message);
        Assert.StartsWith("Service uninstalled.", result.Message, StringComparison.Ordinal);
        Assert.Contains(_paths.DaemonEnvironmentFilePath, result.Message, StringComparison.Ordinal);
        Assert.Equal(content, File.ReadAllText(_paths.DaemonEnvironmentFilePath));
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Uninstall_sets_mode_0600_on_a_kept_daemon_env()
    {
        if (OperatingSystem.IsWindows())
            return;

        File.WriteAllText(_paths.DaemonEnvironmentFilePath, "NETCLAW_HOME=/x/home\nPATH=/usr/bin\n");
        File.SetUnixFileMode(
            _paths.DaemonEnvironmentFilePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        var result = await CreateManager(new FakeSystemCommandRunner()).UninstallAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(_paths.DaemonEnvironmentFilePath));
    }

    // Fleet tools can link daemon.env to a managed file. Install writes through the link and
    // never replaces the link with a regular file.
    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Install_writes_through_a_symlinked_daemon_env()
    {
        if (OperatingSystem.IsWindows())
            return;

        var targetDir = Path.Combine(_dir.Path, "managed");
        Directory.CreateDirectory(targetDir);
        var target = Path.Combine(targetDir, "daemon.env");
        File.WriteAllText(target, "HTTP_PROXY=http://proxy.example:3128\nPATH=/old\n");
        File.CreateSymbolicLink(_paths.DaemonEnvironmentFilePath, target);

        var result = await CreateManager(new FakeSystemCommandRunner()).InstallAsync(["NETCLAW_HOME=/x/home"]);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(new FileInfo(_paths.DaemonEnvironmentFilePath).LinkTarget);
        var text = File.ReadAllText(target);
        Assert.StartsWith("HTTP_PROXY=http://proxy.example:3128\n", text, StringComparison.Ordinal);
        Assert.Contains("NETCLAW_HOME=/x/home\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PATH=/old", text, StringComparison.Ordinal);
        Assert.Equal(text, File.ReadAllText(_paths.DaemonEnvironmentFilePath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));
        Assert.Equal([target], Directory.GetFiles(targetDir));
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Install_refuses_with_the_path_when_the_symlink_target_cannot_be_written()
    {
        if (OperatingSystem.IsWindows())
            return;

        var targetDir = Path.Combine(_dir.Path, "locked");
        Directory.CreateDirectory(targetDir);
        var target = Path.Combine(targetDir, "daemon.env");
        const string original = "HTTP_PROXY=http://proxy.example:3128\nPATH=/old\n";
        File.WriteAllText(target, original);
        File.CreateSymbolicLink(_paths.DaemonEnvironmentFilePath, target);
        File.SetUnixFileMode(targetDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = await CreateManager(new FakeSystemCommandRunner()).InstallAsync([]);

            Assert.False(result.Success);
            Assert.Contains(target, result.Message, StringComparison.Ordinal);
            Assert.NotNull(new FileInfo(_paths.DaemonEnvironmentFilePath).LinkTarget);
            Assert.Equal(original, File.ReadAllText(target));
            Assert.False(File.Exists(_unitPath));
        }
        finally
        {
            File.SetUnixFileMode(targetDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Install_writes_through_a_relative_symlink()
    {
        var managed = Path.Combine(Path.GetDirectoryName(_paths.DaemonEnvironmentFilePath)!, "managed");
        Directory.CreateDirectory(managed);
        var target = Path.Combine(managed, "real.env");
        File.WriteAllText(target, "HTTP_PROXY=http://proxy.example:3128\nPATH=/old\n");
        File.CreateSymbolicLink(_paths.DaemonEnvironmentFilePath, Path.Combine("managed", "real.env"));

        var result = await CreateManager(new FakeSystemCommandRunner()).InstallAsync([]);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(new FileInfo(_paths.DaemonEnvironmentFilePath).LinkTarget);
        Assert.StartsWith("HTTP_PROXY=http://proxy.example:3128\n", File.ReadAllText(target), StringComparison.Ordinal);
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Install_over_a_symlink_loop_and_over_a_symlink_to_a_directory_fails_and_writes_nothing()
    {
        var env = _paths.DaemonEnvironmentFilePath;
        File.CreateSymbolicLink(env, env + ".b");
        File.CreateSymbolicLink(env + ".b", env);

        var loop = await CreateManager(new FakeSystemCommandRunner()).InstallAsync([]);

        Assert.False(loop.Success);
        Assert.False(File.Exists(_unitPath));

        File.Delete(env);
        File.Delete(env + ".b");
        var targetDir = Path.Combine(_dir.Path, "adir");
        Directory.CreateDirectory(targetDir);
        File.CreateSymbolicLink(env, targetDir);

        var directory = await CreateManager(new FakeSystemCommandRunner()).InstallAsync([]);

        Assert.False(directory.Success);
        Assert.Empty(Directory.GetFileSystemEntries(targetDir));
        Assert.False(File.Exists(_unitPath));
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Install_over_a_dangling_symlink_names_the_link_and_the_missing_target()
    {
        var target = Path.Combine(_dir.Path, "later", "daemon.env");
        File.CreateSymbolicLink(_paths.DaemonEnvironmentFilePath, target);

        var result = await CreateManager(new FakeSystemCommandRunner()).InstallAsync([]);

        Assert.False(result.Success);
        Assert.Contains(_paths.DaemonEnvironmentFilePath, result.Message, StringComparison.Ordinal);
        Assert.Contains(target, result.Message, StringComparison.Ordinal);
        Assert.Contains("missing", result.Message, StringComparison.Ordinal);
        Assert.NotNull(new FileInfo(_paths.DaemonEnvironmentFilePath).LinkTarget);
        Assert.False(File.Exists(target));
        Assert.False(Directory.Exists(Path.GetDirectoryName(target)));
        Assert.False(File.Exists(_unitPath));
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Install_refuses_a_symlink_to_a_file_that_another_user_owns()
    {
        Assert.SkipWhen(UnixOwnership.CurrentUserId() == 0, "Root owns the target, so the target is not foreign.");
        const string foreign = "/etc/passwd";
        File.CreateSymbolicLink(_paths.DaemonEnvironmentFilePath, foreign);

        var result = await CreateManager(new FakeSystemCommandRunner()).InstallAsync([]);

        Assert.False(result.Success);
        Assert.Contains(_paths.DaemonEnvironmentFilePath, result.Message, StringComparison.Ordinal);
        Assert.Contains(foreign, result.Message, StringComparison.Ordinal);
        Assert.Contains("another user", result.Message, StringComparison.Ordinal);
        Assert.Equal(foreign, new FileInfo(_paths.DaemonEnvironmentFilePath).LinkTarget);
        Assert.False(File.Exists(_unitPath));
        Assert.Empty(Directory.GetFiles("/etc", "passwd.tmp-*"));
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Uninstall_keeps_going_and_fails_when_the_kept_file_belongs_to_another_user()
    {
        Assert.SkipWhen(UnixOwnership.CurrentUserId() == 0, "Root owns the target, so the target is not foreign.");
        const string foreign = "/etc/passwd";
        File.CreateSymbolicLink(_paths.DaemonEnvironmentFilePath, foreign);
        Directory.CreateDirectory(DaemonManager.SystemdUserUnitDirectory);
        File.WriteAllText(_unitPath, "[Service]\n");
        var runner = new FakeSystemCommandRunner();

        var result = await CreateManager(runner).UninstallAsync();

        Assert.False(result.Success);
        Assert.Contains(_paths.DaemonEnvironmentFilePath, result.Message, StringComparison.Ordinal);
        Assert.Contains("another user", result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(_unitPath));
        Assert.Contains(("systemctl", "--user daemon-reload"), runner.Commands);
        Assert.Equal(foreign, new FileInfo(_paths.DaemonEnvironmentFilePath).LinkTarget);
    }

    [SlopwatchSuppress("SW001", "Service installation is Linux-only.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Service installation is Linux-only.")]
    public async Task Uninstall_says_so_when_it_cannot_delete_a_daemon_env_that_holds_only_the_PATH_line()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.SkipWhen(UnixOwnership.CurrentUserId() == 0, "Root can delete from a read-only directory.");
        File.WriteAllText(_paths.DaemonEnvironmentFilePath, "PATH=/usr/bin\n");
        var configDirectory = Path.GetDirectoryName(_paths.DaemonEnvironmentFilePath)!;
        var runner = new FakeSystemCommandRunner();
        File.SetUnixFileMode(configDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        DaemonResult result;
        try
        {
            result = await CreateManager(runner).UninstallAsync();
        }
        finally
        {
            File.SetUnixFileMode(configDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.False(result.Success);
        Assert.Contains(("systemctl", "--user daemon-reload"), runner.Commands);
        Assert.Contains(_paths.DaemonEnvironmentFilePath, result.Message, StringComparison.Ordinal);
        Assert.Contains("could not delete", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("lines that you wrote", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secure", result.Message, StringComparison.Ordinal);
    }

    private DaemonManager CreateManager(FakeSystemCommandRunner runner)
        => new(_paths, TimeProvider.System, new FakeSupervisor(), runner);

    private sealed class FakeSupervisor : IContainerSupervisor
    {
        public bool IsExternallySupervised => false;
    }

    private sealed class FakeSystemCommandRunner : ISystemCommandRunner
    {
        private readonly SystemCommandResult? _always;

        public FakeSystemCommandRunner(SystemCommandResult? always = null) => _always = always;

        public List<(string Command, string Arguments)> Commands { get; } = [];

        public Task<SystemCommandResult> RunAsync(string command, string arguments)
        {
            Commands.Add((command, arguments));
            return Task.FromResult(_always ?? new SystemCommandResult(0, string.Empty));
        }
    }
}
