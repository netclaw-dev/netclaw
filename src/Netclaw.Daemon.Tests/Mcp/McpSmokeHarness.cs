// -----------------------------------------------------------------------
// <copyright file="McpSmokeHarness.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Providers.OAuth;
using Netclaw.Tools;

namespace Netclaw.Daemon.Tests.Mcp;

/// <summary>
/// Builds and owns an <see cref="McpClientManager"/> for the MCP smoke tests:
/// wires the OAuth scaffolding the constructor requires, and on
/// <see cref="DisposeAsync"/> tears down the whole graph — the manager, its
/// child MCP server processes, and the HTTP clients. Each test creates its own
/// harness so the spawned MCP processes stay isolated per test.
/// </summary>
internal sealed class McpSmokeHarness : IAsyncDisposable
{
    private readonly HttpClient _pkceHttp;
    private readonly HttpClient _oauthHttp;

    private McpSmokeHarness(
        McpClientManager manager,
        ApplicationLifetime appLifetime,
        HttpClient pkceHttp,
        HttpClient oauthHttp)
    {
        Manager = manager;
        AppLifetime = appLifetime;
        _pkceHttp = pkceHttp;
        _oauthHttp = oauthHttp;
    }

    public McpClientManager Manager { get; }

    /// <summary>
    /// The real <see cref="IHostApplicationLifetime"/> implementation backing
    /// <see cref="Manager"/>. Tests call <see cref="ApplicationLifetime.StopApplication"/>
    /// to fire <c>ApplicationStopping</c> synchronously, exactly as the
    /// generic host does on SIGTERM/graceful stop, without needing a full
    /// <see cref="IHost"/>.
    /// </summary>
    public ApplicationLifetime AppLifetime { get; }

    public static McpSmokeHarness Create(
        Dictionary<string, McpServerEntry> serverEntries,
        ToolRegistry registry,
        ILogger<McpClientManager>? logger = null)
    {
        var pkceHttp = new HttpClient();
        var oauthHttp = new HttpClient();
        var oauthService = new McpOAuthService(
            oauthHttp,
            new NetclawPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())),
            TimeProvider.System,
            NullLogger<McpOAuthService>.Instance,
            new OAuthPkceService(pkceHttp),
            NullNotificationSink.Instance);
        var appLifetime = new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance);
        var manager = new McpClientManager(
            serverEntries,
            registry,
            new ToolConfig(),
            oauthService,
            NullNotificationSink.Instance,
            TimeProvider.System,
            logger ?? NullLogger<McpClientManager>.Instance,
            appLifetime);
        return new McpSmokeHarness(manager, appLifetime, pkceHttp, oauthHttp);
    }

    public async ValueTask DisposeAsync()
    {
        await Manager.StopAsync(CancellationToken.None);
        Manager.Dispose();
        _pkceHttp.Dispose();
        _oauthHttp.Dispose();
    }
}
