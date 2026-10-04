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

    [Theory]
    [InlineData(ShellPlatform.Linux)]
    [InlineData(ShellPlatform.MacOS)]
    [InlineData(ShellPlatform.Windows)]
    public void System_skills_are_readable_but_not_writable(ShellPlatform platform)
    {
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), "netclaw-policy-contract"));
        var environment = platform == ShellPlatform.Windows
            ? ShellExecutionEnvironment.CreatePowerShell(
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                PwshDialect.WindowsPowerShell51)
            : ShellExecutionEnvironment.CreateBash(platform);
        var policy = DaemonToolPathPolicyFactory.Create(paths, environment);
        var skillPath = Path.Combine(paths.SystemSkillsDirectory, "netclaw-operations", "SKILL.md");

        Assert.False(policy.FileSystem.IsProtected(skillPath, PathOperation.Read));
        Assert.True(policy.FileSystem.IsProtected(skillPath, PathOperation.Write));
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
