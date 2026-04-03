using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Webhooks;
using Xunit;

namespace Netclaw.Daemon.Tests.Webhooks;

public sealed class WebhookEndpointRouteBuilderExtensionsTests : IDisposable
{
    private int _writeVersion;
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;
    private readonly Netclaw.Configuration.WebhookRouteStore _store;

    public WebhookEndpointRouteBuilderExtensionsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-webhook-endpoints-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
        _store = new Netclaw.Configuration.WebhookRouteStore(_paths);
    }

    [Fact]
    public async Task Unknown_route_returns_404()
    {
        await using var app = await CreateHostAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsync("/api/webhooks/missing", JsonContent.Create(new { hello = "world" }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Route_file_added_after_start_becomes_active_without_restart()
    {
        await using var app = await CreateHostAsync();
        var client = app.GetTestClient();

        var initialResponse = await client.PostAsync("/api/webhooks/github-issues", JsonContent.Create(new { hello = "world" }), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, initialResponse.StatusCode);

        _store.Save("github-issues", CreateRoute());
        BumpWriteTime("github-issues");

        using var request = BuildGitHubRequest("/api/webhooks/github-issues", "{\"repository\":{\"full_name\":\"petabridge/netclaw\"}}", "issues", "delivery-1", "secret");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(app.Services.GetRequiredService<FakeWebhookExecutionService>().Invocations);
    }

    [Fact]
    public async Task Invalid_signature_returns_401()
    {
        _store.Save("github-issues", CreateRoute());
        BumpWriteTime("github-issues");
        await using var app = await CreateHostAsync();
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/github-issues")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Hub-Signature-256", "sha256=deadbeef");
        request.Headers.Add("X-GitHub-Event", "issues");
        request.Headers.Add("X-GitHub-Delivery", "delivery-1");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_body_returns_413()
    {
        _store.Save("github-issues", CreateRoute(maxBodyBytes: 8));
        BumpWriteTime("github-issues");
        await using var app = await CreateHostAsync();
        var client = app.GetTestClient();
        var body = "{\"payload\":\"too-large\"}";

        using var request = BuildGitHubRequest("/api/webhooks/github-issues", body, "issues", "delivery-1", "secret");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_delivery_returns_202_and_does_not_start_second_execution()
    {
        _store.Save("github-issues", CreateRoute());
        BumpWriteTime("github-issues");
        await using var app = await CreateHostAsync();
        var client = app.GetTestClient();
        var body = "{\"repository\":{\"full_name\":\"petabridge/netclaw\"}}";

        using var first = BuildGitHubRequest("/api/webhooks/github-issues", body, "issues", "delivery-1", "secret");
        using var second = BuildGitHubRequest("/api/webhooks/github-issues", body, "issues", "delivery-1", "secret");

        var firstResponse = await client.SendAsync(first, TestContext.Current.CancellationToken);
        var secondResponse = await client.SendAsync(second, TestContext.Current.CancellationToken);
        var payload = await secondResponse.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var execution = app.Services.GetRequiredService<FakeWebhookExecutionService>();

        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
        Assert.Equal("duplicate_delivery", payload.GetProperty("reason").GetString());
        Assert.Single(execution.Invocations);
    }

    [Fact]
    public async Task Accepted_request_starts_execution_and_emits_receipt_alert()
    {
        _store.Save("github-issues", CreateRoute());
        BumpWriteTime("github-issues");
        await using var app = await CreateHostAsync();
        var client = app.GetTestClient();
        var body = "{\"repository\":{\"full_name\":\"petabridge/netclaw\"}}";

        using var request = BuildGitHubRequest("/api/webhooks/github-issues", body, "issues", "delivery-1", "secret");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        var execution = app.Services.GetRequiredService<FakeWebhookExecutionService>();
        var notifications = app.Services.GetRequiredService<RecordingNotificationSink>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(execution.Invocations);
        Assert.Equal("github-issues", execution.Invocations[0].Route.Name);
        Assert.Contains(notifications.Alerts, x => x.Category == AlertType.WebhookReceived);
        Assert.Equal("accepted", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Invalid_edit_removes_route_without_restart_and_emits_route_alert()
    {
        _store.Save("github-issues", CreateRoute());
        BumpWriteTime("github-issues");
        await using var app = await CreateHostAsync();
        var client = app.GetTestClient();
        var body = "{\"repository\":{\"full_name\":\"petabridge/netclaw\"}}";

        using var first = BuildGitHubRequest("/api/webhooks/github-issues", body, "issues", "delivery-1", "secret");
        var firstResponse = await client.SendAsync(first, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);

        File.WriteAllText(Path.Combine(_paths.WebhooksDirectory, "github-issues.json"), "{ not valid json");
        BumpWriteTime("github-issues");

        using var second = BuildGitHubRequest("/api/webhooks/github-issues", body, "issues", "delivery-2", "secret");
        var secondResponse = await client.SendAsync(second, TestContext.Current.CancellationToken);

        var notifications = app.Services.GetRequiredService<RecordingNotificationSink>();
        Assert.Equal(HttpStatusCode.NotFound, secondResponse.StatusCode);
        Assert.Contains(notifications.Alerts, x => x.Category == AlertType.WebhookRouteInvalid);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private void BumpWriteTime(string routeName)
    {
        var filePath = Path.Combine(_paths.WebhooksDirectory, $"{routeName}.json");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddSeconds(++_writeVersion));
    }

    private async Task<WebApplication> CreateHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(_paths);
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton(new WebhooksConfig { Enabled = true });
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.Parse("2026-04-02T18:30:00Z")));
        builder.Services.AddSingleton<WebhookRouteCatalog>();
        builder.Services.AddSingleton<WebhookRequestVerifier>();
        builder.Services.AddSingleton<WebhookIngressGuard>(sp =>
            new WebhookIngressGuard(sp.GetRequiredService<TimeProvider>(), NullLogger<WebhookIngressGuard>.Instance));
        builder.Services.AddSingleton<FakeWebhookExecutionService>();
        builder.Services.AddSingleton<IWebhookExecutionService>(sp => sp.GetRequiredService<FakeWebhookExecutionService>());
        builder.Services.AddSingleton<RecordingNotificationSink>();
        builder.Services.AddSingleton<IOperationalNotificationSink>(sp => sp.GetRequiredService<RecordingNotificationSink>());
        builder.Services.AddLogging();

        var app = builder.Build();
        app.MapWebhookEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static HttpRequestMessage BuildGitHubRequest(string path, string body, string eventType, string deliveryId, string secret)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = $"sha256={Convert.ToHexString(hmac.ComputeHash(bodyBytes)).ToLowerInvariant()}";

        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Hub-Signature-256", signature);
        request.Headers.Add("X-GitHub-Event", eventType);
        request.Headers.Add("X-GitHub-Delivery", deliveryId);
        return request;
    }

    private static WebhookRouteConfig CreateRoute(int maxBodyBytes = 1024 * 1024) => new()
    {
        Prompt = "triage this event",
        Events = ["issues"],
        NotificationTarget = new NotificationTargetConfig
        {
            Kind = NotificationTargetKind.Slack,
            ChannelId = "C123"
        },
        Verification = new WebhookVerificationConfig
        {
            Kind = WebhookVerifierKind.Hmac,
            Secret = new SensitiveString("secret"),
            SignatureHeaderName = "X-Hub-Signature-256",
            SignaturePrefix = "sha256=",
            EventHeaderName = "X-GitHub-Event",
            DeliveryIdHeaderName = "X-GitHub-Delivery"
        },
        MaxBodyBytes = maxBodyBytes,
        RateLimitPerMinute = 30
    };

    private sealed class FakeWebhookExecutionService : IWebhookExecutionService
    {
        public List<WebhookInvocation> Invocations { get; } = [];

        public void StartInvocation(WebhookInvocation invocation)
            => Invocations.Add(invocation);
    }

    private sealed class RecordingNotificationSink : IOperationalNotificationSink
    {
        public List<OperationalAlert> Alerts { get; } = [];

        public void Emit(OperationalAlert alert)
            => Alerts.Add(alert);
    }
}
