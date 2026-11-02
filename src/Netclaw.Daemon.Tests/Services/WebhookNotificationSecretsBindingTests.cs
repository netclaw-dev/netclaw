// -----------------------------------------------------------------------
// <copyright file="WebhookNotificationSecretsBindingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

/// <summary>
/// Serializes the tests that set the process-wide <see cref="SensitiveStringTypeConverter.Protector"/> hook.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WebhookSecretsStaticStateCollection
{
    public const string Name = "Webhook secrets static state";
}

/// <summary>
/// Issue #2777. Proves the layered config (netclaw.json, then secrets.json) binds the operational
/// notification webhooks and that the delivery service posts to the value the binder produced.
/// </summary>
[Collection(WebhookSecretsStaticStateCollection.Name)]
public sealed class WebhookNotificationSecretsBindingTests : IDisposable
{
    private static readonly ServiceIdentity TestIdentity = new("test-agent", "test-ns", "test-host:4321", "9.9.9");

    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly ISecretsProtector? _previousProtector = SensitiveStringTypeConverter.Protector;
    private readonly List<WebhookNotificationService> _services = [];

    public WebhookNotificationSecretsBindingTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        foreach (var service in _services)
            service.Dispose();

        SensitiveStringTypeConverter.Protector = _previousProtector;
        _dir.Dispose();
    }

    [Fact]
    public async Task Keyed_webhook_with_an_encrypted_url_and_header_in_secrets_json_posts_to_the_decrypted_url()
    {
        const string url = "https://hooks.slack.com/services/T000/B000/TOKEN";
        File.WriteAllText(_paths.NetclawConfigPath, """
            { "configVersion": 1, "Notifications": { "DeduplicationWindowSeconds": 0,
                "Webhooks": { "ops": { "Format": "Slack" } } } }
            """);
        var protector = SecretsProtection.CreateProtector(_paths);
        // SecretsFileWriter encrypts every leaf, so the file holds ENC: values as `netclaw secrets set` writes them.
        SecretsFileWriter.Write(_paths.SecretsPath, $$"""
            { "Notifications": { "Webhooks": { "ops": { "Url": "{{url}}", "Headers": { "Authorization": "Bearer s3cret" } } } } }
            """, protector);
        Assert.StartsWith("ENC:", ReadRawSecretUrl("ops"), StringComparison.Ordinal);
        SensitiveStringTypeConverter.Protector = protector;

        var handler = new RecordingHandler(HttpStatusCode.OK);
        var service = await StartServiceAsync(BindNotifications(), handler);
        service.Emit(CreateAlert());
        await WebhookTestInfrastructure.WaitForDeliveryAsync(handler, expectedCount: 1);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(url, request.RequestUri?.ToString());
        Assert.Equal("Bearer s3cret", request.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task Old_list_shape_with_plain_text_urls_in_netclaw_json_still_binds_as_keys_0_and_1_and_delivers()
    {
        File.WriteAllText(_paths.NetclawConfigPath, """
            { "configVersion": 1, "Notifications": { "DeduplicationWindowSeconds": 0, "Webhooks": [
                { "Name": "ops", "Url": "https://a.example.test/hook", "Format": "Generic" },
                { "Name": "slack", "Url": "https://hooks.slack.com/services/T/B/C", "Format": "Slack" } ] } }
            """);

        var notifications = BindNotifications();
        Assert.Equal(["0", "1"], notifications.Webhooks.Keys.OrderBy(static k => k, StringComparer.Ordinal));

        var handler = new RecordingHandler(HttpStatusCode.OK);
        var service = await StartServiceAsync(notifications, handler);
        service.Emit(CreateAlert());
        await WebhookTestInfrastructure.WaitForDeliveryAsync(handler, expectedCount: 2);

        var urls = handler.Requests.Select(static r => r.RequestUri?.ToString()).OrderBy(static u => u, StringComparer.Ordinal);
        Assert.Equal(["https://a.example.test/hook", "https://hooks.slack.com/services/T/B/C"], urls);
    }

    private NotificationsConfig BindNotifications()
    {
        var configuration = new ConfigurationBuilder().AddNetclawDaemonSources(_paths).Build();
        return configuration.GetSection("Notifications").Get<NotificationsConfig>()
               ?? throw new InvalidOperationException("The Notifications section did not bind.");
    }

    private string ReadRawSecretUrl(string key)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(_paths.SecretsPath));
        return doc.RootElement.GetProperty("Notifications").GetProperty("Webhooks").GetProperty(key).GetProperty("Url").GetString()!;
    }

    private async Task<WebhookNotificationService> StartServiceAsync(NotificationsConfig config, RecordingHandler handler)
    {
        var service = new WebhookNotificationService(
            config,
            new TestHttpClientFactory(handler),
            TimeProvider.System,
            TestIdentity,
            NullLogger<WebhookNotificationService>.Instance);
        _services.Add(service);
        await service.StartAsync(CancellationToken.None);
        return service;
    }

    private static OperationalAlert CreateAlert() => new()
    {
        AlertId = Guid.NewGuid().ToString("N")[..12],
        Type = "mcp.server.disconnected",
        Category = AlertType.McpServerDisconnected,
        Summary = "Test alert",
        Timestamp = DateTimeOffset.UtcNow,
        Severity = AlertSeverity.Warning,
    };
}
