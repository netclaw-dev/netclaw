using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class ChannelsStepViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WizardContext _context;

    public ChannelsStepViewModelTests()
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
    public void IsApplicable_True_WhenChatServicesEnabled()
    {
        _context.AnyChatServicesEnabled = true;
        using var step = new ChannelsStepViewModel();
        Assert.True(step.IsApplicable(_context));
    }

    [Fact]
    public void IsApplicable_False_WhenNoChatServices()
    {
        _context.AnyChatServicesEnabled = false;
        using var step = new ChannelsStepViewModel();
        Assert.False(step.IsApplicable(_context));
    }

    [Fact]
    public void OnEnter_Forward_PopulatesChannelEntries()
    {
        _context.SelectedPosture = DeploymentPosture.Team;
        using var step = new ChannelsStepViewModel();

        step.OnEnter(_context, NavigationDirection.Forward);

        // Should have at least a DM entry
        Assert.NotEmpty(_context.ChannelEntries);
        Assert.True(_context.ChannelEntries[0].IsDmRow);
    }

    [Fact]
    public void SubStepCount_IsOne()
    {
        using var step = new ChannelsStepViewModel();
        Assert.Equal(1, step.SubStepCount);
    }

    [Fact]
    public void TryAdvance_ReturnsFalse()
    {
        using var step = new ChannelsStepViewModel();
        Assert.False(step.TryAdvance());
    }
}
