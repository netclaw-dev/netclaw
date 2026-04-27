using Netclaw.Actors.Channels;
using Netclaw.Cli.Discord;
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
    private readonly FakeDiscordProbe _fakeProbe = new();

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
        using var step = new DiscordStepViewModel(_fakeProbe);
        step.DiscordEnabled = false;
        Assert.Equal(1, step.SubStepCount);
    }

    [Fact]
    public void SubStepCount_IsFive_WhenEnabled()
    {
        using var step = new DiscordStepViewModel(_fakeProbe);
        step.DiscordEnabled = true;
        Assert.Equal(5, step.SubStepCount);
    }

    [Fact]
    public void TryAdvance_ThroughAllSubSteps()
    {
        using var step = new DiscordStepViewModel(_fakeProbe);
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
        using var step = new DiscordStepViewModel(_fakeProbe)
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
        using var step = new DiscordStepViewModel(_fakeProbe)
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
        using var step = new DiscordStepViewModel(_fakeProbe)
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

    [Fact]
    public async Task ContributeHealthChecks_ProbeSuccess_ReportsAuthenticated()
    {
        using var step = new DiscordStepViewModel(_fakeProbe)
        {
            DiscordEnabled = true,
            BotToken = "test-token"
        };

        var results = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(results, () => { });

        await step.ContributeHealthChecksAsync(runner, CancellationToken.None);

        Assert.Single(results);
        Assert.True(results[0].Passed);
        Assert.Contains("TestBot", results[0].Label);
        Assert.Equal(1, _fakeProbe.ProbeCallCount);
        Assert.Equal("test-token", _fakeProbe.LastBotToken);
    }

    [Fact]
    public async Task ContributeHealthChecks_ProbeFailure_ReportsError()
    {
        _fakeProbe.NextProbeResult = new DiscordProbeResult(false, "Bot token is invalid.", null);

        using var step = new DiscordStepViewModel(_fakeProbe)
        {
            DiscordEnabled = true,
            BotToken = "bad-token"
        };

        var results = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(results, () => { });

        await step.ContributeHealthChecksAsync(runner, CancellationToken.None);

        Assert.Single(results);
        Assert.False(results[0].Passed);
        Assert.Contains("Bot token is invalid", results[0].Label);
    }

    [Fact]
    public async Task ContributeHealthChecks_ResolvesChannelNames_UpdatesDisplayNames()
    {
        _fakeProbe.NextResolutionResult = new DiscordChannelResolutionResult(
            true, null,
            [new ResolvedDiscordChannel("129847561203948576", "general", "MyServer")],
            []);

        _context.SelectedPosture = DeploymentPosture.Team;
        using var step = new DiscordStepViewModel(_fakeProbe)
        {
            DiscordEnabled = true,
            BotToken = "test-token",
            ChannelIdsInput = "129847561203948576"
        };

        step.OnEnter(_context, NavigationDirection.Forward);
        step.OnLeave();

        var results = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(results, () => { });

        await step.ContributeHealthChecksAsync(runner, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.True(results[1].Passed);
        Assert.Contains("resolved (1)", results[1].Label);

        var entries = _context.ChannelEntries[ChannelType.Discord];
        var channelEntry = entries.First(e => !e.IsDmRow);
        Assert.Equal("MyServer / #general", channelEntry.DisplayName);
    }

    [Fact]
    public async Task ContributeHealthChecks_PartialResolution_ReportsUnresolved()
    {
        _fakeProbe.NextResolutionResult = new DiscordChannelResolutionResult(
            false, null,
            [new ResolvedDiscordChannel("111111111111111111", "general", "MyServer")],
            ["999999999999999999"]);

        _context.SelectedPosture = DeploymentPosture.Team;
        using var step = new DiscordStepViewModel(_fakeProbe)
        {
            DiscordEnabled = true,
            BotToken = "test-token",
            ChannelIdsInput = "111111111111111111,999999999999999999"
        };

        step.OnEnter(_context, NavigationDirection.Forward);
        step.OnLeave();

        var results = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(results, () => { });

        await step.ContributeHealthChecksAsync(runner, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.False(results[1].Passed);
        Assert.Contains("resolved 1/2", results[1].Label);
        Assert.Contains("999999999999999999", results[1].Label);

        var entries = _context.ChannelEntries[ChannelType.Discord];
        var resolvedEntry = entries.First(e => e.Id == "111111111111111111");
        Assert.Equal("MyServer / #general", resolvedEntry.DisplayName);

        var unresolvedEntry = entries.First(e => e.Id == "999999999999999999");
        Assert.Equal("Discord:999999999999999999", unresolvedEntry.DisplayName);
    }
}
