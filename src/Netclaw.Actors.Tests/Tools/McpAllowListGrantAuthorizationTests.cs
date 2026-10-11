// -----------------------------------------------------------------------
// <copyright file="McpAllowListGrantAuthorizationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Authorization;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// What an allow-listed MCP server exposes to an audience, decided by the authorizer that
/// every tool call uses. <c>netclaw mcp tools --grant</c> adds a server to the allow-list, so
/// these tests show whether that exposes more than the tools the operator named.
/// </summary>
public sealed class McpAllowListGrantAuthorizationTests
{
    private static readonly EffectivePolicyDefaults Defaults = new(
        DeploymentPosture.Personal,
        TrustAudience.Personal,
        ShellExecutionMode.HostAllowed,
        UsedStrictFallback: false);

    private static readonly string[] ServerTools = ["copy", "delete", "move"];

    [Fact]
    public async Task AllowedServerWithGrantsEntry_ExposesOnlyTheGrantedTool()
    {
        // The state `mcp tools dropbox --grant copy --audience team` leaves behind.
        var config = TeamConfig(allowed: ["dropbox"], grants: ["copy"]);

        Assert.False(await IsDeniedAsync(config, "copy"));
        Assert.True(await IsDeniedAsync(config, "delete"));
        Assert.True(await IsDeniedAsync(config, "move"));
    }

    [Fact]
    public async Task AllowedServerWithoutGrantsEntry_ExposesEveryTool()
    {
        // Why the command must not allow a server before it writes a grants entry.
        var config = TeamConfig(allowed: ["dropbox"], grants: null);

        foreach (var tool in ServerTools)
            Assert.False(await IsDeniedAsync(config, tool));
    }

    [Fact]
    public async Task GrantsEntryForServerMissingFromAllowList_ExposesNothing()
    {
        // The 0.27.1 state this change repairs on an explicit --grant.
        var config = TeamConfig(allowed: [], grants: ["copy"]);

        foreach (var tool in ServerTools)
            Assert.True(await IsDeniedAsync(config, tool));
    }

    private static ToolConfig TeamConfig(string[] allowed, string[]? grants)
    {
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        var team = config.AudienceProfiles.Team;
        team.AllowedMcpServers = [.. allowed];
        if (grants is not null)
            team.McpServerToolGrants = new Dictionary<string, List<string>> { ["dropbox"] = [.. grants] };

        // What `mcp add` writes for Team.
        team.ApprovalPolicy = new ToolApprovalConfig
        {
            McpServerDefaults = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["dropbox"] = ToolApprovalMode.Approval
            }
        };
        return config;
    }

    private static async Task<bool> IsDeniedAsync(ToolConfig config, string tool)
    {
        var registry = new ToolRegistry();
        foreach (var name in ServerTools)
            registry.Register(new McpToolAdapter(AIFunctionFactory.Create(() => "ok", name), "dropbox", name));

        var policy = new ToolAccessPolicy(new NetclawPaths(), config, Defaults, new ShellCommandPolicy(), new ToolPathPolicy([]));
        var authorizer = new DispatchingToolExecutor(registry, policy).Authorizer;
        var context = TestToolExecutionContext.CreateBound("slack/thread-1", null, new TestToolExecutionContextOptions
        {
            Audience = TrustAudience.Team,
            Boundary = TrustBoundary.Team,
            ChannelType = "slack"
        });

        var decision = await authorizer.AuthorizeAsync(
            new FunctionCallContent("call-1", $"dropbox/{tool}", ToolInput.Empty()),
            context,
            TestContext.Current.CancellationToken);

        return decision is AuthorizationDecision.Denied denied
            && denied.Reason.StartsWith("mcp_", StringComparison.Ordinal);
    }
}
