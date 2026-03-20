using System.Net;
using System.Text.Json;
using Netclaw.Cli.Notification;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Notification;

public sealed class NotificationCommandTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public NotificationCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-notification-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public async Task Help_ShowsWebhookSubcommands()
    {
        using var output = new StringWriter();

        var exitCode = await NotificationCommand.RunAsync(["notification", "webhook", "--help"], _paths, output: output);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("Plain CLI, offline", text, StringComparison.Ordinal);
        Assert.Contains("list", text, StringComparison.Ordinal);
        Assert.Contains("add", text, StringComparison.Ordinal);
        Assert.Contains("remove", text, StringComparison.Ordinal);
        Assert.Contains("test", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidSubcommand_ReturnsUsageError()
    {
        using var output = new StringWriter();

        var exitCode = await NotificationCommand.RunAsync(["notification", "webhook", "rotate"], _paths, output: output);

        Assert.Equal(2, exitCode);
        Assert.Contains("Unsupported notification webhook subcommand 'rotate'", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_NoTargets_ShowsEmptyMessage()
    {
        using var output = new StringWriter();

        var exitCode = await NotificationCommand.RunAsync(["notification", "webhook", "list"], _paths, output: output);

        Assert.Equal(0, exitCode);
        Assert.Contains("No notification webhook targets are configured", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_ShowsStableIndexesAndRedactedHeaders()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Notifications": {
                "Webhooks": [
                  {
                    "Name": "ops-primary",
                    "Url": "https://alerts.example/hooks/main"
                  },
                  {
                    "Url": "https://alerts.example/hooks/backup"
                  }
                ]
              }
            }
            """);

        WriteSecrets(
            """
            {
              "Notifications": {
                "Webhooks": [
                  {
                    "Headers": {
                      "Authorization": "Bearer secret-token"
                    }
                  }
                ]
              }
            }
            """);

        using var output = new StringWriter();
        var exitCode = await NotificationCommand.RunAsync(["notification", "webhook", "list"], _paths, output: output);

        Assert.Equal(0, exitCode);
        var text = output.ToString();
        Assert.Contains("[0] ops-primary", text, StringComparison.Ordinal);
        Assert.Contains("[1] https://alerts.example/<redacted>", text, StringComparison.Ordinal);
        Assert.Contains("Authorization=<redacted>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hooks/backup", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_WritesBaseConfigAndSecretsWithoutEchoingSecretValue()
    {
        using var output = new StringWriter();

        var exitCode = await NotificationCommand.RunAsync(
            ["notification", "webhook", "add", "--url", "https://alerts.example/hooks/netclaw", "--name", "ops-primary", "--header", "Authorization: Bearer secret-token"],
            _paths,
            output: output);

        Assert.Equal(0, exitCode);
        var config = ReadJson(_paths.NetclawConfigPath);
        var configWebhook = config.RootElement.GetProperty("Notifications").GetProperty("Webhooks")[0];
        Assert.False(configWebhook.TryGetProperty("Url", out _));
        Assert.False(configWebhook.TryGetProperty("Headers", out _));

        var secrets = ReadJson(_paths.SecretsPath);
        var secretsWebhook = secrets.RootElement
            .GetProperty("Notifications")
            .GetProperty("Webhooks")[0];
        var encryptedUrl = secretsWebhook.GetProperty("Url").GetString();
        Assert.StartsWith("ENC:", encryptedUrl, StringComparison.Ordinal);

        var encryptedHeader = secretsWebhook
            .GetProperty("Headers")
            .GetProperty("Authorization")
            .GetString();
        Assert.StartsWith("ENC:", encryptedHeader, StringComparison.Ordinal);

        var text = output.ToString();
        Assert.Contains("Authorization=<redacted>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_InvalidTargetFailsBeforeWritingFiles()
    {
        using var output = new StringWriter();

        var exitCode = await NotificationCommand.RunAsync(
            ["notification", "webhook", "add", "--url", "http://alerts.example/hooks/netclaw"],
            _paths,
            output: output);

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(_paths.NetclawConfigPath));
        Assert.False(File.Exists(_paths.SecretsPath));
        Assert.Contains("Notifications.Webhooks[0].Url", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_ByIndex_DeletesTargetAndSecretOverlay()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Notifications": {
                "Webhooks": [
                  {
                    "Name": "ops-primary",
                    "Url": "https://alerts.example/hooks/main"
                  },
                  {
                    "Name": "ops-backup",
                    "Url": "https://alerts.example/hooks/backup"
                  }
                ]
              }
            }
            """);

        WriteSecrets(
            """
            {
              "Notifications": {
                "Webhooks": [
                  {
                    "Headers": {
                      "Authorization": "Bearer first"
                    }
                  },
                  {
                    "Headers": {
                      "Authorization": "Bearer second"
                    }
                  }
                ]
              }
            }
            """);

        using var output = new StringWriter();
        var exitCode = await NotificationCommand.RunAsync(["notification", "webhook", "remove", "--index", "1"], _paths, output: output);

        Assert.Equal(0, exitCode);
        var config = ReadJson(_paths.NetclawConfigPath);
        Assert.Single(config.RootElement.GetProperty("Notifications").GetProperty("Webhooks").EnumerateArray());
        Assert.Equal("ops-primary", config.RootElement.GetProperty("Notifications").GetProperty("Webhooks")[0].GetProperty("Name").GetString());

        var secrets = ReadJson(_paths.SecretsPath);
        Assert.Single(secrets.RootElement.GetProperty("Notifications").GetProperty("Webhooks").EnumerateArray());
    }

    [Fact]
    public async Task List_MigratesLegacyBaseConfigSecretsIntoSecretsFile()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Notifications": {
                "Webhooks": [
                  {
                    "Name": "ops-primary",
                    "Url": "https://alerts.example/hooks/main",
                    "Headers": {
                      "Authorization": "Bearer legacy"
                    }
                  }
                ]
              }
            }
            """);

        using var output = new StringWriter();
        var exitCode = await NotificationCommand.RunAsync(["notification", "webhook", "list"], _paths, output: output);

        Assert.Equal(0, exitCode);

        var config = ReadJson(_paths.NetclawConfigPath);
        var configWebhook = config.RootElement.GetProperty("Notifications").GetProperty("Webhooks")[0];
        Assert.False(configWebhook.TryGetProperty("Url", out _));
        Assert.False(configWebhook.TryGetProperty("Headers", out _));

        var secrets = ReadJson(_paths.SecretsPath);
        var secretsWebhook = secrets.RootElement.GetProperty("Notifications").GetProperty("Webhooks")[0];
        Assert.StartsWith("ENC:", secretsWebhook.GetProperty("Url").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("ENC:", secretsWebhook.GetProperty("Headers").GetProperty("Authorization").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_AmbiguousName_RequiresIndex()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Notifications": {
                "Webhooks": [
                  {
                    "Name": "ops-primary",
                    "Url": "https://alerts.example/hooks/main"
                  },
                  {
                    "Name": "ops-primary",
                    "Url": "https://alerts.example/hooks/backup"
                  }
                ]
              }
            }
            """);

        using var output = new StringWriter();
        var exitCode = await NotificationCommand.RunAsync(["notification", "webhook", "remove", "--name", "ops-primary"], _paths, output: output);

        Assert.Equal(1, exitCode);
        Assert.Contains("ambiguous", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--index", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_SucceedsWithSingleProbe()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Notifications": {
                "TimeoutSeconds": 1,
                "Webhooks": [
                  {
                    "Name": "ops-primary",
                    "Url": "https://alerts.example/hooks/main"
                  }
                ]
              }
            }
            """);

        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var output = new StringWriter();

        var exitCode = await NotificationCommand.RunAsync(["notification", "webhook", "test", "--index", "0"], _paths, probeHandler: handler, output: output);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("HTTP 204 NoContent", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_HttpFailure_RedactsSecretValues()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Notifications": {
                "TimeoutSeconds": 1,
                "Webhooks": [
                  {
                    "Name": "ops-primary",
                    "Url": "https://alerts.example/hooks/main"
                  }
                ]
              }
            }
            """);

        WriteSecrets(
            """
            {
              "Notifications": {
                "Webhooks": [
                  {
                    "Headers": {
                      "Authorization": "Bearer secret-token"
                    }
                  }
                ]
              }
            }
            """);

        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("remote saw Bearer secret-token")
        });
        using var output = new StringWriter();

        var exitCode = await NotificationCommand.RunAsync(["notification", "webhook", "test", "--index", "0"], _paths, probeHandler: handler, output: output);

        Assert.Equal(1, exitCode);
        Assert.Equal(1, handler.CallCount);
        var text = output.ToString();
        Assert.Contains("HTTP 400 BadRequest", text, StringComparison.Ordinal);
        Assert.Contains("<redacted>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_Timeout_DoesNotRetry()
    {
        WriteConfig(
            """
            {
              "configVersion": 1,
              "Notifications": {
                "TimeoutSeconds": 1,
                "Webhooks": [
                  {
                    "Name": "ops-primary",
                    "Url": "https://alerts.example/hooks/main"
                  }
                ]
              }
            }
            """);

        var handler = new CountingHandler((Func<CancellationToken, Task<HttpResponseMessage>>)(_ => throw new TaskCanceledException("simulated timeout")));
        using var output = new StringWriter();

        var exitCode = await NotificationCommand.RunAsync(["notification", "webhook", "test", "--index", "0"], _paths, probeHandler: handler, output: output);

        Assert.Equal(1, exitCode);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("timed out", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private void WriteConfig(string json)
    {
        File.WriteAllText(_paths.NetclawConfigPath, json);
    }

    private void WriteSecrets(string json)
    {
        File.WriteAllText(_paths.SecretsPath, json);
    }

    private static JsonDocument ReadJson(string path)
    {
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private sealed class CountingHandler(Func<CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _handler = handler;

        public CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : this(_ => Task.FromResult(handler(new HttpRequestMessage())))
        {
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return _handler(cancellationToken);
        }
    }
}
