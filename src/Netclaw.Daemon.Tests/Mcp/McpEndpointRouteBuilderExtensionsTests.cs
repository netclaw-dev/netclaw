// -----------------------------------------------------------------------
// <copyright file="McpEndpointRouteBuilderExtensionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
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
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Daemon.Security;
using Netclaw.Providers.OAuth;
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
    /// Constructs a real <see cref="McpOAuthService"/> over a <see cref="FakeHttpMessageHandler"/>
    /// so tests can exercise OAuth state transitions without a live server.
    /// </summary>
    private async Task<WebApplication> CreateAppAsync(
        bool spoofLoopback,
        Func<HttpRequestMessage, HttpResponseMessage>? discoveryHandler = null,
        Func<HttpRequestMessage, HttpResponseMessage>? tokenHandler = null,
        Dictionary<string, McpServerEntry>? mcpServers = null,
        IMcpReconnectable? reconnectable = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();
        builder.Services.AddLogging();

        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();

        var servers = mcpServers ?? [];

        // Construct a real McpOAuthService over fake HTTP handlers
        var discoveryClient = new HttpClient(
            new FakeHttpMessageHandler(discoveryHandler ?? DefaultDiscoveryHandler));
        var pkceService = new OAuthPkceService(
            new HttpClient(new FakeHttpMessageHandler(tokenHandler ?? DefaultTokenHandler)));
        var oauthService = new McpOAuthService(
            discoveryClient,
            paths,
            TimeProvider.System,
            NullLogger<McpOAuthService>.Instance,
            pkceService,
            NullNotificationSink.Instance);

        // Minimal McpClientManager with empty state
        var toolRegistry = new ToolRegistry();
        var mcpManager = new McpClientManager(
            servers,
            toolRegistry,
            new ToolConfig(),
            oauthService,
            NullNotificationSink.Instance,
            TimeProvider.System,
            NullLogger<McpClientManager>.Instance);

        builder.Services.AddSingleton(oauthService);
        builder.Services.AddSingleton(mcpManager);
        builder.Services.AddSingleton<McpClientManager>(mcpManager);
        builder.Services.AddSingleton(servers);
        builder.Services.AddSingleton<IMcpReconnectable>(reconnectable ?? new NoOpReconnectable());
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

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("Authorization failed", html);
        Assert.Contains("Missing code or state parameter", html);
    }

    [Fact]
    public async Task Callback_returns_failure_html_for_unknown_state()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: false);
        var client = app.GetTestClient();

        // Unknown state triggers CompleteAuthorizationAsync to throw InvalidOperationException
        var response = await client.GetAsync("/api/mcp/oauth/callback?code=abc&state=unknown-state", ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("Authorization failed", html);
    }

    [Fact]
    public async Task Callback_is_reachable_anonymously()
    {
        // This test ensures AllowAnonymous is actually wired — the anonymous
        // request reaches the handler rather than being rejected with 401.
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: false);
        var client = app.GetTestClient();
        // Deliberately no Authorization header

        var response = await client.GetAsync("/api/mcp/oauth/callback", ct);

        // Any non-401 proves the endpoint was reached
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
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

    // ─── Trivial GETs — authenticated returns 200 with expected shape ──────────

    [Fact]
    public async Task GetStatuses_returns_200_with_empty_dictionary_when_no_servers_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/mcp/statuses", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        // No servers registered → empty object
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        Assert.Empty(body.EnumerateObject());
    }

    [Fact]
    public async Task GetTools_returns_200_with_empty_array_for_unknown_server()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/mcp/tools/no-such-server", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(0, body.GetArrayLength());
    }

    [Fact]
    public async Task GetOauthStatus_returns_200_with_status_field_for_known_server()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/mcp/oauth/status/any-server", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(body.TryGetProperty("status", out _));
    }

    [Fact]
    public async Task GetOauthStatusByState_returns_200_with_status_field_for_any_state()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/mcp/oauth/status-by-state/some-arbitrary-state", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(body.TryGetProperty("status", out _));
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

        await using var app = await CreateAppAsync(spoofLoopback: true, mcpServers: servers);
        var client = app.GetTestClient();

        var response = await client.PostAsync("/api/mcp/oauth/start/test-mcp", null, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.True(body.TryGetProperty("authorizationUrl", out var urlProp));
        Assert.True(body.TryGetProperty("state", out var stateProp));
        Assert.False(string.IsNullOrWhiteSpace(urlProp.GetString()));
        Assert.False(string.IsNullOrWhiteSpace(stateProp.GetString()));
    }

    [Fact]
    public async Task Callback_happy_path_returns_success_html_and_triggers_reconnect()
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

        var reconnectable = new TrackingReconnectable();

        await using var app = await CreateAppAsync(
            spoofLoopback: true,
            mcpServers: servers,
            reconnectable: reconnectable);
        var client = app.GetTestClient();

        // Start the OAuth flow to get a valid state token
        var startResponse = await client.PostAsync("/api/mcp/oauth/start/test-mcp", null, ct);
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);

        var startBody = await startResponse.Content.ReadFromJsonAsync<JsonElement>(ct);
        var state = startBody.GetProperty("state").GetString()!;

        // Complete via callback — no Authorization header (AllowAnonymous)
        var callbackResponse = await client.GetAsync(
            $"/api/mcp/oauth/callback?code=test-code&state={state}", ct);

        Assert.Equal(HttpStatusCode.OK, callbackResponse.StatusCode);
        var html = await callbackResponse.Content.ReadAsStringAsync(ct);
        Assert.Contains("Authorization complete", html);

        // Wait until the fire-and-forget reconnect task signals completion.
        // TrackingReconnectable.ReconnectCalled is set before TCS is signalled so
        // the assertion below is race-free.
        await reconnectable.ReconnectCalledTask.WaitAsync(ct);
        Assert.True(reconnectable.WasReconnectCalled, "TryReconnectAsync should have been called post-OAuth");
    }

    // ─── Default fake HTTP handlers ───────────────────────────────────────────

    private static HttpResponseMessage DefaultDiscoveryHandler(HttpRequestMessage request)
    {
        var uri = request.RequestUri!.ToString();
        return uri switch
        {
            "https://mcp.example.com/" or "https://mcp.example.com" =>
                new HttpResponseMessage(HttpStatusCode.Unauthorized),
            "https://mcp.example.com/.well-known/oauth-protected-resource" =>
                JsonResponse(new
                {
                    authorization_servers = new[] { "https://auth.example.com" },
                    resource = "https://mcp.example.com/resource"
                }),
            "https://auth.example.com/.well-known/oauth-authorization-server" =>
                JsonResponse(new
                {
                    authorization_endpoint = "https://auth.example.com/authorize",
                    token_endpoint = "https://auth.example.com/token"
                }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }

    private static HttpResponseMessage DefaultTokenHandler(HttpRequestMessage request) =>
        JsonResponse(new
        {
            access_token = "test-access-token",
            refresh_token = "test-refresh-token",
            expires_in = 3600
        });

    private static HttpResponseMessage JsonResponse(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    // ─── Fakes ────────────────────────────────────────────────────────────────

    private sealed class NoOpReconnectable : IMcpReconnectable
    {
        public IReadOnlyDictionary<McpServerName, McpServerStatus> GetServerStatuses() =>
            new Dictionary<McpServerName, McpServerStatus>();

        public Task<bool> TryReconnectAsync(McpServerName serverName, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class TrackingReconnectable : IMcpReconnectable
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasReconnectCalled { get; private set; }

        /// <summary>
        /// Completes when <see cref="TryReconnectAsync"/> has been called.
        /// Use this instead of Task.Delay to synchronize with the fire-and-forget reconnect.
        /// </summary>
        public Task ReconnectCalledTask => _tcs.Task;

        public IReadOnlyDictionary<McpServerName, McpServerStatus> GetServerStatuses() =>
            new Dictionary<McpServerName, McpServerStatus>();

        public Task<bool> TryReconnectAsync(McpServerName serverName, CancellationToken ct = default)
        {
            WasReconnectCalled = true;
            _tcs.TrySetResult();
            return Task.FromResult(true);
        }
    }
}
