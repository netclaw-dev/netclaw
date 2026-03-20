using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Providers.OAuth;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

/// <summary>
/// End-to-end smoke tests that connect to a real MCP server over stdio.
/// Uses @modelcontextprotocol/server-everything (the official test server).
/// Requires Node.js/npx on the PATH.
/// </summary>
public class McpStdioSmokeTests : IAsyncDisposable
{
    private McpClient? _client;

    [Fact]
    public async Task ConnectToStdioServer_DiscoversTools()
    {
        var entry = new McpServerEntry
        {
            Transport = "stdio",
            Command = "npx",
            Arguments = ["-y", "@modelcontextprotocol/server-everything"],
            Enabled = true,
        };

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = entry.Command,
            Arguments = entry.Arguments,
            Name = "everything-test",
            ShutdownTimeout = TimeSpan.FromSeconds(10),
        });

        _client = await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new() { Name = "netclaw-smoke-test", Version = "0.1.0" },
            InitializationTimeout = TimeSpan.FromMinutes(3),
        }, cancellationToken: CancellationToken.None);

        var tools = await _client.ListToolsAsync(cancellationToken: CancellationToken.None);

        // server-everything exposes several test tools
        Assert.NotEmpty(tools);
        Assert.True(tools.Count >= 1, $"Expected at least 1 tool, got {tools.Count}");
    }

    [Fact]
    public async Task McpToolAdapter_WrapsDiscoveredTools()
    {
        var entry = new McpServerEntry
        {
            Transport = "stdio",
            Command = "npx",
            Arguments = ["-y", "@modelcontextprotocol/server-everything"],
            Enabled = true,
        };

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = entry.Command,
            Arguments = entry.Arguments,
            Name = "everything-adapter-test",
            ShutdownTimeout = TimeSpan.FromSeconds(10),
        });

        _client = await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new() { Name = "netclaw-smoke-test", Version = "0.1.0" },
            InitializationTimeout = TimeSpan.FromMinutes(3),
        }, cancellationToken: CancellationToken.None);

        var tools = await _client.ListToolsAsync(cancellationToken: CancellationToken.None);

        // Wrap with our adapter and verify namespacing
        var registry = new ToolRegistry();
        registry.WithMcpTools("everything", tools);

        var allTools = registry.GetAllRegistrations();
        Assert.NotEmpty(allTools);

        // All tool names should be namespaced with server name
        foreach (var reg in allTools)
        {
            Assert.StartsWith("everything/", reg.Tool.Name);
            Assert.Equal("mcp:everything", reg.GrantCategory);
        }

        // Tools should NOT be in always-loaded set (MCP tools are dynamic)
        var alwaysLoaded = registry.GetAlwaysLoadedTools();
        Assert.Empty(alwaysLoaded);
    }

    [Fact]
    public async Task SearchTools_FindsMcpToolsByName()
    {
        var entry = new McpServerEntry
        {
            Transport = "stdio",
            Command = "npx",
            Arguments = ["-y", "@modelcontextprotocol/server-everything"],
            Enabled = true,
        };

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = entry.Command,
            Arguments = entry.Arguments,
            Name = "everything-search-test",
            ShutdownTimeout = TimeSpan.FromSeconds(10),
        });

        _client = await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new() { Name = "netclaw-smoke-test", Version = "0.1.0" },
            InitializationTimeout = TimeSpan.FromMinutes(3),
        }, cancellationToken: CancellationToken.None);

        var tools = await _client.ListToolsAsync(cancellationToken: CancellationToken.None);

        var registry = new ToolRegistry();
        registry.WithMcpTools("everything", tools);

        // Pick the first tool name and search for it
        var firstTool = tools[0];
        var results = registry.SearchTools(firstTool.Name, null, 10);

        Assert.NotEmpty(results);
        Assert.Contains(results, t => t.Name == $"everything/{firstTool.Name}");
    }

    [Fact]
    public async Task McpClientManager_ConnectsAndRegistersTools()
    {
        var entry = new McpServerEntry
        {
            Transport = "stdio",
            Command = "npx",
            Arguments = ["-y", "@modelcontextprotocol/server-everything"],
            Enabled = true,
        };

        var serverEntries = new Dictionary<string, McpServerEntry>
        {
            ["everything"] = entry
        };

        var registry = new ToolRegistry();
        var logger = NullLogger<McpClientManager>.Instance;
        var pkceService = new OAuthPkceService(new HttpClient());
        var oauthService = new McpOAuthService(
            new HttpClient(),
            new NetclawPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString())),
            TimeProvider.System,
            NullLogger<McpOAuthService>.Instance,
            pkceService,
            NullNotificationSink.Instance);
        var manager = new McpClientManager(serverEntries, registry, oauthService, NullNotificationSink.Instance, TimeProvider.System, logger);

        try
        {
            await manager.StartAsync(CancellationToken.None);

            var statuses = manager.GetServerStatuses();
            Assert.True(statuses.ContainsKey("everything"));

            var status = statuses["everything"];
            Assert.Equal(McpConnectionState.Connected, status.State);
            Assert.True(status.ToolCount > 0, $"Expected tools, got {status.ToolCount}");
            Assert.Null(status.ErrorMessage);

            // Verify tools were registered in the registry
            var allRegs = registry.GetAllRegistrations();
            Assert.NotEmpty(allRegs);
            Assert.All(allRegs, r => Assert.StartsWith("everything/", r.Tool.Name));

            // GetClient should return a live client
            var client = manager.GetClient("everything");
            Assert.NotNull(client);
        }
        finally
        {
            await manager.StopAsync(CancellationToken.None);
            manager.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }
}
