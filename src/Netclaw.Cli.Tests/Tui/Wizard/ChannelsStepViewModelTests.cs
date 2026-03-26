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
    public void AllEntries_FlattensAcrossSources()
    {
        _context.ChannelEntries["slack"] =
        [
            new ChannelEntry("#general", "C123", "team"),
            new ChannelEntry("DMs", "dm", "personal", isDmRow: true)
        ];
        _context.ChannelEntries["discord"] =
        [
            new ChannelEntry("#dev-chat", "123456", "team")
        ];

        using var step = new ChannelsStepViewModel();
        step.OnEnter(_context, NavigationDirection.Forward);

        var all = step.AllEntries;
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void AddEntry_AddsToCorrectSourceBucket()
    {
        using var step = new ChannelsStepViewModel();
        step.OnEnter(_context, NavigationDirection.Forward);

        step.AddEntry("slack", new ChannelEntry("#random", "random", "team"));

        Assert.Single(_context.ChannelEntries["slack"]);
        Assert.Equal("#random", _context.ChannelEntries["slack"][0].DisplayName);
    }

    [Fact]
    public void RemoveEntry_RemovesFromCorrectBucket()
    {
        var entry = new ChannelEntry("#general", "C123", "team");
        _context.ChannelEntries["slack"] = [entry];

        using var step = new ChannelsStepViewModel();
        step.OnEnter(_context, NavigationDirection.Forward);

        Assert.True(step.RemoveEntry(entry));
        Assert.Empty(_context.ChannelEntries["slack"]);
    }

    [Fact]
    public void GetSource_ReturnsCorrectSource()
    {
        var slackEntry = new ChannelEntry("#general", "C123", "team");
        var discordEntry = new ChannelEntry("#dev", "123", "team");
        _context.ChannelEntries["slack"] = [slackEntry];
        _context.ChannelEntries["discord"] = [discordEntry];

        using var step = new ChannelsStepViewModel();
        step.OnEnter(_context, NavigationDirection.Forward);

        Assert.Equal("slack", step.GetSource(slackEntry));
        Assert.Equal("discord", step.GetSource(discordEntry));
    }
}
