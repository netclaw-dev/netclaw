// -----------------------------------------------------------------------
// <copyright file="ExposureModeStepViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Cli.Config;
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
public sealed class ExposureModeStepViewModel : IWizardStepViewModel
{
    /// <summary>Default bind address suggested in the reverse-proxy config sub-step.</summary>
    public const string DefaultReverseProxyHost = "0.0.0.0";

    private static readonly JsonSerializerOptions DevicesJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private int _currentSubStep;
    private int _highWaterSubStep;

    // Bootstrap device state — populated during ContributeSecrets for non-Local modes.
    private string? _bootstrapRawToken;
    private PairedDevice? _bootstrapDevice;

    public string StepId => WizardStepIds.ExposureMode;
    public string DisplayTitle => "Network Exposure";

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
    public int SubStepCount => IsReverseProxy ? 5 : (NeedsConfirmation ? 3 : 2);

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
    internal int WebhookSubStep => SubStepCount - 1;

    public string GetHelpText()
    {
        if (_currentSubStep == 0)
            return "  Local is safest — daemon only reachable from this machine. Use tunnels for remote access.";

        if (_currentSubStep == WebhookSubStep)
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

    /// <summary>
    /// Writes the Daemon section (non-local modes) and Webhooks section (when enabled).
    /// For reverse-proxy mode the section also carries the operator-supplied bind address
    /// and trusted-proxy list — required by <c>DaemonExposureValidator</c> at startup.
    /// </summary>
    public void ContributeConfig(WizardConfigBuilder builder)
    {
        if (SelectedMode != ExposureMode.Local)
        {
            builder.Daemon = new DaemonConfigSection
            {
                ExposureMode = SelectedMode,
                Host = IsReverseProxy ? Host : null,
                TrustedProxies = IsReverseProxy ? TrustedProxies : [],
            };
        }

        if (WebhooksEnabled)
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

        var now = DateTimeOffset.UtcNow;
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
        File.WriteAllText(paths.DevicesPath, json);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(paths.DevicesPath))
            File.SetUnixFileMode(paths.DevicesPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
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

    public void Dispose() { }
}
