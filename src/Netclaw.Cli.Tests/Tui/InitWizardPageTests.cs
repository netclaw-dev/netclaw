// -----------------------------------------------------------------------
// <copyright file="InitWizardPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Channels;
using Netclaw.Cli.Provider;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Tests.Utilities;
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
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeProviderProbe _fakeProbe = new();
    private readonly FakeSlackProbe _fakeSlackProbe = new();
    private readonly FakeDiscordProbe _fakeDiscordProbe = new();
    private readonly ProviderDescriptorRegistry _registry = ProviderCommand.CreateDefaultRegistry();

    public InitWizardPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

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

        // Make Channels step applicable before the picker's OnLeave
        vm.Context.AnyChatServicesEnabled = true;

        // Skip: provider -> security-posture -> feature-selection -> channel-picker -> channels
        vm.Orchestrator.GoNext(); // provider → security-posture
        vm.Orchestrator.GoNext(); // security-posture → feature-selection
        vm.Orchestrator.GoNext(); // feature-selection → channel-picker
        vm.Orchestrator.GoNext(); // channel-picker → channels (additive flag preserved)

        Assert.Equal("channels", vm.Orchestrator.CurrentStep?.StepId);

        // Populate entries for the Channels step to render
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

        // Make Channels step applicable before the picker's OnLeave
        vm.Context.AnyChatServicesEnabled = true;

        // Skip: provider -> security-posture -> feature-selection -> channel-picker -> channels
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

    // ── Channel picker sub-flow key routing ──────────────────────────────────

    /// <summary>
    /// Regression test: entering a valid Slack bot token (xoxb-...) and pressing
    /// Enter must advance to the app token sub-step, not loop back to bot token.
    /// Exercises the full Termina rendering + ChannelPicker sub-flow pipeline.
    /// </summary>
    [Fact]
    public async Task SlackSubFlow_BotTokenSubmit_AdvancesToAppToken()
    {
        var (terminal, app, vm) = CreateHeadlessApp(out var input);

        // Navigate to channel-picker step
        vm.Orchestrator.GoNext(); // provider → security-posture
        vm.Orchestrator.GoNext(); // security-posture → feature-selection
        vm.Orchestrator.GoNext(); // feature-selection → channel-picker
        Assert.Equal("channel-picker", vm.Orchestrator.CurrentStep?.StepId);

        // In picker mode: Enter on Slack (index 0) toggles it on and enters sub-flow
        input.EnqueueKey(ConsoleKey.Enter);

        // Now in Slack sub-flow at bot token (sub-step 1, since enable is skipped).
        // Type a valid token and press Enter to submit.
        input.EnqueueString("xoxb-test-token-12345");
        input.EnqueueKey(ConsoleKey.Enter);

        // If the bug is present, we'd still be on bot token.
        // Ctrl+Q to exit after the advance should have happened.
        input.EnqueueKey(ConsoleKey.Q, control: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        // The Slack VM should have advanced past bot token to app token (sub-step 2)
        var pickerVm = (ChannelPickerStepViewModel)vm.Orchestrator.CurrentStep!;
        var slackVm = (SlackStepViewModel)pickerVm.ActiveAdapterVm!;
        Assert.Equal("xoxb-test-token-12345", slackVm.BotToken);
        Assert.True(terminal.Contains("App Token"),
            $"Expected 'App Token' prompt after submitting bot token. Screen:\n{terminal}");
    }

    /// <summary>
    /// Navigates to channel-picker via keyboard through the security-posture step
    /// (instead of programmatic GoNext), building Termina's focus stack naturally.
    /// The SecurityPostureStepView's SelectionListNode remains on the focus stack
    /// when the Slack sub-flow's TextInputNode takes over — matching the real
    /// terminal scenario where stale focused components may intercept keys.
    /// </summary>
    [Fact]
    public async Task SlackSubFlow_WithFocusStackFromPriorSteps_BotTokenAdvances()
    {
        var (terminal, app, vm) = CreateHeadlessApp(out var input);

        // Skip provider (too many sub-steps to drive via keyboard)
        vm.Orchestrator.GoNext(); // provider -> security-posture

        // Enter on security-posture selects "Personal" (index 0).
        // Personal skips feature-selection, lands on channel-picker.
        // SecurityPostureStepView's SelectionListNode is now stale on the focus stack.
        input.EnqueueKey(ConsoleKey.Enter);

        // Enter on channel-picker toggles Slack on and enters sub-flow.
        // The picker's SelectionListNode is now also stale.
        input.EnqueueKey(ConsoleKey.Enter);

        // Type valid bot token and submit
        input.EnqueueString("xoxb-focus-stack-test");
        input.EnqueueKey(ConsoleKey.Enter);

        input.EnqueueKey(ConsoleKey.Q, control: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var pickerVm = (ChannelPickerStepViewModel)vm.Orchestrator.CurrentStep!;
        var slackVm = (SlackStepViewModel)pickerVm.ActiveAdapterVm!;
        Assert.Equal("xoxb-focus-stack-test", slackVm.BotToken);
        Assert.True(terminal.Contains("App Token"),
            $"Expected 'App Token' prompt after submitting bot token. Screen:\n{terminal}");
    }

    /// <summary>
    /// Full Slack sub-flow traversal: bot token -> app token -> channel names -> DM enabled.
    /// Exercises multiple TextInputNode and SelectionListNode transitions within the sub-flow,
    /// verifying that focus state is correctly managed across sub-step boundaries.
    /// By the time the DM SelectionListNode renders, multiple stale TextInputNodes sit
    /// on the focus stack.
    /// </summary>
    [Fact]
    public async Task SlackSubFlow_FullTraversal_BotTokenThroughDmEnabled()
    {
        var (terminal, app, vm) = CreateHeadlessApp(out var input);

        vm.Orchestrator.GoNext(); // provider -> security-posture

        // Enter: selects Personal, skips feature-selection, lands on channel-picker
        input.EnqueueKey(ConsoleKey.Enter);

        // Enter: toggles Slack on, enters sub-flow at bot token
        input.EnqueueKey(ConsoleKey.Enter);

        // Sub-step 1: Bot token
        input.EnqueueString("xoxb-full-traversal-token");
        input.EnqueueKey(ConsoleKey.Enter);

        // Sub-step 2: App token
        input.EnqueueString("xapp-full-traversal-token");
        input.EnqueueKey(ConsoleKey.Enter);

        // Sub-step 3: Channel names (Enter to skip)
        input.EnqueueKey(ConsoleKey.Enter);

        // Sub-step 4: DM enabled (SelectionListNode, Enter selects first = "Yes")
        input.EnqueueKey(ConsoleKey.Enter);

        input.EnqueueKey(ConsoleKey.Q, control: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var pickerVm = (ChannelPickerStepViewModel)vm.Orchestrator.CurrentStep!;
        var slackVm = (SlackStepViewModel)pickerVm.ActiveAdapterVm!;

        Assert.Equal("xoxb-full-traversal-token", slackVm.BotToken);
        Assert.Equal("xapp-full-traversal-token", slackVm.AppToken);
        Assert.True(slackVm.AllowDirectMessages,
            "Expected DM to be enabled after selecting 'Yes' on the DM sub-step");
    }

    // ── Config integrity: wizard choices must match written config ──────────

    /// <summary>
    /// Crash barrier: selecting Personal posture via keyboard, then writing config,
    /// must not produce Enabled=false for any feature subsystem. Reproduces #905.
    /// </summary>
    [Fact]
    public async Task PersonalPosture_WrittenConfig_DoesNotDisableFeatures()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        // Skip provider step programmatically (too many sub-steps to drive via keyboard)
        vm.Orchestrator.GoNext(); // provider → security-posture

        // Select Personal (index 0) via keyboard — this is the critical decision
        input.EnqueueKey(ConsoleKey.Enter);

        // Ctrl+Q to exit after posture selection routes us past feature-selection
        input.EnqueueKey(ConsoleKey.Q, control: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        // Verify posture was selected correctly
        Assert.Equal(DeploymentPosture.Personal, vm.Context.SelectedPosture);

        // Now write config through the orchestrator (same path the health check step uses)
        vm.Orchestrator.WriteConfig();

        // Read back the written config and verify no features were silently disabled
        var configText = File.ReadAllText(_paths.NetclawConfigPath);
        var config = System.Text.Json.JsonSerializer.Deserialize<
            System.Text.Json.JsonElement>(configText);

        // Webhooks is omitted: Personal posture skips FeatureSelection, so only
        // ExposureModeStep can write Webhooks — and it only does so when enabled.
        string[] featureSections = ["Memory", "Search", "SkillSync", "Scheduling", "SubAgents"];
        foreach (var section in featureSections)
        {
            if (config.TryGetProperty(section, out var sectionObj)
                && sectionObj.TryGetProperty("Enabled", out var enabled))
            {
                Assert.True(enabled.GetBoolean(),
                    $"Section '{section}' has Enabled=false in written config — " +
                    $"Personal posture must not disable features. Full config:\n{configText}");
            }
        }
    }

    /// <summary>
    /// Crash barrier: selecting Team posture and advancing through feature selection
    /// (all defaults = all enabled) must produce Enabled=true for every feature.
    /// </summary>
    [Fact]
    public async Task TeamPosture_DefaultFeatures_AllEnabledInWrittenConfig()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        vm.Orchestrator.GoNext(); // provider → security-posture

        // Select Team (index 1) via keyboard: DownArrow then Enter
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);

        // Now on feature-selection step (Team posture shows it).
        // Team defaults all features ON. Press Enter to accept defaults.
        input.EnqueueKey(ConsoleKey.Enter);

        input.EnqueueKey(ConsoleKey.Q, control: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(DeploymentPosture.Team, vm.Context.SelectedPosture);

        vm.Orchestrator.WriteConfig();

        var configText = File.ReadAllText(_paths.NetclawConfigPath);
        var config = System.Text.Json.JsonSerializer.Deserialize<
            System.Text.Json.JsonElement>(configText);

        string[] featureSections = ["Memory", "Search", "SkillSync", "Scheduling", "SubAgents", "Webhooks"];
        foreach (var section in featureSections)
        {
            Assert.True(config.TryGetProperty(section, out var sectionObj),
                $"Section '{section}' missing from config");
            Assert.True(sectionObj.TryGetProperty("Enabled", out var enabled),
                $"Section '{section}' missing 'Enabled' key");
            Assert.True(enabled.GetBoolean(),
                $"Section '{section}' has Enabled=false — Team defaults should be all-on. " +
                $"Full config:\n{configText}");
        }
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
                        _paths, _registry, _fakeProbe, _fakeSlackProbe, _fakeDiscordProbe);
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
