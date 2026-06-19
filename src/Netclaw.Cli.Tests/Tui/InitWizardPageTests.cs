// -----------------------------------------------------------------------
// <copyright file="InitWizardPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Cli.Provider;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
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


    // ── Config integrity: wizard choices must match written config ──────────

    /// <summary>
    /// Crash barrier: selecting Personal posture via keyboard, then writing config,
    /// must not produce Enabled=false for any feature subsystem. Reproduces #905.
    /// </summary>
    [Fact]
    public async Task PersonalPosture_WrittenConfig_DoesNotDisableFeatures()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        // Advance to the posture step (provider → identity → security-posture).
        AdvanceToStep(vm, "security-posture");

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

        AdvanceToStep(vm, "security-posture"); // provider → identity → security-posture

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

    [Fact]
    public async Task EnteringHealthCheckStep_StartsValidationWithoutSecondEnter()
    {
        var vm = CreateViewModel();
        try
        {
            AdvanceToStep(vm, WizardStepIds.SecurityPosture);
            var postureStep = Assert.IsType<SecurityPostureStepViewModel>(vm.Orchestrator.CurrentStep);
            postureStep.SelectedPosture = DeploymentPosture.Personal;

            vm.GoNext();

            Assert.Equal(WizardStepIds.HealthCheck, vm.Orchestrator.CurrentStep?.StepId);
            var completion = vm.HealthCheckStep.HealthCheckCompletion;
            Assert.NotNull(completion);

            await completion!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.True(vm.HealthCheckStep.IsComplete.Value);
            Assert.False(vm.HealthCheckStep.IsRunning.Value);
            Assert.Contains(vm.HealthCheckStep.Results, r => r.Label == "Configuration written" && r.Passed == true);
        }
        finally
        {
            vm.Dispose();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Drive the orchestrator forward until the named step is current. Identity sits
    /// between Provider and Security Posture in the bootstrap flow and advances purely
    /// on its sub-step counter, so GoNext walks straight through it.
    /// </summary>
    private static void AdvanceToStep(InitWizardViewModel vm, string stepId)
    {
        for (var i = 0; i < 30 && vm.Orchestrator.CurrentStep?.StepId != stepId; i++)
            vm.Orchestrator.GoNext();
        Assert.Equal(stepId, vm.Orchestrator.CurrentStep?.StepId);
    }

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
                    capturedVm = CreateViewModel();
                    return capturedVm;
                });
        });

        // Resolving TerminaApplication triggers NavigateTo("/init"), which
        // calls the factory above and wires the page to the ViewModel.
        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();

        return (terminal, app, capturedVm!);
    }

    private InitWizardViewModel CreateViewModel()
        => new(_paths, _registry, _fakeProbe, _fakeSlackProbe, _fakeDiscordProbe);
}
