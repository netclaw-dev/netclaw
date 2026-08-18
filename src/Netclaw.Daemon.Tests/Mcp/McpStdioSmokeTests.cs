// -----------------------------------------------------------------------
// <copyright file="McpStdioSmokeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using ModelContextProtocol.Client;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

/// <summary>
/// End-to-end smoke tests that connect to a real MCP server over stdio.
/// Uses the repository's deterministic MCP smoke server.
/// </summary>
public class McpStdioSmokeTests : IAsyncDisposable
{
    private McpClient? _client;

    [Fact]
    public async Task ConnectToStdioServer_DiscoversTools()
    {
        _client = await CreateClientAsync("discovery");
        var tools = await _client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEmpty(tools);
        Assert.Contains(tools, tool => tool.Name == "add");
        Assert.Contains(tools, tool => tool.Name == "echo");
    }

    [Fact]
    public async Task McpToolAdapter_WrapsDiscoveredTools()
    {
        _client = await CreateClientAsync("adapter");
        var tools = await _client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Wrap with our adapter and verify namespacing
        var registry = new ToolRegistry();
        registry.WithMcpTools("smoke", tools);

        var allTools = registry.GetAllRegistrations();
        Assert.NotEmpty(allTools);

        // All tool names should be namespaced with server name
        foreach (var reg in allTools)
        {
            Assert.StartsWith("smoke/", reg.Tool.Name);
            Assert.Equal("mcp:smoke", reg.GrantCategory);
        }

        // Tools should NOT be in always-loaded set (MCP tools are dynamic)
        var alwaysLoaded = registry.GetAlwaysLoadedTools();
        Assert.Empty(alwaysLoaded);
    }

    [Fact]
    public async Task SearchTools_FindsMcpToolsByName()
    {
        _client = await CreateClientAsync("search");
        var tools = await _client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        var registry = new ToolRegistry();
        registry.WithMcpTools("smoke", tools);

        // Pick the first tool name and search for it
        var firstTool = tools[0];
        var results = registry.SearchTools(firstTool.Name, null, 10);

        Assert.NotEmpty(results);
        Assert.Contains(results, t => t.Name == $"smoke/{firstTool.Name}");
    }

    [Fact]
    public async Task McpClientManager_ConnectsAndRegistersTools()
    {
        var registry = new ToolRegistry();
        await using var harness = McpSmokeHarness.Create(
            new Dictionary<string, McpServerEntry> { ["smoke"] = CreateEntry() }, registry);

        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);

        var statuses = harness.Manager.GetServerStatuses();
        Assert.True(statuses.ContainsKey(new McpServerName("smoke")));

        var status = statuses[new McpServerName("smoke")];
        Assert.Equal(McpConnectionState.Connected, status.State);
        Assert.True(status.ToolCount > 0, $"Expected tools, got {status.ToolCount}");
        Assert.Null(status.ErrorMessage);

        // Verify tools were registered in the registry
        var allRegs = registry.GetAllRegistrations();
        Assert.NotEmpty(allRegs);
        Assert.All(allRegs, r => Assert.StartsWith("smoke/", r.Tool.Name));

        // GetClient should return a live client
        var client = harness.Manager.GetClient(new McpServerName("smoke"));
        Assert.NotNull(client);
    }

    private async Task<McpClient> CreateClientAsync(string name)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [SmokeMcpServerLocator.LocateDll()],
            Name = $"smoke-{name}",
            ShutdownTimeout = TimeSpan.FromSeconds(10),
        });

        return await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ClientInfo = new() { Name = "netclaw-smoke-test", Version = "0.1.0" },
            InitializationTimeout = TimeSpan.FromSeconds(30),
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static McpServerEntry CreateEntry()
        => new()
        {
            Transport = "stdio",
            Command = "dotnet",
            Arguments = [SmokeMcpServerLocator.LocateDll()],
            Enabled = true,
        };

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }
}
