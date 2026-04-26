using Netclaw.Channels.Slack;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Discord;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using R3;
using Termina.Clipboard;
using Termina.Input;
using Termina.Reactive;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Reactive ViewModel for the <c>netclaw init</c> onboarding wizard.
/// Thin wrapper that delegates step sequencing to <see cref="WizardOrchestrator"/>
/// and step-specific state to individual <see cref="IWizardStepViewModel"/> instances.
/// </summary>
public partial class InitWizardViewModel : ReactiveViewModel
{
    private readonly WizardContext _context;
    private readonly WizardOrchestrator _orchestrator;
    private readonly Dictionary<string, IWizardStepView> _stepViews;
    private readonly HealthCheckStepViewModel _healthCheckStep;

    /// <summary>The wizard orchestrator managing step sequencing.</summary>
    public WizardOrchestrator Orchestrator => _orchestrator;

    /// <summary>Shared wizard context.</summary>
    public WizardContext Context => _context;

    /// <summary>Step views keyed by step ID.</summary>
    public IReadOnlyDictionary<string, IWizardStepView> StepViews => _stepViews;

    /// <summary>Whether the health check has completed (reactive for key bindings).</summary>
    public ReactiveProperty<bool> IsComplete => _healthCheckStep.IsComplete;

    /// <summary>The health check step VM, exposed for page subscriptions.</summary>
    internal HealthCheckStepViewModel HealthCheckStep => _healthCheckStep;

    /// <summary>The provider step VM, exposed for page subscriptions.</summary>
    internal ProviderStepViewModel ProviderStep { get; }

    public InitWizardViewModel(
        NetclawPaths paths,
        ProviderDescriptorRegistry registry,
        ISlackProbe slackProbe,
        IDiscordProbe discordProbe,
        ChatNavigationState? navigationState = null,
        DeviceFlowServiceFactory? oauthFactory = null,
        DaemonManager? daemonManager = null,
        DaemonApi? daemonApi = null,
        IClipboardService? clipboardService = null)
        : this(paths, registry, registry, slackProbe, discordProbe,
            navigationState: navigationState,
            oauthFactory: oauthFactory, daemonManager: daemonManager, daemonApi: daemonApi,
            clipboardService: clipboardService)
    {
    }

    /// <summary>
    /// Test constructor allowing a separate probe implementation from the registry.
    /// </summary>
    internal InitWizardViewModel(
        NetclawPaths paths,
        ProviderDescriptorRegistry registry,
        IProviderProbe probe,
        ISlackProbe slackProbe,
        IDiscordProbe discordProbe,
        ChatNavigationState? navigationState = null,
        DeviceFlowServiceFactory? oauthFactory = null,
        DaemonManager? daemonManager = null,
        DaemonApi? daemonApi = null,
        IClipboardService? clipboardService = null)
    {
        // Create shared context
        _context = new WizardContext
        {
            Paths = paths,
            Registry = registry,
            RequestRedraw = RequestRedraw
        };

        // Create step VMs in the canonical order:
        // provider -> security-posture -> channel-picker -> channels -> search -> browser-automation -> identity -> external-skills -> exposure-mode -> health-check
        ProviderStep = new ProviderStepViewModel(registry, probe, oauthFactory);
        var securityPostureStep = new SecurityPostureStepViewModel();
        var exposureModeStep = new ExposureModeStepViewModel();
        var channelPickerStep = new ChannelPickerStepViewModel(slackProbe, discordProbe);
        var channelsStep = new ChannelsStepViewModel();
        var searchStep = new SearchStepViewModel();
        var browserStep = new BrowserAutomationStepViewModel();
        var identityStep = new IdentityStepViewModel();
        var externalSkillsStep = new ExternalSkillsStepViewModel();
        _healthCheckStep = new HealthCheckStepViewModel(daemonManager, daemonApi, navigationState);

        var steps = new List<IWizardStepViewModel>
        {
            ProviderStep,
            securityPostureStep,
            channelPickerStep,
            channelsStep,
            searchStep,
            browserStep,
            identityStep,
            externalSkillsStep,
            exposureModeStep,
            _healthCheckStep
        };

        // Wire Navigate callback: set the onboarding trigger on navigation state
        // before delegating to the ViewModel's Navigate delegate.
        _healthCheckStep.Navigate = route =>
        {
            if (navigationState is not null)
                navigationState.InitialMessage = identityStep.BuildOnboardingTrigger(paths);
            Navigate?.Invoke(route);
        };

        // Create orchestrator
        _orchestrator = new WizardOrchestrator(steps, _context);

        // Create step views
        _stepViews = new Dictionary<string, IWizardStepView>
        {
            ["provider"] = new ProviderStepView(clipboardService),
            ["security-posture"] = new SecurityPostureStepView(),
            ["exposure-mode"] = new ExposureModeStepView(),
            ["channel-picker"] = new ChannelPickerStepView(),
            ["channels"] = new ChannelsStepView(),
            ["search"] = new SearchStepView(),
            ["browser-automation"] = new BrowserAutomationStepView(),
            ["identity"] = new IdentityStepView(),
            ["external-skills"] = new ExternalSkillsStepView(),
            ["health-check"] = new HealthCheckStepView()
        };
    }

    public override void OnActivated()
    {
        base.OnActivated();

        Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleGlobalKey)
            .DisposeWith(Subscriptions);
    }

    /// <summary>
    /// Advance the wizard. On the health check step, triggers RunWithOrchestrator.
    /// Calls <see cref="OnStepContentChanged"/> after navigation so the Page can
    /// invalidate its layout nodes (sub-step changes don't update CurrentStepIndex).
    /// </summary>
    public void GoNext()
    {
        if (_orchestrator.CurrentStep is HealthCheckStepViewModel healthStep)
        {
            if (!healthStep.IsRunning.Value && !healthStep.IsComplete.Value)
                healthStep.StartWithOrchestrator(_orchestrator);
            return;
        }

        _orchestrator.GoNext();
        _context.StatusMessage.Value = "";
        OnStepContentChanged?.Invoke();
        RequestRedraw();
    }

    /// <summary>
    /// Go back in the wizard. Returns false if at the beginning (caller should quit).
    /// </summary>
    public bool GoBack()
    {
        if (!_orchestrator.GoBack())
            return false; // at the very beginning

        _context.StatusMessage.Value = "";
        OnStepContentChanged?.Invoke();
        RequestRedraw();
        return true;
    }

    /// <summary>
    /// Invoked after any navigation (step or sub-step change) so the Page can
    /// invalidate its DynamicLayoutNodes. Wired by the Page in OnBound.
    /// </summary>
    public Action? OnStepContentChanged { get; set; }

    /// <summary>
    /// Request application quit.
    /// </summary>
    public void RequestQuit()
    {
        Shutdown();
    }

    private void HandleGlobalKey(KeyPressed key)
    {
        if (key.KeyInfo.Key == ConsoleKey.Q &&
            key.KeyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            Shutdown();
        }
    }

    public override void Dispose()
    {
        _orchestrator.Dispose();
        _context.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Result of a single health check probe.
/// </summary>
/// <param name="Label">Display text.</param>
/// <param name="Passed">Null while running, true/false when complete.</param>
public sealed record HealthCheckItem(string Label, bool? Passed);

/// <summary>
/// A channel entry in the Channels wizard step with editable audience.
/// </summary>
public sealed class ChannelEntry
{
    public string DisplayName { get; set; }
    public string Id { get; }
    public TrustAudience Audience { get; set; }
    public bool IsDmRow { get; }

    public ChannelEntry(string displayName, string id, TrustAudience audience, bool isDmRow = false)
    {
        DisplayName = displayName;
        Id = id;
        Audience = audience;
        IsDmRow = isDmRow;
    }
}
