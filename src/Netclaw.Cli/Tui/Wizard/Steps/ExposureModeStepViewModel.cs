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
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for selecting the daemon network exposure mode and inbound webhook enablement.
/// Sub-steps: mode selection → optional confirmation/notice → webhook toggle.
/// </summary>
public sealed class ExposureModeStepViewModel : IWizardStepViewModel
{
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

    public string StepId => "exposure-mode";
    public string DisplayTitle => "Network Exposure";

    /// <summary>The selected exposure mode. Defaults to <see cref="ExposureMode.Local"/>.</summary>
    public ExposureMode SelectedMode { get; set; } = ExposureMode.Local;

    /// <summary>Whether inbound webhook ingestion is enabled.</summary>
    public bool WebhooksEnabled { get; set; }

    public bool IsApplicable(WizardContext context) => true;

    public int CurrentSubStep => _currentSubStep;

    /// <summary>
    /// Sub-steps: mode selection + optional confirmation/notice + webhook toggle.
    /// </summary>
    public int SubStepCount => NeedsConfirmation ? 3 : 2;

    /// <summary>True when the selected mode requires a confirmation or notice screen.</summary>
    internal bool NeedsConfirmation => SelectedMode != ExposureMode.Local;

    /// <summary>True for modes that expose the daemon to the public internet.</summary>
    public bool IsHighRisk =>
        SelectedMode is ExposureMode.TailscaleFunnel or ExposureMode.CloudflareTunnel;

    /// <summary>The sub-step index for the inbound webhook toggle (always last).</summary>
    internal int WebhookSubStep => NeedsConfirmation ? 2 : 1;

    public string GetHelpText()
    {
        if (_currentSubStep == 0)
            return "  Local is safest — daemon only reachable from this machine. Use tunnels for remote access.";

        if (_currentSubStep == WebhookSubStep)
            return "  Inbound webhooks let external services trigger autonomous runs via HTTP POST.";

        // Sub-step 1 confirmation (non-Local modes only)
        return IsHighRisk
            ? "  This mode exposes your daemon beyond your tailnet. Ensure hub authentication is configured."
            : "  Tailscale Serve limits access to your tailnet only. Press Enter to confirm.";
    }

    public bool TryAdvance()
    {
        if (_currentSubStep == 0 && NeedsConfirmation)
        {
            _currentSubStep = 1;
            _highWaterSubStep = 1;
            return true; // mode selection → confirmation
        }

        if (_currentSubStep < WebhookSubStep)
        {
            _currentSubStep = WebhookSubStep;
            _highWaterSubStep = WebhookSubStep;
            return true; // → webhook toggle
        }

        return false; // step complete, orchestrator advances
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
            _currentSubStep = _highWaterSubStep;
        else
            _currentSubStep = 0;
    }

    public void OnLeave() { }

    /// <summary>
    /// Writes the Daemon section (non-local modes) and Webhooks section (when enabled).
    /// </summary>
    public void ContributeConfig(WizardConfigBuilder builder)
    {
        if (SelectedMode != ExposureMode.Local)
        {
            builder.Daemon = new DaemonConfigSection
            {
                ExposureMode = SelectedMode
            };
        }

        if (WebhooksEnabled)
        {
            builder.Webhooks = new WebhooksConfigSection { Enabled = true };
        }
    }

    public void ContributeSecrets(WizardSecretsBuilder builder)
    {
        if (SelectedMode == ExposureMode.Local)
            return;

        // Generate a bootstrap device token so the daemon can start with at least
        // one paired device — satisfies ExposureModeValidationService's requirement.
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var rawToken = Base64Url.EncodeToString(tokenBytes);

        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var saltHex = Convert.ToHexString(saltBytes).ToLowerInvariant();

        // SHA256(token_bytes || salt_bytes) — matches DeviceRegistry.ComputeTokenHash
        Span<byte> combined = stackalloc byte[tokenBytes.Length + saltBytes.Length];
        tokenBytes.CopyTo(combined);
        saltBytes.CopyTo(combined[tokenBytes.Length..]);
        var tokenHash = Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant();

        var now = DateTimeOffset.UtcNow;
        _bootstrapDevice = new PairedDevice
        {
            Name = Environment.MachineName,
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

        var json = JsonSerializer.Serialize(new[] { _bootstrapDevice }, DevicesJsonOptions);
        File.WriteAllText(paths.DevicesPath, json);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(paths.DevicesPath))
            File.SetUnixFileMode(paths.DevicesPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>The raw bootstrap token, exposed for testing.</summary>
    internal string? BootstrapRawToken => _bootstrapRawToken;

    /// <summary>The bootstrap device, exposed for testing.</summary>
    internal PairedDevice? BootstrapDevice => _bootstrapDevice;

    public void Dispose() { }
}
