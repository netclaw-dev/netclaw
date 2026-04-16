using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Channels.Slack.Webhooks;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class SlackBlockKitWebhookFormatterTests : IAsyncDisposable
{
    private static OperationalAlert CreateAlert(
        string type = "mcp.auth.expired",
        AlertSeverity severity = AlertSeverity.Warning,
        string? source = null,
        Dictionary<string, string>? context = null)
    {
        return new OperationalAlert
        {
            AlertId = "test123",
            Type = type,
            Category = AlertType.McpAuthExpired,
            Summary = $"Test alert: {type}",
            Timestamp = new DateTimeOffset(2026, 3, 21, 12, 0, 0, TimeSpan.Zero),
            Severity = severity,
            Source = source,
            Context = context
        };
    }

    #region SlackWebhookPayloadBuilder unit tests

    [Fact]
    public void Build_ProducesPayloadWithTextField()
    {
        var alert = CreateAlert();
        var payload = SlackWebhookPayloadBuilder.Build(alert);

        var json = JsonSerializer.Serialize(payload);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("text", out var text));
        Assert.Contains("mcp.auth.expired", text.GetString());
        Assert.Contains("Warning", text.GetString());
    }

    [Fact]
    public void Build_ProducesBlocksArray()
    {
        var alert = CreateAlert();
        var payload = SlackWebhookPayloadBuilder.Build(alert);

        var json = JsonSerializer.Serialize(payload);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("blocks", out var blocks));
        Assert.Equal(JsonValueKind.Array, blocks.ValueKind);
        Assert.True(blocks.GetArrayLength() >= 3); // header + section + fields

        // Verify block types
        var blockTypes = blocks.EnumerateArray()
            .Select(b => b.GetProperty("type").GetString())
            .ToList();
        Assert.Equal("header", blockTypes[0]);
        Assert.Equal("section", blockTypes[1]);
        Assert.Equal("section", blockTypes[2]); // fields section
    }

    [Fact]
    public void Build_IncludesContextBlock_WhenAlertHasContext()
    {
        var alert = CreateAlert(context: new Dictionary<string, string>
        {
            ["serverName"] = "notion",
            ["action"] = "re-authorize"
        });

        var payload = SlackWebhookPayloadBuilder.Build(alert);

        var json = JsonSerializer.Serialize(payload);
        var doc = JsonDocument.Parse(json);
        var blocks = doc.RootElement.GetProperty("blocks");

        var lastBlock = blocks[blocks.GetArrayLength() - 1];
        Assert.Equal("context", lastBlock.GetProperty("type").GetString());

        var elements = lastBlock.GetProperty("elements");
        Assert.Equal(2, elements.GetArrayLength());
    }

    [Fact]
    public void Build_OmitsContextBlock_WhenAlertContextIsNull()
    {
        var alert = CreateAlert(context: null);

        var payload = SlackWebhookPayloadBuilder.Build(alert);

        var json = JsonSerializer.Serialize(payload);
        var doc = JsonDocument.Parse(json);
        var blocks = doc.RootElement.GetProperty("blocks");

        // No context block — only header + section + fields = 3
        Assert.Equal(3, blocks.GetArrayLength());
    }

    [Theory]
    [InlineData(AlertSeverity.Critical, ":red_circle:")]
    [InlineData(AlertSeverity.Warning, ":warning:")]
    [InlineData(AlertSeverity.Info, ":information_source:")]
    public void Build_SeverityEmoji_MapsCorrectly(AlertSeverity severity, string expectedEmoji)
    {
        var alert = CreateAlert(severity: severity);

        var payload = SlackWebhookPayloadBuilder.Build(alert);

        var json = JsonSerializer.Serialize(payload);
        var doc = JsonDocument.Parse(json);

        var text = doc.RootElement.GetProperty("text").GetString()!;
        Assert.Contains(expectedEmoji, text);
    }

    [Fact]
    public void Build_IncludesSourceField_WhenSourceSet()
    {
        var alert = CreateAlert(source: "notion-mcp");

        var payload = SlackWebhookPayloadBuilder.Build(alert);

        var json = JsonSerializer.Serialize(payload);
        Assert.Contains("notion-mcp", json);
    }

    #endregion

    #region Integration: WebhookNotificationService with Slack format

    private readonly List<WebhookNotificationService> _services = [];

    private WebhookNotificationService CreateService(
        NotificationsConfig config,
        RecordingHandler handler)
    {
        var factory = new TestHttpClientFactory(handler);
        var service = new WebhookNotificationService(
            config,
            factory,
            TimeProvider.System,
            NullLogger<WebhookNotificationService>.Instance);
        _services.Add(service);
        return service;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _services)
            service.Dispose();
        _services.Clear();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task UsesSlackFormat_WhenTargetFormatIsSlack()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var config = new NotificationsConfig
        {
            Webhooks = [new WebhookTarget { Url = "https://hooks.slack.com/test", Format = WebhookFormat.Slack }],
            DeduplicationWindowSeconds = 0
        };

        var service = CreateService(config, handler);
        await service.StartAsync(CancellationToken.None);

        service.Emit(CreateAlert());
        await WaitForDeliveryAsync(handler, expectedCount: 1);

        var body = handler.RequestBodies[0];
        var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Slack format has "text" + "blocks"
        Assert.True(root.TryGetProperty("text", out _));
        Assert.True(root.TryGetProperty("blocks", out _));
        // Should NOT have "alertId" at root (that's generic format)
        Assert.False(root.TryGetProperty("alertId", out _));
    }

    [Fact]
    public async Task UsesGenericFormat_WhenTargetFormatIsDefault()
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

        var body = handler.RequestBodies[0];
        var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Generic format has "alertId", "type", "severity", etc.
        Assert.True(root.TryGetProperty("alertId", out _));
        Assert.True(root.TryGetProperty("type", out _));
        // Should NOT have "blocks"
        Assert.False(root.TryGetProperty("blocks", out _));
    }

    [Fact]
    public async Task MixedFormats_EachTargetGetsCorrectPayload()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var config = new NotificationsConfig
        {
            Webhooks =
            [
                new WebhookTarget { Url = "https://example.com/generic", Format = WebhookFormat.Generic },
                new WebhookTarget { Url = "https://hooks.slack.com/slack", Format = WebhookFormat.Slack },
            ],
            DeduplicationWindowSeconds = 0
        };

        var service = CreateService(config, handler);
        await service.StartAsync(CancellationToken.None);

        service.Emit(CreateAlert());
        await WaitForDeliveryAsync(handler, expectedCount: 2);

        // Find which body is which by URL
        var genericIdx = handler.Requests
            .Select((r, i) => (r, i))
            .First(x => x.r.RequestUri?.ToString() == "https://example.com/generic").i;
        var slackIdx = handler.Requests
            .Select((r, i) => (r, i))
            .First(x => x.r.RequestUri?.ToString() == "https://hooks.slack.com/slack").i;

        var genericDoc = JsonDocument.Parse(handler.RequestBodies[genericIdx]);
        var slackDoc = JsonDocument.Parse(handler.RequestBodies[slackIdx]);

        Assert.True(genericDoc.RootElement.TryGetProperty("alertId", out _));
        Assert.True(slackDoc.RootElement.TryGetProperty("blocks", out _));
    }

    #endregion

    private static Task WaitForDeliveryAsync(
        RecordingHandler handler,
        int expectedCount,
        int timeoutMs = 5000)
        => WebhookTestInfrastructure.WaitForDeliveryAsync(handler, expectedCount, timeoutMs);
}
