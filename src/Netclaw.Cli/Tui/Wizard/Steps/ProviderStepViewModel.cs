using System.Diagnostics;
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using R3;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for selecting and configuring the LLM provider.
/// Sub-steps: 0=provider selection, 1=auth method, 2=credentials, 3=validation,
/// 4=model selection, 5=OAuth device flow, 6=OAuth browser flow.
/// </summary>
public sealed class ProviderStepViewModel : IWizardStepViewModel
{
    private static readonly TimeSpan ProbeHardTimeout = TimeSpan.FromSeconds(20);

    private readonly IProviderProbe _probe;
    private readonly ProviderDescriptorRegistry _registry;
    private readonly DeviceFlowServiceFactory? _oauthFactory;
    private CancellationTokenSource? _probeCts;
    private WizardContext? _context;

    // Sub-step tracking: these map to the same values as the monolith's _providerSubStep
    private int _currentSubStep;
    private int _highWaterSubStep;

    public ProviderStepViewModel(
        ProviderDescriptorRegistry registry,
        IProviderProbe probe,
        DeviceFlowServiceFactory? oauthFactory = null)
    {
        _registry = registry;
        _probe = probe;
        _oauthFactory = oauthFactory;
        OAuth = new OAuthFlowCoordinator(registry, oauthFactory, null, () => { });
    }

    public string StepId => "provider";
    public string DisplayTitle => "LLM Provider";

    // ── State ──
    public string? SelectedProviderType { get; set; }
    public AuthMethod SelectedAuthMethod { get; set; } = AuthMethod.None;
    public string? ApiKeyInput { get; set; }
    public string? EndpointInput { get; set; }
    public string? SelectedModelId { get; set; }
    public List<DiscoveredModel> DiscoveredModels { get; } = [];
    public OAuthFlowCoordinator OAuth { get; }
    public ProviderDescriptorRegistry Registry => _registry;

    // ── Reactive state ──
    public ReactiveProperty<bool> IsProbing { get; } = new(false);
    public ReactiveProperty<ProviderProbeResult?> ProbeResult { get; } = new(null);
    public ReactiveProperty<int> ProbeElapsedSeconds { get; } = new(0);
    public ReactiveProperty<int> SpinnerTick { get; } = new(0);

    public bool IsApplicable(WizardContext context) => true;

    public int CurrentSubStep => _currentSubStep;
    public int SubStepCount => 5; // min: select → auth → creds → validate → model

    public string GetHelpText() => _currentSubStep switch
    {
        0 => "  Select your LLM provider. Ollama runs locally (no auth required).",
        1 => "  Choose how to authenticate with this provider.",
        2 => "  Enter your API key. It will be stored in secrets.json.",
        3 => "  Validating connection and discovering available models...",
        4 => "  Select the model to use for conversations.",
        5 => "  Complete the authorization in your browser.",
        6 => "  Complete the authorization in your browser.",
        _ => ""
    };

    /// <summary>
    /// Set the sub-step directly (used by the View for non-linear transitions like
    /// OAuth flow selection or probe auto-advance).
    /// </summary>
    public void SetSubStep(int step)
    {
        _currentSubStep = step;
        if (step > _highWaterSubStep)
            _highWaterSubStep = step;
    }

    public bool TryAdvance()
    {
        // Provider step has non-linear sub-step transitions (OAuth branches, probe auto-advance).
        // The View calls SetSubStep directly for most transitions.
        // TryAdvance handles the final model selection → step complete transition.
        return false; // step complete
    }

    public bool TryGoBack()
    {
        if (_currentSubStep == 0)
            return false;

        // Special back-navigation rules from the monolith
        switch (_currentSubStep)
        {
            case 5: // OAuth device flow → auth selection
                OAuth.Cancel();
                _currentSubStep = 1;
                return true;
            case 6: // OAuth browser flow → auth selection
                OAuth.Cancel();
                _currentSubStep = 1;
                return true;
            case 4: // Model selection → credentials
                _currentSubStep = 2;
                return true;
            case 3: // Validation → back to correct input
                CancelProbe();
                _currentSubStep = SelectedAuthMethod switch
                {
                    AuthMethod.OAuthPkce => 6,
                    AuthMethod.OAuthDevice => 5,
                    _ => 2
                };
                return true;
            default:
                _currentSubStep--;
                return true;
        }
    }

    public void OnEnter(WizardContext context, NavigationDirection direction)
    {
        _context = context;
        if (direction == NavigationDirection.Back)
            _currentSubStep = _highWaterSubStep;
    }

    public void OnLeave() { }

    // ── Probing ──

    public void StartProbe()
    {
        CancelProbe();
        ProbeCompletion = ProbeProviderAsync();
    }

    public void CancelProbe()
    {
        if (_probeCts is not null)
        {
            _probeCts.Cancel();
            _probeCts.Dispose();
            _probeCts = null;
        }
    }

    internal Task? ProbeCompletion { get; private set; }

    internal async Task ProbeProviderAsync()
    {
        _probeCts = new CancellationTokenSource();
        var ct = _probeCts.Token;
        var providerType = SelectedProviderType ?? "unknown";
        var credential = ApiKeyInput;
        if (string.IsNullOrWhiteSpace(credential)
            && SelectedAuthMethod is AuthMethod.OAuthDevice or AuthMethod.OAuthPkce
            && OAuth.Result is not null)
        {
            credential = OAuth.Result.AccessToken.Value;
        }

        IsProbing.Value = true;
        ProbeResult.Value = null;
        ProbeElapsedSeconds.Value = 0;

        _ = RunProbeTimerAsync(ct);

        var result = new ProviderProbeResult(false, "Validation failed before probe completed.", []);
        try
        {
            result = await _probe.ProbeAsync(
                    providerType,
                    EndpointInput,
                    credential,
                    SelectedAuthMethod,
                    ct)
                .WaitAsync(ProbeHardTimeout, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = new ProviderProbeResult(false, "Validation cancelled.", []);
        }
        catch (TimeoutException)
        {
            result = new ProviderProbeResult(false,
                $"Validation timed out after {(int)ProbeHardTimeout.TotalSeconds} seconds.", []);
        }
        catch (Exception ex)
        {
            result = new ProviderProbeResult(false, $"Validation failed: {ex.Message}", []);
        }
        finally
        {
            CancelProbe();
        }

        DiscoveredModels.Clear();
        if (result.Success)
            DiscoveredModels.AddRange(result.Models);

        IsProbing.Value = false;
        ProbeResult.Value = result;
    }

    private async Task RunProbeTimerAsync(CancellationToken ct)
    {
        var tickCount = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(120, ct); }
            catch (OperationCanceledException) { return; }

            tickCount++;
            SpinnerTick.Value = tickCount;
            if (tickCount % 8 == 0)
                ProbeElapsedSeconds.Value++;
        }
    }

    // ── OAuth ──

    public void StartOAuthFlow()
    {
        if (SelectedProviderType is null) return;
        ProbeElapsedSeconds.Value = 0;
        var ct = OAuth.StartDeviceFlow(SelectedProviderType, result =>
        {
            ApiKeyInput = result.AccessToken.Value;
            StartProbe();
        });
        _ = RunProbeTimerAsync(ct);
    }

    public void StartBrowserOAuthFlow()
    {
        if (SelectedProviderType is null) return;
        ProbeElapsedSeconds.Value = 0;
        var ct = OAuth.StartBrowserFlow(SelectedProviderType, result =>
        {
            ApiKeyInput = result.AccessToken.Value;
            StartProbe();
        });
        _ = RunProbeTimerAsync(ct);
    }

    public Task SubmitRedirectUrlAsync(string? pastedUrl)
        => OAuth.SubmitRedirectUrlAsync(pastedUrl);

    internal void ClearFromProvider()
    {
        CancelProbe();
        OAuth.Reset();
        SelectedAuthMethod = AuthMethod.None;
        ApiKeyInput = null;
        EndpointInput = null;
        ProbeResult.Value = null;
        ProbeElapsedSeconds.Value = 0;
        SelectedModelId = null;
        DiscoveredModels.Clear();
    }

    // ── Config ──

    public void ContributeConfig(WizardConfigBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(SelectedProviderType))
            return;

        var providerName = SelectedProviderType!.ToLowerInvariant();
        builder.Provider = new ProviderConfigSection
        {
            TypeKey = providerName,
            AuthMethod = SelectedAuthMethod,
            Endpoint = !string.IsNullOrWhiteSpace(EndpointInput)
                ? EndpointInput
                : _registry.TryGet(providerName, out var desc) && desc.Auth is EndpointOnlyAuth
                    ? desc.DefaultEndpoint
                    : null
        };

        builder.Model = new ModelConfigSection
        {
            Provider = providerName,
            ModelId = SelectedModelId
        };
    }

    public void ContributeSecrets(WizardSecretsBuilder builder)
    {
        // Provider credentials use ProviderCredentialWriter which writes directly
        // to disk. This is deferred to WriteProviderCredentials() which the
        // orchestrator calls during finalization (after health checks pass).
    }

    /// <summary>
    /// Write provider credentials to disk. Called by the orchestrator during
    /// config finalization, not during ContributeSecrets, so credentials are
    /// only persisted after health checks pass.
    /// </summary>
    public void WriteProviderCredentials(NetclawPaths paths)
    {
        if (string.IsNullOrWhiteSpace(SelectedProviderType))
            return;

        ProviderCredentialWriter.WriteProvider(
            paths,
            SelectedProviderType!.ToLowerInvariant(),
            SelectedProviderType.ToLowerInvariant(),
            SelectedAuthMethod,
            EndpointInput,
            OAuth.Result,
            ApiKeyInput,
            _registry,
            SensitiveStringTypeConverter.Protector);
    }

    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
    {
        // Provider check
        var providerOk = !string.IsNullOrWhiteSpace(SelectedProviderType);
        var providerLabel = providerOk ? Registry.Get(SelectedProviderType!).DisplayName : "none";
        runner.Add(new HealthCheckItem($"LLM provider configured ({providerLabel})", providerOk));

        // Model check
        var modelOk = !string.IsNullOrWhiteSpace(SelectedModelId);
        runner.Add(new HealthCheckItem(
            modelOk
                ? $"Model selected ({SelectedModelId})"
                : "Model selected (none — will use provider default)",
            true)); // not a hard failure

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        CancelProbe();
        OAuth.Dispose();
        IsProbing.Dispose();
        ProbeResult.Dispose();
        ProbeElapsedSeconds.Dispose();
        SpinnerTick.Dispose();
    }
}
