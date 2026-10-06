// -----------------------------------------------------------------------
// <copyright file="SecurityAccessViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Cli.Mcp;
using Netclaw.Cli.Tui.Config;
using Netclaw.Cli.Tests.Tui.Wizard;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Media;
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
            "Exposure Mode",
            "Done"
        ], labels);
    }

    [Fact]
    public void Done_item_returns_to_the_config_dashboard()
    {
        using var vm = new SecurityAccessViewModel(Context.Paths);
        string? routed = null;
        vm.RouteRequested = route => routed = route;

        var doneIndex = vm.Items
            .Select((item, index) => (item, index))
            .Single(entry => entry.item.Label == "Done")
            .index;
        vm.SelectedIndex.Value = doneIndex;
        vm.ActivateSelected();

        Assert.Equal("/config", routed);
    }

    [Fact]
    public void Posture_editor_done_row_backs_out_to_menu()
    {
        using var vm = new SecurityAccessViewModel(Context.Paths);
        vm.OpenPostureEditor();
        vm.SelectedPostureIndex.Value = vm.PostureOptions.Count; // the appended Done row
        vm.ApplySelectedPosture();                               // Enter on Done
        Assert.Equal(SecurityAccessEditorMode.Menu, vm.Mode.Value);
    }

    [Fact]
    public void Feature_editor_done_row_backs_out_to_menu()
    {
        using var vm = new SecurityAccessViewModel(Context.Paths);
        vm.OpenFeatureEditor();
        vm.SelectedFeatureIndex.Value = vm.FeatureNames.Count;   // the appended Done row
        vm.ToggleSelectedFeature();                              // Space/Enter on Done
        Assert.Equal(SecurityAccessEditorMode.Menu, vm.Mode.Value);
    }

    [Fact]
    public void Audience_list_done_row_backs_out_to_menu()
    {
        using var vm = new SecurityAccessViewModel(Context.Paths);
        vm.OpenAudienceList();
        vm.SelectedAudienceIndex.Value = vm.AudienceOptions.Count; // the appended Done row
        vm.OpenSelectedAudienceProfile();                          // Enter on Done
        Assert.Equal(SecurityAccessEditorMode.Menu, vm.Mode.Value);
    }

    [Fact]
    public void Audience_profile_done_row_backs_out_to_audience_list()
    {
        using var vm = new SecurityAccessViewModel(Context.Paths);
        vm.OpenAudienceList();
        vm.OpenSelectedAudienceProfile();                         // enter the first audience's profile
        vm.SelectedAudienceRowIndex.Value = vm.ProfileRows.Count; // the appended Done row
        vm.ActivateSelectedAudienceProfileRow();                  // Space/Enter on Done
        Assert.Equal(SecurityAccessEditorMode.AudienceList, vm.Mode.Value);
    }

    [Fact]
    public void Unparseable_posture_fails_loud_and_closed_not_permissive()
    {
        // A stored posture the editor cannot parse (renamed enum member, stale value, hand-edited
        // typo) must NOT be silently treated as the permissive Personal default. It fails closed to
        // Public (matching the daemon's TrustContextPolicy fallback) and surfaces the corruption.
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Galaxy-Brain" }
            }
            """);
        using var vm = new SecurityAccessViewModel(Context.Paths);

        Assert.NotEqual(DeploymentPosture.Personal, vm.CurrentPosture); // no permissive assumption
        Assert.Equal(DeploymentPosture.Public, vm.CurrentPosture);      // fail closed
        Assert.NotNull(vm.PostureConfigWarning);
        Assert.Contains("Galaxy-Brain", vm.PostureConfigWarning!, StringComparison.Ordinal);

        var postureSummary = vm.Items.Single(static item => item.Label == "Security Posture").Summary;
        Assert.Contains("Unknown", postureSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_exposure_and_audience_config_render_a_status_without_crashing()
    {
        // A hand-edited/migrated config with an unsupported ExposureMode or a malformed
        // Tools.AudienceProfiles blob must not throw on the render path (Items is read every frame)
        // or on the audience-profile load path — it degrades to a visible status instead.
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Daemon": { "ExposureMode": "WormHole" },
              "Tools": { "AudienceProfiles": "not-an-object" }
            }
            """);
        using var vm = new SecurityAccessViewModel(Context.Paths);

        var items = vm.Items; // render path — must not throw
        Assert.Contains("WormHole", items.Single(static i => i.Label == "Exposure Mode").Summary, StringComparison.Ordinal);
        Assert.Contains("Unreadable", items.Single(static i => i.Label == "Audience Profiles").Summary, StringComparison.Ordinal);

        // Audience-profile load path (used by mutation handlers + override-status reads) must not throw.
        var status = vm.SelectedAudienceOverrideStatus;
        Assert.False(string.IsNullOrEmpty(status));
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

    // The shape that `netclaw mcp add` writes: each audience has only MCP keys. The daemon reads
    // the posture defaults for the keys that the file does not set.
    private const string ProfilesWithOnlyMcpKeys =
        """
        {
          "configVersion": 1,
          "Security": { "DeploymentPosture": "Personal" },
          "McpServers": { "textforge": { "Transport": "http", "Url": "https://textforge.net/mcp", "Enabled": true } },
          "Tools": {
            "AudienceProfiles": {
              "Personal": { "ApprovalPolicy": { "McpServerDefaults": { "textforge": "Auto" } } },
              "Team": {
                "McpServerToolGrants": { "textforge": [] },
                "ApprovalPolicy": { "McpServerDefaults": { "textforge": "Approval" } }
              }
            }
          }
        }
        """;

    [Fact]
    public void Edit_of_one_personal_row_on_a_partial_profile_changes_only_that_value()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath, ProfilesWithOnlyMcpKeys);
        using var vm = new SecurityAccessViewModel(Context.Paths);
        vm.SelectedAudienceIndex.Value = 0;
        vm.OpenSelectedAudienceProfile();
        Assert.Equal(TrustAudience.Personal, vm.SelectedAudience);
        Assert.Equal("All files", vm.AudienceValue(AudienceProfileRowKind.FileAccess));

        vm.SelectedAudienceRowIndex.Value = (int)AudienceProfileRowKind.IncomingAttachments;
        vm.ChangeSelectedAudienceProfileRow(-1);

        // Before the fix, this one edit saved empty tool and MCP allowlists and no file access.
        var personal = BindLikeTheDaemon().AudienceProfiles.Personal;
        Assert.Equal(ToolProfileMode.All, personal.ToolsMode);
        Assert.Equal(ToolProfileMode.All, personal.McpServersMode);
        Assert.Equal(ToolFilesystemMode.All, personal.ReadFiles.Mode);
        Assert.Equal(ToolFilesystemMode.All, personal.WriteFiles.Mode);
        Assert.Equal(ToolApprovalMode.Auto, personal.ApprovalPolicy!.McpServerDefaults["textforge"]);
        Assert.DoesNotContain(AttachmentCategory.Other, personal.ChannelAttachments.AllowedCategories);

        var config = ConfigFileHelper.LoadJsonDict(Context.Paths.NetclawConfigPath);
        Assert.False(ConfigFileHelper.TryGetPathValue(config, "Tools.AudienceProfiles.Personal.ToolsMode", out _));
        Assert.False(ConfigFileHelper.TryGetPathValue(config, "Tools.AudienceProfiles.Personal.McpServersMode", out _));
        Assert.False(ConfigFileHelper.TryGetPathValue(config, "Tools.AudienceProfiles.Personal.McpServerToolGrants", out _));
    }

    [Fact]
    public void Tool_group_toggle_on_a_partial_team_profile_keeps_the_mcp_keys_and_the_other_tools()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath, ProfilesWithOnlyMcpKeys);
        using var vm = new SecurityAccessViewModel(Context.Paths);
        vm.SelectedAudienceIndex.Value = 1;
        vm.OpenSelectedAudienceProfile();
        Assert.Equal(TrustAudience.Team, vm.SelectedAudience);
        Assert.True(vm.IsAudienceToggleEnabled(AudienceProfileRowKind.WebAccess));

        vm.SelectedAudienceRowIndex.Value = (int)AudienceProfileRowKind.WebAccess;
        vm.ActivateSelectedAudienceProfileRow();

        var team = BindLikeTheDaemon().AudienceProfiles.Team;
        Assert.Equal(
            ToolAudienceProfileToolCatalog.TeamDefaultAllowedTools.Except(ToolAudienceProfileToolCatalog.WebTools),
            team.AllowedTools);
        Assert.Empty(team.McpServerToolGrants!["textforge"]);
        Assert.Equal(ToolApprovalMode.Approval, team.ApprovalPolicy!.McpServerDefaults["textforge"]);
        Assert.Equal(ToolFilesystemMode.Roots, team.ReadFiles.Mode);
        Assert.NotEmpty(team.ChannelAttachments.AllowedCategories);
    }

    [Theory]
    [InlineData("""{ "configVersion": 1 }""", DeploymentPosture.Public)]
    [InlineData("""{ "configVersion": 1, "Security": { "StrictDefaults": false } }""", DeploymentPosture.Personal)]
    public void Absent_posture_reads_the_same_as_the_daemon(string json, DeploymentPosture expected)
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath, json);
        using var vm = new SecurityAccessViewModel(Context.Paths);

        var daemon = PolicyConfiguration.Bind(
            new ConfigurationBuilder().AddNetclawDaemonSources(Context.Paths).Build()).Defaults.DeploymentPosture;

        Assert.Equal(expected, daemon);
        Assert.Equal(daemon, vm.CurrentPosture);
        Assert.Null(vm.PostureConfigWarning);
    }

    private ToolConfig BindLikeTheDaemon()
        => PolicyConfiguration.Bind(new ConfigurationBuilder().AddNetclawDaemonSources(Context.Paths).Build()).Tools;

    [Fact]
    public void Narrowed_team_profile_round_trips_to_the_daemon_binding()
    {
        // Cross-boundary proof: the TUI writes narrowed Team lists, and the daemon sources and
        // binder must produce the same lists. Before the fix, the daemon kept the Team defaults.
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
        vm.SelectedAudienceRowIndex.Value = (int)AudienceProfileRowKind.IncomingAttachments;
        vm.ChangeSelectedAudienceProfileRow(-1); // Images
        vm.ChangeSelectedAudienceProfileRow(-1); // None: [] with zero caps

        var configuration = new ConfigurationBuilder().AddNetclawDaemonSources(Context.Paths).Build();
        var bound = PolicyConfiguration.Bind(configuration);
        var toolConfig = bound.Tools;
        var warnings = bound.ToolWarnings;

        var team = ToolAudienceProfileDefaults.GetResolvedProfile(toolConfig.AudienceProfiles, TrustAudience.Team);
        Assert.Equal(
            ToolAudienceProfileToolCatalog.TeamDefaultAllowedTools.Except(ToolAudienceProfileToolCatalog.WebTools),
            team.AllowedTools);
        Assert.Empty(team.ChannelAttachments.AllowedCategories);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Audience_profile_toggle_from_all_mode_materializes_profile_managed_allowlist()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Personal" }
            }
            """);

        using var vm = new SecurityAccessViewModel(Context.Paths);
        vm.SelectedAudienceIndex.Value = 0;
        vm.OpenSelectedAudienceProfile();
        vm.SelectedAudienceRowIndex.Value = (int)AudienceProfileRowKind.WebAccess;

        vm.ActivateSelectedAudienceProfileRow();

        var config = ConfigFileHelper.LoadJsonDict(Context.Paths.NetclawConfigPath);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Tools.AudienceProfiles.Personal.ToolsMode", out var mode));
        Assert.Equal("Allowlist", mode);
        Assert.True(ConfigFileHelper.TryGetPathValue(config, "Tools.AudienceProfiles.Personal.AllowedTools", out var tools));
        var allowedTools = Assert.IsAssignableFrom<object[]>(tools).Select(static tool => tool?.ToString() ?? string.Empty).ToArray();
        var expected = ToolAudienceProfileToolCatalog.ProfileManagedTools
            .Except(ToolAudienceProfileToolCatalog.WebTools)
            .ToArray();

        Assert.Equal(expected, allowedTools);
        Assert.Contains(ToolAudienceProfileToolCatalog.ShellExecute, allowedTools);
        Assert.Contains(ToolAudienceProfileToolCatalog.SetWebhook, allowedTools);
        Assert.DoesNotContain(ToolAudienceProfileToolCatalog.WebSearch, allowedTools);
        Assert.DoesNotContain(ToolAudienceProfileToolCatalog.WebFetch, allowedTools);
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
    public void Reset_to_posture_default_clears_hidden_mcp_and_approval_settings()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Team" },
              "Tools": {
                "AudienceProfiles": {
                  "Team": {
                    "ToolsMode": "Allowlist",
                    "AllowedTools": ["file_read", "file_list", "attach_file"],
                    "McpServersMode": "All",
                    "AllowedMcpServers": ["memorizer"],
                    "McpServerToolGrants": {
                      "memorizer": ["search_memories", "get"]
                    },
                    "ApprovalPolicy": {
                      "DefaultMode": "Deny",
                      "ToolOverrides": {
                        "shell_execute": "Approval"
                      }
                    }
                  }
                }
              }
            }
            """);

        using var vm = new SecurityAccessViewModel(Context.Paths);
        vm.SelectedAudienceIndex.Value = 1;
        vm.OpenSelectedAudienceProfile();
        vm.SelectedAudienceRowIndex.Value = (int)AudienceProfileRowKind.ResetToDefault;

        vm.ActivateSelectedAudienceProfileRow();

        var root = JsonSerializer.Deserialize<SecurityAccessConfigRoot>(
            File.ReadAllText(Context.Paths.NetclawConfigPath),
            JsonDefaults.ConfigRead);
        Assert.NotNull(root);
        var team = root.Tools.AudienceProfiles.Team;
        Assert.Contains(ToolAudienceProfileToolCatalog.WebSearch, team.AllowedTools);
        Assert.Equal(ToolProfileMode.Allowlist, team.McpServersMode);
        Assert.Null(team.McpServerToolGrants);
        Assert.Null(team.ApprovalPolicy);
        Assert.Empty(team.AllowedMcpServers);
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
    public void Toggle_selected_feature_surfaces_save_failure_and_rolls_back_without_crashing()
    {
        File.WriteAllText(Context.Paths.NetclawConfigPath,
            """
            {
              "configVersion": 1,
              "Security": { "DeploymentPosture": "Team" },
              "Search": { "Enabled": false }
            }
            """);

        using var vm = new SecurityAccessViewModel(Context.Paths);
        vm.SelectedFeatureIndex.Value = 1;
        var before = vm.IsFeatureEnabled(1);

        // Force the ConfigEditorSession write to fail the way a disk-full / permission-denied failure
        // would: AtomicFile cannot replace a path that is a directory. LoadJsonDict treats the
        // directory as "missing" (File.Exists is false), so only the Save() write throws — matching
        // the real bug where the toggle's session.Save() was unguarded.
        ReplaceConfigFileWithDirectory();

        // Must not throw into the Termina event loop.
        vm.ToggleSelectedFeature();

        Assert.Contains("Failed to save", vm.StatusMessage.Value, StringComparison.OrdinalIgnoreCase);
        // The in-memory flip rolled back: a toggle that never reached disk must not stick.
        Assert.Equal(before, vm.IsFeatureEnabled(1));
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

    private void ReplaceConfigFileWithDirectory()
    {
        if (File.Exists(Context.Paths.NetclawConfigPath))
            File.Delete(Context.Paths.NetclawConfigPath);
        Directory.CreateDirectory(Context.Paths.NetclawConfigPath);
    }

    private sealed class SecurityAccessConfigRoot
    {
        public ToolConfig Tools { get; set; } = new();
    }
}
