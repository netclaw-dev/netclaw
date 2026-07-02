// -----------------------------------------------------------------------
// <copyright file="ProviderStepViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Json;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Sections;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using R3;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for selecting and configuring the LLM provider.
/// Sub-steps: 0=provider selection, 1=auth method, 2=credentials, 3=validation,
/// 4=model selection, 5=OAuth device flow, 6=OAuth browser flow,
/// 7=GitHub Copilot host mode, 8=GitHub Enterprise host, 9=GitHub Enterprise API base.
/// </summary>
public sealed class ProviderStepViewModel : IWizardStepViewModel, ISectionEditor
{
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
        DeviceFlowServiceFactory? oauthFactory = null,
        DaemonApi? daemonApi = null)
    {
        _registry = registry;
        _probe = probe;
        _oauthFactory = oauthFactory;
        OAuth = new OAuthFlowCoordinator(registry, oauthFactory, daemonApi, () => { });
    }

    public string StepId => WizardStepIds.Provider;
    public string DisplayTitle => "LLM Provider";
    public string SectionId => StepId;
    public string DisplayName => DisplayTitle;
    public string? Category => null;
    public bool ShowInMenu => false;
    public IReadOnlyList<string> RelevantDoctorChecks => ["Config Schema", "Context Window"];

    // ── State ──
    public string? SelectedProviderType { get; set; }
    public AuthMethod SelectedAuthMethod { get; set; } = AuthMethod.None;
    public string? ApiKeyInput { get; set; }
    public string? EndpointInput { get; set; }
    public IReadOnlyDictionary<string, object?>? VendorOptions { get; set; }
    public GitHubCopilotAuthHostMode GitHubCopilotHostMode { get; set; } = GitHubCopilotAuthHostMode.GitHubCom;
    public string? GitHubCopilotHostInput { get; set; }
    public string? GitHubCopilotApiBaseInput { get; set; }
    public string? SelectedModelId { get; set; }
    public bool HasStoredCredential { get; private set; }
    public List<DiscoveredModel> DiscoveredModels { get; } = [];
    public OAuthFlowCoordinator OAuth { get; }
    public ProviderDescriptorRegistry Registry => _registry;

    // ── Reactive state ──
    public ReactiveProperty<bool> IsProbing { get; } = new(false);
    public ReactiveProperty<ProviderProbeResult?> ProbeResult { get; } = new(null);
    public ReactiveProperty<int> ProbeElapsedSeconds { get; } = new(0);

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
        7 => "  Choose whether GitHub Copilot should authenticate through GitHub.com or GitHub Enterprise.",
        8 => "  Enter the GitHub Enterprise web host used for OAuth.",
        9 => "  Enter the GitHub Enterprise API base, or leave blank to use the derived default.",
        _ => ""
    };

    /// <summary>
    /// Set the sub-step directly (used by the View for non-linear transitions like
    /// OAuth flow selection or probe auto-advance).
    /// </summary>
    public void SetSubStep(int step)
    {
        _currentSubStep = step;
        if (IsResumeSubStep(step) && step > _highWaterSubStep)
            _highWaterSubStep = step;
    }

    private static bool IsResumeSubStep(int step) => step is >= 0 and <= 4;

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
                _currentSubStep = GitHubCopilotSetupFlow.IsGitHubCopilot(SelectedProviderType) ? 7 : 1;
                return true;
            case 6: // OAuth browser flow → auth selection
                OAuth.Cancel();
                _currentSubStep = 1;
                return true;
            case 7: // GitHub Copilot host mode → auth selection
                _currentSubStep = 1;
                return true;
            case 8: // GitHub Enterprise host → host mode
                _currentSubStep = 7;
                return true;
            case 9: // GitHub Enterprise API base → GitHub Enterprise host
                _currentSubStep = 8;
                return true;
            case 4: // Model selection → credentials
                _currentSubStep = SelectedAuthMethod switch
                {
                    AuthMethod.OAuthDevice when GitHubCopilotSetupFlow.IsGitHubCopilot(SelectedProviderType) => 7,
                    AuthMethod.OAuthDevice => 5,
                    AuthMethod.OAuthPkce => 6,
                    _ => 2,
                };
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
        PrefillFromExistingConfig(context);
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
        // Atomically take ownership of the active CTS so a concurrently-completing probe's finally
        // cannot also cancel/dispose it (double dispose, or cancelling a newer probe's live CTS).
        var cts = Interlocked.Exchange(ref _probeCts, null);
        cts?.Cancel();
        cts?.Dispose();
    }

    internal Task? ProbeCompletion { get; private set; }

    internal async Task ProbeProviderAsync()
    {
        var cts = new CancellationTokenSource();
        _probeCts = cts;
        var ct = cts.Token;
        var providerType = SelectedProviderType ?? "unknown";
        var probeEntry = BuildProbeEntry(providerType);

        IsProbing.Value = true;
        ProbeResult.Value = null;
        ProbeElapsedSeconds.Value = 0;

        _ = RunProbeTimerAsync(ct);

        var result = new ProviderProbeResult(false, "Validation failed before probe completed.", []);
        try
        {
            // Outer wall-clock for the WHOLE probe. The descriptor's own per-request
            // deadline covers only the /models call; it does NOT cover pre-request work
            // such as OAuth token exchange, which would otherwise be bounded only by the
            // HttpClient default (~100s). This budget is deliberately larger than the
            // descriptor's self-hosted deadline (see ProbeTimeouts.InteractiveWallClock)
            // so it bounds a hung token exchange without truncating a legitimately slow
            // self-hosted /models probe — the truncation that was the heart of #1292.
            result = await _probe.ProbeAsync(probeEntry, ct)
                .WaitAsync(ProbeTimeouts.InteractiveWallClock, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = new ProviderProbeResult(false, "Validation cancelled.", []);
        }
        catch (TimeoutException)
        {
            result = new ProviderProbeResult(false,
                $"Validation timed out after {(int)ProbeTimeouts.InteractiveWallClock.TotalSeconds} seconds — "
                + "the provider did not respond. Check connectivity and try again.", []);
        }
        catch (Exception ex)
        {
            result = new ProviderProbeResult(false, $"Validation failed: {ex.Message}", []);
        }
        finally
        {
            // Tear down only THIS probe's CTS, and only if it is still the active one. A newer probe
            // (StartProbe → CancelProbe) may have already replaced and disposed it; claiming the field
            // atomically stops this finally from cancelling/disposing the newer probe's live CTS.
            if (Interlocked.CompareExchange(ref _probeCts, null, cts) == cts)
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        DiscoveredModels.Clear();
        if (result.Success)
            DiscoveredModels.AddRange(result.Models);

        IsProbing.Value = false;
        ProbeResult.Value = result;
    }

    // Drives only the cosmetic "(Ns)" elapsed counter now that the spinner glyph
    // self-animates via SpinnerNode; a 1 Hz tick is all that's needed.
    private async Task RunProbeTimerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return; }

            ProbeElapsedSeconds.Value++;
        }
    }

    private ProviderEntry BuildProbeEntry(string providerType)
    {
        var entry = new ProviderEntry
        {
            Type = providerType,
            Endpoint = EndpointInput ?? "",
            AuthMethod = SelectedAuthMethod
        };

        if (SelectedAuthMethod is AuthMethod.OAuthDevice or AuthMethod.OAuthPkce)
        {
            var result = OAuth.Result;
            var credential = ApiKeyInput;
            if (string.IsNullOrWhiteSpace(credential))
                credential = result?.AccessToken.Value;

            entry.OAuthAccessToken = !string.IsNullOrWhiteSpace(credential)
                ? new SensitiveString(credential)
                : null;
            entry.OAuthRefreshToken = result?.RefreshToken;
            entry.OAuthTokenExpiry = result?.ExpiresAt;
            entry.OAuthAccountId = result?.AccountId;
        }
        else if (!string.IsNullOrWhiteSpace(ApiKeyInput))
        {
            entry.ApiKey = new SensitiveString(ApiKeyInput);
        }

        entry.SetVendorOptions(ToJsonObject(VendorOptions));

        return entry;
    }

    // ── OAuth ──

    public void StartOAuthFlow()
    {
        if (SelectedProviderType is null) return;
        if (!TryBuildOAuthFlowEntry(out var oauthEntry, out var error))
        {
            ProbeResult.Value = new ProviderProbeResult(false, error, []);
            return;
        }

        ProbeElapsedSeconds.Value = 0;
        var ct = OAuth.StartDeviceFlow(SelectedProviderType, result =>
        {
            ApiKeyInput = result.AccessToken.Value;
            StartProbe();
        }, oauthEntry);
        _ = RunProbeTimerAsync(ct);
    }

    public void SelectGitHubCopilotAuthHost(GitHubCopilotAuthHostMode mode)
    {
        GitHubCopilotHostMode = mode;

        if (mode == GitHubCopilotAuthHostMode.GitHubCom)
        {
            GitHubCopilotHostInput = null;
            GitHubCopilotApiBaseInput = null;
            VendorOptions = null;
            SetSubStep(5);
            StartOAuthFlow();
            return;
        }

        SetSubStep(8);
    }

    public bool TrySetGitHubCopilotEnterpriseHost(string? gitHubHost, out string error)
    {
        var previousHost = GitHubCopilotHostInput;
        var trimmedHost = gitHubHost?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedHost))
        {
            error = "GitHub Enterprise host is required.";
            return false;
        }

        if (!GitHubCopilotSetupFlow.TryResolveEnterpriseVendorOptions(
                trimmedHost,
                gitHubApiBase: null,
                out _,
                out error))
        {
            return false;
        }

        GitHubCopilotHostInput = trimmedHost;
        if (!string.Equals(previousHost, trimmedHost, StringComparison.Ordinal))
        {
            GitHubCopilotApiBaseInput = null;
            VendorOptions = null;
        }

        error = string.Empty;
        return true;
    }

    public bool TryStartGitHubCopilotEnterpriseOAuth(string? gitHubApiBase, out string error)
    {
        GitHubCopilotApiBaseInput = string.IsNullOrWhiteSpace(gitHubApiBase)
            ? null
            : gitHubApiBase.Trim();

        if (!GitHubCopilotSetupFlow.TryResolveEnterpriseVendorOptions(
                GitHubCopilotHostInput,
                GitHubCopilotApiBaseInput,
                out var vendorOptions,
                out error))
        {
            return false;
        }

        VendorOptions = vendorOptions;
        SetSubStep(5);
        StartOAuthFlow();
        return true;
    }

    private bool TryBuildOAuthFlowEntry(out ProviderEntry? entry, out string error)
    {
        entry = null;
        error = string.Empty;
        if (!GitHubCopilotSetupFlow.IsGitHubCopilot(SelectedProviderType))
            return true;

        entry = GitHubCopilotSetupFlow.BuildOAuthEntry(VendorOptions);
        return true;
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
        VendorOptions = null;
        GitHubCopilotHostMode = GitHubCopilotAuthHostMode.GitHubCom;
        GitHubCopilotHostInput = null;
        GitHubCopilotApiBaseInput = null;
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
                    : null,
            VendorOptions = VendorOptions,
        };

        var selectedModel = DiscoveredModels.FirstOrDefault(model =>
            string.Equals(model.ModelId.Value, SelectedModelId, StringComparison.OrdinalIgnoreCase));

        builder.Model = new ModelConfigSection
        {
            Provider = providerName,
            ModelId = SelectedModelId,
            ContextWindow = selectedModel?.ContextWindowTokens,
            Provenance = selectedModel is null ? ModelDiscoverySource.Manual : ModelDiscoverySource.Live,
            InputModalities = selectedModel?.InputModalities,
            OutputModalities = selectedModel?.OutputModalities,
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
            registry: _registry,
            // Protector for this config's keys directory, not the process-wide static service locator.
            protector: SecretsProtection.CreateProtector(paths),
            vendorOptions: VendorOptions);
    }

    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
    {
        // Provider check. When no provider is selected we emit a warn-level
        // item rather than a hard failure: the daemon will still start (in
        // degraded mode with the No-Op chat client) so the operator can fix
        // the configuration via `netclaw doctor` / `netclaw model`.
        var providerOk = !string.IsNullOrWhiteSpace(SelectedProviderType);
        if (providerOk)
        {
            var providerLabel = Registry.Get(SelectedProviderType!).DisplayName;
            runner.Add(new HealthCheckItem($"LLM provider configured ({providerLabel})", true));
        }
        else
        {
            runner.Add(new HealthCheckItem(
                "No provider configured — No-Op chat client will be active (run `netclaw init` or edit `netclaw.json`)",
                Passed: true,
                IsWarning: true));
        }

        // Model check
        var modelOk = !string.IsNullOrWhiteSpace(SelectedModelId);
        runner.Add(new HealthCheckItem(
            modelOk
                ? $"Model selected ({SelectedModelId})"
                : "No model selected — No-Op chat client will be active (run `netclaw model` or pick a model)",
            Passed: true,
            IsWarning: !modelOk));

        return Task.CompletedTask;
    }

    public SectionStatus GetStatus(WizardContext context)
        => !string.IsNullOrWhiteSpace(SelectedProviderType) || ConfigFileHelper.PathPresent(context.ExistingConfig ?? [], "Providers")
            ? SectionStatus.Configured
            : SectionStatus.NotConfigured;

    public string Summary(WizardContext context)
    {
        var providerType = SelectedProviderType ?? ReadExistingProviderType(context);
        var modelId = SelectedModelId ?? ReadExistingModelId(context);
        if (string.IsNullOrWhiteSpace(providerType))
            return "Not configured";

        return string.IsNullOrWhiteSpace(modelId) ? providerType : $"{providerType} / {modelId}";
    }

    public IWizardStepViewModel CreateEditor(IServiceProvider services)
        => ActivatorUtilities.CreateInstance<ProviderStepViewModel>(services);

    public SectionContribution BuildContribution(IWizardStepViewModel editor)
    {
        var vm = (ProviderStepViewModel)editor;
        if (string.IsNullOrWhiteSpace(vm.SelectedProviderType))
            return SectionContribution.Empty;

        var providerType = vm.SelectedProviderType.ToLowerInvariant();
        var fieldActions = new List<SectionFieldAction>
        {
            new("Providers", SectionFieldActionKind.Set, BuildProvidersDictionary(vm, providerType)),
            new("Models.Main.Provider", SectionFieldActionKind.Set, providerType)
        };

        if (string.IsNullOrWhiteSpace(vm.SelectedModelId))
            fieldActions.Add(new SectionFieldAction("Models.Main.ModelId", SectionFieldActionKind.Delete));
        else
            fieldActions.Add(new SectionFieldAction("Models.Main.ModelId", SectionFieldActionKind.Set, vm.SelectedModelId));

        var secretPath = $"Providers.{providerType}";
        var secretActions = new List<SectionSecretAction>();
        if (!string.IsNullOrWhiteSpace(vm.ApiKeyInput))
        {
            secretActions.Add(new SectionSecretAction($"{secretPath}.ApiKey", SectionSecretActionKind.Set,
                new SensitiveString(vm.ApiKeyInput)));
        }
        else if (vm.HasStoredCredential)
        {
            secretActions.Add(new SectionSecretAction(secretPath, SectionSecretActionKind.Preserve));
        }

        return new SectionContribution(fieldActions, secretActions);
    }

    private void PrefillFromExistingConfig(WizardContext context)
    {
        if (context.ExistingConfig is null)
            return;

        var providerType = ReadExistingProviderType(context);
        if (string.IsNullOrWhiteSpace(providerType))
            return;

        SelectedProviderType ??= providerType;
        SelectedModelId ??= ReadExistingModelId(context);

        if (ConfigFileHelper.TryGetPathValue(context.ExistingConfig, $"Providers.{providerType}.Endpoint", out var endpoint)
            && endpoint is string endpointText)
        {
            EndpointInput ??= endpointText;
        }

        if (TryReadExistingVendorOptions(context, providerType, out var vendorOptions))
            VendorOptions ??= vendorOptions;

        if (ConfigFileHelper.TryGetPathValue(context.ExistingConfig, $"Providers.{providerType}.AuthMethod", out var authMethod)
            && authMethod is string authMethodText
            && Enum.TryParse<AuthMethod>(authMethodText, ignoreCase: true, out var parsed))
        {
            SelectedAuthMethod = parsed;
        }

        HasStoredCredential = ConfigFileHelper.SecretPresent(context.Paths, $"Providers.{providerType}.ApiKey")
            || ConfigFileHelper.SecretPresent(context.Paths, $"Providers.{providerType}.OAuthAccessToken");
    }

    private static string? ReadExistingProviderType(WizardContext context)
    {
        if (context.ExistingConfig is null
            || !ConfigFileHelper.TryGetPathValue(context.ExistingConfig, "Models.Main.Provider", out var provider)
            || provider is not string providerText)
        {
            return null;
        }

        return providerText;
    }

    private static string? ReadExistingModelId(WizardContext context)
        => context.ExistingConfig is not null
           && ConfigFileHelper.TryGetPathValue(context.ExistingConfig, "Models.Main.ModelId", out var model)
            ? model as string
            : null;

    private Dictionary<string, object> BuildProvidersDictionary(ProviderStepViewModel vm, string providerType)
    {
        var providerEntry = new Dictionary<string, object>
        {
            [providerType] = BuildProviderEntry(vm, providerType)
        };

        if (_context?.ExistingConfig is not null
            && ConfigFileHelper.TryGetPathValue(_context.ExistingConfig, "Providers", out var existing)
            && existing is Dictionary<string, object> existingProviders)
        {
            foreach (var (key, value) in existingProviders)
            {
                if (!providerEntry.ContainsKey(key))
                    providerEntry[key] = value;
            }
        }

        return providerEntry;
    }

    private Dictionary<string, object> BuildProviderEntry(ProviderStepViewModel vm, string providerType)
    {
        var entry = new Dictionary<string, object>
        {
            ["Type"] = providerType
        };

        if (vm.SelectedAuthMethod != AuthMethod.None)
            entry["AuthMethod"] = vm.SelectedAuthMethod.ToString();

        var endpoint = !string.IsNullOrWhiteSpace(vm.EndpointInput)
            ? vm.EndpointInput
            : _registry.TryGet(providerType, out var descriptor) && descriptor.Auth is EndpointOnlyAuth
                ? descriptor.DefaultEndpoint
                : null;

        if (!string.IsNullOrWhiteSpace(endpoint))
            entry["Endpoint"] = endpoint;

        if (vm.VendorOptions is not null && vm.VendorOptions.Count > 0)
            entry["VendorOptions"] = vm.VendorOptions;

        return entry;
    }

    private static bool TryReadExistingVendorOptions(
        WizardContext context,
        string providerType,
        out IReadOnlyDictionary<string, object?>? vendorOptions)
    {
        vendorOptions = null;
        if (context.ExistingConfig is null
            || !ConfigFileHelper.TryGetPathValue(context.ExistingConfig, $"Providers.{providerType}.VendorOptions", out var raw)
            || raw is null)
        {
            return false;
        }

        var json = raw is JsonElement element
            ? element.GetRawText()
            : JsonSerializer.Serialize(raw, JsonDefaults.ConfigFile);
        vendorOptions = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, JsonDefaults.ConfigRead);
        return vendorOptions is not null;
    }

    private static JsonObject? ToJsonObject(IReadOnlyDictionary<string, object?>? vendorOptions)
    {
        if (vendorOptions is null || vendorOptions.Count == 0)
            return null;

        return JsonNode.Parse(JsonSerializer.Serialize(vendorOptions, JsonDefaults.ConfigFile))?.AsObject();
    }

    public void Dispose()
    {
        CancelProbe();
        OAuth.Dispose();
        IsProbing.Dispose();
        ProbeResult.Dispose();
        ProbeElapsedSeconds.Dispose();
    }
}
