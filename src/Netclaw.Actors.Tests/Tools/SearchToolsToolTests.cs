using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class SearchToolsToolTests
{
    [Fact]
    public async Task Search_MatchesByName()
    {
        var registry = CreateRegistryWithMcpTools();
        var tool = new SearchToolsTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "store" },
            CancellationToken.None);

        Assert.Contains("memorizer/store", result);
    }

    [Fact]
    public async Task Search_MatchesByDescription()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            CreateFakeAIFunction("search_memories", "Find stored memories"),
            "memorizer", "search_memories"));

        var tool = new SearchToolsTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "memories" },
            CancellationToken.None);

        Assert.Contains("memorizer/search_memories", result);
    }

    [Fact]
    public async Task Search_NoResults()
    {
        var registry = CreateRegistryWithMcpTools();
        var tool = new SearchToolsTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "nonexistent_xyz" },
            CancellationToken.None);

        Assert.Contains("No tools found", result);
    }

    [Fact]
    public async Task Search_FiltersServer()
    {
        var registry = CreateRegistryWithMcpTools();
        var tool = new SearchToolsTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "store",
                ["Server"] = "github"
            },
            CancellationToken.None);

        // "github" server has no "store" tool — only memorizer does
        Assert.Contains("No tools found", result);
    }

    [Fact]
    public async Task Search_ServerDefault_IsTreatedAsNoFilter()
    {
        var registry = CreateRegistryWithMcpTools();
        var tool = new SearchToolsTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "store",
                ["Server"] = "default"
            },
            CancellationToken.None);

        Assert.Contains("memorizer/store", result);
    }

    [Fact]
    public async Task Search_IncludesNonMcpToolsWithoutServerFilter()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateFakeToolInRegistry("shell_execute", "Execute shell command"), "shell");

        var tool = new SearchToolsTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "shell" },
            CancellationToken.None);

        // Without server filter, non-MCP tools are included in search results
        Assert.Contains("shell_execute", result);
    }

    [Fact]
    public void GrantCategory_IsBuiltin()
    {
        var registry = new ToolRegistry();
        var tool = new SearchToolsTool(registry);

        Assert.Equal("builtin", tool.GrantCategory);
    }

    [Fact]
    public async Task Search_IncludesParameterHint_WhenSchemaHasProperties()
    {
        static string Navigate(string url, int timeout) => "ok";

        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            AIFunctionFactory.Create((Func<string, int, string>)Navigate, "navigate_page", "Navigate page"),
            "browser", "navigate_page"));

        var tool = new SearchToolsTool(registry);
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "navigate" },
            CancellationToken.None);

        Assert.Contains("params: url", result);
    }

    [Fact]
    public async Task Search_NoExactMatch_ReturnsSuggestionsWithoutAutoLoadFormat()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            CreateFakeAIFunction("navigate_page", "Navigate the current page"),
            "browser_chrome_devtools",
            "navigate_page"));

        var tool = new SearchToolsTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "navgite pg" },
            CancellationToken.None);

        Assert.Contains("No exact tools found", result);
        Assert.Contains("Did you mean", result);
        Assert.Contains("browser_chrome_devtools/navigate_page", result);
        Assert.Contains("Suggestions are not loaded yet", result);
        Assert.DoesNotContain("browser_chrome_devtools/navigate_page —", result);
    }

    [Fact]
    public async Task Search_ServersQuery_ReturnsServerCatalog()
    {
        var registry = CreateRegistryWithMcpTools();
        var tool = new SearchToolsTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "servers" },
            CancellationToken.None);

        Assert.Contains("Available MCP servers", result);
        Assert.Contains("memorizer (2 tools)", result);
        Assert.Contains("github (1 tools)", result);
    }

    [Fact]
    public async Task Search_AllWithServerFilter_ListsServerTools()
    {
        var registry = CreateRegistryWithMcpTools();
        var tool = new SearchToolsTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["Query"] = "all",
                ["Server"] = "memorizer"
            },
            CancellationToken.None);

        Assert.Contains("Found 2 tool(s) in server 'memorizer'", result);
        Assert.Contains("memorizer/store", result);
        Assert.Contains("memorizer/search", result);
    }

    [Fact]
    public async Task Search_AllWithoutServerFilter_ReturnsServerCatalogHint()
    {
        var registry = CreateRegistryWithMcpTools();
        var tool = new SearchToolsTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "all" },
            CancellationToken.None);

        Assert.Contains("Available MCP servers", result);
        Assert.Contains("call search_tools(query: \"all\", server: \"<server_name>\")", result);
    }

    [Fact]
    public async Task Search_AllowsMemorySafeMcpTools_InTeamContext()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            CreateFakeAIFunction("search_memories", "Find stored memories"),
            "memorizer",
            "search_memories"));

        var tool = new SearchToolsTool(
            registry,
            new ToolAccessPolicy(
                CreateProfileAwareToolConfig("memorizer"),
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false)));

        var context = new Netclaw.Tools.ToolExecutionContext("slack/thread-1", null)
        {
            Audience = TrustAudience.Team.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "slack"
        };

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "memories" },
            context,
            CancellationToken.None);

        Assert.Contains("memorizer/search_memories", result);
    }

    [Fact]
    public async Task Search_HidesMcpServer_WhenAudienceProfileDoesNotAllowServer()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            CreateFakeAIFunction("search_memories", "Find stored memories"),
            "memorizer",
            "search_memories"));

        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        var tool = new SearchToolsTool(
            registry,
            new ToolAccessPolicy(
                config,
                new EffectivePolicyDefaults(
                    DeploymentPosture.Personal,
                    TrustAudience.Personal,
                    ShellExecutionMode.HostAllowed,
                    UsedStrictFallback: false)));

        var context = new Netclaw.Tools.ToolExecutionContext("slack/thread-1", null)
        {
            Audience = TrustAudience.Team.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "slack"
        };

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "servers" },
            context,
            CancellationToken.None);

        Assert.DoesNotContain("memorizer", result);
    }

    private static ToolRegistry CreateRegistryWithMcpTools()
    {
        var registry = new ToolRegistry();

        registry.Register(new McpToolAdapter(
            CreateFakeAIFunction("store", "Store a value"), "memorizer", "store"));
        registry.Register(new McpToolAdapter(
            CreateFakeAIFunction("search", "Search stored values"), "memorizer", "search"));
        registry.Register(new McpToolAdapter(
            CreateFakeAIFunction("create_issue", "Create a GitHub issue"), "github", "create_issue"));

        return registry;
    }

    private static AIFunction CreateFakeAIFunction(string name, string description)
    {
        return AIFunctionFactory.Create(() => "result", name, description);
    }

    private static AITool CreateFakeToolInRegistry(string name, string description)
    {
        return AIFunctionFactory.Create(() => "result", name, description);
    }

    private static ToolConfig CreateProfileAwareToolConfig(params string[] allowedTeamServers)
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        foreach (var server in allowedTeamServers)
        {
            config.AudienceProfiles.Team.AllowedMcpServers.Add(server);
        }
        return config;
    }
}
