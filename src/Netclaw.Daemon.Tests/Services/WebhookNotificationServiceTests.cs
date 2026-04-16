using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class WebhookNotificationServiceTests : IAsyncDisposable
{
    private static OperationalAlert CreateAlert(
        string type = "mcp.server.disconnected",
        AlertType category = AlertType.McpServerDisconnected,
        string? source = null)
    {
        return new OperationalAlert
        {
            AlertId = Guid.NewGuid().ToString("N")[..12],
            Type = type,
            Category = category,
            Summary = $"Test alert: {type}",
            Timestamp = DateTimeOffset.UtcNow,
            Severity = AlertSeverity.Warning,
            Source = source,
            Context = source is not null
                ? new Dictionary<string, string> { ["serverName"] = source }
                : null
        };
    }

    private readonly List<WebhookNotificationService> _services = [];

    private WebhookNotificationService CreateService(
        NotificationsConfig config,
        RecordingHandler handler,
        TimeProvider? timeProvider = null)
    {
        var factory = new TestHttpClientFactory(handler);
        var service = new WebhookNotificationService(
            config,
            factory,
            timeProvider ?? TimeProvider.System,
            NullLogger<WebhookNotificationService>.Instance);
        _services.Add(service);
        return service;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        _services.Clear();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task DeliversAlert_ToSingleTarget()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var config = new NotificationsConfig
        {
            Webhooks = [new WebhookTarget { Url = "https://example.com/hook" }],
            DeduplicationWindowSeconds = 0
        };

        var service = CreateService(config, handler);
        await service.StartAsync(CancellationToken.None);

        service.Emit(CreateAlert());
        await WaitForDeliveryAsync(handler, expectedCount: 1);

        Assert.Single(handler.Requests);
        Assert.Equal("https://example.com/hook", handler.Requests[0].RequestUri?.ToString());
    }

    [Fact]
    public async Task DeliversAlert_ToMultipleTargets()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var config = new NotificationsConfig
        {
            Webhooks =
            [
                new WebhookTarget { Url = "https://target1.com/hook" },
                new WebhookTarget { Url = "https://target2.com/hook" },
                new WebhookTarget { Url = "https://target3.com/hook" }
            ],
            DeduplicationWindowSeconds = 0
        };

        var service = CreateService(config, handler);
        await service.StartAsync(CancellationToken.None);

        service.Emit(CreateAlert());
        await WaitForDeliveryAsync(handler, expectedCount: 3);

        var urls = handler.Requests.Select(r => r.RequestUri?.ToString()).OrderBy(u => u).ToList();
        Assert.Contains("https://target1.com/hook", urls);
        Assert.Contains("https://target2.com/hook", urls);
        Assert.Contains("https://target3.com/hook", urls);
    }

    [Fact]
    public async Task SuppressesDuplicates_WithinWindow()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var config = new NotificationsConfig
        {
            Webhooks = [new WebhookTarget { Url = "https://example.com/hook" }],
            DeduplicationWindowSeconds = 300
        };

        var service = CreateService(config, handler);
        await service.StartAsync(CancellationToken.None);

        // Emit same alert type + context twice
        service.Emit(CreateAlert(source: "server1"));
        service.Emit(CreateAlert(source: "server1"));
        await WaitForDeliveryAsync(handler, expectedCount: 1, timeoutMs: 2000);

        // Only the first should be delivered
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AllowsDifferentContextKeys_EvenWithDedup()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var config = new NotificationsConfig
        {
            Webhooks = [new WebhookTarget { Url = "https://example.com/hook" }],
            DeduplicationWindowSeconds = 300
        };

        var service = CreateService(config, handler);
        await service.StartAsync(CancellationToken.None);

        // Different context keys should not be deduplicated
        service.Emit(CreateAlert(source: "server1"));
        service.Emit(CreateAlert(source: "server2"));
        await WaitForDeliveryAsync(handler, expectedCount: 2);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PayloadContainsExpectedFields()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var config = new NotificationsConfig
        {
            Webhooks = [new WebhookTarget { Url = "https://example.com/hook" }],
            DeduplicationWindowSeconds = 0
        };

        var service = CreateService(config, handler);
        await service.StartAsync(CancellationToken.None);

        service.Emit(CreateAlert(type: "provider.failover", category: AlertType.ProviderFailover));
        await WaitForDeliveryAsync(handler, expectedCount: 1);

        var body = handler.RequestBodies[0];
        var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal("provider.failover", root.GetProperty("type").GetString());
        Assert.Equal("warning", root.GetProperty("severity").GetString());
        Assert.Equal("netclaw", root.GetProperty("source").GetString());
        Assert.True(root.TryGetProperty("hostname", out _));
        Assert.True(root.TryGetProperty("alertId", out _));
        Assert.True(root.TryGetProperty("timestamp", out _));
    }

    [Fact]
    public async Task IncludesCustomHeaders_WhenConfigured()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var config = new NotificationsConfig
        {
            Webhooks =
            [
                new WebhookTarget
                {
                    Url = "https://example.com/hook",
                    Headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = "Bearer test-token",
                        ["X-Custom"] = "custom-value"
                    }
                }
            ],
            DeduplicationWindowSeconds = 0
        };

        var service = CreateService(config, handler);
        await service.StartAsync(CancellationToken.None);

        service.Emit(CreateAlert());
        await WaitForDeliveryAsync(handler, expectedCount: 1);

        var request = handler.Requests[0];
        Assert.Contains("Bearer test-token", request.Headers.GetValues("Authorization"));
        Assert.Contains("custom-value", request.Headers.GetValues("X-Custom"));
    }

    [Fact]
    public async Task ContinuesRunning_WhenAllTargetsFail()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError);
        var config = new NotificationsConfig
        {
            Webhooks = [new WebhookTarget { Url = "https://example.com/hook" }],
            DeduplicationWindowSeconds = 0,
            MaxRetries = 0 // no retries for speed
        };

        var service = CreateService(config, handler);
        await service.StartAsync(CancellationToken.None);

        // First alert fails
        service.Emit(CreateAlert(source: "a"));
        await WaitForDeliveryAsync(handler, expectedCount: 1, timeoutMs: 2000);

        // Service should still be alive for a second alert
        service.Emit(CreateAlert(source: "b"));
        await WaitForDeliveryAsync(handler, expectedCount: 1, timeoutMs: 2000);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DoesNotRetry_On4xxErrors()
    {
        var handler = new RecordingHandler(HttpStatusCode.BadRequest);
        var config = new NotificationsConfig
        {
            Webhooks = [new WebhookTarget { Url = "https://example.com/hook" }],
            DeduplicationWindowSeconds = 0,
            MaxRetries = 2
        };

        var service = CreateService(config, handler);
        await service.StartAsync(CancellationToken.None);

        service.Emit(CreateAlert());
        await WaitForDeliveryAsync(handler, expectedCount: 1, timeoutMs: 2000);

        // Should not retry on 4xx
        Assert.Single(handler.Requests);
    }

    private static Task WaitForDeliveryAsync(
        RecordingHandler handler,
        int expectedCount,
        int timeoutMs = 5000)
        => WebhookTestInfrastructure.WaitForDeliveryAsync(handler, expectedCount, timeoutMs);
}
