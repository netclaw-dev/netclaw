// -----------------------------------------------------------------------
// <copyright file="SecurityAccessViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Cli.Mcp;
using Netclaw.Cli.Tui.Config;
using Netclaw.Cli.Tests.Tui.Wizard;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Config;

public sealed class SecurityAccessViewModelTests : WizardStepTestBase
{
    [Fact]
    public void Security_access_lists_expected_leaf_entries()
    {
        using var vm = new SecurityAccessViewModel(Context.Paths);

        var labels = vm.Items.Select(static item => item.Label).ToArray();

        Assert.Equal(
        [
            "Security Posture",
            "Enabled Features",
            "Audience Profiles",
            "Exposure Mode"
        ], labels);
    }

    [Fact]
    public void Exposure_mode_routes_to_exposure_editor()
    {
        using var vm = new SecurityAccessViewModel(Context.Paths);
        string? route = null;
        vm.RouteRequested = value => route = value;

        vm.Activate(vm.Items.Single(static item => item.Label == "Exposure Mode"));

        Assert.Equal("/exposure-mode", route);
    }

    [Fact]
    public void Enabled_features_opens_inline_global_toggle_editor()
    {
        using var vm = new SecurityAccessViewModel(Context.Paths);
        string? route = null;
        vm.RouteRequested = value => route = value;

        vm.Activate(vm.Items.Single(static item => item.Label == "Enabled Features"));

        Assert.Equal(SecurityAccessEditorMode.Features, vm.Mode.Value);
        Assert.Null(route);
    }

    [Fact]
    public void Security_posture_saves_posture_and_shell_defaults()
    {
        using var vm = new SecurityAccessViewModel(Context.Paths);
        vm.OpenPostureEditor();
        vm.SelectedPostureIndex.Value = 1;

        vm.ApplySelectedPosture();

        var config = ConfigFileHelper.LoadJsonDict(Context.Paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Security.DeploymentPosture", out var posture));
        Assert.Equal("Team", posture);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Security.ShellExecutionMode", out var securityShellMode));
        Assert.Equal("Off", securityShellMode);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Tools.ShellMode", out var toolsShellMode));
        Assert.Equal("Off", toolsShellMode);
        Assert.Equal(SecurityAccessEditorMode.Features, vm.Mode.Value);
    }

    [Fact]
    public void Audience_profiles_opens_inline_audience_list()
    {
        using var vm = new SecurityAccessViewModel(Context.Paths);
        string? route = null;
        vm.RouteRequested = value => route = value;

        vm.Activate(vm.Items.Single(static item => item.Label == "Audience Profiles"));

        Assert.Equal(SecurityAccessEditorMode.AudienceList, vm.Mode.Value);
        Assert.Null(route);
    }

    [Fact]
    public void Audience_profile_toggle_updates_selected_profile_only()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Team" }
            }
            """);

        using var vm = new SecurityAccessViewModel(Context.Paths);
        vm.SelectedAudienceIndex.Value = 1;
        vm.OpenSelectedAudienceProfile();
        vm.SelectedAudienceRowIndex.Value = (int)AudienceProfileRowKind.WebAccess;

        vm.ActivateSelectedAudienceProfileRow();

        var config = ConfigFileHelper.LoadJsonDict(Context.Paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Tools.AudienceProfiles.Team.AllowedTools", out var teamTools));
        var teamAllowedTools = Assert.IsAssignableFrom<object[]>(teamTools);
        Assert.DoesNotContain(teamAllowedTools, static tool => tool?.ToString() == "web_search");
        Assert.DoesNotContain(teamAllowedTools, static tool => tool?.ToString() == "web_fetch");

        Assert.False(ConfigFileHelper.TryGetPathValue(config, "Tools.AudienceProfiles.Public.AllowedTools", out _));
        Assert.Equal("Customized", vm.AudienceOverrideMarker(TrustAudience.Team));
        Assert.Equal("", vm.AudienceOverrideMarker(TrustAudience.Public));
        Assert.Equal("Customized overrides", vm.SelectedAudienceOverrideStatus);
    }

    [Fact]
    public void Audience_profiles_summary_reports_overrides_not_defaults()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Team" }
            }
            """);

        using var vm = new SecurityAccessViewModel(Context.Paths);

        var audienceProfiles = vm.Items.Single(static item => item.Label == "Audience Profiles");
        Assert.Equal("No overrides", audienceProfiles.Summary);
        Assert.Equal("", vm.AudienceOverrideMarker(TrustAudience.Team));
        Assert.False(vm.IsSystemDefaultAudience(TrustAudience.Personal));
        Assert.True(vm.IsSystemDefaultAudience(TrustAudience.Team));
        Assert.False(vm.IsSystemDefaultAudience(TrustAudience.Public));

        vm.SelectedAudienceIndex.Value = 1;
        vm.OpenSelectedAudienceProfile();
        vm.SelectedAudienceRowIndex.Value = (int)AudienceProfileRowKind.WebAccess;
        vm.ActivateSelectedAudienceProfileRow();

        audienceProfiles = vm.Items.Single(static item => item.Label == "Audience Profiles");
        Assert.Equal("Customized", audienceProfiles.Summary);
        Assert.Equal("Customized", vm.AudienceOverrideMarker(TrustAudience.Team));
    }

    [Fact]
    public void Audience_profile_file_scope_cycle_keeps_team_scope_restricted()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Team" }
            }
            """);

        using var vm = new SecurityAccessViewModel(Context.Paths);
        vm.SelectedAudienceIndex.Value = 1;
        vm.OpenSelectedAudienceProfile();
        vm.SelectedAudienceRowIndex.Value = (int)AudienceProfileRowKind.FileAccess;

        vm.ActivateSelectedAudienceProfileRow();
        vm.ActivateSelectedAudienceProfileRow();

        var config = ConfigFileHelper.LoadJsonDict(Context.Paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Tools.AudienceProfiles.Team.ReadFiles.Mode", out var readMode));
        Assert.Equal("Roots", readMode);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Tools.AudienceProfiles.Team.WriteFiles.Mode", out var writeMode));
        Assert.Equal("Roots", writeMode);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Tools.AudienceProfiles.Team.AttachFiles.Mode", out var attachMode));
        Assert.Equal("Roots", attachMode);
    }

    [Fact]
    public void Audience_profile_mcp_grants_routes_to_permissions_for_selected_audience()
    {
        var navigationState = new McpToolPermissionsNavigationState();
        using var vm = new SecurityAccessViewModel(Context.Paths, navigationState);
        string? route = null;
        vm.RouteRequested = value => route = value;
        vm.SelectedAudienceIndex.Value = 1;
        vm.OpenSelectedAudienceProfile();
        vm.SelectedAudienceRowIndex.Value = (int)AudienceProfileRowKind.McpPermissions;

        vm.ActivateSelectedAudienceProfileRow();

        Assert.Equal("/mcp-tools", route);
        Assert.Equal(TrustAudience.Team, navigationState.ConsumeInitialAudience());
    }

    [Fact]
    public void Enabled_features_summary_treats_missing_flags_as_enabled()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Team" }
            }
            """);

        using var vm = new SecurityAccessViewModel(Context.Paths);

        var features = vm.Items.Single(static item => item.Label == "Enabled Features");
        Assert.Equal("6/6 enabled", features.Summary);
    }

    [Fact]
    public void Toggle_selected_feature_persists_global_flag_and_preserves_siblings()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Team" },
              "Memory": { "Enabled": true },
              "Search": {
                "Enabled": false,
                "Backend": "searxng",
                "SearXngEndpoint": "https://search.example.com"
              },
              "SkillSync": { "Enabled": true },
              "Scheduling": { "Enabled": false },
              "SubAgents": { "Enabled": true },
              "Webhooks": { "Enabled": false }
            }
            """);

        using var vm = new SecurityAccessViewModel(Context.Paths);
        vm.SelectedFeatureIndex.Value = 1;

        vm.ToggleSelectedFeature();

        var config = ConfigFileHelper.LoadJsonDict(Context.Paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Search.Enabled", out var searchEnabled));
        Assert.Equal(true, searchEnabled);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Search.Backend", out var backend));
        Assert.Equal("searxng", backend);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Search.SearXngEndpoint", out var endpoint));
        Assert.Equal("https://search.example.com", endpoint);
    }

    [Fact]
    public void Exposure_summary_reads_existing_daemon_mode()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": { "ExposureMode": "cloudflare-tunnel" }
            }
            """);

        using var vm = new SecurityAccessViewModel(Context.Paths);

        var exposure = vm.Items.Single(static item => item.Label == "Exposure Mode");
        Assert.Equal("Cloudflare Tunnel", exposure.Summary);
    }
}
