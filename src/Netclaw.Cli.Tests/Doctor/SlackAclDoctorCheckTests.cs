// -----------------------------------------------------------------------
// <copyright file="SlackAclDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class SlackAclDoctorCheckTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public SlackAclDoctorCheckTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task ReturnsPass_WhenSlackDisabled()
    {
        WriteConfig(new { Slack = new { Enabled = false } });

        var check = new SlackAclDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsWarning_WhenSlackEnabled_NoChannels()
    {
        WriteConfig(new { Slack = new { Enabled = true } });

        var check = new SlackAclDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("no channel", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsPass_WhenSlackEnabled_ChannelsConfigured_DmsDisabled()
    {
        WriteConfig(new
        {
            Slack = new
            {
                Enabled = true,
                AllowedChannelIds = new[] { "C001" },
                AllowDirectMessages = false
            }
        });

        var check = new SlackAclDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsPass_WhenDmsEnabled_WithAllowedUserIds()
    {
        WriteConfig(new
        {
            Slack = new
            {
                Enabled = true,
                AllowedChannelIds = new[] { "C001" },
                AllowDirectMessages = true,
                AllowedUserIds = new[] { "U001", "U002" }
            }
        });

        var check = new SlackAclDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsWarning_WhenDmsEnabled_NoAllowedUserIds()
    {
        WriteConfig(new
        {
            Slack = new
            {
                Enabled = true,
                AllowedChannelIds = new[] { "C001" },
                AllowDirectMessages = true
            }
        });

        var check = new SlackAclDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("DMs enabled", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no user allowlist", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Remediation);
        Assert.Contains("AllowedUserIds", result.Remediation, StringComparison.Ordinal);
    }

    private void WriteConfig(object config)
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }
}
