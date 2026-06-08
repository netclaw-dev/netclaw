// -----------------------------------------------------------------------
// <copyright file="McpOAuthHeaderConflictTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

/// <summary>
/// Regression tests for GitHub issue #1350: "MCP server with static bearer token
/// blocked by OAuth discovery probe".
///
/// When an operator configures a static <c>Authorization</c> header on an HTTP MCP
/// server, the daemon must NOT block the connection with "Awaiting Auth" even if
/// the server responds to the OAuth discovery probe with a 401 +
/// <c>WWW-Authenticate</c> header containing <c>resource_metadata=</c>.
/// The configured header should be sent through to the server.
/// </summary>
public sealed class McpOAuthHeaderConflictTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    /// <summary>
    /// Verifies the bug: when an HTTP MCP server has a static Authorization header
    /// AND returns 401 + WWW-Authenticate with resource_metadata on the probe, the
    /// daemon currently blocks the connection entirely instead of sending the
    /// configured header. This is the exact reproduction of #1350.
    /// </summary>
    [Fact]
    public async Task StaticAuthHeader_WhenServerReturnsOAuthMetadata_StillConnects()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        const string expectedAuth = "Bearer my-static-token-1350";

        // Create a fake OAuth metadata discovery client that mimics a server
        // which returns 401 + WWW-Authenticate: Bearer resource_metadata="..."
        // on the initial probe, AND serves resource metadata at the discovered URL.
        var oauthHttpClient = CreateDiscoveryClientThatReturnsOAuthHints();

        // Create an HTTP client for the PKCE service (used by McpOAuthService
        // for token exchange, which we don't exercise in this test).
        var pkceHttpClient = new HttpClient();

        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();

        var oauthService = new McpOAuthService(
            oauthHttpClient,
            paths,
            TimeProvider.System,
            NullLogger<McpOAuthService>.Instance,
            new OAuthPkceService(pkceHttpClient),
            NullNotificationSink.Instance);

        // Build a McpClientManager with an HTTP server entry that has a static
        // Authorization header configured.
        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = "https://mcp.example.com",
            Enabled = true,
            Headers = new Dictionary<string, SensitiveString>
            {
                ["Authorization"] = new SensitiveString(expectedAuth),
            },
        };

        var registry = new ToolRegistry();
        var manager = new McpClientManager(
            new Dictionary<string, McpServerEntry> { ["mcp-static"] = entry },
            registry,
            new ToolConfig(),
            oauthService,
            NullNotificationSink.Instance,
            TimeProvider.System,
            NullLogger<McpClientManager>.Instance);

        try
        {
            await manager.StartAsync(ct);

            // BUG: The manager would have set "AwaitingAuth" here because the
            // OAuth discovery probe returned metadata, even though the user
            // already configured a static Authorization header.
            //
            // After the fix, the OAuth discovery is skipped (since headers
            // exist), so the connection attempt proceeds to the HTTP layer.
            // The connection to https://mcp.example.com will fail with a
            // network error — but critically, it will NOT be AwaitingAuth.
            var status = manager.GetServerStatuses()[new McpServerName("mcp-static")];

            // This is the key assertion: we must NOT be stuck in AwaitingAuth.
            // After the fix, the status will be Unreachable (connection to a
            // nonexistent server fails) rather than AwaitingAuth.
            Assert.NotEqual(McpConnectionState.AwaitingAuth, status.State);

            // The error should be a connection failure (network unreachable),
            // not an "Awaiting OAuth" message.
            Assert.DoesNotContain("OAuth", status.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("authorization", status.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await manager.StopAsync(ct);
            manager.Dispose();
        }
    }

    /// <summary>
    /// Positive control: when no OAuth hints are returned by the server,
    /// the static auth header test still passes. This validates that the test
    /// harness itself works correctly.
    /// </summary>
    [Fact]
    public async Task StaticAuthHeader_WhenServerReturnsNoOAuthMetadata_ConnectsNormally()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        const string expectedAuth = "Bearer my-static-token-no-oauth";

        // Create a discovery client that does NOT return OAuth hints.
        var oauthHttpClient = new HttpClient(new FakeHttpMessageHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK) // No 401, no OAuth hints
        ));
        var pkceHttpClient = new HttpClient();

        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();

        var oauthService = new McpOAuthService(
            oauthHttpClient,
            paths,
            TimeProvider.System,
            NullLogger<McpOAuthService>.Instance,
            new OAuthPkceService(pkceHttpClient),
            NullNotificationSink.Instance);

        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = "https://mcp.example.com",
            Enabled = true,
            Headers = new Dictionary<string, SensitiveString>
            {
                ["Authorization"] = new SensitiveString(expectedAuth),
            },
        };

        var registry = new ToolRegistry();
        var manager = new McpClientManager(
            new Dictionary<string, McpServerEntry> { ["mcp-static-no-oauth"] = entry },
            registry,
            new ToolConfig(),
            oauthService,
            NullNotificationSink.Instance,
            TimeProvider.System,
            NullLogger<McpClientManager>.Instance);

        try
        {
            await manager.StartAsync(ct);

            var status = manager.GetServerStatuses()[new McpServerName("mcp-static-no-oauth")];

            // This should pass regardless of the bug because there are no OAuth hints.
            Assert.NotEqual(McpConnectionState.AwaitingAuth, status.State);
        }
        finally
        {
            await manager.StopAsync(ct);
            manager.Dispose();
        }
    }

    /// <summary>
    /// When a user has cached OAuth tokens, OAuth discovery probing is skipped
    /// and the static header test should pass even with OAuth hints.
    /// </summary>
    [Fact]
    public async Task CachedOAuthTokens_AreCheckedBeforeOAuthDiscovery()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        // Create a discovery client that returns aggressive OAuth hints.
        var oauthHttpClient = CreateDiscoveryClientThatReturnsOAuthHints();
        var pkceHttpClient = new HttpClient();

        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();

        // Pre-seed the OAuth service with a token so GetTokenSet returns non-null.
        var oauthService = new McpOAuthService(
            oauthHttpClient,
            paths,
            TimeProvider.System,
            NullLogger<McpOAuthService>.Instance,
            new OAuthPkceService(pkceHttpClient),
            NullNotificationSink.Instance);

        // Manually populate the in-memory token cache via reflection.
        var tokensField = oauthService.GetType()
            .GetField("_tokens", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var existingTokens = tokensField.GetValue(oauthService) as ConcurrentDictionary<McpServerName, McpOAuthTokenSet>;
        existingTokens!.TryAdd(new McpServerName("mcp-with-tokens"), new McpOAuthTokenSet
        {
            AccessToken = new SensitiveString("cached-access-token"),
            RefreshToken = new SensitiveString("cached-refresh-token"),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        });

        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = "https://mcp.example.com",
            Enabled = true,
        };

        var registry = new ToolRegistry();
        var manager = new McpClientManager(
            new Dictionary<string, McpServerEntry> { ["mcp-with-tokens"] = entry },
            registry,
            new ToolConfig(),
            oauthService,
            NullNotificationSink.Instance,
            TimeProvider.System,
            NullLogger<McpClientManager>.Instance);

        try
        {
            await manager.StartAsync(ct);

            var status = manager.GetServerStatuses()[new McpServerName("mcp-with-tokens")];

            // When tokens exist, OAuth discovery should be skipped and the connection
            // should proceed (even though the server returns OAuth hints).
            Assert.NotEqual(McpConnectionState.AwaitingAuth, status.State);
        }
        finally
        {
            await manager.StopAsync(ct);
            manager.Dispose();
        }
    }

    private static HttpClient CreateDiscoveryClientThatReturnsOAuthHints()
    {
        return new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url == "https://mcp.example.com/" || url == "https://mcp.example.com")
            {
                // The initial probe returns 401 with WWW-Authenticate containing resource_metadata.
                // This is the pattern that triggers the OAuth blocking logic.
                var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                response.Headers.Add("WWW-Authenticate",
                    "Bearer resource_metadata=\"https://mcp.example.com/oauth/resource-metadata\"");
                return response;
            }

            if (url == "https://mcp.example.com/oauth/resource-metadata")
            {
                // The resource metadata endpoint returns the discovery doc.
                return JsonResponse(new
                {
                    authorization_servers = new[] { "https://auth.example.com" },
                    resource = "https://mcp.example.com/resource"
                });
            }

            if (url == "https://mcp.example.com/.well-known/oauth-protected-resource")
            {
                // Fallback well-known endpoint also returns OAuth metadata.
                return JsonResponse(new
                {
                    authorization_servers = new[] { "https://auth.example.com" },
                    resource = "https://mcp.example.com/resource"
                });
            }

            throw new InvalidOperationException($"Unexpected request URI: {request.RequestUri}");
        }));
    }

    private static HttpResponseMessage JsonResponse(object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }

    public void Dispose()
    {
        _dir.Dispose();
    }
}
