using Netclaw.Actors.Channels;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class DiscordStepViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WizardContext _context;

    public DiscordStepViewModelTests()
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
    public void SubStepCount_IsOne_WhenDisabled()
    {
        using var step = new DiscordStepViewModel();
        step.DiscordEnabled = false;
        Assert.Equal(1, step.SubStepCount);
    }

    [Fact]
    public void SubStepCount_IsFive_WhenEnabled()
    {
        using var step = new DiscordStepViewModel();
        step.DiscordEnabled = true;
        Assert.Equal(5, step.SubStepCount);
    }

    [Fact]
    public void TryAdvance_ThroughAllSubSteps()
    {
        using var step = new DiscordStepViewModel();
        step.DiscordEnabled = true;

        Assert.True(step.TryAdvance());
        Assert.Equal(1, step.CurrentSubStep);

        Assert.True(step.TryAdvance());
        Assert.Equal(2, step.CurrentSubStep);

        Assert.True(step.TryAdvance());
        Assert.Equal(3, step.CurrentSubStep);

        Assert.True(step.TryAdvance());
        Assert.Equal(4, step.CurrentSubStep);

        Assert.False(step.TryAdvance());
    }

    [Fact]
    public void OnLeave_PopulatesChannelEntries_WhenEnabled()
    {
        _context.SelectedPosture = DeploymentPosture.Team;
        using var step = new DiscordStepViewModel
        {
            DiscordEnabled = true,
            AllowDirectMessages = true,
            ChannelIdsInput = "129847561203948576,130111223344556677"
        };

        step.OnEnter(_context, NavigationDirection.Forward);
        step.OnLeave();

        Assert.True(_context.ChannelEntries.ContainsKey(ChannelType.Discord));
        var entries = _context.ChannelEntries[ChannelType.Discord];
        Assert.Equal(3, entries.Count);
        Assert.True(entries[0].IsDmRow);
        Assert.Equal("129847561203948576", entries[1].Id);
    }

    [Fact]
    public void ContributeConfig_Enabled_SetsDiscordSection()
    {
        using var step = new DiscordStepViewModel
        {
            DiscordEnabled = true,
            AllowDirectMessages = true,
            ChannelIdsInput = "129847561203948576",
            AllowedUserIdsInput = "130111223344556677"
        };

        step.OnEnter(_context, NavigationDirection.Forward);
        step.OnLeave();

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Discord);
        Assert.True(builder.Discord!.Enabled);
        Assert.Equal("129847561203948576", builder.Discord.DefaultChannelId);
        Assert.True(builder.Discord.AllowDirectMessages);
        Assert.Equal("130111223344556677", Assert.Single(builder.Discord.AllowedUserIds!));
    }

    [Fact]
    public async Task ContributeHealthChecks_MissingBotToken_Fails()
    {
        using var step = new DiscordStepViewModel
        {
            DiscordEnabled = true,
            BotToken = null
        };

        var results = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(results, () => { });

        await step.ContributeHealthChecksAsync(runner, CancellationToken.None);

        Assert.Single(results);
        Assert.False(results[0].Passed);
        Assert.Contains("bot token missing", results[0].Label);
    }

    [Fact]
    public void ParseChannelIds_ParsesCommaSeparated()
    {
        var ids = DiscordStepViewModel.ParseChannelIds("123, 456, #789");

        Assert.Equal(3, ids.Count);
        Assert.Equal("123", ids[0]);
        Assert.Equal("456", ids[1]);
        Assert.Equal("789", ids[2]);
    }
}
