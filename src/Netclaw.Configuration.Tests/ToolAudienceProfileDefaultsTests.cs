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
    public static TheoryData<TrustAudience, string[]> DefaultAllowlists => new()
    {
        {
            TrustAudience.Public,
            [
                ToolAudienceProfileToolCatalog.FileRead,
                ToolAudienceProfileToolCatalog.FileList,
                ToolAudienceProfileToolCatalog.FileSearch,
                ToolAudienceProfileToolCatalog.FileReadMany,
                ToolAudienceProfileToolCatalog.JsonRead,
                ToolAudienceProfileToolCatalog.ToolOutputRead,
                ToolAudienceProfileToolCatalog.AttachFile
            ]
        },
        {
            TrustAudience.Team,
            [
                ToolAudienceProfileToolCatalog.FileRead,
                ToolAudienceProfileToolCatalog.FileList,
                ToolAudienceProfileToolCatalog.FileSearch,
                ToolAudienceProfileToolCatalog.FileReadMany,
                ToolAudienceProfileToolCatalog.JsonRead,
                ToolAudienceProfileToolCatalog.ToolOutputRead,
                ToolAudienceProfileToolCatalog.FileWrite,
                ToolAudienceProfileToolCatalog.FileEdit,
                ToolAudienceProfileToolCatalog.AttachFile,
                ToolAudienceProfileToolCatalog.WebSearch,
                ToolAudienceProfileToolCatalog.WebFetch,
                ToolAudienceProfileToolCatalog.SkillManage,
                ToolAudienceProfileToolCatalog.SetReminder,
                ToolAudienceProfileToolCatalog.ListReminders,
                ToolAudienceProfileToolCatalog.CancelReminder,
                ToolAudienceProfileToolCatalog.GetReminderHistory,
                ToolAudienceProfileToolCatalog.SetWorkingDirectory
            ]
        }
    };

    public static TheoryData<TrustAudience, string[]> DefaultExcludedTools => new()
    {
        {
            TrustAudience.Public,
            [
                ToolAudienceProfileToolCatalog.FileWrite,
                ToolAudienceProfileToolCatalog.FileEdit,
                ToolAudienceProfileToolCatalog.WebSearch,
                ToolAudienceProfileToolCatalog.WebFetch
            ]
        },
        {
            TrustAudience.Team,
            [
                ToolAudienceProfileToolCatalog.ShellExecute,
                ToolAudienceProfileToolCatalog.SetWebhook,
                ToolAudienceProfileToolCatalog.ListWebhooks,
                ToolAudienceProfileToolCatalog.DeleteWebhook
            ]
        }
    };

    [Theory]
    [MemberData(nameof(DefaultAllowlists))]
    public void Default_allowlists_match_expected_catalog_tools(TrustAudience audience, string[] expectedTools)
    {
        Assert.Equal(expectedTools, GetDefaultCatalogTools(audience));
        Assert.Equal(expectedTools, GetDefaultProfile(audience).AllowedTools);
    }

    [Fact]
    public void Profile_managed_catalog_covers_default_team_shell_and_webhook_tools()
    {
        var profileManaged = ToolAudienceProfileToolCatalog.ProfileManagedTools;

        Assert.All(ToolAudienceProfileToolCatalog.TeamDefaultAllowedTools,
            tool => Assert.Contains(tool, profileManaged));
        Assert.Contains(ToolAudienceProfileToolCatalog.ShellExecute, profileManaged);
        Assert.All(ToolAudienceProfileToolCatalog.WebhookTools,
            tool => Assert.Contains(tool, profileManaged));
    }

    [Theory]
    [MemberData(nameof(DefaultExcludedTools))]
    public void Default_allowlists_exclude_restricted_tools(TrustAudience audience, string[] excludedTools)
    {
        var allowedTools = GetDefaultProfile(audience).AllowedTools;

        Assert.All(excludedTools, tool => Assert.DoesNotContain(tool, allowedTools));
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

    private static ToolAudienceProfile GetDefaultProfile(TrustAudience audience)
        => audience switch
        {
            TrustAudience.Public => ToolAudienceProfileDefaults.CreatePublic(),
            TrustAudience.Team => ToolAudienceProfileDefaults.CreateTeam(),
            TrustAudience.Personal => ToolAudienceProfileDefaults.CreatePersonal(),
            _ => throw new ArgumentOutOfRangeException(nameof(audience), audience, null)
        };

    private static IReadOnlyList<string> GetDefaultCatalogTools(TrustAudience audience)
        => audience switch
        {
            TrustAudience.Public => ToolAudienceProfileToolCatalog.PublicDefaultAllowedTools,
            TrustAudience.Team => ToolAudienceProfileToolCatalog.TeamDefaultAllowedTools,
            _ => throw new ArgumentOutOfRangeException(nameof(audience), audience, null)
        };
}
