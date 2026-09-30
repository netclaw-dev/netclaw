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
    [Fact]
    public void Ordinary_config_is_readable_but_not_writable_or_shell_accessible()
    {
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), "netclaw-policy-contract"));
        var policy = DaemonToolPathPolicyFactory.Create(
            paths,
            ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux));

        Assert.False(policy.FileSystem.IsProtected(paths.NetclawConfigPath, PathOperation.Read));
        Assert.True(policy.FileSystem.IsProtected(paths.NetclawConfigPath, PathOperation.Write));
        Assert.True(policy.CommandReferencesDeniedPath($"cat '{paths.NetclawConfigPath}'"));
    }

    [Fact]
    public void Other_configuration_and_control_plane_files_remain_read_denied()
    {
        var paths = new NetclawPaths(Path.Combine(Path.GetTempPath(), "netclaw-policy-contract"));
        var policy = DaemonToolPathPolicyFactory.Create(
            paths,
            ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux));
        string[] protectedPaths =
        [
            paths.SecretsPath,
            paths.WebhooksDirectory,
            paths.ToolApprovalsPath,
            paths.HardDenyOverridesPath,
            paths.DaemonEnvironmentFilePath,
            paths.DevicesPath,
            paths.BootstrapStatePath,
            paths.SqliteDbPath,
            paths.PidFilePath,
            paths.LockFilePath,
            paths.RestartManifestPath
        ];

        Assert.All(protectedPaths, path => Assert.True(policy.FileSystem.IsProtected(path, PathOperation.Read), path));
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
