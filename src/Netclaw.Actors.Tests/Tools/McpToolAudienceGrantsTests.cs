// -----------------------------------------------------------------------
// <copyright file="McpToolAudienceGrantsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class McpToolAudienceGrantsTests
{
    private static readonly EffectivePolicyDefaults Defaults = new(
        DeploymentPosture.Personal,
        TrustAudience.Personal,
        ShellExecutionMode.HostAllowed,
        UsedStrictFallback: false);

    // ── IsMcpToolAllowed (via ToolAccessPolicy.IsToolExposed) ──

    [Fact]
    public void NullGrants_AllToolsExposed()
    {
        var config = CreateConfigWithTeamServer("memorizer");
        // McpServerToolGrants is null by default
        var policy = new ToolAccessPolicy(config, Defaults);
        var tool = CreateMcpTool("memorizer", "store");

        Assert.True(policy.IsToolExposed(tool, TeamContext()));
    }

    [Fact]
    public void EmptyGrantList_NoToolsExposed()
    {
        var config = CreateConfigWithTeamServer("memorizer");
        config.AudienceProfiles.Team.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["memorizer"] = []
        };
        var policy = new ToolAccessPolicy(config, Defaults);
        var tool = CreateMcpTool("memorizer", "store");

        Assert.False(policy.IsToolExposed(tool, TeamContext()));
    }

    [Fact]
    public void GrantedTool_IsExposed()
    {
        var config = CreateConfigWithTeamServer("memorizer");
        config.AudienceProfiles.Team.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["memorizer"] = ["search_memories", "get"]
        };
        var policy = new ToolAccessPolicy(config, Defaults);

        Assert.True(policy.IsToolExposed(CreateMcpTool("memorizer", "search_memories"), TeamContext()));
        Assert.True(policy.IsToolExposed(CreateMcpTool("memorizer", "get"), TeamContext()));
    }

    [Fact]
    public void UngrantedTool_IsBlocked()
    {
        var config = CreateConfigWithTeamServer("memorizer");
        config.AudienceProfiles.Team.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["memorizer"] = ["search_memories", "get"]
        };
        var policy = new ToolAccessPolicy(config, Defaults);

        Assert.False(policy.IsToolExposed(CreateMcpTool("memorizer", "store"), TeamContext()));
        Assert.False(policy.IsToolExposed(CreateMcpTool("memorizer", "delete"), TeamContext()));
    }

    [Fact]
    public void ServerNotInGrants_AllToolsExposed()
    {
        var config = CreateConfigWithTeamServer("memorizer", "github");
        config.AudienceProfiles.Team.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["memorizer"] = ["search_memories"]
        };
        var policy = new ToolAccessPolicy(config, Defaults);

        // github has no entry in grants → all tools pass
        Assert.True(policy.IsToolExposed(CreateMcpTool("github", "create_issue"), TeamContext()));
    }

    [Fact]
    public void ServerBlocked_ToolBlockedRegardlessOfGrants()
    {
        var config = CreateConfigWithTeamServer("memorizer");
        // memorizer is allowed for Team, but github is NOT
        config.AudienceProfiles.Team.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["github"] = ["create_issue"]
        };
        var policy = new ToolAccessPolicy(config, Defaults);

        // github server is not in AllowedMcpServers → blocked at server level
        Assert.False(policy.IsToolExposed(CreateMcpTool("github", "create_issue"), TeamContext()));
    }

    [Fact]
    public void DifferentAudiences_SeeDifferentTools()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Team.AllowedMcpServers.Add("memorizer");
        config.AudienceProfiles.Team.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["memorizer"] = ["search_memories", "get"]
        };
        // Personal has McpServersMode=All by default, no grants → sees everything
        var policy = new ToolAccessPolicy(config, Defaults);

        var storeTool = CreateMcpTool("memorizer", "store");

        // Team can't see store
        Assert.False(policy.IsToolExposed(storeTool, TeamContext()));
        // Personal can see store (no grants = all tools)
        Assert.True(policy.IsToolExposed(storeTool, PersonalContext()));
    }

    // ── AuthorizeInvocation deny reason ──

    [Fact]
    public void AuthorizeInvocation_DeniesWithCorrectReason_WhenToolNotGranted()
    {
        var config = CreateConfigWithTeamServer("memorizer");
        config.AudienceProfiles.Team.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["memorizer"] = ["search_memories"]
        };
        var policy = new ToolAccessPolicy(config, Defaults);

        var decision = policy.AuthorizeInvocation(
            CreateMcpTool("memorizer", "store"),
            CreateExecutionContext(TrustAudience.Team));

        Assert.False(decision.Allowed);
        Assert.Equal("mcp_tool_not_allowed_for_audience_profile", decision.DenyReason);
    }

    [Fact]
    public void AuthorizeInvocation_AllowsGrantedTool()
    {
        var config = CreateConfigWithTeamServer("memorizer");
        config.AudienceProfiles.Team.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["memorizer"] = ["search_memories"]
        };
        var policy = new ToolAccessPolicy(config, Defaults);

        var decision = policy.AuthorizeInvocation(
            CreateMcpTool("memorizer", "search_memories"),
            CreateExecutionContext(TrustAudience.Team));

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void AuthorizeInvocation_ServerDeny_TakesPrecedenceOverToolDeny()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        // Team has no AllowedMcpServers → server-level deny
        var policy = new ToolAccessPolicy(config, Defaults);

        var decision = policy.AuthorizeInvocation(
            CreateMcpTool("memorizer", "search_memories"),
            CreateExecutionContext(TrustAudience.Team));

        Assert.False(decision.Allowed);
        Assert.Equal("mcp_server_not_allowed_for_audience_profile", decision.DenyReason);
    }

    // ── search_tools filtering ──

    [Fact]
    public async Task SearchTools_ExcludesToolsBlockedByGrants()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateMcpTool("memorizer", "search_memories", "Find stored memories"));
        registry.Register(CreateMcpTool("memorizer", "store", "Store a value"));
        registry.Register(CreateMcpTool("memorizer", "delete", "Delete a memory"));

        var config = CreateConfigWithTeamServer("memorizer");
        config.AudienceProfiles.Team.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["memorizer"] = ["search_memories"]
        };

        var tool = new SearchToolsTool(
            registry,
            new ToolAccessPolicy(config, Defaults));

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Query", "memor"),
            CreateExecutionContext(TrustAudience.Team),
            CancellationToken.None);

        Assert.Contains("search_memories", result);
        Assert.DoesNotContain("memorizer/store", result);
        Assert.DoesNotContain("memorizer/delete", result);
    }

    // ── FilterExposedTools (session hot path) ──

    [Fact]
    public void FilterExposedTools_RemovesToolsBlockedByGrants()
    {
        var registry = new ToolRegistry();
        var granted = CreateMcpTool("memorizer", "search_memories");
        var blocked = CreateMcpTool("memorizer", "store");
        registry.Register(granted);
        registry.Register(blocked);

        var config = CreateConfigWithTeamServer("memorizer");
        config.AudienceProfiles.Team.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["memorizer"] = ["search_memories"]
        };
        var policy = new ToolAccessPolicy(config, Defaults);

        var trustContext = new EffectiveTrustContext(
            DeploymentPosture.Personal,
            TrustAudience.Team,
            TrustAudience.Team,
            TrustAudience.Team,
            TrustBoundary.TrustedInstance,
            PrincipalClassification.TrustedInternal,
            TransportAuthenticity.Verified,
            PayloadTaint.Trusted,
            null, null, false, false, null);

        var aiTools = new[] { granted.ToAITool(), blocked.ToAITool() };
        var filtered = policy.FilterExposedTools(aiTools, registry, trustContext);

        Assert.Single(filtered);
        // FilterExposedTools surfaces the AIFunction wrapper that goes to the LLM,
        // which uses the Anthropic-safe sanitized alias (server__tool).
        Assert.Equal("memorizer__search_memories", ((AIFunction)filtered[0]).Name);
    }

    // ── load_tool denial ──

    [Fact]
    public async Task LoadTool_DeniesBlockedTool()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateMcpTool("memorizer", "search_memories"));
        registry.Register(CreateMcpTool("memorizer", "store"));

        var config = CreateConfigWithTeamServer("memorizer");
        config.AudienceProfiles.Team.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["memorizer"] = ["search_memories"]
        };
        var policy = new ToolAccessPolicy(config, Defaults);
        var tool = new LoadToolTool(registry, policy);

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Name", "memorizer/store"),
            CreateExecutionContext(TrustAudience.Team),
            CancellationToken.None);

        Assert.Contains("not available", result);
    }

    [Fact]
    public async Task LoadTool_AllowsGrantedTool()
    {
        var registry = new ToolRegistry();
        registry.Register(CreateMcpTool("memorizer", "search_memories"));

        var config = CreateConfigWithTeamServer("memorizer");
        config.AudienceProfiles.Team.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["memorizer"] = ["search_memories"]
        };
        var policy = new ToolAccessPolicy(config, Defaults);
        var tool = new LoadToolTool(registry, policy);

        var result = await tool.ExecuteAsync(
            ToolInput.Create("Name", "memorizer/search_memories"),
            CreateExecutionContext(TrustAudience.Team),
            CancellationToken.None);

        Assert.Equal("memorizer/search_memories", result);
    }

    // ── Public audience ──

    [Fact]
    public void PublicAudience_WithGrants_OnlyExposesGrantedTools()
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Public.AllowedMcpServers.Add("memorizer");
        config.AudienceProfiles.Public.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["memorizer"] = ["search_memories"]
        };
        var policy = new ToolAccessPolicy(config, Defaults);

        Assert.True(policy.IsToolExposed(
            CreateMcpTool("memorizer", "search_memories"),
            CreateExecutionContext(TrustAudience.Public)));
        Assert.False(policy.IsToolExposed(
            CreateMcpTool("memorizer", "store"),
            CreateExecutionContext(TrustAudience.Public)));
    }

    // ── Multiple servers with independent grants ──

    [Fact]
    public void MultipleServers_GrantsAreIndependent()
    {
        var config = CreateConfigWithTeamServer("memorizer", "github");
        config.AudienceProfiles.Team.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["memorizer"] = ["search_memories"],
            ["github"] = ["create_issue", "list_issues"]
        };
        var policy = new ToolAccessPolicy(config, Defaults);

        // memorizer grants don't affect github
        Assert.True(policy.IsToolExposed(CreateMcpTool("github", "create_issue"), TeamContext()));
        Assert.True(policy.IsToolExposed(CreateMcpTool("github", "list_issues"), TeamContext()));
        Assert.False(policy.IsToolExposed(CreateMcpTool("github", "delete_repo"), TeamContext()));

        // github grants don't affect memorizer
        Assert.True(policy.IsToolExposed(CreateMcpTool("memorizer", "search_memories"), TeamContext()));
        Assert.False(policy.IsToolExposed(CreateMcpTool("memorizer", "store"), TeamContext()));
    }

    // ── Config deserialization round-trip ──

    [Fact]
    public void McpServerToolGrants_DeserializesFromJson()
    {
        var json = """
        {
            "ShellMode": "HostAllowed",
            "AudienceProfiles": {
                "Team": {
                    "McpServersMode": "Allowlist",
                    "AllowedMcpServers": ["memorizer"],
                    "McpServerToolGrants": {
                        "memorizer": ["search_memories", "get"]
                    }
                }
            }
        }
        """;

        var config = System.Text.Json.JsonSerializer.Deserialize<ToolConfig>(json,
            new System.Text.Json.JsonSerializerOptions
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });

        Assert.NotNull(config);
        Assert.NotNull(config.AudienceProfiles.Team.McpServerToolGrants);
        Assert.True(config.AudienceProfiles.Team.McpServerToolGrants.ContainsKey("memorizer"));
        Assert.Equal(["search_memories", "get"], config.AudienceProfiles.Team.McpServerToolGrants["memorizer"]);

        // Verify it actually enforces correctly when wired through the policy
        var policy = new ToolAccessPolicy(config, Defaults);
        Assert.True(policy.IsToolExposed(CreateMcpTool("memorizer", "search_memories"), TeamContext()));
        Assert.False(policy.IsToolExposed(CreateMcpTool("memorizer", "store"), TeamContext()));
    }

    // ── Helpers ──

    private static McpToolAdapter CreateMcpTool(string serverName, string toolName, string? description = null)
    {
        var func = AIFunctionFactory.Create(() => "result", toolName, description ?? toolName);
        return new McpToolAdapter(func, serverName, toolName);
    }

    private static ToolConfig CreateConfigWithTeamServer(params string[] servers)
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        foreach (var server in servers)
            config.AudienceProfiles.Team.AllowedMcpServers.Add(server);
        return config;
    }

    private static ToolExecutionContext CreateExecutionContext(TrustAudience audience)
    {
        return new ToolExecutionContext("slack/thread-1", null)
        {
            Audience = audience,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "slack"
        };
    }

    private static ToolExecutionContext TeamContext() => CreateExecutionContext(TrustAudience.Team);
    private static ToolExecutionContext PersonalContext() => CreateExecutionContext(TrustAudience.Personal);
}
