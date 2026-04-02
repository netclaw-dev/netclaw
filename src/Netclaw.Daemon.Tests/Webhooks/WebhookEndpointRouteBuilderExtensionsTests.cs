using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Webhooks;
using Xunit;

namespace Netclaw.Daemon.Tests.Webhooks;

public sealed class WebhookEndpointRouteBuilderExtensionsTests
{
    [Fact]
    public async Task Unknown_route_returns_404()
    {
        await using var app = await CreateHostAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsync("/api/webhooks/missing", JsonContent.Create(new { hello = "world" }), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_signature_returns_401()
    {
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
        await using var app = await CreateHostAsync(configure: config =>
            config.Routes["github-issues"].MaxBodyBytes = 8);
        var client = app.GetTestClient();
        var body = "{\"payload\":\"too-large\"}";

        using var request = BuildGitHubRequest("/api/webhooks/github-issues", body, "issues", "delivery-1", "secret");
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_delivery_returns_202_and_does_not_start_second_execution()
    {
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
        Assert.Equal(AlertType.WebhookReceived, Assert.Single(notifications.Alerts).Category);
        Assert.Equal("accepted", payload.GetProperty("status").GetString());
    }

    private static async Task<WebApplication> CreateHostAsync(Action<WebhooksConfig>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var config = new WebhooksConfig
        {
            Enabled = true,
            Routes = new Dictionary<string, WebhookRouteConfig>
            {
                ["github-issues"] = new()
                {
                    Prompt = "triage",
                    Verification = new WebhookVerificationConfig
                    {
                        Kind = WebhookVerifierKind.GitHubHmacSha256,
                        Secret = new SensitiveString("secret")
                    },
                    Events = ["issues"],
                    NotificationTarget = new NotificationTargetConfig
                    {
                        Kind = NotificationTargetKind.Slack,
                        ChannelId = "C123"
                    }
                }
            }
        };
        configure?.Invoke(config);

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.Parse("2026-04-02T18:30:00Z")));
        builder.Services.AddSingleton<WebhookRouteCatalog>();
        builder.Services.AddSingleton<WebhookRequestVerifier>();
        builder.Services.AddSingleton<WebhookIngressGuard>();
        builder.Services.AddSingleton<FakeWebhookExecutionService>();
        builder.Services.AddSingleton<IWebhookExecutionService>(sp => sp.GetRequiredService<FakeWebhookExecutionService>());
        builder.Services.AddSingleton<RecordingNotificationSink>();
        builder.Services.AddSingleton<IOperationalNotificationSink>(sp => sp.GetRequiredService<RecordingNotificationSink>());

        var app = builder.Build();
        app.MapWebhookEndpoints();
        await app.StartAsync();
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
