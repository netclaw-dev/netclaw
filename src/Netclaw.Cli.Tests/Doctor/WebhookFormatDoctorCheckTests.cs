// -----------------------------------------------------------------------
// <copyright file="WebhookFormatDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class WebhookFormatDoctorCheckTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public WebhookFormatDoctorCheckTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task ReturnsPass_WhenNoWebhooksConfigured()
    {
        WriteConfig(new { configVersion = 1 });

        var check = new WebhookFormatDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsPass_WhenSlackUrlHasSlackFormat()
    {
        WriteConfig(new
        {
            Notifications = new
            {
                Webhooks = new[]
                {
                    new { Url = "https://hooks.slack.com/services/T00/B00/xxx", Format = "Slack" }
                }
            }
        });

        var check = new WebhookFormatDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    [Fact]
    public async Task ReturnsWarning_WhenSlackUrlHasNoFormat()
    {
        WriteConfig(new
        {
            Notifications = new
            {
                Webhooks = new[]
                {
                    new { Url = "https://hooks.slack.com/services/T00/B00/xxx" }
                }
            }
        });

        var check = new WebhookFormatDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("hooks.slack.com", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Remediation);
    }

    [Fact]
    public async Task ReturnsWarning_WhenSlackUrlHasGenericFormat()
    {
        WriteConfig(new
        {
            Notifications = new
            {
                Webhooks = new[]
                {
                    new { Url = "https://hooks.slack.com/services/T00/B00/xxx", Format = "Generic" }
                }
            }
        });

        var check = new WebhookFormatDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
    }

    [Fact]
    public async Task ReturnsPass_WhenGenericUrlHasNoFormat()
    {
        WriteConfig(new
        {
            Notifications = new
            {
                Webhooks = new[]
                {
                    new { Url = "https://example.com/webhook" }
                }
            }
        });

        var check = new WebhookFormatDoctorCheck(_paths);
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
    }

    private void WriteConfig(object config)
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
    }
}
