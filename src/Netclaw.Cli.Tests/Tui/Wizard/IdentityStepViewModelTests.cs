using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class IdentityStepViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WizardContext _context;

    public IdentityStepViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        var paths = new NetclawPaths(_tempDir);
        paths.EnsureDirectoriesExist();
        _context = new WizardContext
        {
            Paths = paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = () => { }
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

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

        step.OnEnter(_context, NavigationDirection.Back);
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

        var builder = new WizardConfigBuilder(_context.Paths);
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

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.Null(builder.Notifications);
    }

    [Fact]
    public void WriteIdentityFiles_CreatesSoulAndAgents()
    {
        using var step = new IdentityStepViewModel();
        step.AgentName = "TestBot";
        step.CommunicationStyle = "Concise & casual";
        step.UserName = "Bob";
        step.UserTimezone = "UTC";

        step.WriteIdentityFiles(_context.Paths);

        Assert.True(File.Exists(_context.Paths.SoulPath));
        var soul = File.ReadAllText(_context.Paths.SoulPath);
        Assert.Contains("TestBot", soul);
        Assert.Contains("Bob", soul);
        Assert.Contains("UTC", soul);

        Assert.True(File.Exists(_context.Paths.AgentsPath));
        var agents = File.ReadAllText(_context.Paths.AgentsPath);
        Assert.Contains("Operating Rules", agents);
    }

    [Fact]
    public void DefaultValues()
    {
        using var step = new IdentityStepViewModel();
        Assert.Equal("Netclaw", step.AgentName);
        Assert.Null(step.CommunicationStyle);
        Assert.Equal(TimeZoneInfo.Local.Id, step.UserTimezone);
    }
}
