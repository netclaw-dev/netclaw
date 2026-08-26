// -----------------------------------------------------------------------
// <copyright file="SchedulingToolAudienceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tests.Memory;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Verifies that scheduling tools are profile-managed: blocked for the Public
/// audience by default, and granted to Team and Personal by default.
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
        var policy = new ToolAccessPolicy(config, Defaults, new ShellCommandPolicy(), new ToolPathPolicy([]));
        var tool = CreateFakeTool(toolName, "scheduling");

        Assert.False(policy.IsToolExposed(tool, TrustAudience.Public));
    }

    [Theory]
    [InlineData("set_reminder")]
    [InlineData("list_reminders")]
    [InlineData("cancel_reminder")]
    [InlineData("get_reminder_history")]
    public void SchedulingTools_AllowedForTeamAudience_ByDefault(string toolName)
    {
        var config = new ToolConfig();
        var policy = new ToolAccessPolicy(config, Defaults, new ShellCommandPolicy(), new ToolPathPolicy([]));
        var tool = CreateFakeTool(toolName, "scheduling");

        Assert.True(policy.IsToolExposed(tool, TrustAudience.Team));
    }

    [Theory]
    [InlineData("set_reminder")]
    [InlineData("list_reminders")]
    [InlineData("cancel_reminder")]
    [InlineData("get_reminder_history")]
    public void SchedulingTools_AllowedForPersonalAudience_ByDefault(string toolName)
    {
        var config = new ToolConfig();
        var policy = new ToolAccessPolicy(config, Defaults, new ShellCommandPolicy(), new ToolPathPolicy([]));
        var tool = CreateFakeTool(toolName, "scheduling");

        Assert.True(policy.IsToolExposed(tool, TrustAudience.Personal));
    }

    private static FakeNetclawTool CreateFakeTool(string name, string grantCategory)
        => new(name, "ok", grantCategory);

}
