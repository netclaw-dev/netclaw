// -----------------------------------------------------------------------
// <copyright file="DaemonToolPathPolicyFactoryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Security;
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

        Assert.False(policy.IsReadDenied(paths.NetclawConfigPath));
        Assert.True(policy.IsDenied(paths.NetclawConfigPath));
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

        Assert.All(protectedPaths, path => Assert.True(policy.IsReadDenied(path), path));
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

        Assert.True(policy.IsDenied(catalogPath));
        Assert.True(policy.IsReadDenied(catalogPath));
        Assert.True(policy.CommandReferencesDeniedPath($"inspect '{catalogPath}'"));
        Assert.True(policy.CommandReferencesDeniedPath("find", catalogPath));
    }
}
