// -----------------------------------------------------------------------
// <copyright file="SecurityPolicyDefaultsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class SecurityPolicyDefaultsTests
{
    [Fact]
    public void Resolve_uses_strict_public_defaults_when_policy_missing()
    {
        var result = SecurityPolicyDefaults.Resolve(null);

        Assert.Equal(DeploymentPosture.Public, result.DeploymentPosture);
        Assert.Equal(TrustAudience.Public, result.Audience);
        Assert.Equal(ShellExecutionMode.Off, result.ShellExecutionMode);
        Assert.True(result.UsedStrictFallback);
    }

    [Fact]
    public void Resolve_uses_personal_host_shell_when_personal_posture_selected()
    {
        var result = SecurityPolicyDefaults.Resolve(new SecurityPolicyConfig
        {
            DeploymentPosture = DeploymentPosture.Personal
        });

        Assert.Equal(DeploymentPosture.Personal, result.DeploymentPosture);
        Assert.Equal(TrustAudience.Personal, result.Audience);
        Assert.Equal(ShellExecutionMode.HostAllowed, result.ShellExecutionMode);
    }

    [Fact]
    public void Resolve_honors_explicit_shell_mode_override()
    {
        var result = SecurityPolicyDefaults.Resolve(new SecurityPolicyConfig
        {
            DeploymentPosture = DeploymentPosture.Personal,
            ShellExecutionMode = ShellExecutionMode.SandboxOnly
        });

        Assert.Equal(ShellExecutionMode.SandboxOnly, result.ShellExecutionMode);
    }

    [Fact]
    public void Tool_profile_defaults_keep_public_and_team_session_scoped()
    {
        var defaults = ToolAudienceProfileDefaults.CreateProfiles();

        Assert.Equal(ToolFilesystemMode.Roots, defaults.Public.ReadFiles.Mode);
        Assert.Equal([ToolAudienceProfileDefaults.SessionDirectoryToken], defaults.Public.ReadFiles.Roots);
        Assert.Equal(ToolFilesystemMode.Roots, defaults.Team.WriteFiles.Mode);
        Assert.Equal([ToolAudienceProfileDefaults.SessionDirectoryToken], defaults.Team.WriteFiles.Roots);
        Assert.Equal(ToolProfileMode.Allowlist, defaults.Public.McpServersMode);
        Assert.Empty(defaults.Team.AllowedMcpServers);
    }

    [Fact]
    public void Tool_profile_defaults_allow_personal_all_mode()
    {
        var defaults = ToolAudienceProfileDefaults.CreateProfiles();

        Assert.Equal(ToolProfileMode.All, defaults.Personal.ToolsMode);
        Assert.Equal(ToolProfileMode.All, defaults.Personal.McpServersMode);
        Assert.Equal(ToolFilesystemMode.All, defaults.Personal.ReadFiles.Mode);
        Assert.Equal(ToolFilesystemMode.All, defaults.Personal.WriteFiles.Mode);
        Assert.Equal(ToolFilesystemMode.All, defaults.Personal.AttachFiles.Mode);
    }

    [Fact]
    public void Personal_posture_requires_shell_approval()
    {
        var profiles = ToolAudienceProfileDefaults.CreateProfilesForPosture(DeploymentPosture.Personal);

        var policy = Assert.IsType<ToolApprovalConfig>(profiles.Personal.ApprovalPolicy);
        Assert.Equal(ToolApprovalMode.Approval, policy.ToolOverrides[ToolAudienceProfileToolCatalog.ShellExecute]);
    }
}
