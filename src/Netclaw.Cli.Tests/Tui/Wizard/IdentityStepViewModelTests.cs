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
    public void SubStepCount_IsFour()
    {
        using var step = new IdentityStepViewModel();
        Assert.Equal(4, step.SubStepCount);
    }

    [Fact]
    public void TryAdvance_ThroughAllSubSteps()
    {
        using var step = new IdentityStepViewModel();

        for (var i = 0; i < step.SubStepCount - 1; i++)
        {
            Assert.True(step.TryAdvance());
            Assert.Equal(i + 1, step.CurrentSubStep);
        }

        // Last sub-step → complete
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
        var last = step.SubStepCount - 1;
        for (var i = 0; i < last; i++)
            step.TryAdvance();

        step.OnEnter(Context, NavigationDirection.Back);
        Assert.Equal(last, step.CurrentSubStep);
    }

    [Fact]
    public void ContributeConfig_SetsIdentitySection()
    {
        using var step = new IdentityStepViewModel();
        step.AgentName = "TestBot";
        step.CommunicationStyle = "Detailed & formal";
        step.UserName = "Alice";
        step.UserTimezone = "America/New_York";

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Identity);
        Assert.Equal("TestBot", builder.Identity!.AgentName);
        Assert.Equal("Detailed & formal", builder.Identity.CommunicationStyle);
        Assert.Equal("Alice", builder.Identity.UserName);
        Assert.Equal("America/New_York", builder.Identity.UserTimezone);

        // Workspaces directory and notification webhooks are post-install settings
        // owned by `netclaw config`; the init Identity step must not contribute them.
        Assert.Null(builder.Workspaces);
        Assert.Null(builder.Notifications);
    }

    [Fact]
    public void WriteIdentityFiles_CreatesSoulAgentsAndTooling()
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

        Assert.True(File.Exists(Context.Paths.AgentsPath));
        var agents = File.ReadAllText(Context.Paths.AgentsPath);
        Assert.Contains("Deployment Mission and Operating Playbook", agents);
        Assert.DoesNotContain("Search Decision Rules", agents);
        Assert.True(File.Exists(Context.Paths.ToolingPath));
    }

    [Fact]
    public void WriteIdentityFiles_PreservesExistingAgentsPlaybook()
    {
        using var step = new IdentityStepViewModel();
        const string existing = "# Mission\nNever skip the customer email review.";
        File.WriteAllText(Context.Paths.AgentsPath, existing);

        step.WriteIdentityFiles(Context.Paths);

        Assert.Equal(existing, File.ReadAllText(Context.Paths.AgentsPath));
    }

    [Fact]
    public void SeedBuiltInAgents_PreservesAnExistingTaskWorkerProfile()
    {
        using var step = new IdentityStepViewModel();
        var taskWorkerPath = Path.Combine(Context.Paths.AgentsDirectory, "task-worker.md");
        const string existing = "---\nname: task-worker\n---\noperator profile\n";
        File.WriteAllText(taskWorkerPath, existing);

        step.SeedBuiltInAgents(Context.Paths);

        Assert.Equal(existing, File.ReadAllText(taskWorkerPath));
    }

    [Fact]
    public void SeedBuiltInAgents_RejectsALinkedAgentDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var step = new IdentityStepViewModel();
        var agentsDirectory = Context.Paths.AgentsDirectory;
        Directory.Delete(agentsDirectory, recursive: true);
        var outside = Path.Combine(Context.Paths.BasePath, "agent-seed-dir-target");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(agentsDirectory, outside);
        try
        {
            var error = Assert.Throws<IOException>(() => step.SeedBuiltInAgents(Context.Paths));

            Assert.Contains("symbolic link", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFiles(outside));
        }
        finally
        {
            Directory.Delete(agentsDirectory);
        }
    }

    [Fact]
    public void SeedBuiltInAgents_RejectsALinkedTaskWorkerTarget()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var step = new IdentityStepViewModel();
        var target = Path.Combine(Context.Paths.BasePath, "agent-seed-target");
        const string existing = "operator-controlled target";
        File.WriteAllText(target, existing);
        var taskWorkerPath = Path.Combine(Context.Paths.AgentsDirectory, "task-worker.md");
        File.CreateSymbolicLink(taskWorkerPath, target);
        try
        {
            var error = Assert.Throws<IOException>(() => step.SeedBuiltInAgents(Context.Paths));

            Assert.Contains("symbolic link", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(existing, File.ReadAllText(target));
        }
        finally
        {
            File.Delete(taskWorkerPath);
        }
    }

    [Fact]
    public void SeedBuiltInAgents_AllowsAnOrdinaryOperatingSystemAncestorAlias()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var step = new IdentityStepViewModel();
        var realRoot = Path.Combine(Context.Paths.BasePath, "agent-seed-real-root");
        Directory.CreateDirectory(realRoot);
        var alias = Path.Combine(Context.Paths.BasePath, "agent-seed-alias");
        Directory.CreateSymbolicLink(alias, realRoot);
        var paths = new NetclawPaths(Path.Combine(alias, "home"));
        try
        {
            step.SeedBuiltInAgents(paths);

            Assert.True(File.Exists(Path.Combine(realRoot, "home", "agents", "task-worker.md")));
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    [Fact]
    public void BuildOnboardingTrigger_SeparatesSoulFromMissionAndRequiresConfirmation()
    {
        using var step = new IdentityStepViewModel();

        var trigger = step.BuildOnboardingTrigger(Context.Paths);

        Assert.Contains(Context.Paths.SoulPath, trigger);
        Assert.Contains(Context.Paths.AgentsPath, trigger);
        Assert.Contains("skill-selection rules", trigger);
        Assert.Contains("ask me to confirm", trigger);
        Assert.Contains("next message", trigger);
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
                }
            }
        };

        step.OnEnter(context, NavigationDirection.Forward);

        Assert.Equal("ExistingBot", step.AgentName);
        Assert.Equal("Detailed & casual", step.CommunicationStyle);
        Assert.Equal("Dana", step.UserName);
        Assert.Equal("UTC", step.UserTimezone);
    }
}
