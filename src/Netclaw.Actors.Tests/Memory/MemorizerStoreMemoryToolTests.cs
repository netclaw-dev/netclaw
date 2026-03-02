using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Tests.SubAgents;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Netclaw.Actors.Tests.Memory;

public class MemorizerStoreMemoryToolTests : TestKit
{
    private readonly FakeChatClient _fakeChatClient = new();

    public MemorizerStoreMemoryToolTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // No persistence or hosting needed
    }

    [Fact]
    public async Task Store_succeeds_when_memorizer_tools_available()
    {
        var registry = new ToolRegistry();
        // Register fake Memorizer MCP tools the curation agent needs
        registry.Register(new FakeNetclawTool("memorizer/store", "stored-ok"));
        registry.Register(new FakeNetclawTool("memorizer/search_memories", "no results"));
        registry.Register(new FakeNetclawTool("memorizer/get_workspace", "workspace-info"));

        var provider = new SingleClientProvider(_fakeChatClient);
        var tool = new MemorizerStoreMemoryTool(Sys, provider, registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Title"] = "Test Memory",
                ["Content"] = "Some important content.",
                ["Tags"] = "reference, test"
            },
            CancellationToken.None);

        Assert.Contains("Memory saved", result);
        Assert.Contains("Test Memory", result);
    }

    [Fact]
    public async Task Returns_unavailable_when_no_memorizer_tools()
    {
        var registry = new ToolRegistry();
        // Empty registry — no Memorizer tools

        var provider = new SingleClientProvider(_fakeChatClient);
        var tool = new MemorizerStoreMemoryTool(Sys, provider, registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Title"] = "Test Memory",
                ["Content"] = "Some content."
            },
            CancellationToken.None);

        Assert.Contains("unavailable", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grant_category_is_builtin()
    {
        var registry = new ToolRegistry();
        var provider = new SingleClientProvider(_fakeChatClient);
        var tool = new MemorizerStoreMemoryTool(Sys, provider, registry);

        Assert.Equal("builtin", tool.GrantCategory);
    }
}
