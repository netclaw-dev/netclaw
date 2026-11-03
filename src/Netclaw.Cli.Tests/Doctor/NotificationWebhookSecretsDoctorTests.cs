// -----------------------------------------------------------------------
// <copyright file="NotificationWebhookSecretsDoctorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

/// <summary>
/// Issue #2777. The check reports a notification webhook Url or header value in netclaw.json.
/// The fix converts the list shape to the keyed shape and moves the values to secrets.json.
/// </summary>
public sealed class NotificationWebhookSecretsDoctorTests : IDisposable
{
    private const string OpsUrl = "https://alerts.example.test/hook?token=OPSTOKEN";
    private const string SlackUrl = "https://hooks.slack.com/services/T000/B000/SLACKTOKEN";

    private const string OldListShape = """
        {
          "configVersion": 1,
          "Notifications": {
            "MaxRetries": 4,
            "Webhooks": [
              { "Name": "ops", "Url": "https://alerts.example.test/hook?token=OPSTOKEN",
                "Headers": { "Authorization": "Bearer OPSHEADER" }, "Format": "Generic" },
              { "Name": "slack", "Url": "https://hooks.slack.com/services/T000/B000/SLACKTOKEN", "Format": "Slack" }
            ]
          }
        }
        """;

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public NotificationWebhookSecretsDoctorTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("""{ "Notifications": { "Webhooks": { "ops": { "Url": "https://a.example.test/SECRETURL", "Format": "Generic" } } } }""", true)]
    [InlineData("""{ "Notifications": { "Webhooks": { "ops": { "Headers": { "Authorization": "Bearer SECRETURL" }, "Format": "Generic" } } } }""", true)]
    [InlineData("""{ "Notifications": { "Webhooks": [ { "Name": "ops", "Url": "https://a.example.test/SECRETURL" } ] } }""", true)]
    [InlineData("""{ "Notifications": { "Webhooks": { "ops": { "Format": "Generic" } } } }""", false)]
    // The old list shape, with the Url in secrets.json or the environment: the shape alone is the finding.
    [InlineData("""{ "Notifications": { "Webhooks": [ { "Name": "ops", "Format": "Slack" } ] } }""", true)]
    public async Task The_check_warns_when_a_url_or_header_value_sits_in_netclaw_json_and_is_silent_when_it_does_not(
        string netclawJson, bool expectWarning)
    {
        File.WriteAllText(_paths.NetclawConfigPath, netclawJson);
        // The value in secrets.json is where the product keeps it, so it must never trigger the check.
        SecretsFileWriter.Write(
            _paths.SecretsPath,
            """{ "Notifications": { "Webhooks": { "ops": { "Url": "https://b.example.test/hook" } } } }""",
            SecretsProtection.CreateProtector(_paths));

        var result = await new NotificationWebhookSecretsDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectWarning ? DoctorSeverity.Warning : DoctorSeverity.Pass, result.Severity);
        // The report names the webhook. It never repeats the secret.
        Assert.DoesNotContain("SECRETURL", result.Message, StringComparison.Ordinal);
        if (expectWarning)
            Assert.Contains("netclaw doctor --fix", result.Remediation + result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fix_converts_the_list_to_keyed_entries_moves_the_secrets_encrypted_and_leaves_a_backup()
    {
        File.WriteAllText(_paths.NetclawConfigPath, OldListShape);
        var service = ConfigOnlyService();

        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);
        var backups = await service.ApplyAsync(plan, TestContext.Current.CancellationToken);

        var backup = Assert.Single(backups);
        Assert.Equal(OldListShape, File.ReadAllText(backup));

        var netclawJson = File.ReadAllText(_paths.NetclawConfigPath);
        foreach (var secret in new[] { "OPSTOKEN", "OPSHEADER", "SLACKTOKEN" })
            Assert.DoesNotContain(secret, netclawJson, StringComparison.Ordinal);
        using (var config = JsonDocument.Parse(netclawJson))
        {
            var notifications = config.RootElement.GetProperty("Notifications");
            Assert.Equal(4, notifications.GetProperty("MaxRetries").GetInt32());
            var webhooks = notifications.GetProperty("Webhooks");
            Assert.Equal(JsonValueKind.Object, webhooks.ValueKind);
            Assert.Equal("Generic", webhooks.GetProperty("ops").GetProperty("Format").GetString());
            Assert.Equal("Slack", webhooks.GetProperty("slack").GetProperty("Format").GetString());
            Assert.False(webhooks.GetProperty("ops").TryGetProperty("Url", out _));
            Assert.False(webhooks.GetProperty("ops").TryGetProperty("Headers", out _));
        }

        var rawSecrets = File.ReadAllText(_paths.SecretsPath);
        foreach (var secret in new[] { "OPSTOKEN", "OPSHEADER", "SLACKTOKEN" })
            Assert.DoesNotContain(secret, rawSecrets, StringComparison.Ordinal);
        using (var raw = JsonDocument.Parse(rawSecrets))
        {
            var stored = raw.RootElement.GetProperty("Notifications").GetProperty("Webhooks");
            Assert.StartsWith("ENC:", stored.GetProperty("ops").GetProperty("Url").GetString(), StringComparison.Ordinal);
            Assert.StartsWith("ENC:", stored.GetProperty("ops").GetProperty("Headers").GetProperty("Authorization").GetString(), StringComparison.Ordinal);
            Assert.StartsWith("ENC:", stored.GetProperty("slack").GetProperty("Url").GetString(), StringComparison.Ordinal);
        }

        var after = await new NotificationWebhookSecretsDoctorCheck(_paths).RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DoctorSeverity.Pass, after.Severity);
        Assert.Empty((await service.BuildPlanAsync(TestContext.Current.CancellationToken)).Fixes);
    }

    [Fact]
    public async Task Fix_converts_a_list_shape_that_holds_no_url_in_netclaw_json_to_the_keyed_shape()
    {
        const string listWithoutUrl = """
            { "configVersion": 1, "Notifications": { "Webhooks": [ { "Name": "ops", "Format": "Slack" } ] } }
            """;
        File.WriteAllText(_paths.NetclawConfigPath, listWithoutUrl);
        var service = ConfigOnlyService();

        var plan = await service.BuildPlanAsync(TestContext.Current.CancellationToken);
        Assert.Single(plan.Fixes);
        var backup = Assert.Single(await service.ApplyAsync(plan, TestContext.Current.CancellationToken));

        Assert.Equal(listWithoutUrl, File.ReadAllText(backup));
        using var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var webhooks = config.RootElement.GetProperty("Notifications").GetProperty("Webhooks");
        Assert.Equal(JsonValueKind.Object, webhooks.ValueKind);
        Assert.Equal("Slack", webhooks.GetProperty("ops").GetProperty("Format").GetString());
    }

    [Fact]
    public async Task Fix_keeps_a_value_that_secrets_json_already_holds_for_the_same_key()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """
            { "configVersion": 1, "Notifications": { "Webhooks": [
                { "Name": "ops", "Url": "https://old.example.test/NETCLAWJSONURL",
                  "Headers": { "Authorization": "Bearer NETCLAWJSONHEADER" }, "Format": "Generic" } ] } }
            """);
        SecretsFileWriter.Write(
            _paths.SecretsPath,
            """{ "Notifications": { "Webhooks": { "ops": { "Url": "https://kept.example.test/hook", "Headers": { "Authorization": "Bearer kept" } } } } }""",
            SecretsProtection.CreateProtector(_paths));
        var service = ConfigOnlyService();

        await service.ApplyAsync(await service.BuildPlanAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        // secrets.json overrides netclaw.json at bind time, so the fix must not replace the value the operator set there.
        using var secrets = JsonDocument.Parse(SecretsFileWriter.DecryptJsonLeaves(
            File.ReadAllText(_paths.SecretsPath), SecretsProtection.CreateProtector(_paths)));
        var ops = secrets.RootElement.GetProperty("Notifications").GetProperty("Webhooks").GetProperty("ops");
        Assert.Equal("https://kept.example.test/hook", ops.GetProperty("Url").GetString());
        Assert.Equal("Bearer kept", ops.GetProperty("Headers").GetProperty("Authorization").GetString());
        Assert.DoesNotContain("NETCLAWJSON", File.ReadAllText(_paths.NetclawConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_the_fix_removing_the_first_webhook_does_not_change_the_url_of_the_second()
    {
        File.WriteAllText(_paths.NetclawConfigPath, OldListShape);
        var service = ConfigOnlyService();
        await service.ApplyAsync(await service.BuildPlanAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        var before = BindWithoutWebhook(remove: null);
        Assert.Equal(OpsUrl, before.Webhooks["ops"].Url!.Value);
        Assert.Equal(SlackUrl, before.Webhooks["slack"].Url!.Value);

        // The list shape joined secrets.json by position, so this removal used to point "slack" at the wrong Url.
        var after = BindWithoutWebhook(remove: "ops");
        var slack = Assert.Single(after.Webhooks);
        Assert.Equal("slack", slack.Key);
        Assert.Equal(SlackUrl, slack.Value.Url!.Value);
        Assert.Equal(WebhookFormat.Slack, slack.Value.Format);
    }

    // Binds netclaw.json plus the decrypted secrets.json after an operator deletes one webhook from both files.
    // The test decrypts by hand so it does not touch the process-wide SensitiveStringTypeConverter.Protector.
    private NotificationsConfig BindWithoutWebhook(string? remove)
    {
        var config = JsonNode.Parse(File.ReadAllText(_paths.NetclawConfigPath))!.AsObject();
        var secrets = JsonNode.Parse(SecretsFileWriter.DecryptJsonLeaves(
            File.ReadAllText(_paths.SecretsPath), SecretsProtection.CreateProtector(_paths)))!.AsObject();
        if (remove is not null)
        {
            config["Notifications"]!["Webhooks"]!.AsObject().Remove(remove);
            secrets["Notifications"]!["Webhooks"]!.AsObject().Remove(remove);
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(config.ToJsonString())))
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(secrets.ToJsonString())))
            .Build();
        return configuration.GetSection("Notifications").Get<NotificationsConfig>()
               ?? throw new InvalidOperationException("The Notifications section did not bind.");
    }

    private DoctorFixService ConfigOnlyService()
        => new(_paths, Path.Combine(_paths.BasePath, "unused.service"), systemdEnabled: false);
}
