// -----------------------------------------------------------------------
// <copyright file="McpEndpointRouteBuilderExtensionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Authentication;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Daemon.Mcp;
using Netclaw.Daemon.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

/// <summary>
/// Real integration tests for the MCP endpoints registered by
/// <see cref="McpEndpointRouteBuilderExtensions.MapMcpEndpoints"/>.
///
/// The test host calls the actual extension method — no handler reimplementation.
/// </summary>
public sealed class McpEndpointRouteBuilderExtensionsTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // ─── App factory ───────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a test host wired with real <see cref="McpEndpointRouteBuilderExtensions.MapMcpEndpoints"/>.
    /// Uses the production flow broker and credential store with a manager whose
    /// network path remains dormant unless a test supplies a pending broker flow.
    /// </summary>
    private async Task<WebApplication> CreateAppAsync(
        bool spoofLoopback,
        Dictionary<string, McpServerEntry>? mcpServers = null,
        McpOAuthFlowBroker? flowBroker = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();
        builder.Services.AddLogging();

        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();

        var servers = mcpServers ?? [];

        var credentialStore = new McpOAuthCredentialStore(
            paths,
            TimeProvider.System,
            new NullSecretsProtector(),
            NullLogger<McpOAuthCredentialStore>.Instance);
        flowBroker ??= new McpOAuthFlowBroker(TimeProvider.System, CancellationToken.None);

        // Minimal McpClientManager with empty state
        var toolRegistry = new ToolRegistry();
        var dependencies = McpManagerTestDependencies.Create();
        var mcpManager = new McpClientManager(
            servers,
            toolRegistry,
            dependencies.SkillRegistry,
            dependencies.SkillIndexPublisher,
            dependencies.ToolAccessPolicy,
            dependencies.ToolConfig,
            credentialStore,
            McpOAuthTestDoubles.UnusedRegistrar(),
            flowBroker,
            new DaemonConfig(),
            NullNotificationSink.Instance,
            TimeProvider.System,
            new McpClientRuntime(),
            NullLogger<McpClientManager>.Instance,
            new SessionConfig());

        builder.Services.AddSingleton(credentialStore);
        builder.Services.AddSingleton(flowBroker);
        builder.Services.AddSingleton(mcpManager);
        builder.Services.AddSingleton<McpClientManager>(mcpManager);
        builder.Services.AddSingleton(servers);
        builder.Services.AddSingleton<ILogger<McpClientManager>>(NullLogger<McpClientManager>.Instance);

        var app = builder.Build();

        if (spoofLoopback)
        {
            app.Use(async (ctx, next) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
                await next(ctx);
            });
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapMcpEndpoints();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    // ─── Auth gates — five .RequireAuthorization() endpoints → 401 ────────────

    [Theory]
    [InlineData("POST", "/api/mcp/oauth/start/test-server")]
    [InlineData("GET", "/api/mcp/statuses")]
    [InlineData("GET", "/api/mcp/tools/test-server")]
    [InlineData("GET", "/api/mcp/oauth/status/test-server")]
    [InlineData("GET", "/api/mcp/oauth/status-by-state/some-state")]
    public async Task RequiresAuthorization_returns_401_for_unauthenticated_request(string method, string path)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: false);
        var client = app.GetTestClient();

        var request = new HttpRequestMessage(new HttpMethod(method), path);
        var response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ─── GET /api/mcp/oauth/callback (.AllowAnonymous) ────────────────────────

    [Fact]
    public async Task Callback_returns_failure_html_when_code_and_state_are_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: false); // no auth needed
        var client = app.GetTestClient();

        // No Authorization header — proves AllowAnonymous is wired
        var response = await client.GetAsync("/api/mcp/oauth/callback", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("Authorization failed", html);
        Assert.DoesNotContain("abc", html, StringComparison.Ordinal);
        Assert.DoesNotContain("unknown-state", html, StringComparison.Ordinal);
        Assert.Contains("Missing code or state parameter", html);
    }

    [Fact]
    public async Task Callback_returns_failure_html_for_unknown_state()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: false);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/mcp/oauth/callback?code=abc&state=unknown-state", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("Authorization failed", html);
    }

    // ─── POST /api/mcp/oauth/start/{name} ─────────────────────────────────────

    [Fact]
    public async Task OauthStart_returns_404_for_unknown_server_name()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.PostAsync("/api/mcp/oauth/start/nonexistent", null, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Contains("nonexistent", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task OauthStart_returns_400_when_server_has_no_url()
    {
        var ct = TestContext.Current.CancellationToken;
        var servers = new Dictionary<string, McpServerEntry>
        {
            ["stdio-server"] = new McpServerEntry { Transport = "stdio", Command = "mcp-server" }
        };

        await using var app = await CreateAppAsync(spoofLoopback: true, mcpServers: servers);
        var client = app.GetTestClient();

        var response = await client.PostAsync("/api/mcp/oauth/start/stdio-server", null, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Contains("no URL", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetStatuses_includes_lastErrorAt_for_connection_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var servers = new Dictionary<string, McpServerEntry>
        {
            ["broken"] = new()
            {
                Enabled = true,
                Transport = "stdio",
                Command = "definitely-not-a-real-command",
            },
        };
        await using var app = await CreateAppAsync(spoofLoopback: true, mcpServers: servers);
        var manager = app.Services.GetRequiredService<McpClientManager>();
        await manager.StartAsync(ct);

        var response = await app.GetTestClient().GetAsync("/api/mcp/statuses", ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Unreachable", body.GetProperty("broken").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.String, body.GetProperty("broken").GetProperty("lastErrorAt").ValueKind);
        await manager.StopAsync(ct);
    }

    [Fact]
    public async Task OAuthStatusByNameAndStateIncludeSameSafeStructuredTerminalError()
    {
        var ct = TestContext.Current.CancellationToken;
        using var broker = new McpOAuthFlowBroker(TimeProvider.System, CancellationToken.None);
        var flow = broker.StartOrJoin(new McpServerName("failed-server")).Flow;
        // Look-up by state needs a state, and only the SDK can supply one. Drive the
        // callback handler far enough to publish the authorization URL it built.
        _ = InvokeCallbackHandler(flow, new Uri("https://auth.example.com/authorize?state=failed-state"), ct);
        await flow.WaitForAuthorizationRequestAsync(ct);
        broker.Fail(flow, new McpErrorResponse(
            "MCP OAuth dynamic client registration failed: HTTP 403 Forbidden.",
            "dynamic client registration",
            403));
        await using var app = await CreateAppAsync(spoofLoopback: true, flowBroker: broker);

        var client = app.GetTestClient();
        var response = await client.GetAsync(
            $"/api/mcp/oauth/status-by-state/{flow.State}", ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var byNameResponse = await client.GetAsync("/api/mcp/oauth/status/failed-server", ct);
        var byName = await byNameResponse.Content.ReadFromJsonAsync<JsonElement>(ct);

        Assert.Equal("Failed", body.GetProperty("status").GetString());
        Assert.Equal(403, body.GetProperty("error").GetProperty("status").GetInt32());
        Assert.Equal(
            "dynamic client registration",
            body.GetProperty("error").GetProperty("operation").GetString());
        Assert.Equal(body.GetRawText(), byName.GetRawText());
    }

    // ─── Happy-path: oauth/start then callback ─────────────────────────────────

    [Fact]
    public async Task OauthStart_returns_200_with_authorizationUrl_and_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var servers = new Dictionary<string, McpServerEntry>
        {
            ["test-mcp"] = new McpServerEntry
            {
                Transport = "http",
                Url = "https://mcp.example.com",
                Enabled = true,
                OAuthClientId = "test-client"
            }
        };

        using var broker = new McpOAuthFlowBroker(TimeProvider.System, CancellationToken.None);
        var pending = broker.StartOrJoin(new McpServerName("test-mcp")).Flow;
        var expectedUrl = new Uri("https://auth.example.com/authorize?client_id=test-client&state=sdk-state");
        var owner = InvokeCallbackHandler(pending, expectedUrl, ct);
        await pending.WaitForAuthorizationRequestAsync(ct);
        await using var app = await CreateAppAsync(
            spoofLoopback: true,
            mcpServers: servers,
            flowBroker: broker);
        var client = app.GetTestClient();

        var response = await client.PostAsync("/api/mcp/oauth/start/test-mcp", null, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(body.TryGetProperty("authorizationUrl", out var urlProp));
        Assert.True(body.TryGetProperty("state", out var stateProp));
        Assert.False(string.IsNullOrWhiteSpace(urlProp.GetString()));
        Assert.Equal("sdk-state", stateProp.GetString());
        Assert.Equal(expectedUrl.ToString(), urlProp.GetString());
        broker.Fail(pending, new McpErrorResponse("test cleanup"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await owner);
    }

    [Fact]
    public async Task Callback_happy_path_returns_success_html_after_owner_exchange_completes()
    {
        var ct = TestContext.Current.CancellationToken;
        var servers = new Dictionary<string, McpServerEntry>
        {
            ["test-mcp"] = new McpServerEntry
            {
                Transport = "http",
                Url = "https://mcp.example.com",
                Enabled = true,
                OAuthClientId = "test-client"
            }
        };

        using var broker = new McpOAuthFlowBroker(TimeProvider.System, CancellationToken.None);
        var flow = broker.StartOrJoin(new McpServerName("test-mcp")).Flow;
        var owner = InvokeCallbackHandler(
            flow,
            new Uri("https://auth.example.com/authorize?state=happy-path"),
            ct);
        await flow.WaitForAuthorizationRequestAsync(ct);
        await using var app = await CreateAppAsync(
            spoofLoopback: true,
            mcpServers: servers,
            flowBroker: broker);
        var client = app.GetTestClient();

        // Complete via callback — no Authorization header (AllowAnonymous)
        var callback = client.GetAsync(
            $"/api/mcp/oauth/callback?code=test-code&state={flow.State}&iss=https%3A%2F%2Fauth.example.com", ct);
        var result = await owner;
        Assert.Equal("test-code", result?.Code);
        // The SDK validates both against what it generated, so both must survive the round trip.
        Assert.Equal("happy-path", result?.State);
        Assert.Equal("https://auth.example.com", result?.Iss);
        broker.BeginCommit(flow);
        broker.Complete(flow);
        var callbackResponse = await callback;

        Assert.Equal(HttpStatusCode.OK, callbackResponse.StatusCode);
        var html = await callbackResponse.Content.ReadAsStringAsync(ct);
        Assert.Contains("Authorization complete", html);
    }

    [Fact]
    public async Task Callback_exchange_failure_returns_safe_html_without_code()
    {
        var ct = TestContext.Current.CancellationToken;
        using var broker = new McpOAuthFlowBroker(TimeProvider.System, CancellationToken.None);
        var flow = broker.StartOrJoin(new McpServerName("test-mcp")).Flow;
        var owner = InvokeCallbackHandler(
            flow,
            new Uri("https://auth.example.com/authorize?state=failure-path"),
            ct);
        await flow.WaitForAuthorizationRequestAsync(ct);
        await using var app = await CreateAppAsync(spoofLoopback: true, flowBroker: broker);
        var callback = app.GetTestClient().GetAsync(
            $"/api/mcp/oauth/callback?code=sensitive-code&state={flow.State}", ct);
        Assert.Equal("sensitive-code", (await owner)?.Code);
        broker.Fail(flow, new McpErrorResponse(
            "MCP OAuth authorization code exchange failed: HTTP 403 Forbidden.",
            "authorization code exchange",
            403));

        var response = await callback;
        var html = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("authorization code exchange failed", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensitive-code", html, StringComparison.Ordinal);
        Assert.DoesNotContain("failure-path", html, StringComparison.Ordinal);
    }

    private static Task<AuthorizationResult?> InvokeCallbackHandler(
        McpOAuthFlow flow,
        Uri authorizationUri,
        CancellationToken cancellationToken)
        => flow.HandleAuthorizationCallbackAsync(
            new AuthorizationCallbackContext
            {
                AuthorizationUri = authorizationUri,
                RedirectUri = new Uri("http://127.0.0.1:5199/api/mcp/oauth/callback"),
            },
            cancellationToken);
}
