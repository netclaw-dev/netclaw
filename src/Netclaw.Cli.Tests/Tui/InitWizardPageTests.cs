using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Cli.Provider;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Headless TUI tests for <see cref="InitWizardPage"/> using Termina's
/// <see cref="VirtualTerminal"/> and <see cref="VirtualInputSource"/>.
/// Exercises the full Termina rendering and input-routing pipeline.
/// </summary>
public sealed class InitWizardPageTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;
    private readonly FakeProviderProbe _fakeProbe = new();
    private readonly FakeSlackProbe _fakeSlackProbe = new();
    private readonly ProviderDescriptorRegistry _registry = ProviderCommand.CreateDefaultRegistry();

    public InitWizardPageTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-tuipage-test-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    /// <summary>
    /// Verifies the Termina rendering pipeline: the provider step title and
    /// step indicator must appear in the virtual terminal buffer after startup.
    /// </summary>
    [Fact]
    public async Task ProviderStep_RendersStepTitleToTerminal()
    {
        var (terminal, app, _) = CreateHeadlessApp(out var input);

        // Ctrl+Q immediately — verifies initial render before exit
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("LLM Provider"),
            "Expected step title 'LLM Provider' in terminal output");
        Assert.True(terminal.Contains("Step 1 of"),
            "Expected step indicator 'Step 1 of' in terminal output");
    }

    /// <summary>
    /// Verifies the keyboard input pipeline: Enter on the provider selection list
    /// routes through Termina's input -> page -> SelectionListNode -> ProviderStepViewModel.
    /// KnownTypeKeys is alphabetically sorted; index 0 is "anthropic".
    /// </summary>
    [Fact]
    public async Task EnterOnProviderList_CommitsFirstAlphabeticalProvider()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        // Enter confirms the highlighted item (index 0, alphabetical order), Ctrl+Q exits
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(_registry.KnownTypeKeys[0], vm.ProviderStep.SelectedProviderType);
    }

    /// <summary>
    /// Verifies that arrow key navigation through the selection list works:
    /// Down arrow moves the highlighted item, and Enter on the new position
    /// commits a different provider than the default.
    /// </summary>
    [Fact]
    public async Task DownArrowThenEnter_SelectsSecondProvider()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        // Down moves selection from index 0 to index 1 (next alphabetically)
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(_registry.KnownTypeKeys[1], vm.ProviderStep.SelectedProviderType);
    }

    // ── Channels step key routing (#539) ───────────────────────────────────

    /// <summary>
    /// Verifies that DownArrow reaches the Channels step view through
    /// HandlePageInput, even when a stale SelectionListNode is on the
    /// focus stack from a previous step.
    /// </summary>
    [Fact]
    public async Task ChannelsStep_DownArrow_RendersChannelList()
    {
        var (terminal, app, vm) = CreateHeadlessApp(out var input);

        // Make Channels step applicable
        vm.Context.AnyChatServicesEnabled = true;

        // Skip: provider -> security-posture -> slack -> discord -> channels
        vm.Orchestrator.GoNext(); // provider → security-posture
        vm.Orchestrator.GoNext(); // security-posture → slack
        vm.Orchestrator.GoNext(); // slack -> discord
        vm.Orchestrator.GoNext(); // discord -> channels (Slack/Discord OnLeave can clear entries)

        Assert.Equal("channels", vm.Orchestrator.CurrentStep?.StepId);

        // Populate entries AFTER Slack.OnLeave has run (it removes Slack entries when disabled)
        vm.Context.ChannelEntries[ChannelType.Slack] =
        [
            new ChannelEntry("#general", "C123", TrustAudience.Team),
            new ChannelEntry("#random", "C456", TrustAudience.Team),
        ];

        // Send DownArrow (the key that was broken) then Ctrl+Q to exit
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Q, control: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        // Terminal should contain both channel names
        Assert.True(terminal.Contains("#general"),
            $"Expected #general in terminal. Screen:\n{terminal}");
        Assert.True(terminal.Contains("#random"),
            $"Expected #random in terminal. Screen:\n{terminal}");
    }

    /// <summary>
    /// Verifies that the 'A' key enters add-channel mode on the Channels step.
    /// </summary>
    [Fact]
    public async Task ChannelsStep_AKey_EntersAddMode()
    {
        var (terminal, app, vm) = CreateHeadlessApp(out var input);

        vm.Context.AnyChatServicesEnabled = true;

        // Skip to channels
        vm.Orchestrator.GoNext();
        vm.Orchestrator.GoNext();
        vm.Orchestrator.GoNext();
        vm.Orchestrator.GoNext();

        Assert.Equal("channels", vm.Orchestrator.CurrentStep?.StepId);

        // No entries needed — testing add mode ('A' key should work regardless)

        // Send 'A' key then Ctrl+Q to exit
        input.EnqueueKey(ConsoleKey.A);
        input.EnqueueKey(ConsoleKey.Q, control: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        // Verify the Channels view entered add mode
        var channelsView = (ChannelsStepView)vm.StepViews["channels"];
        Assert.True(channelsView.IsAddMode,
            $"Expected Channels view to be in add mode after pressing 'A'. " +
            $"CurrentStep={vm.Orchestrator.CurrentStep?.StepId}, Screen:\n{terminal}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private (VirtualTerminal Terminal, TerminaApplication App, InitWizardViewModel Vm)
        CreateHeadlessApp(out VirtualInputSource input)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        InitWizardViewModel? capturedVm = null;

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/init", builder =>
        {
            builder.RegisterRoute<InitWizardPage, InitWizardViewModel>(
                "/init",
                _ => new InitWizardPage(),
                _ =>
                {
                    capturedVm = new InitWizardViewModel(
                        _paths, _registry, _fakeProbe, _fakeSlackProbe);
                    return capturedVm;
                });
        });

        // Resolving TerminaApplication triggers NavigateTo("/init"), which
        // calls the factory above and wires the page to the ViewModel.
        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();

        return (terminal, app, capturedVm!);
    }
}
