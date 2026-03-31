using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
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
            new Dictionary<string, object?> { ["Query"] = "memor" },
            CreateExecutionContext(TrustAudience.Team),
            CancellationToken.None);

        Assert.Contains("search_memories", result);
        Assert.DoesNotContain("memorizer/store", result);
        Assert.DoesNotContain("memorizer/delete", result);
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
            Audience = audience.ToWireValue(),
            Boundary = SecurityPolicyDefaults.TrustedInstanceBoundary,
            ChannelType = "slack"
        };
    }

    private static ToolExecutionContext TeamContext() => CreateExecutionContext(TrustAudience.Team);
    private static ToolExecutionContext PersonalContext() => CreateExecutionContext(TrustAudience.Personal);
}
