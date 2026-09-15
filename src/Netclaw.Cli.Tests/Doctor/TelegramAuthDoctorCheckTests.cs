// -----------------------------------------------------------------------
// <copyright file="TelegramAuthDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Telegram;
using Netclaw.Cli.Tests.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class TelegramAuthDoctorCheckTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public TelegramAuthDoctorCheckTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task ReturnsPass_WhenTelegramDisabled()
    {
        WriteConfig(false);
        var probe = new FakeTelegramProbe();

        var result = await new TelegramAuthDoctorCheck(_paths, probe)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Equal(0, probe.ProbeCallCount);
    }

    [Fact]
    public async Task ReturnsError_WhenTokenIsMissing()
    {
        WriteConfig(true);
        var probe = new FakeTelegramProbe();

        var result = await new TelegramAuthDoctorCheck(_paths, probe)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("no bot token", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, probe.ProbeCallCount);
    }

    [Fact]
    public async Task ReturnsPass_WhenGetMeSucceeds()
    {
        WriteConfig(true);
        WriteSecrets("valid-token");
        var probe = new FakeTelegramProbe
        {
            NextProbeResult = new TelegramProbeResult(true, null, "netclaw_agent_bot")
        };

        var result = await new TelegramAuthDoctorCheck(_paths, probe)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("@netclaw_agent_bot", result.Message, StringComparison.Ordinal);
        Assert.Equal("valid-token", probe.LastBotToken);
    }

    [Fact]
    public async Task ReturnsError_WhenGetMeFails()
    {
        WriteConfig(true);
        WriteSecrets("invalid-token");
        var probe = new FakeTelegramProbe
        {
            NextProbeResult = new TelegramProbeResult(false, "Bot token is invalid.", null)
        };

        var result = await new TelegramAuthDoctorCheck(_paths, probe)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("invalid", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Remediation);
    }

    [Fact]
    public async Task ReturnsError_WhenConfiguredGroupIsNotAccessible()
    {
        WriteConfig(true, ["-5364308250"]);
        WriteSecrets("valid-token");
        var probe = new FakeTelegramProbe
        {
            NextResolutionResult = new TelegramChatResolutionResult(
                false,
                null,
                [],
                ["-5364308250"])
        };

        var result = await new TelegramAuthDoctorCheck(_paths, probe)
            .RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("-5364308250", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, probe.ResolveCallCount);
    }

    private void WriteConfig(bool enabled, string[]? allowedChatIds = null) => WriteJson(_paths.NetclawConfigPath, new
    {
        Telegram = new { Enabled = enabled, AllowedChatIds = allowedChatIds ?? [] }
    });

    private void WriteSecrets(string token) => WriteJson(_paths.SecretsPath, new
    {
        Telegram = new { BotToken = token }
    });

    private static void WriteJson(string path, object value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}
