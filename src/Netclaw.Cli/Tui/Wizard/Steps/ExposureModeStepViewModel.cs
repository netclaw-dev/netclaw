// -----------------------------------------------------------------------
// <copyright file="ExposureModeStepViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Cli.Config;
using Netclaw.Cli.Doctor;
using Netclaw.Cli.Tui.Sections;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for selecting the daemon network exposure mode and inbound webhook enablement.
/// Sub-step plan by mode:
///   Local:        mode → webhook (2 sub-steps)
///   ReverseProxy: mode → bind address → trusted proxies → notice → webhook (5 sub-steps)
///   Tailscale*/Cloudflare: mode → notice → webhook (3 sub-steps)
/// One <c>TextInputNode</c> per sub-step matches the established wizard pattern
/// (see SlackStepView, IdentityStepView).
/// </summary>
public sealed class ExposureModeStepViewModel : IWizardStepViewModel, ISectionEditor
{
    /// <summary>Default bind address suggested in the reverse-proxy config sub-step.</summary>
    public const string DefaultReverseProxyHost = "0.0.0.0";

    private const string ReverseProxyHostStateKey = "ReverseProxy.Host";
    private const string ReverseProxyTrustedProxiesStateKey = "ReverseProxy.TrustedProxies";

    private static readonly JsonSerializerOptions DevicesJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private int _currentSubStep;
    private int _highWaterSubStep;
    private readonly TimeProvider _timeProvider;

    // Bootstrap device state — populated during ContributeSecrets for non-Local modes.
    private string? _bootstrapRawToken;
    private PairedDevice? _bootstrapDevice;

    public ExposureModeStepViewModel()
        : this(TimeProvider.System, includeWebhookToggle: true)
    {
    }

    public ExposureModeStepViewModel(TimeProvider timeProvider)
        : this(timeProvider, includeWebhookToggle: true)
    {
    }

    internal ExposureModeStepViewModel(bool includeWebhookToggle)
        : this(TimeProvider.System, includeWebhookToggle)
    {
    }

    private ExposureModeStepViewModel(TimeProvider timeProvider, bool includeWebhookToggle)
    {
        _timeProvider = timeProvider;
        IncludeWebhookToggle = includeWebhookToggle;
    }

    public string StepId => WizardStepIds.ExposureMode;
    public string DisplayTitle => "Network Exposure";
    public string SectionId => StepId;
    public string DisplayName => "Exposure Mode";
    public string? Category => "Security & Access";
    public bool ShowInMenu => true;
    public IReadOnlyList<string> RelevantDoctorChecks => ["Config Schema", "exposure-mode"];

    internal bool IncludeWebhookToggle { get; }

    /// <summary>The selected exposure mode. Defaults to <see cref="ExposureMode.Local"/>.</summary>
    public ExposureMode SelectedMode { get; set; } = ExposureMode.Local;

    /// <summary>Whether inbound webhook ingestion is enabled.</summary>
    public bool WebhooksEnabled { get; set; }

    /// <summary>
    /// Bind address collected on the reverse-proxy config sub-step. Loopback / format
    /// validation is left to <c>DaemonExposureValidator</c> and the doctor check —
    /// the wizard does not duplicate that logic.
    /// </summary>
    public string Host { get; set; } = DefaultReverseProxyHost;

    /// <summary>
    /// Trusted reverse-proxy IPs / CIDR ranges collected on the reverse-proxy config sub-step.
    /// At least one entry is required to advance past that sub-step (matches the runtime contract
    /// in <c>DaemonExposureValidator.Validate</c>).
    /// </summary>
    public IReadOnlyList<string> TrustedProxies { get; set; } = [];

    public bool IsApplicable(WizardContext context) => true;

    public int CurrentSubStep => _currentSubStep;

    /// <summary>Sub-step count varies by mode — see class summary.</summary>
    public int SubStepCount
    {
        get
        {
            var count = IsReverseProxy ? 4 : (NeedsConfirmation ? 2 : 1);
            return IncludeWebhookToggle ? count + 1 : count;
        }
    }

    /// <summary>True when the selected mode requires a confirmation or notice screen.</summary>
    internal bool NeedsConfirmation => SelectedMode != ExposureMode.Local;

    /// <summary>True when the selected mode is reverse proxy.</summary>
    internal bool IsReverseProxy => SelectedMode == ExposureMode.ReverseProxy;

    /// <summary>True for modes that expose the daemon to the public internet.</summary>
    public bool IsHighRisk =>
        SelectedMode is ExposureMode.TailscaleFunnel or ExposureMode.CloudflareTunnel;

    /// <summary>Sub-step index of the reverse-proxy bind-address input. Only valid when <see cref="IsReverseProxy"/>.</summary>
    internal int ReverseProxyHostSubStep => 1;

    /// <summary>Sub-step index of the reverse-proxy trusted-proxies input. Only valid when <see cref="IsReverseProxy"/>.</summary>
    internal int ReverseProxyTrustedProxiesSubStep => 2;

    /// <summary>The sub-step index for the confirmation/notice screen. Only valid when <see cref="NeedsConfirmation"/>.</summary>
    internal int NoticeSubStep => IsReverseProxy ? 3 : 1;

    /// <summary>The sub-step index for the inbound webhook toggle (always last in the plan).</summary>
    internal int WebhookSubStep => IncludeWebhookToggle ? SubStepCount - 1 : -1;

    public string GetHelpText()
    {
        if (_currentSubStep == 0)
            return "  Local is safest — daemon only reachable from this machine. Use tunnels for remote access.";

        if (IncludeWebhookToggle && _currentSubStep == WebhookSubStep)
            return "  Inbound webhooks let external services trigger autonomous runs via HTTP POST.";

        if (IsReverseProxy && _currentSubStep == ReverseProxyHostSubStep)
            return "  Bind to a non-loopback address. Loopback auto-auth cannot be inherited through a proxy.";

        if (IsReverseProxy && _currentSubStep == ReverseProxyTrustedProxiesSubStep)
            return "  Comma-separated IPs / CIDRs. At least one is required — the daemon refuses to start without it.";

        if (IsReverseProxy && _currentSubStep == NoticeSubStep)
            return "  Point your reverse proxy at the serving URL shown above. Press Enter to continue.";

        // Notice sub-step for non-reverse-proxy modes.
        return IsHighRisk
            ? "  This mode exposes your daemon beyond your tailnet. Ensure hub authentication is configured."
            : "  Tailscale Serve limits access to your tailnet only. Press Enter to confirm.";
    }

    public bool TryAdvance()
    {
        // Gate: cannot leave the trusted-proxies sub-step until ≥1 entry is present.
        // Mirrors DaemonExposureValidator's startup contract so the wizard never emits
        // a config the daemon would refuse. Return true (not false): per IWizardStepViewModel,
        // true means "handled internally — stay in this step." Returning false here would
        // tell the orchestrator the step is complete and to advance to the next wizard step,
        // which would skip the notice + webhook sub-steps entirely.
        if (IsReverseProxy
            && _currentSubStep == ReverseProxyTrustedProxiesSubStep
            && TrustedProxies.Count == 0)
        {
            return true;
        }

        var next = _currentSubStep + 1;
        if (next >= SubStepCount)
            return false; // step complete; orchestrator advances the wizard

        _currentSubStep = next;
        if (_currentSubStep > _highWaterSubStep)
            _highWaterSubStep = _currentSubStep;
        return true;
    }

    public bool TryGoBack()
    {
        if (_currentSubStep > 0)
        {
            _currentSubStep--;
            return true;
        }
        return false;
    }

    public void OnEnter(WizardContext context, NavigationDirection direction)
    {
        if (direction == NavigationDirection.Forward)
            TryPrefillFromExisting(context);

        if (direction == NavigationDirection.Back)
        {
            // SubStepCount depends on SelectedMode, which the operator can change
            // at sub-step 0. Clamp so we don't restore a high-water mark from a
            // mode with more sub-steps than the currently selected one.
            _currentSubStep = Math.Min(_highWaterSubStep, SubStepCount - 1);
        }
        else
        {
            _currentSubStep = 0;
        }
    }

    public void OnLeave() { }

    internal void ReturnToModeSelection()
    {
        _currentSubStep = 0;
    }

    /// <summary>
    /// Writes the Daemon section (non-local modes) and Webhooks section (when enabled).
    /// For reverse-proxy mode the section also carries the operator-supplied bind address
    /// and trusted-proxy list — required by <c>DaemonExposureValidator</c> at startup.
    /// </summary>
    public void ContributeConfig(WizardConfigBuilder builder)
    {
        if (IncludeWebhookToggle && SelectedMode != ExposureMode.Local)
        {
            builder.Daemon = new DaemonConfigSection
            {
                ExposureMode = SelectedMode,
                Host = IsReverseProxy ? Host : null,
                TrustedProxies = IsReverseProxy ? TrustedProxies : [],
            };
        }

        if (IncludeWebhookToggle && WebhooksEnabled)
        {
            builder.Webhooks = new WebhooksConfigSection { Enabled = true };
        }
    }

    public void ContributeSecrets(WizardSecretsBuilder builder)
    {
        _bootstrapRawToken = null;
        _bootstrapDevice = null;

        if (SelectedMode == ExposureMode.Local)
            return;

        var bootstrapStateStore = new BootstrapStateStore(builder.Paths);
        if (bootstrapStateStore.HasCompletedNonLocalBootstrap()
            || File.Exists(builder.Paths.DevicesPath)
            || HasExistingLocalDeviceToken(builder.Paths))
        {
            return;
        }

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Base64Url.EncodeToString(tokenBytes);

        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var saltHex = Convert.ToHexString(saltBytes).ToLowerInvariant();
        var tokenHash = PairedDevice.ComputeTokenHash(rawToken, saltHex);

        var now = _timeProvider.GetUtcNow();
        _bootstrapDevice = new PairedDevice
        {
            Name = Environment.MachineName,
            IsBootstrapDevice = true,
            TokenHash = tokenHash,
            Salt = saltHex,
            CreatedAt = now,
            LastUsedAt = now,
        };
        _bootstrapRawToken = rawToken;

        builder.AddValue("DeviceToken", rawToken);
    }

    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
        => Task.CompletedTask;

    public SectionStatus GetStatus(WizardContext context) => SectionStatus.Configured;

    public string Summary(WizardContext context)
        => FormatModeLabel(ReadExistingMode(context));

    public IWizardStepViewModel CreateEditor(IServiceProvider services)
        => new ExposureModeStepViewModel(includeWebhookToggle: false);

    internal string? GetStructuralValidationError()
    {
        if (SelectedMode != ExposureMode.ReverseProxy)
            return null;

        var host = string.IsNullOrWhiteSpace(Host) ? DefaultReverseProxyHost : Host.Trim();
        if (DaemonExposureValidator.IsLoopbackHost(host))
            return $"Daemon.Host '{host}' is loopback and cannot be used for reverse-proxy exposure.";

        if (TrustedProxies.Count == 0)
            return "Daemon.TrustedProxies must contain at least one IP address or CIDR for reverse-proxy exposure.";

        return DaemonExposureValidator.TryGetInvalidTrustedProxy(TrustedProxies, out var error)
            ? error
            : null;
    }

    /// <summary>
    /// Guarantees the operator's current client keeps daemon access after a non-local exposure
    /// mode is saved. If the local <c>DeviceToken</c> does not already match a paired device, the
    /// configuring client is paired: an existing-but-unmatched token (orphaned or mismatched local
    /// state) gets a device minted to accept it; a missing token gets a fresh token+device. Existing
    /// devices are never removed, so this only ever ADDS access for the operator at the keyboard.
    ///
    /// This replaces an earlier hard "fix pairing via `netclaw doctor` before saving" block: that
    /// block locked the configuring client out of <c>netclaw chat</c> on any leftover/partial pairing
    /// state. Auto-pairing here mirrors the wizard's bootstrap (<see cref="ContributeSecrets"/>) and
    /// the daemon's <c>BootstrapDeviceSeeder</c>, which only auto-pair on a fully fresh install.
    /// </summary>
    public void EnsureCurrentClientPaired(NetclawPaths paths)
    {
        if (!SelectedMode.RequiresRemoteAuthentication())
            return;

        var snapshot = DeviceRegistryInspector.Read(paths);
        if (snapshot.LocalTokenMatchesDevice)
            return; // The configuring client already has a working pairing — nothing to do.

        var saltHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        // Keep the operator's existing local token when one is present and usable (orphaned/
        // mismatched) so an already-distributed token keeps working; otherwise — including a
        // corrupted/unparseable token — mint a fresh one for this client rather than crash the save.
        var rawToken = snapshot.HasLocalDeviceToken ? ReadLocalDeviceTokenValue(paths) : null;
        if (string.IsNullOrWhiteSpace(rawToken) || !TryComputeTokenHash(rawToken, saltHex, out var tokenHash))
        {
            rawToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
            WriteLocalDeviceTokenValue(paths, rawToken);
            tokenHash = PairedDevice.ComputeTokenHash(rawToken, saltHex);
        }

        var now = _timeProvider.GetUtcNow();
        var device = new PairedDevice
        {
            Name = Environment.MachineName,
            IsBootstrapDevice = true,
            TokenHash = tokenHash,
            Salt = saltHex,
            CreatedAt = now,
            LastUsedAt = now,
        };

        var devices = ReadPairedDevices(paths);
        devices.Add(device);
        WritePairedDevices(paths, devices);
    }

    private static string? ReadLocalDeviceTokenValue(NetclawPaths paths)
        => ConfigFileHelper.ReadDecryptedSecret(paths, "DeviceToken");

    private static void WriteLocalDeviceTokenValue(NetclawPaths paths, string rawToken)
    {
        var secrets = File.Exists(paths.SecretsPath)
            ? ConfigFileHelper.LoadJsonDict(paths.SecretsPath)
            : new Dictionary<string, object>();
        secrets["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;
        secrets["DeviceToken"] = rawToken;
        ConfigFileHelper.WriteSecretsFile(paths, secrets);
    }

    private static List<PairedDevice> ReadPairedDevices(NetclawPaths paths)
    {
        if (!File.Exists(paths.DevicesPath))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(paths.DevicesPath));
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? JsonSerializer.Deserialize<List<PairedDevice>>(doc.RootElement.GetRawText(), DevicesJsonOptions) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void WritePairedDevices(NetclawPaths paths, IReadOnlyList<PairedDevice> devices)
    {
        var json = JsonSerializer.Serialize(devices, DevicesJsonOptions);
        AtomicFile.WriteAllText(paths.DevicesPath, json, AtomicFile.HardenOwnerOnly);
    }

    private static bool TryComputeTokenHash(string rawToken, string saltHex, out string tokenHash)
    {
        try
        {
            tokenHash = PairedDevice.ComputeTokenHash(rawToken, saltHex);
            return true;
        }
        catch (FormatException)
        {
            // A corrupted/non-base64url local token cannot produce a usable device hash; signal the
            // caller to mint a fresh token instead of letting the save crash.
            tokenHash = string.Empty;
            return false;
        }
    }

    public SectionContribution BuildContribution(IWizardStepViewModel editor)
    {
        var vm = (ExposureModeStepViewModel)editor;
        var actions = new List<SectionFieldAction>
        {
            new("Daemon.ExposureMode", SectionFieldActionKind.Set, vm.SelectedMode.ToWireValue())
        };
        var stateActions = new List<SectionEditorStateAction>();

        if (vm.SelectedMode == ExposureMode.ReverseProxy)
        {
            var host = string.IsNullOrWhiteSpace(vm.Host) ? DefaultReverseProxyHost : vm.Host;
            var trustedProxies = vm.TrustedProxies.ToArray();
            actions.Add(new SectionFieldAction("Daemon.Host", SectionFieldActionKind.Set, host));
            actions.Add(new SectionFieldAction("Daemon.TrustedProxies", SectionFieldActionKind.Set,
                trustedProxies));

            stateActions.Add(CreateStateAction(ReverseProxyHostStateKey, host, host != DefaultReverseProxyHost));
            stateActions.Add(CreateStateAction(ReverseProxyTrustedProxiesStateKey, trustedProxies,
                trustedProxies.Length > 0));
        }
        else
        {
            var trustedProxies = vm.TrustedProxies.ToArray();

            if (!string.IsNullOrWhiteSpace(vm.Host)
                && !DaemonExposureValidator.IsLoopbackHost(vm.Host)
                && vm.Host != DefaultReverseProxyHost)
            {
                stateActions.Add(CreateStateAction(ReverseProxyHostStateKey, vm.Host, keepValue: true));
            }

            stateActions.Add(CreateStateAction(ReverseProxyTrustedProxiesStateKey, trustedProxies,
                trustedProxies.Length > 0));

            // These fields are runtime-active whenever they remain under Daemon.
            // Move dormant reverse-proxy values to editor state so local/tunnel
            // startup validation ignores them until reverse-proxy mode is active again.
            actions.Add(new SectionFieldAction("Daemon.Host", SectionFieldActionKind.Delete));
            actions.Add(new SectionFieldAction("Daemon.TrustedProxies", SectionFieldActionKind.Delete));
        }

        return new SectionContribution(actions, StateActions: stateActions);
    }

    /// <summary>
    /// Write the bootstrap paired device to <c>devices.json</c> so the daemon can start
    /// with at least one paired device. No-op for Local mode.
    /// Called from <see cref="WizardOrchestrator.WriteConfig"/> after secrets are written.
    /// </summary>
    public void WriteBootstrapDevice(NetclawPaths paths)
    {
        if (_bootstrapDevice is null)
            return;

        if (File.Exists(paths.DevicesPath))
            return;

        var json = JsonSerializer.Serialize(new[] { _bootstrapDevice }, DevicesJsonOptions);
        AtomicFile.WriteAllText(paths.DevicesPath, json, AtomicFile.HardenOwnerOnly);
    }

    /// <summary>The raw bootstrap token, exposed for testing.</summary>
    internal string? BootstrapRawToken => _bootstrapRawToken;

    /// <summary>The bootstrap device, exposed for testing.</summary>
    internal PairedDevice? BootstrapDevice => _bootstrapDevice;

    private static bool HasExistingLocalDeviceToken(NetclawPaths paths)
    {
        if (!File.Exists(paths.SecretsPath))
            return false;

        var secrets = ConfigFileHelper.LoadJsonDict(paths.SecretsPath);
        if (!secrets.TryGetValue("DeviceToken", out var rawValue))
            return false;

        var rawToken = rawValue is JsonElement jsonElement ? jsonElement.GetString() : rawValue?.ToString();
        return !string.IsNullOrWhiteSpace(ConfigFileHelper.DecryptIfEncrypted(paths, rawToken));
    }

    private void TryPrefillFromExisting(WizardContext context)
    {
        if (context.ExistingConfig is null)
            return;

        SelectedMode = ReadExistingMode(context);
        var editorState = new ConfigEditorStateStore(context.Paths);

        if (SelectedMode == ExposureMode.ReverseProxy
            && ConfigFileHelper.TryGetPathValue(context.ExistingConfig, "Daemon.Host", out var hostValue)
            && TryReadHost(hostValue, out var activeHost))
        {
            Host = activeHost;
        }
        else if (editorState.TryGetValue(SectionId, ReverseProxyHostStateKey, out var storedHostValue)
                 && TryReadHost(storedHostValue, out var storedHost))
        {
            Host = storedHost;
        }
        else if (ConfigFileHelper.TryGetPathValue(context.ExistingConfig, "Daemon.Host", out var inactiveHostValue)
                 && TryReadHost(inactiveHostValue, out var inactiveHost)
                 && !DaemonExposureValidator.IsLoopbackHost(inactiveHost))
        {
            Host = inactiveHost;
        }

        if (SelectedMode == ExposureMode.ReverseProxy
            && ConfigFileHelper.TryGetPathValue(context.ExistingConfig, "Daemon.TrustedProxies", out var proxiesValue))
        {
            TrustedProxies = ReadTrustedProxies(proxiesValue);
        }
        else if (editorState.TryGetValue(SectionId, ReverseProxyTrustedProxiesStateKey, out var storedProxiesValue))
        {
            TrustedProxies = ReadTrustedProxies(storedProxiesValue);
        }
        else if (ConfigFileHelper.TryGetPathValue(context.ExistingConfig, "Daemon.TrustedProxies", out var inactiveProxiesValue))
        {
            TrustedProxies = ReadTrustedProxies(inactiveProxiesValue);
        }
    }

    private static SectionEditorStateAction CreateStateAction(string key, object? value, bool keepValue)
        => new(
            WizardStepIds.ExposureMode,
            key,
            keepValue ? SectionEditorStateActionKind.Set : SectionEditorStateActionKind.Delete,
            value);

    private static bool TryReadHost(object? value, out string host)
    {
        host = value?.ToString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(host);
    }

    private static ExposureMode ReadExistingMode(WizardContext context)
    {
        if (context.ExistingConfig is null
            || !ConfigFileHelper.TryGetPathValue(context.ExistingConfig, "Daemon.ExposureMode", out var modeValue))
        {
            return ExposureMode.Local;
        }

        try
        {
            return DaemonConfig.ParseExposureMode(modeValue?.ToString());
        }
        catch (InvalidOperationException)
        {
            // A migrated/hand-edited config with an unsupported ExposureMode must not crash wizard
            // prefill or the mode label render; fall back to the most restrictive Local default.
            return ExposureMode.Local;
        }
    }

    private static IReadOnlyList<string> ReadTrustedProxies(object? value)
        => value switch
        {
            string[] strings => strings,
            object[] objects => objects.Select(static item => item?.ToString()).Where(static item => !string.IsNullOrWhiteSpace(item)).Cast<string>().ToArray(),
            IEnumerable<string> strings => strings.ToArray(),
            _ => []
        };

    private static string FormatModeLabel(ExposureMode mode)
        => mode switch
        {
            ExposureMode.Local => "Local",
            ExposureMode.ReverseProxy => "Reverse Proxy",
            ExposureMode.TailscaleServe => "Tailscale Serve",
            ExposureMode.TailscaleFunnel => "Tailscale Funnel",
            ExposureMode.CloudflareTunnel => "Cloudflare Tunnel",
            _ => mode.ToString()
        };

    public void Dispose() { }
}
