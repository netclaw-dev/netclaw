using Netclaw.Actors.Channels;
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
        _context.ChannelEntries[ChannelType.Slack] =
        [
            new ChannelEntry("#general", "C123", TrustAudience.Team),
            new ChannelEntry("DMs", "dm", TrustAudience.Personal, isDmRow: true)
        ];
        _context.ChannelEntries[ChannelType.Tui] =
        [
            new ChannelEntry("#dev-chat", "123456", TrustAudience.Team)
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

        step.AddEntry(ChannelType.Slack, new ChannelEntry("#random", "random", TrustAudience.Team));

        Assert.Single(_context.ChannelEntries[ChannelType.Slack]);
        Assert.Equal("#random", _context.ChannelEntries[ChannelType.Slack][0].DisplayName);
    }

    [Fact]
    public void RemoveEntry_RemovesFromCorrectBucket()
    {
        var entry = new ChannelEntry("#general", "C123", TrustAudience.Team);
        _context.ChannelEntries[ChannelType.Slack] = [entry];

        using var step = new ChannelsStepViewModel();
        step.OnEnter(_context, NavigationDirection.Forward);

        Assert.True(step.RemoveEntry(entry));
        Assert.Empty(_context.ChannelEntries[ChannelType.Slack]);
    }

    [Fact]
    public void GetSource_ReturnsCorrectSource()
    {
        var slackEntry = new ChannelEntry("#general", "C123", TrustAudience.Team);
        var discordEntry = new ChannelEntry("#dev", "123", TrustAudience.Team);
        _context.ChannelEntries[ChannelType.Slack] = [slackEntry];
        _context.ChannelEntries[ChannelType.Tui] = [discordEntry];

        using var step = new ChannelsStepViewModel();
        step.OnEnter(_context, NavigationDirection.Forward);

        Assert.Equal(ChannelType.Slack, step.GetSource(slackEntry));
        Assert.Equal(ChannelType.Tui, step.GetSource(discordEntry));
    }

    [Fact]
    public void GetPreferredAddSource_ReturnsOnlyConfiguredSource()
    {
        _context.ChannelEntries[ChannelType.Discord] = [new ChannelEntry("Discord DMs", "dm", TrustAudience.Team, true)];

        using var step = new ChannelsStepViewModel();
        step.OnEnter(_context, NavigationDirection.Forward);

        Assert.Equal(ChannelType.Discord, step.GetPreferredAddSource());
    }

    [Fact]
    public void GetPreferredAddSource_PrefersSlack_WhenMultipleSourcesExist()
    {
        _context.ChannelEntries[ChannelType.Discord] = [new ChannelEntry("Discord DMs", "dm", TrustAudience.Team, true)];
        _context.ChannelEntries[ChannelType.Slack] = [new ChannelEntry("DMs", "dm", TrustAudience.Team, true)];

        using var step = new ChannelsStepViewModel();
        step.OnEnter(_context, NavigationDirection.Forward);

        Assert.Equal(ChannelType.Slack, step.GetPreferredAddSource());
    }
}
