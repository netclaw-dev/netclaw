using Microsoft.Extensions.DependencyInjection;
using Netclaw.Cli.Provider;
using Netclaw.Cli.Tui;
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
/// Exercises the full Termina rendering and input-routing pipeline,
/// complementing the ViewModel-level tests in <see cref="InitWizardViewModelTests"/>.
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
    /// routes through Termina's input → page → SelectionListNode → ViewModel.
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

        Assert.Equal(_registry.KnownTypeKeys[0], vm.SelectedProviderType);
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

        Assert.Equal(_registry.KnownTypeKeys[1], vm.SelectedProviderType);
    }

    /// <summary>
    /// Verifies the SecurityPosture step renders posture options when the
    /// ViewModel is set directly to that step.
    /// </summary>
    [Fact]
    public async Task SecurityPostureStep_RendersPostureOptions()
    {
        var (terminal, app, vm) = CreateHeadlessApp(out var input);

        // Jump directly to SecurityPosture step (bypass Provider sub-steps)
        vm.CurrentStep.Value = WizardStep.SecurityPosture;

        // Quit immediately to capture the rendered output
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("Security Posture"),
            "Expected step title 'Security Posture' in terminal output");
        Assert.True(terminal.Contains("Personal"),
            "Expected 'Personal' posture option in terminal output");
        Assert.True(terminal.Contains("Team"),
            "Expected 'Team' posture option in terminal output");
    }

    /// <summary>
    /// Verifies the Channels step renders channel entries when pre-populated.
    /// </summary>
    [Fact]
    public async Task ChannelsStep_RendersChannelEntries()
    {
        var (terminal, app, vm) = CreateHeadlessApp(out var input);

        // Pre-populate channels
        vm.ChannelEntries.Add(new ChannelEntry("DMs", "dm", "personal", isDmRow: true));
        vm.ChannelEntries.Add(new ChannelEntry("#general", "C0AGM484P0Q", "team"));

        // Jump directly to Channels step
        vm.CurrentStep.Value = WizardStep.Channels;

        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("Channels"),
            "Expected step title 'Channels' in terminal output");
        Assert.True(terminal.Contains("DMs"),
            "Expected DMs entry in terminal output");
        Assert.True(terminal.Contains("#general"),
            "Expected #general entry in terminal output");
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
