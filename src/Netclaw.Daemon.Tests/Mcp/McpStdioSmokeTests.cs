// -----------------------------------------------------------------------
// <copyright file="McpStdioSmokeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

/// <summary>
/// End-to-end smoke tests that connect to a real MCP server over stdio.
/// Uses the repository's deterministic MCP smoke server.
/// </summary>
[Collection(McpSmokeChildProcessCollection.Name)]
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
    public async Task MultiContentResult_ProjectsTextAndMarkerWithoutImageBytes()
    {
        _client = await CreateClientAsync("multi-content");
        var tools = await _client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var tool = Assert.Single(tools, candidate => candidate.Name == "image-with-notes");

        var result = await tool.InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.IsType<AIContent[]>(result);
        var projection = McpToolResultFormatter.Project(result, "smoke/image-with-notes");
        var artifact = Assert.Single(projection.Artifacts);
        Assert.Equal("[image: image/png]\nchart-notes", projection.Text);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, artifact.Data.Span[..4].ToArray());
        Assert.DoesNotContain(Convert.ToBase64String(artifact.Data.Span), projection.Text);
    }

    [Fact]
    public async Task MetadataResult_ProjectsTextAndMarkerWithoutImageBytes()
    {
        _client = await CreateClientAsync("metadata-content");
        var tools = await _client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var tool = Assert.Single(tools, candidate => candidate.Name == "image-with-metadata");

        var result = await tool.InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.IsType<JsonElement>(result);
        var projection = McpToolResultFormatter.Project(result, "smoke/image-with-metadata");
        var artifact = Assert.Single(projection.Artifacts);
        Assert.Equal("[image: image/png]", projection.Text);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, artifact.Data.Span[..4].ToArray());
        Assert.DoesNotContain(Convert.ToBase64String(artifact.Data.Span), projection.Text);
        Assert.DoesNotContain("_meta", projection.Text);
    }

    [Fact]
    public async Task McpClientManager_RejectsInvalidImageBytesFromRealStdioServer()
    {
        // The real SDK returns a JSON result whose server MIME claims image/png.
        // This test proves that the manager rejects its invalid bytes before storage.
        // Arrange
        using var directory = new DisposableTempDir();
        var registry = new ToolRegistry();
        await using var harness = McpSmokeHarness.Create(
            new Dictionary<string, McpServerEntry> { ["smoke"] = CreateEntry() }, registry);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        harness.AssertConnected("smoke");
        var context = CreateStoredContext(directory, ModelModality.Text | ModelModality.Image);
        var arguments = new Dictionary<string, object?> { ["validContent"] = false };

        // Act
        var result = await harness.Manager.InvokeAsync(
            "smoke",
            "image-with-metadata",
            arguments,
            context.Invocation,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("[image: image/png]", result);
        Assert.Contains("content validation failed", result);
        Assert.Empty(context.Outputs.FileAttachments);
        Assert.Empty(context.Outputs.ModelInputFiles);
        Assert.False(Directory.Exists(context.SessionStorage!.ArtifactDirectory.Value));
    }

    [Fact]
    public async Task McpClientManager_PreservesTextWhenRealStdioImageFailsAdmission()
    {
        // An invalid artifact must not discard readable text from the same MCP result.
        // This test proves that the manager keeps the text and adds a visible rejection.
        // Arrange
        using var directory = new DisposableTempDir();
        var registry = new ToolRegistry();
        await using var harness = McpSmokeHarness.Create(
            new Dictionary<string, McpServerEntry> { ["smoke"] = CreateEntry() }, registry);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        harness.AssertConnected("smoke");
        var context = CreateStoredContext(directory, ModelModality.Text | ModelModality.Image);
        var arguments = new Dictionary<string, object?> { ["validContent"] = false };

        // Act
        var result = await harness.Manager.InvokeAsync(
            "smoke",
            "image-with-notes",
            arguments,
            context.Invocation,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.StartsWith("[image: image/png]\nchart-notes", result, StringComparison.Ordinal);
        Assert.Contains("content validation failed", result);
        Assert.Empty(context.Outputs.FileAttachments);
        Assert.Empty(context.Outputs.ModelInputFiles);
    }

    [Fact]
    public async Task McpClientManager_RoutesValidImageThroughExistingToolOutputs()
    {
        // The manager joins the MCP result path to the normal session tool-output path.
        // This test proves that a verified STDIO image becomes one user and model output.
        // Arrange
        using var directory = new DisposableTempDir();
        var registry = new ToolRegistry();
        await using var harness = McpSmokeHarness.Create(
            new Dictionary<string, McpServerEntry> { ["smoke"] = CreateEntry() }, registry);
        await harness.Manager.StartAsync(TestContext.Current.CancellationToken);
        harness.AssertConnected("smoke");
        var context = CreateStoredContext(directory, ModelModality.Text | ModelModality.Image);

        // Act
        var result = await harness.Manager.InvokeAsync(
            "smoke",
            "image-with-metadata",
            null,
            context.Invocation,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("[image: image/png]", result);
        var userOutput = Assert.Single(context.Outputs.FileAttachments);
        var modelOutput = Assert.Single(context.Outputs.ModelInputFiles);
        Assert.Equal(userOutput.FilePath, modelOutput.FilePath);
        Assert.Equal("image/png", userOutput.MimeType.Value);
        Assert.True(File.Exists(userOutput.FilePath));
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

    private static ToolExecutionContext CreateStoredContext(
        DisposableTempDir directory,
        ModelModality modalities)
    {
        var storage = SessionStoragePaths.CreateLegacy(
            Path.Combine(directory.Path, "session"),
            Path.Combine(directory.Path, "logs"),
            "test-thread");
        return TestToolExecutionContext.CreateBoundWithStorage(
            "test/thread",
            storage,
            new TestToolExecutionContextOptions
            {
                Audience = TrustAudience.Personal,
                ModelInputModalities = modalities,
            });
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
