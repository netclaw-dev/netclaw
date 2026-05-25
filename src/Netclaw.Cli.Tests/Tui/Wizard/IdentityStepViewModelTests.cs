// -----------------------------------------------------------------------
// <copyright file="IdentityStepViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class IdentityStepViewModelTests : WizardStepTestBase
{

    [Fact]
    public void SubStepCount_IsSix()
    {
        using var step = new IdentityStepViewModel();
        Assert.Equal(6, step.SubStepCount);
    }

    [Fact]
    public void TryAdvance_ThroughAllSubSteps()
    {
        using var step = new IdentityStepViewModel();

        for (var i = 0; i < 5; i++)
        {
            Assert.True(step.TryAdvance());
            Assert.Equal(i + 1, step.CurrentSubStep);
        }

        // Sub-step 5 → complete
        Assert.False(step.TryAdvance());
    }

    [Fact]
    public void TryGoBack_ThroughSubSteps()
    {
        using var step = new IdentityStepViewModel();
        step.TryAdvance(); // → 1
        step.TryAdvance(); // → 2
        step.TryAdvance(); // → 3

        Assert.True(step.TryGoBack()); // 3 → 2
        Assert.Equal(2, step.CurrentSubStep);

        Assert.True(step.TryGoBack()); // 2 → 1
        Assert.True(step.TryGoBack()); // 1 → 0
        Assert.False(step.TryGoBack()); // at start
    }

    [Fact]
    public void OnEnter_Back_ResumesAtLastSubStep()
    {
        using var step = new IdentityStepViewModel();
        for (var i = 0; i < 5; i++)
            step.TryAdvance();

        step.OnEnter(Context, NavigationDirection.Back);
        Assert.Equal(5, step.CurrentSubStep);
    }

    [Fact]
    public void ContributeConfig_SetsIdentitySection()
    {
        using var step = new IdentityStepViewModel();
        step.AgentName = "TestBot";
        step.CommunicationStyle = "Detailed & formal";
        step.UserName = "Alice";
        step.UserTimezone = "America/New_York";
        step.WebhookUrl = "https://hooks.example.com";

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Identity);
        Assert.Equal("TestBot", builder.Identity!.AgentName);
        Assert.Equal("Detailed & formal", builder.Identity.CommunicationStyle);
        Assert.Equal("Alice", builder.Identity.UserName);
        Assert.NotNull(builder.Notifications);
        Assert.Equal("https://hooks.example.com", builder.Notifications!.WebhookUrl);
    }

    [Fact]
    public void ContributeConfig_NoWebhook_WhenEmpty()
    {
        using var step = new IdentityStepViewModel();
        step.WebhookUrl = null;

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.Null(builder.Notifications);
    }

    [Fact]
    public void WriteIdentityFiles_CreatesSoulAndTooling()
    {
        using var step = new IdentityStepViewModel();
        step.AgentName = "TestBot";
        step.CommunicationStyle = "Concise & casual";
        step.UserName = "Bob";
        step.UserTimezone = "UTC";

        step.WriteIdentityFiles(Context.Paths);

        Assert.True(File.Exists(Context.Paths.SoulPath));
        var soul = File.ReadAllText(Context.Paths.SoulPath);
        Assert.Contains("TestBot", soul);
        Assert.Contains("Bob", soul);
        Assert.Contains("UTC", soul);

        // AGENTS.md is no longer written to disk — it is loaded from embedded
        // resources at runtime per audience. TOOLING.md is still written.
        Assert.False(File.Exists(Context.Paths.AgentsPath));
        Assert.True(File.Exists(Context.Paths.ToolingPath));
    }

    [Fact]
    public void DefaultValues()
    {
        using var step = new IdentityStepViewModel();
        Assert.Equal("Netclaw", step.AgentName);
        Assert.Null(step.CommunicationStyle);
        Assert.Equal(TimeZoneInfo.Local.Id, step.UserTimezone);
    }

    [Fact]
    public void OnEnter_PrefillsFromExistingConfig()
    {
        using var step = new IdentityStepViewModel();
        using var context = new WizardContext
        {
            Paths = Context.Paths,
            Registry = Context.Registry,
            RequestRedraw = () => { },
            ExistingConfig = new Dictionary<string, object>
            {
                ["Identity"] = new Dictionary<string, object>
                {
                    ["AgentName"] = "ExistingBot",
                    ["CommunicationStyle"] = "Detailed & casual",
                    ["UserName"] = "Dana",
                    ["UserTimezone"] = "UTC"
                },
                ["Workspaces"] = new Dictionary<string, object>
                {
                    ["Directory"] = "/tmp/workspaces"
                }
            }
        };

        step.OnEnter(context, NavigationDirection.Forward);

        Assert.Equal("ExistingBot", step.AgentName);
        Assert.Equal("Detailed & casual", step.CommunicationStyle);
        Assert.Equal("Dana", step.UserName);
        Assert.Equal("UTC", step.UserTimezone);
        Assert.Equal("/tmp/workspaces", step.WorkspacesDirectory);
    }
}
