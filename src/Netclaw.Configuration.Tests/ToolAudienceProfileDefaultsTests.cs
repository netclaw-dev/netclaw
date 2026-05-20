// -----------------------------------------------------------------------
// <copyright file="ToolAudienceProfileDefaultsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Pins the default audience tool-profile grants: Public is read/enumerate/
/// attach only, Team adds the file-mutation, scheduling, and skill tools, and
/// the grant sets stay monotonic across the trust ladder.
/// </summary>
public sealed class ToolAudienceProfileDefaultsTests
{
    [Fact]
    public void Public_default_grants_read_list_and_attach_only()
    {
        var publicProfile = ToolAudienceProfileDefaults.CreatePublic();

        Assert.Equal(
            ["file_read", "file_list", "attach_file"],
            publicProfile.AllowedTools);
        Assert.DoesNotContain("file_write", publicProfile.AllowedTools);
        Assert.DoesNotContain("file_edit", publicProfile.AllowedTools);
    }

    [Fact]
    public void Team_default_grants_file_web_scheduling_and_skill_tools()
    {
        var team = ToolAudienceProfileDefaults.CreateTeam().AllowedTools;

        Assert.Contains("file_read", team);
        Assert.Contains("file_list", team);
        Assert.Contains("file_write", team);
        Assert.Contains("file_edit", team);
        Assert.Contains("attach_file", team);
        Assert.Contains("web_search", team);
        Assert.Contains("web_fetch", team);
        Assert.Contains("skill_manage", team);
        Assert.Contains("set_reminder", team);
        Assert.Contains("list_reminders", team);
        Assert.Contains("cancel_reminder", team);
        Assert.Contains("get_reminder_history", team);
        Assert.Contains("set_working_directory", team);
    }

    [Fact]
    public void Public_default_excludes_outbound_web_tools()
    {
        var publicProfile = ToolAudienceProfileDefaults.CreatePublic();

        Assert.DoesNotContain("web_search", publicProfile.AllowedTools);
        Assert.DoesNotContain("web_fetch", publicProfile.AllowedTools);
    }

    [Fact]
    public void Team_default_excludes_shell_and_webhook_tools()
    {
        var team = ToolAudienceProfileDefaults.CreateTeam().AllowedTools;

        Assert.DoesNotContain("shell_execute", team);
        Assert.DoesNotContain("set_webhook", team);
        Assert.DoesNotContain("list_webhooks", team);
        Assert.DoesNotContain("delete_webhook", team);
    }

    [Fact]
    public void Team_default_disables_mcp_servers()
    {
        var team = ToolAudienceProfileDefaults.CreateTeam();

        Assert.Equal(ToolProfileMode.Allowlist, team.McpServersMode);
        Assert.Empty(team.AllowedMcpServers);
    }

    [Fact]
    public void Default_grants_are_monotonic_across_audiences()
    {
        var publicTools = ToolAudienceProfileDefaults.CreatePublic().AllowedTools;
        var teamTools = ToolAudienceProfileDefaults.CreateTeam().AllowedTools;

        // Public ⊆ Team.
        Assert.All(publicTools, tool => Assert.Contains(tool, teamTools));

        // Team ⊆ Personal — Personal grants every tool via ToolsMode.All.
        Assert.Equal(ToolProfileMode.All, ToolAudienceProfileDefaults.CreatePersonal().ToolsMode);
    }
}
