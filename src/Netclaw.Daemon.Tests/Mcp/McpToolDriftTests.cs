// -----------------------------------------------------------------------
// <copyright file="McpToolDriftTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Daemon.Mcp;
using Xunit;

namespace Netclaw.Daemon.Tests.Mcp;

public sealed class McpToolDriftTests
{
    private static readonly McpServerName Server = new("dropbox");

    [Fact]
    public void AllPosture_GrantSnapshot_ProducesNoDrift()
    {
        var profiles = new ToolAudienceProfiles();
        // Personal is All posture by default.
        profiles.Personal.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["dropbox"] = ["copy"]
        };

        var report = McpClientManager.ComputeToolDrift(profiles, Server, ["copy", "get_upload_url"]);

        // The server added get_upload_url after the snapshot. In All posture the
        // grant list is additive, so there is no drift to warn about.
        Assert.Empty(report.Ungranted);
        Assert.Empty(report.Stale);
    }

    [Fact]
    public void AllowlistPosture_DiscoveredToolMissingFromGrants_ReportsUngranted()
    {
        var profiles = new ToolAudienceProfiles();
        // Team is Allowlist posture by default.
        profiles.Team.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["dropbox"] = ["copy"]
        };

        var report = McpClientManager.ComputeToolDrift(profiles, Server, ["copy", "delete"]);

        Assert.Contains("delete", report.Ungranted);
    }

    [Fact]
    public void AllowlistPosture_GrantNamesMissingTool_ReportsStale()
    {
        var profiles = new ToolAudienceProfiles();
        profiles.Team.McpServerToolGrants = new Dictionary<string, List<string>>
        {
            ["dropbox"] = ["copy", "gone"]
        };

        var report = McpClientManager.ComputeToolDrift(profiles, Server, ["copy"]);

        Assert.Contains("gone", report.Stale);
    }
}
