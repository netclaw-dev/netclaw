// -----------------------------------------------------------------------
// <copyright file="TelegramAclDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class TelegramAclDoctorCheckTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public TelegramAclDoctorCheckTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task ReturnsPass_WhenTelegramDisabled()
    {
        WriteConfig(new { Telegram = new { Enabled = false } });

        var result = await new TelegramAclDoctorCheck(_paths)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsWarning_WhenNoDestinationIsReachable()
    {
        WriteConfig(new { Telegram = new { Enabled = true, AllowDirectMessages = false } });

        var result = await new TelegramAclDoctorCheck(_paths)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("no restricted destination", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsWarning_WhenDirectMessagesAllowEveryUser()
    {
        WriteConfig(new
        {
            Telegram = new
            {
                Enabled = true,
                AllowDirectMessages = true,
                AllowedChatIds = new[] { "-100123" }
            }
        });

        var result = await new TelegramAclDoctorCheck(_paths)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("any Telegram user", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Remediation);
    }

    [Fact]
    public async Task ReturnsPass_WhenPrivateUsersAreRestricted()
    {
        WriteConfig(new
        {
            Telegram = new
            {
                Enabled = true,
                AllowDirectMessages = true,
                AllowedUserIds = new[] { "6875639362" }
            }
        });

        var result = await new TelegramAclDoctorCheck(_paths)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsPass_WhenGroupChatsAreRestricted()
    {
        WriteConfig(new
        {
            Telegram = new
            {
                Enabled = true,
                AllowDirectMessages = false,
                AllowedChatIds = new[] { "-5364308250" }
            }
        });

        var result = await new TelegramAclDoctorCheck(_paths)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    private void WriteConfig(object config) =>
        File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
}
