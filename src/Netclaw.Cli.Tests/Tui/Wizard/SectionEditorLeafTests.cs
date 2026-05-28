// -----------------------------------------------------------------------
// <copyright file="SectionEditorLeafTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Provider;
using Netclaw.Cli.Tui.Sections;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class ProviderSectionEditorTests : SectionEditorTestBase<ProviderStepViewModel>
{
    [Fact]
    public void BuildContribution_BlankCredential_PreservesExistingSecret()
    {
        File.WriteAllText(Context.Paths.SecretsPath, """
            { "Providers": { "openai": { "ApiKey": "ENC:stored" } } }
            """);
        using var context = new WizardContext
        {
            Paths = Context.Paths,
            Registry = ProviderCommand.CreateDefaultRegistry(),
            RequestRedraw = () => { },
            ExistingConfig = new Dictionary<string, object>
            {
                ["Models"] = new Dictionary<string, object>
                {
                    ["Main"] = new Dictionary<string, object> { ["Provider"] = "openai", ["ModelId"] = "gpt-4.1" }
                },
                ["Providers"] = new Dictionary<string, object>
                {
                    ["openai"] = new Dictionary<string, object> { ["Type"] = "openai", ["AuthMethod"] = "ApiKey" }
                }
            }
        };

        using var editor = CreateEditor();
        editor.OnEnter(context, NavigationDirection.Forward);
        var contribution = editor.BuildContribution(editor);

        Assert.Contains(contribution.SecretActionsOrEmpty, a => a.Action == SectionSecretActionKind.Preserve);
    }
}

public sealed class IdentitySectionEditorTests : SectionEditorTestBase<IdentityStepViewModel>
{
    [Fact]
    public void BuildContribution_WritesSyntheticIdentityFields()
    {
        using var editor = CreateEditor();
        editor.AgentName = "Netclaw";
        editor.UserTimezone = "UTC";

        var contribution = editor.BuildContribution(editor);

        Assert.Contains(contribution.FieldActionsOrEmpty, a => a.Path == "Identity.AgentName");
        Assert.Contains(contribution.FieldActionsOrEmpty, a => a.Path == "Workspaces.Directory");
    }
}

public sealed class SecurityPostureSectionEditorTests : SectionEditorTestBase<SecurityPostureStepViewModel>
{
    [Fact]
    public void BuildContribution_PersonalPosture_PreservesShellApprovalDefaults()
    {
        using var editor = CreateEditor();
        editor.SelectedPosture = DeploymentPosture.Personal;

        var contribution = editor.BuildContribution(editor);

        Assert.Contains(contribution.FieldActionsOrEmpty, a => a.Path == "Security.DeploymentPosture" && Equals(a.Value, "Personal"));
        Assert.Contains(contribution.FieldActionsOrEmpty, a => a.Path == "Tools");
    }
}

public sealed class FeatureSelectionSectionEditorTests : SectionEditorTestBase<FeatureSelectionStepViewModel>
{
    [Fact]
    public void BuildContribution_EmitsEnabledFlagsForAllFeatureLeaves()
    {
        using var editor = CreateEditor();
        using var context = new WizardContext
        {
            Paths = Context.Paths,
            Registry = Context.Registry,
            RequestRedraw = () => { },
            SelectedPosture = DeploymentPosture.Team
        };
        editor.OnEnter(context, NavigationDirection.Forward);

        var contribution = editor.BuildContribution(editor);

        Assert.Equal(6, contribution.FieldActionsOrEmpty.Count);
        Assert.Contains(contribution.FieldActionsOrEmpty, a => a.Path == "Memory.Enabled");
        Assert.Contains(contribution.FieldActionsOrEmpty, a => a.Path == "Webhooks.Enabled");
    }
}

public sealed class ExposureModeSectionEditorTests : SectionEditorTestBase<ExposureModeStepViewModel>
{
    [Fact]
    public void BuildContribution_ReverseProxy_EmitsExistingDaemonShapeFields()
    {
        using var editor = CreateEditor();
        editor.SelectedMode = ExposureMode.ReverseProxy;
        editor.Host = "10.0.0.5";
        editor.TrustedProxies = ["10.0.0.0/24"];

        var contribution = editor.BuildContribution(editor);

        Assert.Contains(contribution.FieldActionsOrEmpty,
            a => a.Path == "Daemon.ExposureMode" && Equals(a.Value, "reverse-proxy"));
        Assert.Contains(contribution.FieldActionsOrEmpty,
            a => a.Path == "Daemon.Host" && Equals(a.Value, "10.0.0.5"));
        Assert.Contains(contribution.FieldActionsOrEmpty,
            a => a.Path == "Daemon.TrustedProxies" && Assert.IsType<string[]>(a.Value).SequenceEqual(["10.0.0.0/24"]));
    }

    [Fact]
    public void BuildContribution_Local_DropsActiveHostField()
    {
        using var editor = CreateEditor();
        editor.SelectedMode = ExposureMode.Local;

        var contribution = editor.BuildContribution(editor);

        Assert.Contains(contribution.FieldActionsOrEmpty,
            a => a.Path == "Daemon.ExposureMode" && Equals(a.Value, "local"));
        Assert.Contains(contribution.FieldActionsOrEmpty,
            a => a.Path == "Daemon.Host" && a.Action == SectionFieldActionKind.Delete);
    }
}
