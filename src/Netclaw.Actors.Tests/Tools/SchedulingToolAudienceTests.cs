// -----------------------------------------------------------------------
// <copyright file="SchedulingToolAudienceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Verifies that scheduling tools are profile-managed and therefore blocked
/// for Public and Team audiences by default, while Personal retains access.
/// </summary>
public sealed class SchedulingToolAudienceTests
{
    private static readonly string[] SchedulingToolNames =
        ["set_reminder", "list_reminders", "cancel_reminder", "get_reminder_history"];

    private static readonly EffectivePolicyDefaults Defaults = new(
        DeploymentPosture.Personal,
        TrustAudience.Personal,
        ShellExecutionMode.HostAllowed,
        UsedStrictFallback: false);

    [Theory]
    [InlineData("set_reminder")]
    [InlineData("list_reminders")]
    [InlineData("cancel_reminder")]
    [InlineData("get_reminder_history")]
    public void SchedulingTools_BlockedForPublicAudience_ByDefault(string toolName)
    {
        var config = new ToolConfig();
        var policy = new ToolAccessPolicy(config, Defaults);
        var tool = CreateFakeTool(toolName, "scheduling");

        Assert.False(policy.IsToolExposed(tool, CreateContext(TrustAudience.Public)));
    }

    [Theory]
    [InlineData("set_reminder")]
    [InlineData("list_reminders")]
    [InlineData("cancel_reminder")]
    [InlineData("get_reminder_history")]
    public void SchedulingTools_BlockedForTeamAudience_ByDefault(string toolName)
    {
        var config = new ToolConfig();
        var policy = new ToolAccessPolicy(config, Defaults);
        var tool = CreateFakeTool(toolName, "scheduling");

        Assert.False(policy.IsToolExposed(tool, CreateContext(TrustAudience.Team)));
    }

    [Theory]
    [InlineData("set_reminder")]
    [InlineData("list_reminders")]
    [InlineData("cancel_reminder")]
    [InlineData("get_reminder_history")]
    public void SchedulingTools_AllowedForPersonalAudience_ByDefault(string toolName)
    {
        var config = new ToolConfig();
        var policy = new ToolAccessPolicy(config, Defaults);
        var tool = CreateFakeTool(toolName, "scheduling");

        Assert.True(policy.IsToolExposed(tool, CreateContext(TrustAudience.Personal)));
    }

    [Theory]
    [InlineData("set_reminder")]
    [InlineData("list_reminders")]
    [InlineData("cancel_reminder")]
    [InlineData("get_reminder_history")]
    public void SchedulingTools_AllowedForTeam_WhenExplicitlyGranted(string toolName)
    {
        var config = new ToolConfig();
        config.AudienceProfiles.Team.AllowedTools.Add(toolName);
        var policy = new ToolAccessPolicy(config, Defaults);
        var tool = CreateFakeTool(toolName, "scheduling");

        Assert.True(policy.IsToolExposed(tool, CreateContext(TrustAudience.Team)));
    }

    private static FakeNetclawTool CreateFakeTool(string name, string grantCategory)
        => new(name, "ok", grantCategory);

    private static ToolExecutionContext CreateContext(TrustAudience audience)
        => new ToolExecutionContext("slack/thread-1", null)
        {
            Audience = audience,
            Boundary = TrustBoundary.TrustedInstance,
            ChannelType = "slack"
        };
}
