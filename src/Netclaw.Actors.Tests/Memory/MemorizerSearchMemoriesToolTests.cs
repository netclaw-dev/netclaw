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

public class MemorizerSearchMemoriesToolTests : TestKit
{
    private readonly FakeChatClient _fakeChatClient = new();

    public MemorizerSearchMemoriesToolTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // No persistence or hosting needed
    }

    [Fact]
    public async Task Search_returns_results_when_memorizer_tools_available()
    {
        var registry = new ToolRegistry();
        registry.Register(new FakeNetclawTool("memorizer/search_memories", "found 3 results"));
        registry.Register(new FakeNetclawTool("memorizer/get", "memory details"));
        registry.Register(new FakeNetclawTool("memorizer/get_workspace", "workspace-info"));

        var provider = new SingleClientProvider(_fakeChatClient);
        var tool = new MemorizerSearchMemoriesTool(Sys, provider, registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "akka clustering"
            },
            CancellationToken.None);

        // SubAgent returns the FakeChatClient's text response (subagent completed)
        Assert.NotNull(result);
        Assert.DoesNotContain("unavailable", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Returns_unavailable_when_no_memorizer_tools()
    {
        var registry = new ToolRegistry();
        // Empty registry — no Memorizer tools

        var provider = new SingleClientProvider(_fakeChatClient);
        var tool = new MemorizerSearchMemoriesTool(Sys, provider, registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "something"
            },
            CancellationToken.None);

        Assert.Contains("unavailable", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grant_category_is_builtin()
    {
        var registry = new ToolRegistry();
        var provider = new SingleClientProvider(_fakeChatClient);
        var tool = new MemorizerSearchMemoriesTool(Sys, provider, registry);

        Assert.Equal("builtin", tool.GrantCategory);
    }
}
