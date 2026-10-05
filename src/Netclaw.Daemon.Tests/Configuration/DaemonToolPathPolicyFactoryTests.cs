// -----------------------------------------------------------------------
// <copyright file="DaemonToolPathPolicyFactoryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Security;
using Netclaw.Security.Authorization.Filesystem;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class DaemonToolPathPolicyFactoryTests
{
    // Owner decision D6: the agent may read each file under the config
    // directory, with a file tool and with a read-only shell program, except
    // secrets.json and the webhook route files. The shell text screen does not deny it. A write stays
    // denied by the write list, which the shell trusted-root check applies.
    [Theory]
    [InlineData("netclaw.json")]
    [InlineData("tool-approvals.json")]
    [InlineData("hard-deny-overrides.json")]
    [InlineData("daemon.env")]
    [InlineData("devices.json")]
    [InlineData("bootstrap-state.json")]
    public void Config_file_is_readable_but_not_writable(string fileName)
    {
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), "netclaw-policy-contract"));
        var policy = DaemonToolPathPolicyFactory.Create(
            paths,
            ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux));
        var configPath = Path.Combine(paths.ConfigDirectory, fileName);

        Assert.False(policy.FileSystem.IsProtected(configPath, PathOperation.Read));
        Assert.True(policy.FileSystem.IsProtected(configPath, PathOperation.Write));
        Assert.False(policy.CommandReferencesDeniedPath($"cat '{configPath}'"));
    }

    [Fact]
    public void Credentials_and_control_plane_files_remain_read_denied()
    {
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), "netclaw-policy-contract"));
        var policy = DaemonToolPathPolicyFactory.Create(
            paths,
            ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux));
        string[] protectedPaths =
        [
            paths.SecretsPath,
            Path.Combine(paths.WebhooksDirectory, "github-issues.json"),
            Path.Combine(paths.KeysDirectory, "key-1.xml"),
            paths.SqliteDbPath,
            paths.PidFilePath,
            paths.LockFilePath,
            paths.RestartManifestPath
        ];

        Assert.All(protectedPaths, path => Assert.True(policy.FileSystem.IsProtected(path, PathOperation.Read), path));
        // The shell text screen denies each of them, whatever the program.
        Assert.All(protectedPaths, path => Assert.True(policy.CommandReferencesDeniedPath($"cat '{path}'"), path));
    }

    // Program text can name the config directory in another spelling. The text
    // screen collapses "//", "/./", a trailing "/.", and "name/../" before it
    // matches the ".netclaw/config" marker, so each spelling stays denied.
    [Theory]
    [InlineData("jq -n 'import \"secrets\" as $s {search: \"~/.netclaw/./config\"}; $s'")]
    [InlineData("jq -n 'import \"secrets\" as $s {search: \"~/.netclaw//config\"}; $s'")]
    [InlineData("jq -n 'import \"secrets\" as $s {search: \"~/.netclaw/x/../config\"}; $s'")]
    [InlineData("jq -n 'import \"secrets\" as $s {search: \"$HOME/.netclaw/./config\"}; $s'")]
    [InlineData("python3 -c \"import os; print(os.listdir('/srv/.netclaw/config/.'))\"")]
    public void Program_text_that_spells_the_config_directory_stays_denied(string command)
    {
        var paths = new NetclawPaths("/home/user/.netclaw");
        var policy = DaemonToolPathPolicyFactory.Create(
            paths,
            ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux));

        Assert.True(policy.CommandReferencesDeniedPath(command), command);
        Assert.False(policy.CommandReferencesDeniedPath("jq -n 'import \"x\" as $s {search: \"~/.netclaw/./skills\"}; $s'"));
    }

    // The Netclaw home below the launch HOME, as in the default layout. The shell
    // screen denies each home form of a credential: "~user", "$HOME", "${HOME}",
    // and a "/./" segment. No file is read or written.
    [Theory]
    [InlineData("cat ~{user}/.netclaw/config/secrets.json")]
    [InlineData("cat \"$HOME\"/.netclaw/config/secrets.json")]
    [InlineData("cat ${HOME}/.netclaw/./config/secrets.json")]
    [InlineData("cat ~{user}/.netclaw/keys/key-1.xml")]
    [InlineData("cat \"$HOME\"/.netclaw/keys/key-1.xml")]
    [InlineData("cat ${HOME}/.netclaw/./keys/key-1.xml")]
    [InlineData("cat ~{user}/.netclaw/config/webhooks/route.json")]
    [InlineData("cat \"$HOME\"/.netclaw/config/webhooks/route.json")]
    [InlineData("cat ${HOME}/.netclaw/./config/webhooks/route.json")]
    public void Home_forms_of_a_credential_stay_denied(string template)
    {
        if (OperatingSystem.IsWindows())
            return;

        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var home = Assert.IsType<string>(environment.HomeDirectory);
        var policy = DaemonToolPathPolicyFactory.Create(new NetclawPaths(Path.Combine(home, ".netclaw")), environment);
        var command = template.Replace("{user}", Environment.UserName, StringComparison.Ordinal);

        Assert.True(policy.CommandReferencesDeniedPath(command), command);
    }

    [Theory]
    [InlineData(ShellPlatform.Linux)]
    [InlineData(ShellPlatform.MacOS)]
    [InlineData(ShellPlatform.Windows)]
    public void Skill_folders_are_writable_and_the_control_plane_is_not(ShellPlatform platform)
    {
        // Owner decision (2026-10-05): skills are agent guidance, not control plane.
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), "netclaw-policy-contract"));
        var environment = platform == ShellPlatform.Windows
            ? ShellExecutionEnvironment.CreatePowerShell(
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                PwshDialect.WindowsPowerShell51)
            : ShellExecutionEnvironment.CreateBash(platform);
        var policy = DaemonToolPathPolicyFactory.Create(paths, environment);
        string[] skillPaths =
        [
            Path.Combine(paths.SystemSkillsDirectory, "netclaw-operations", "SKILL.md"),
            Path.Combine(paths.ServerFeedDirectory("team"), "disk-cleanup", "scripts", "audit.sh"),
        ];
        string[] controlPlanePaths =
        [
            Path.Combine(paths.ConfigDirectory, "netclaw.json"),
            Path.Combine(paths.ConfigDirectory, "tool-approvals.json"),
            paths.SecretsPath,
            Path.Combine(paths.WebhooksDirectory, "github-issues.json"),
            Path.Combine(paths.KeysDirectory, "key-1.xml"),
            paths.SqliteDbPath,
            paths.SqliteDbPath + "-wal",
            paths.PidFilePath,
            paths.LockFilePath,
            paths.RestartManifestPath,
            Path.Combine(paths.ToolingShadowDirectory, "tool-index.md"),
        ];

        Assert.All(skillPaths, path => Assert.False(policy.FileSystem.IsProtected(path, PathOperation.Write), path));
        Assert.All(skillPaths, path => Assert.False(policy.FileSystem.IsProtected(path, PathOperation.Read), path));
        Assert.All(controlPlanePaths, path => Assert.True(policy.FileSystem.IsProtected(path, PathOperation.Write), path));
    }

    [Theory]
    [InlineData("tool-index.md")]
    [InlineData("mcp/synthetic-server.md")]
    public void Operator_tool_catalogs_are_denied_to_read_write_and_shell(string relativePath)
    {
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), "netclaw-policy-contract"));
        var policy = DaemonToolPathPolicyFactory.Create(
            paths,
            ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux));
        var catalogPath = Path.Combine([paths.ToolingShadowDirectory, .. relativePath.Split('/')]);

        Assert.True(policy.FileSystem.IsProtected(catalogPath, PathOperation.Write));
        Assert.True(policy.FileSystem.IsProtected(catalogPath, PathOperation.Read));
        Assert.True(policy.CommandReferencesDeniedPath($"inspect '{catalogPath}'"));
        Assert.True(policy.CommandReferencesDeniedPath("find", catalogPath));
    }
}
