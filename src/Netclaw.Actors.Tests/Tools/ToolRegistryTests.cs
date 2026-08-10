// -----------------------------------------------------------------------
// <copyright file="ToolRegistryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class ToolRegistryTests
{
    [Fact]
    public void GetAllTools_returns_all_registered_tools()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateFakeTool("search"), "web_search");
        registry.Register(CreateFakeTool("fetch"), "web_fetch");
        registry.Register(CreateFakeTool("run_shell"), "shell");

        var tools = registry.GetAllTools();

        Assert.Equal(3, tools.Count);
    }

    [Fact]
    public void GetToolsForGrants_filters_by_granted_categories()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateFakeTool("search"), "web_search");
        registry.Register(CreateFakeTool("fetch"), "web_fetch");
        registry.Register(CreateFakeTool("run_shell"), "shell");
        registry.Register(CreateFakeTool("gh_issue"), "github");

        var granted = new HashSet<string> { "web_search", "shell" };
        var tools = registry.GetToolsForGrants(granted);

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t is AIFunction f && f.Name == "search");
        Assert.Contains(tools, t => t is AIFunction f && f.Name == "run_shell");
    }

    [Fact]
    public void GetToolsForGrants_empty_grants_returns_nothing()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateFakeTool("search"), "web_search");

        var tools = registry.GetToolsForGrants(new HashSet<string>());

        Assert.Empty(tools);
    }

    [Fact]
    public void GetAllTools_empty_registry_returns_empty()
    {
        var registry = new ToolRegistry();
        Assert.Empty(registry.GetAllTools());
    }

    [Fact]
    public void Multiple_tools_in_same_grant_category_all_returned()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateFakeTool("mcp_memorizer_store"), "mcp:memorizer");
        registry.Register(CreateFakeTool("mcp_memorizer_search"), "mcp:memorizer");

        var granted = new HashSet<string> { "mcp:memorizer" };
        var tools = registry.GetToolsForGrants(granted);

        Assert.Equal(2, tools.Count);
    }

    [Fact]
    public void GetAlwaysLoadedTools_returns_non_mcp_tools()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateFakeTool("shell_execute"), "shell");
        registry.Register(CreateFakeTool("search_tools"), "builtin");
        registry.Register(CreateFakeTool("file_read"), "file");
        // MCP tools should be excluded
        registry.Register(new McpToolAdapter(
            CreateFakeTool("store"), "memorizer", "store"));

        var alwaysLoaded = registry.GetAlwaysLoadedTools();

        Assert.Equal(3, alwaysLoaded.Count);
        Assert.DoesNotContain(alwaysLoaded, t => t is AIFunction f && f.Name == "store");
    }

    [Fact]
    public void SearchTools_finds_matching_tools()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            CreateFakeTool("store"), "memorizer", "store"));
        registry.Register(new McpToolAdapter(
            CreateFakeTool("search"), "memorizer", "search"));

        var results = registry.SearchTools("store", null, 10);

        Assert.Single(results);
        Assert.Equal("memorizer/store", results[0].Name);
    }

    [Fact]
    public void SearchTools_filters_by_server()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            CreateFakeTool("store"), "memorizer", "store"));
        registry.Register(new McpToolAdapter(
            CreateFakeTool("store"), "github", "store"));

        var results = registry.SearchTools("store", new McpServerName("memorizer"), 10);

        Assert.Single(results);
        Assert.Equal("memorizer/store", results[0].Name);
    }

    [Fact]
    public void SuggestTools_returns_fuzzy_matches_by_name()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            CreateFakeTool("navigate_page"), "browser_chrome_devtools", "navigate_page"));

        var results = registry.SuggestTools("navgite pg", null, 10);

        Assert.NotEmpty(results);
        Assert.Equal("browser_chrome_devtools/navigate_page", results[0].Name);
    }

    [Fact]
    public void SuggestTools_respects_server_filter()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            CreateFakeTool("navigate_page"), "browser_chrome_devtools", "navigate_page"));
        registry.Register(new McpToolAdapter(
            CreateFakeTool("navigate_page"), "playwright", "navigate_page"));

        var results = registry.SuggestTools("navgite pg", new McpServerName("playwright"), 10);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.StartsWith("playwright/", r.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateCompressedIndex_uses_mcp_server_progressive_disclosure()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            CreateFakeTool("store"), "memorizer", "store"));
        registry.Register(new McpToolAdapter(
            CreateFakeTool("search"), "memorizer", "search"));
        registry.Register(CreateFakeTool("shell_execute"), "shell");

        var index = registry.GenerateCompressedIndex();

        Assert.Contains("[MCP capability servers - discover tools with search_tools]", index);
        Assert.Contains("memorizer (2 tools)", index);
        Assert.Contains("search_tools(query: \"all\", server: \"<server_name>\")", index);
        Assert.Contains("shell: shell_execute", index);
        Assert.Contains("search_tools", index);
        Assert.DoesNotContain("mcp:memorizer: store, search", index);
    }

    [Fact]
    public void GetMcpServerSummaries_includes_capability_descriptions()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            CreateFakeTool("navigate_page"), "browser_playwright", "navigate_page"));
        registry.Register(new McpToolAdapter(
            CreateFakeTool("snapshot"), "browser_playwright", "snapshot"));

        var summaries = registry.GetMcpServerSummaries();

        var browser = Assert.Single(summaries);
        Assert.Equal("browser_playwright", browser.ServerName);
        Assert.Equal(2, browser.ToolCount);
        Assert.Contains("browser automation", browser.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenerateCompressedIndex_empty_registry_returns_empty()
    {
        var registry = new ToolRegistry();
        var index = registry.GenerateCompressedIndex();

        Assert.Empty(index);
    }

    [Fact]
    public void GenerateCompressedIndex_for_public_hides_blocked_capabilities()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateFakeTool("file_read"), "file");
        registry.Register(CreateFakeTool("set_reminder"), "builtin");
        registry.Register(CreateFakeTool("spawn_agent"), "builtin");
        registry.Register(new McpToolAdapter(
            CreateFakeTool("search"), "memorizer", "search"));

        var policy = new ToolAccessPolicy(
            new ToolConfig(),
            new EffectivePolicyDefaults(
                DeploymentPosture.Public,
                TrustAudience.Public,
                ShellExecutionMode.Off,
                UsedStrictFallback: true),
            shellCommandPolicy: new ShellCommandPolicy(),
            toolPathPolicy: new ToolPathPolicy([]),
            featureGates: new FeatureGates(SubAgentsEnabled: false, SchedulingEnabled: false));

        var index = registry.GenerateCompressedIndex(TrustAudience.Public, policy);

        Assert.Contains("file: file_read", index);
        Assert.DoesNotContain("set_reminder", index);
        Assert.DoesNotContain("spawn_agent", index);
        Assert.DoesNotContain("memorizer", index);
    }

    [Fact]
    public void GetByName_resolves_McpTool_by_sanitized_alias()
    {
        // tool_use responses from Anthropic come back with the sanitized name
        // (server__tool), but skill text and load_tool results use the
        // canonical server/tool form. The registry has to accept either.
        var registry = new ToolRegistry();
        var adapter = new McpToolAdapter(
            CreateFakeTool("store"), "memorizer", "store");
        registry.Register(adapter);

        var byCanonical = registry.GetByName("memorizer/store");
        var bySanitized = registry.GetByName("memorizer__store");

        Assert.NotNull(byCanonical);
        Assert.NotNull(bySanitized);
        Assert.Same(adapter, byCanonical);
        Assert.Same(adapter, bySanitized);
    }

    [Fact]
    public void GetRegistrationByToolName_resolves_McpTool_by_sanitized_alias()
    {
        var registry = new ToolRegistry();
        var adapter = new McpToolAdapter(
            CreateFakeTool("search_memories"), "memorizer", "search_memories");
        registry.Register(adapter);

        var byCanonical = registry.GetRegistrationByToolName("memorizer/search_memories");
        var bySanitized = registry.GetRegistrationByToolName("memorizer__search_memories");

        Assert.NotNull(byCanonical);
        Assert.NotNull(bySanitized);
        Assert.Same(byCanonical, bySanitized);
    }

    [Fact]
    public void ToCanonicalName_maps_sanitized_alias_to_canonical_for_McpTool()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            CreateFakeTool("create-pages"), "notion", "create-pages"));

        Assert.Equal("notion/create-pages", registry.ToCanonicalName("notion__create-pages"));
        // Already canonical → idempotent
        Assert.Equal("notion/create-pages", registry.ToCanonicalName("notion/create-pages"));
        // Unknown tool — pass-through, no throw
        Assert.Equal("unregistered__tool", registry.ToCanonicalName("unregistered__tool"));
    }

    [Fact]
    public void ToLlmFacingName_maps_canonical_to_sanitized_alias_for_McpTool()
    {
        var registry = new ToolRegistry();
        registry.Register(new McpToolAdapter(
            CreateFakeTool("create-pages"), "notion", "create-pages"));

        Assert.Equal("notion__create-pages", registry.ToLlmFacingName("notion/create-pages"));
        // Already LLM-facing — pass-through (no registered tool by that
        // exact name; falls through to identity)
        Assert.Equal("notion__create-pages", registry.ToLlmFacingName("notion__create-pages"));
    }

    [Fact]
    public void Canonical_and_LlmFacing_round_trip_for_first_party_tools_is_identity()
    {
        // First-party tool names already satisfy the Anthropic regex —
        // canonical and LLM-facing are the same string. Round-tripping
        // must not mangle them in either direction.
        var config = new ToolConfig();
        var registry = new ToolRegistry();
        registry.WithFirstPartyTools(config, new NetclawPaths(), new Netclaw.Security.ToolPathPolicy([]), new Netclaw.Security.ShellCommandPolicy());

        Assert.Equal("shell_execute", registry.ToCanonicalName("shell_execute"));
        Assert.Equal("shell_execute", registry.ToLlmFacingName("shell_execute"));
        Assert.Equal("file_read", registry.ToCanonicalName("file_read"));
        Assert.Equal("file_read", registry.ToLlmFacingName("file_read"));
    }

    private static AIFunction CreateFakeTool(string name)
    {
        return AIFunctionFactory.Create(() => "result", name);
    }
}
