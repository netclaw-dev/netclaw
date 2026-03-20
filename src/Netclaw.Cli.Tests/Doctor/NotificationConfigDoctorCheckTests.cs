using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class NotificationConfigDoctorCheckTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public NotificationConfigDoctorCheckTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-notifications-doctor-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task Passes_WhenNotificationConfigIsValid()
    {
        await File.WriteAllTextAsync(_paths.NetclawConfigPath,
            """
            {
              "Notifications": {
                "Webhooks": [
                  {
                    "Name": "ops"
                  }
                ],
                "DeduplicationWindowSeconds": 300,
                "MaxRetries": 2,
                "TimeoutSeconds": 10
              }
            }
            """);

        await File.WriteAllTextAsync(_paths.SecretsPath,
            """
            {
              "Notifications": {
                "Webhooks": [
                  {
                    "Url": "https://alerts.example/hooks/netclaw"
                  }
                ]
              }
            }
            """);

        var check = CreateCheck();
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("valid", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ops", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Warns_WhenAuthLikeHeaderIsInBaseConfig()
    {
        await File.WriteAllTextAsync(_paths.NetclawConfigPath,
            """
            {
              "Notifications": {
                "Webhooks": [
                  {
                    "Url": "https://alerts.example/hooks/netclaw",
                    "Headers": {
                      "Authorization": "Bearer should-move"
                    }
                  }
                ]
              }
            }
            """);

        var check = CreateCheck();
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Notifications.Webhooks[0].Headers.Authorization", result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.Remediation);
        Assert.Contains("secrets.json", result.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Warns_WhenWebhookUrlIsInBaseConfig()
    {
        await File.WriteAllTextAsync(_paths.NetclawConfigPath,
            """
            {
              "Notifications": {
                "Webhooks": [
                  {
                    "Url": "https://alerts.example/hooks/netclaw"
                  }
                ]
              }
            }
            """);

        var check = CreateCheck();
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("Notifications.Webhooks[0].Url", result.Message, StringComparison.Ordinal);
        Assert.Contains("secrets.json", result.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fails_WhenNotificationConfigIsInvalid()
    {
        await File.WriteAllTextAsync(_paths.NetclawConfigPath,
            """
            {
              "Notifications": {
                "Webhooks": [
                  {
                    "Url": "http://alerts.internal.example/hooks/netclaw"
                  }
                ],
                "MaxRetries": 6
              }
            }
            """);

        var check = CreateCheck();
        var result = await check.RunAsync();

        Assert.Equal(DoctorSeverity.Error, result.Severity);
        Assert.Contains("Notifications.Webhooks[0].Url", result.Message, StringComparison.Ordinal);
        Assert.Contains("Notifications.MaxRetries", result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.Remediation);
        Assert.Contains("https://", result.Remediation, StringComparison.Ordinal);
    }

    private NotificationConfigDoctorCheck CreateCheck()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(_paths.NetclawConfigPath, optional: true, reloadOnChange: false)
            .AddJsonFile(_paths.SecretsPath, optional: true, reloadOnChange: false)
            .AddEnvironmentVariables("NETCLAW_")
            .Build();

        return new NotificationConfigDoctorCheck(_paths, configuration);
    }
}
