// -----------------------------------------------------------------------
// <copyright file="McpOAuthHeaderConflictTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
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
/// Regression tests for GitHub issue #1350: when an operator configures static
/// headers on an HTTP MCP server, the daemon must NOT block the connection with
/// "Awaiting Auth" even if the server's OAuth discovery probe returns metadata.
/// The probe still runs (caching metadata for fallback), but the blocking gate
/// is skipped so the real connection attempt — with the user's headers — decides.
/// </summary>
public sealed class McpOAuthHeaderConflictTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    /// <summary>
    /// The bug reproduction: server has a static Authorization header AND returns
    /// 401 + WWW-Authenticate with resource_metadata on the probe. Without the fix,
    /// the connection is blocked with AwaitingAuth instead of using the static header.
    /// </summary>
    [Fact]
    public async Task StaticAuthHeader_WhenServerReturnsOAuthMetadata_StillConnects()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;

        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = "https://mcp.example.com",
            Enabled = true,
            Headers = new Dictionary<string, SensitiveString>
            {
                ["Authorization"] = new SensitiveString("Bearer my-static-token-1350"),
            },
        };

        using var manager = CreateManager("mcp-static", entry, CreateDiscoveryClientThatReturnsOAuthHints());
        try
        {
            await manager.StartAsync(ct);

            var status = manager.GetServerStatuses()[new McpServerName("mcp-static")];

            Assert.NotEqual(McpConnectionState.AwaitingAuth, status.State);
        }
        finally
        {
            await manager.StopAsync(ct);
        }
    }

    /// <summary>
    /// Non-Authorization headers (e.g. X-API-Key) also suppress the pre-flight block.
    /// The probe still caches metadata, but the real connection attempt decides.
    /// </summary>
    [Fact]
    public async Task NonAuthorizationHeader_WhenServerReturnsOAuthMetadata_StillConnects()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;

        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = "https://mcp.example.com",
            Enabled = true,
            Headers = new Dictionary<string, SensitiveString>
            {
                ["X-API-Key"] = new SensitiveString("sk-my-api-key"),
            },
        };

        using var manager = CreateManager("mcp-apikey", entry, CreateDiscoveryClientThatReturnsOAuthHints());
        try
        {
            await manager.StartAsync(ct);

            var status = manager.GetServerStatuses()[new McpServerName("mcp-apikey")];

            Assert.NotEqual(McpConnectionState.AwaitingAuth, status.State);
        }
        finally
        {
            await manager.StopAsync(ct);
        }
    }

    /// <summary>
    /// Positive control: when no OAuth hints are returned by the server,
    /// the static auth header works regardless of the bug fix.
    /// </summary>
    [Fact]
    public async Task StaticAuthHeader_WhenServerReturnsNoOAuthMetadata_ConnectsNormally()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;

        using var noOAuthClient = new HttpClient(new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)));

        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = "https://mcp.example.com",
            Enabled = true,
            Headers = new Dictionary<string, SensitiveString>
            {
                ["Authorization"] = new SensitiveString("Bearer my-static-token-no-oauth"),
            },
        };

        using var manager = CreateManager("mcp-static-no-oauth", entry, noOAuthClient);
        try
        {
            await manager.StartAsync(ct);

            var status = manager.GetServerStatuses()[new McpServerName("mcp-static-no-oauth")];
            Assert.NotEqual(McpConnectionState.AwaitingAuth, status.State);
        }
        finally
        {
            await manager.StopAsync(ct);
        }
    }

    /// <summary>
    /// Pre-existing cached OAuth tokens should skip discovery even when the server
    /// returns aggressive OAuth hints.
    /// </summary>
    [Fact]
    public async Task CachedOAuthTokens_AreCheckedBeforeOAuthDiscovery()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = cts.Token;

        var entry = new McpServerEntry
        {
            Transport = "http",
            Url = "https://mcp.example.com",
            Enabled = true,
        };

        using var manager = CreateManager(
            "mcp-with-tokens", entry, CreateDiscoveryClientThatReturnsOAuthHints(),
            seedTokens: serverName =>
            {
                return new McpOAuthTokenSet
                {
                    AccessToken = new SensitiveString("cached-access-token"),
                    RefreshToken = new SensitiveString("cached-refresh-token"),
                    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                };
            });

        try
        {
            await manager.StartAsync(ct);

            var status = manager.GetServerStatuses()[new McpServerName("mcp-with-tokens")];
            Assert.NotEqual(McpConnectionState.AwaitingAuth, status.State);
        }
        finally
        {
            await manager.StopAsync(ct);
        }
    }

    /// <summary>
    /// Builds a <see cref="McpClientManager"/> with the OAuth service wired to the
    /// given discovery <paramref name="oauthHttpClient"/>. Optionally seeds the
    /// token cache via <paramref name="seedTokens"/>.
    /// </summary>
    private McpClientManager CreateManager(
        string serverName,
        McpServerEntry entry,
        HttpClient oauthHttpClient,
        Func<McpServerName, McpOAuthTokenSet>? seedTokens = null)
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();

        using var pkceHttpClient = new HttpClient();
        var oauthService = new McpOAuthService(
            oauthHttpClient,
            paths,
            TimeProvider.System,
            NullLogger<McpOAuthService>.Instance,
            new OAuthPkceService(pkceHttpClient),
            NullNotificationSink.Instance);

        if (seedTokens is not null)
        {
            var name = new McpServerName(serverName);
            var tokensField = typeof(McpOAuthService)
                .GetField("_tokens", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(tokensField);
            var cache = (ConcurrentDictionary<McpServerName, McpOAuthTokenSet>)tokensField.GetValue(oauthService)!;
            cache.TryAdd(name, seedTokens(name));
        }

        return new McpClientManager(
            new Dictionary<string, McpServerEntry> { [serverName] = entry },
            new ToolRegistry(),
            new ToolConfig(),
            oauthService,
            NullNotificationSink.Instance,
            TimeProvider.System,
            NullLogger<McpClientManager>.Instance);
    }

    private static HttpClient CreateDiscoveryClientThatReturnsOAuthHints()
    {
        return new HttpClient(new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url is "https://mcp.example.com/" or "https://mcp.example.com")
            {
                var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                response.Headers.Add("WWW-Authenticate",
                    "Bearer resource_metadata=\"https://mcp.example.com/oauth/resource-metadata\"");
                return response;
            }

            if (url == "https://mcp.example.com/oauth/resource-metadata")
            {
                return JsonResponse(new
                {
                    authorization_servers = new[] { "https://auth.example.com" },
                    resource = "https://mcp.example.com/resource"
                });
            }

            if (url == "https://mcp.example.com/.well-known/oauth-protected-resource")
            {
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
