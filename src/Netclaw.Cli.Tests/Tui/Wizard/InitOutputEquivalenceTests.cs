// -----------------------------------------------------------------------
// <copyright file="InitOutputEquivalenceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

/// <summary>
/// <c>netclaw init</c> writes the posture, not the audience profiles. The daemon computes the
/// profiles from the posture. These tests prove that a new install gets the same effective
/// configuration as before, and that init does not write a copy of a default.
/// </summary>
/// <remarks>
/// The InitOutput files hold the netclaw.json that init wrote before this change, from the same
/// steps as <see cref="RunInit"/>. Do not regenerate them from the current code.
/// </remarks>
public sealed class InitOutputEquivalenceTests : WizardStepTestBase
{
    private readonly DisposableTempDir _oldInstall = new();

    public override void Dispose()
    {
        _oldInstall.Dispose();
        base.Dispose();
    }

    public static TheoryData<DeploymentPosture, bool> PostureCases()
    {
        var cases = new TheoryData<DeploymentPosture, bool>();
        foreach (var posture in (DeploymentPosture[])[DeploymentPosture.Personal, DeploymentPosture.Team, DeploymentPosture.Public])
        {
            // Init writes StrictDefaults: true. The false rows cover a later hand edit.
            cases.Add(posture, true);
            cases.Add(posture, false);
        }

        return cases;
    }

    // The gate for this change: for each posture, the daemon binds the same full Security and
    // Tools object graph from the new init output as from the old init output.
    [Theory]
    [MemberData(nameof(PostureCases))]
    public void New_init_output_binds_to_the_same_daemon_config_as_old_init_output(DeploymentPosture posture, bool strictDefaults)
    {
        RunInit(posture);
        var newPaths = Context.Paths;
        SetStrictDefaults(newPaths, strictDefaults);

        var oldPaths = new NetclawPaths(_oldInstall.Path);
        oldPaths.EnsureDirectoriesExist();
        File.Copy(OldInitOutputPath(posture), oldPaths.NetclawConfigPath);
        SetStrictDefaults(oldPaths, strictDefaults);

        var before = BindLikeTheDaemon(oldPaths);
        var after = BindLikeTheDaemon(newPaths);

        Assert.Equal(posture, after.Defaults.DeploymentPosture);
        Assert.Equal(before.Defaults, after.Defaults);
        Assert.Equal(JsonSerializer.Serialize(before.Security), JsonSerializer.Serialize(after.Security));
        Assert.Equal(JsonSerializer.Serialize(before.Tools), JsonSerializer.Serialize(after.Tools));
        Assert.Equal(before.ToolWarnings, after.ToolWarnings);
        Assert.Empty(after.ToolWarnings);
    }

    // Guard: init writes intent only. Tools holds only the shell mode, and no step writes an
    // audience profile or a default list anywhere in netclaw.json.
    [Theory]
    [InlineData(DeploymentPosture.Personal, "HostAllowed")]
    [InlineData(DeploymentPosture.Team, "Off")]
    [InlineData(DeploymentPosture.Public, "Off")]
    public void Init_writes_no_audience_profiles(DeploymentPosture posture, string shellMode)
    {
        RunInit(posture);

        var config = ReadConfig(Context.Paths);
        var tools = Assert.IsType<JsonObject>(config["Tools"]);
        Assert.Equal(["ShellMode"], tools.Select(property => property.Key));
        Assert.Equal(shellMode, tools["ShellMode"]!.GetValue<string>());
        var text = config.ToJsonString();
        Assert.DoesNotContain("AudienceProfiles", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AllowedTools", text, StringComparison.OrdinalIgnoreCase);
    }

    // A second init on an old install resets the profiles by deleting them, not by writing the
    // defaults again. The old init replaced the whole Tools section with the defaults.
    [Fact]
    public void Init_on_an_old_install_deletes_the_stored_profiles()
    {
        File.Copy(OldInitOutputPath(DeploymentPosture.Team), Context.Paths.NetclawConfigPath);

        RunInit(DeploymentPosture.Personal);

        var tools = Assert.IsType<JsonObject>(ReadConfig(Context.Paths)["Tools"]);
        Assert.Null(tools["AudienceProfiles"]);
        var personal = BindLikeTheDaemon(Context.Paths).Tools.AudienceProfiles.Personal;
        Assert.True(personal.ApprovalPolicy!.TryGetExplicitMode(ToolAudienceProfileToolCatalog.ShellExecute, out var mode));
        Assert.Equal(ToolApprovalMode.Approval, mode);
    }

    private void RunInit(DeploymentPosture posture)
    {
        var identity = new IdentityStepViewModel();
        var security = new SecurityPostureStepViewModel();
        var features = new FeatureSelectionStepViewModel();
        security.OnEnter(Context, NavigationDirection.Forward);
        security.SelectedPosture = posture;
        security.OnLeave();
        Context.SelectedPosture = posture;
        features.OnEnter(Context, NavigationDirection.Forward);
        features.OnLeave();
        identity.AgentName = "Netclaw";
        identity.UserTimezone = "UTC";

        using var orchestrator = new WizardOrchestrator([identity, security, features], Context);
        orchestrator.WriteConfig();
    }

    private static PolicyConfiguration BindLikeTheDaemon(NetclawPaths paths)
        => PolicyConfiguration.Bind(new ConfigurationBuilder().AddNetclawDaemonSources(paths).Build());

    private static string OldInitOutputPath(DeploymentPosture posture)
        => Path.Combine(AppContext.BaseDirectory, "Tui", "Wizard", "InitOutput", $"before-init-saves-intent.{posture}.json");

    private static JsonObject ReadConfig(NetclawPaths paths)
        => Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(paths.NetclawConfigPath)));

    private static void SetStrictDefaults(NetclawPaths paths, bool strictDefaults)
    {
        var config = ReadConfig(paths);
        Assert.True(config["Security"]!["StrictDefaults"]!.GetValue<bool>());
        config["Security"]!["StrictDefaults"] = strictDefaults;
        File.WriteAllText(paths.NetclawConfigPath, config.ToJsonString());
    }
}
