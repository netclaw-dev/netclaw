using Netclaw.Actors.Channels;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class ChannelPickerStepViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WizardContext _context;
    private readonly FakeSlackProbe _fakeProbe = new();
    private readonly FakeDiscordProbe _fakeDiscordProbe = new();

    public ChannelPickerStepViewModelTests()
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
        _context.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void StepId_IsChannelPicker()
    {
        using var picker = new ChannelPickerStepViewModel(_fakeProbe, _fakeDiscordProbe);
        Assert.Equal("channel-picker", picker.StepId);
    }

    [Fact]
    public void IsApplicable_AlwaysTrue()
    {
        using var picker = new ChannelPickerStepViewModel(_fakeProbe, _fakeDiscordProbe);
        Assert.True(picker.IsApplicable(_context));
    }

    [Fact]
    public void PickerMode_TryAdvance_ReturnsFalse()
    {
        using var picker = new ChannelPickerStepViewModel(_fakeProbe, _fakeDiscordProbe);
        picker.OnEnter(_context, NavigationDirection.Forward);

        Assert.True(picker.IsInPickerMode);
        Assert.False(picker.TryAdvance());
    }

    [Fact]
    public void PickerMode_TryGoBack_ReturnsFalse()
    {
        using var picker = new ChannelPickerStepViewModel(_fakeProbe, _fakeDiscordProbe);
        picker.OnEnter(_context, NavigationDirection.Forward);

        Assert.False(picker.TryGoBack());
    }

    [Fact]
    public void ToggleOn_EntersSubFlow()
    {
        using var picker = new ChannelPickerStepViewModel(_fakeProbe, _fakeDiscordProbe);
        picker.OnEnter(_context, NavigationDirection.Forward);

        picker.ToggleAdapter(0); // Toggle Slack on

        Assert.True(picker.IsInSubFlow);
        Assert.NotNull(picker.ActiveAdapterVm);
        Assert.True(picker.IsAdapterEnabled(0));
    }

    [Fact]
    public void SubFlow_TryAdvance_DelegatesToChild()
    {
        using var picker = new ChannelPickerStepViewModel(_fakeProbe, _fakeDiscordProbe);
        picker.OnEnter(_context, NavigationDirection.Forward);

        picker.ToggleAdapter(0); // Slack sub-flow starts at sub-step 1 (bot token)
        Assert.True(picker.IsInSubFlow);

        // Advance within the Slack sub-flow (sub-step 1 → 2)
        Assert.True(picker.TryAdvance());
        Assert.True(picker.IsInSubFlow);
    }

    [Fact]
    public void SubFlow_Complete_ReturnsToPicker_WithSummary()
    {
        using var picker = new ChannelPickerStepViewModel(_fakeProbe, _fakeDiscordProbe);
        picker.OnEnter(_context, NavigationDirection.Forward);

        picker.ToggleAdapter(0); // Slack sub-flow

        // Advance through all Slack sub-steps (1→2→3→4→5)
        // Sub-step 1: bot token → 2
        Assert.True(picker.TryAdvance());
        // Sub-step 2: app token → 3
        Assert.True(picker.TryAdvance());
        // Sub-step 3: channel names → 4
        Assert.True(picker.TryAdvance());
        // Sub-step 4: DM enabled → 5
        Assert.True(picker.TryAdvance());
        // Sub-step 5: user IDs → complete (TryAdvance returns false from child)
        // Picker captures it and returns to picker mode
        Assert.True(picker.TryAdvance());

        Assert.True(picker.IsInPickerMode);
        Assert.NotNull(picker.GetAdapterSummary(0));
    }

    [Fact]
    public void SubFlow_TryGoBack_AtFirstSubStep_ReturnsToPicker()
    {
        using var picker = new ChannelPickerStepViewModel(_fakeProbe, _fakeDiscordProbe);
        picker.OnEnter(_context, NavigationDirection.Forward);

        picker.ToggleAdapter(0); // Slack sub-flow at sub-step 1
        Assert.True(picker.IsInSubFlow);

        // Go back at first sub-step (1) — should return to picker
        Assert.True(picker.TryGoBack());
        Assert.True(picker.IsInPickerMode);
        // Fresh toggle-on that was cancelled — adapter should be unchecked
        Assert.False(picker.IsAdapterEnabled(0));
    }

    [Fact]
    public void SubFlow_TryGoBack_InMiddle_DelegatesToChild()
    {
        using var picker = new ChannelPickerStepViewModel(_fakeProbe, _fakeDiscordProbe);
        picker.OnEnter(_context, NavigationDirection.Forward);

        picker.ToggleAdapter(0); // Slack sub-flow at sub-step 1
        picker.TryAdvance(); // sub-step 1 → 2

        // Go back in middle — child handles it (sub-step 2 → 1)
        Assert.True(picker.TryGoBack());
        Assert.True(picker.IsInSubFlow); // Still in sub-flow
    }

    [Fact]
    public void ToggleOff_ClearsConfig()
    {
        using var picker = new ChannelPickerStepViewModel(_fakeProbe, _fakeDiscordProbe);
        picker.OnEnter(_context, NavigationDirection.Forward);

        // Complete a full Slack sub-flow
        picker.ToggleAdapter(0);
        for (var i = 0; i < 5; i++) picker.TryAdvance();
        Assert.True(picker.IsInPickerMode);
        Assert.True(picker.IsAdapterEnabled(0));
        Assert.NotNull(picker.GetAdapterSummary(0));

        // Toggle Slack OFF
        picker.ToggleAdapter(0);
        Assert.False(picker.IsAdapterEnabled(0));
        Assert.Null(picker.GetAdapterSummary(0));
    }

    [Fact]
    public void EditAdapter_ReEntersSubFlow()
    {
        using var picker = new ChannelPickerStepViewModel(_fakeProbe, _fakeDiscordProbe);
        picker.OnEnter(_context, NavigationDirection.Forward);

        // Complete Slack sub-flow first
        picker.ToggleAdapter(0);
        for (var i = 0; i < 5; i++) picker.TryAdvance();
        Assert.True(picker.IsInPickerMode);

        // Edit
        picker.EditAdapter(0);
        Assert.True(picker.IsInSubFlow);
    }

    [Fact]
    public void OnLeave_SetsAnyChatServicesEnabled()
    {
        using var picker = new ChannelPickerStepViewModel(_fakeProbe, _fakeDiscordProbe);
        picker.OnEnter(_context, NavigationDirection.Forward);

        // Complete Slack sub-flow
        picker.ToggleAdapter(0);
        for (var i = 0; i < 5; i++) picker.TryAdvance();

        picker.OnLeave();

        Assert.True(_context.AnyChatServicesEnabled);
        Assert.True(_context.ChannelEntries.ContainsKey(ChannelType.Slack));
    }

    [Fact]
    public void OnLeave_NoneEnabled_AnyChatServicesDisabled()
    {
        using var picker = new ChannelPickerStepViewModel(_fakeProbe, _fakeDiscordProbe);
        picker.OnEnter(_context, NavigationDirection.Forward);

        picker.OnLeave();

        Assert.False(_context.AnyChatServicesEnabled);
    }

    [Fact]
    public void OnEnter_Back_ResumesPickerMode()
    {
        using var picker = new ChannelPickerStepViewModel(_fakeProbe, _fakeDiscordProbe);
        picker.OnEnter(_context, NavigationDirection.Forward);

        // Complete Slack sub-flow and leave
        picker.ToggleAdapter(0);
        for (var i = 0; i < 5; i++) picker.TryAdvance();
        picker.OnLeave();

        // Re-enter from back navigation
        picker.OnEnter(_context, NavigationDirection.Back);

        Assert.True(picker.IsInPickerMode);
        Assert.True(picker.IsAdapterEnabled(0));
    }

    [Fact]
    public void ContributeConfig_DelegatesToAllAdapters()
    {
        using var picker = new ChannelPickerStepViewModel(_fakeProbe, _fakeDiscordProbe);
        picker.OnEnter(_context, NavigationDirection.Forward);

        // Configure Slack with a bot token
        picker.ToggleAdapter(0);
        // Set token on the child VM before completing sub-flow
        var slackVm = (SlackStepViewModel)picker.ActiveAdapterVm!;
        slackVm.BotToken = "xoxb-test";
        slackVm.AppToken = "xapp-test";
        for (var i = 0; i < 5; i++) picker.TryAdvance();

        picker.OnLeave();

        var builder = new WizardConfigBuilder(_context.Paths);
        picker.ContributeConfig(builder);

        Assert.NotNull(builder.Slack);
        Assert.True(builder.Slack!.Enabled);
    }

    [Fact]
    public void GetHelpText_PickerMode_ReturnsPickerHelp()
    {
        using var picker = new ChannelPickerStepViewModel(_fakeProbe, _fakeDiscordProbe);
        picker.OnEnter(_context, NavigationDirection.Forward);

        Assert.Contains("channel", picker.GetHelpText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetHelpText_SubFlowMode_DelegatesToChild()
    {
        using var picker = new ChannelPickerStepViewModel(_fakeProbe, _fakeDiscordProbe);
        picker.OnEnter(_context, NavigationDirection.Forward);

        picker.ToggleAdapter(0); // Slack sub-flow

        var help = picker.GetHelpText();
        Assert.Contains("token", help, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CancelSubFlow_OnEdit_PreservesEnabled()
    {
        using var picker = new ChannelPickerStepViewModel(_fakeProbe, _fakeDiscordProbe);
        picker.OnEnter(_context, NavigationDirection.Forward);

        // Complete Slack sub-flow
        picker.ToggleAdapter(0);
        for (var i = 0; i < 5; i++) picker.TryAdvance();
        Assert.True(picker.IsInPickerMode);

        // Re-enter via edit, then cancel
        picker.EditAdapter(0);
        Assert.True(picker.IsInSubFlow);

        // Back out of the edit
        Assert.True(picker.TryGoBack());
        Assert.True(picker.IsInPickerMode);
        // Should still be enabled because it was previously configured
        Assert.True(picker.IsAdapterEnabled(0));
    }
}
