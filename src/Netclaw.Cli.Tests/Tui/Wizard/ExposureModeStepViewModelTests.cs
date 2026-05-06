// -----------------------------------------------------------------------
// <copyright file="ExposureModeStepViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class ExposureModeStepViewModelTests : WizardStepTestBase
{

    // ── ContributeConfig ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(ExposureMode.Local, false)]
    [InlineData(ExposureMode.TailscaleServe, true)]
    [InlineData(ExposureMode.TailscaleFunnel, true)]
    [InlineData(ExposureMode.CloudflareTunnel, true)]
    public void ContributeConfig_WritesDaemonSectionForNonLocalModes(ExposureMode mode, bool expectDaemon)
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = mode;

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        if (expectDaemon)
        {
            Assert.NotNull(builder.Daemon);
            Assert.Equal(mode, builder.Daemon.ExposureMode);
        }
        else
        {
            Assert.Null(builder.Daemon);
        }
    }

    [Fact]
    public void ContributeConfig_DefaultMode_OmitsDaemonSection()
    {
        using var step = new ExposureModeStepViewModel();

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.Null(builder.Daemon);
    }

    // ── BuildConfigDictionary integration ────────────────────────────────────

    [Theory]
    [InlineData(ExposureMode.TailscaleServe, "tailscale-serve")]
    [InlineData(ExposureMode.TailscaleFunnel, "tailscale-funnel")]
    [InlineData(ExposureMode.CloudflareTunnel, "cloudflare-tunnel")]
    public void BuildConfigDictionary_NonLocal_WritesKebabCaseWireValue(ExposureMode mode, string expectedWireValue)
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Daemon = new DaemonConfigSection { ExposureMode = mode }
        };

        var config = builder.BuildConfigDictionary();

        Assert.True(config.ContainsKey("Daemon"));
        var daemon = (Dictionary<string, object>)config["Daemon"];
        Assert.Equal(expectedWireValue, daemon["ExposureMode"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildConfigDictionary_LocalOrNull_OmitsDaemonKey(bool setDaemon)
    {
        var builder = new WizardConfigBuilder(Context.Paths);
        if (setDaemon)
            builder.Daemon = new DaemonConfigSection { ExposureMode = ExposureMode.Local };

        var config = builder.BuildConfigDictionary();

        Assert.False(config.ContainsKey("Daemon"));
    }

    // ── ContributeConfig — Webhooks ─────────────────────────────────────────

    [Fact]
    public void ContributeConfig_WebhooksEnabled_WritesWebhooksSection()
    {
        using var step = new ExposureModeStepViewModel();
        step.WebhooksEnabled = true;

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Webhooks);
        Assert.True(builder.Webhooks.Enabled);
    }

    [Fact]
    public void ContributeConfig_WebhooksDisabled_OmitsWebhooksSection()
    {
        using var step = new ExposureModeStepViewModel();
        // WebhooksEnabled defaults to false

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.Null(builder.Webhooks);
    }

    // ── BuildConfigDictionary — Webhooks ───────��─────────────────────────────

    [Fact]
    public void BuildConfigDictionary_WebhooksEnabled_WritesEnabledTrue()
    {
        var builder = new WizardConfigBuilder(Context.Paths)
        {
            Webhooks = new WebhooksConfigSection { Enabled = true }
        };

        var config = builder.BuildConfigDictionary();

        Assert.True(config.ContainsKey("Webhooks"));
        var webhooks = (Dictionary<string, object>)config["Webhooks"];
        Assert.Equal(true, webhooks["Enabled"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildConfigDictionary_WebhooksDisabledOrNull_OmitsWebhooksKey(bool setWebhooks)
    {
        var builder = new WizardConfigBuilder(Context.Paths);
        if (setWebhooks)
            builder.Webhooks = new WebhooksConfigSection { Enabled = false };

        var config = builder.BuildConfigDictionary();

        Assert.False(config.ContainsKey("Webhooks"));
    }

    // ── Sub-step navigation ───────────────────────────────────────────────────

    [Fact]
    public void TryAdvance_LocalMode_AdvancesToWebhookSubStep()
    {
        using var step = new ExposureModeStepViewModel();
        step.OnEnter(Context, NavigationDirection.Forward);
        step.SelectedMode = ExposureMode.Local;

        var result = step.TryAdvance();

        Assert.True(result);
        Assert.Equal(1, step.CurrentSubStep); // webhook toggle
    }

    [Theory]
    [InlineData(ExposureMode.TailscaleServe)]
    [InlineData(ExposureMode.TailscaleFunnel)]
    [InlineData(ExposureMode.CloudflareTunnel)]
    public void TryAdvance_NonLocal_AdvancesToConfirmation(ExposureMode mode)
    {
        using var step = new ExposureModeStepViewModel();
        step.OnEnter(Context, NavigationDirection.Forward);
        step.SelectedMode = mode;

        var result = step.TryAdvance();

        Assert.True(result);
        Assert.Equal(1, step.CurrentSubStep);
    }

    [Fact]
    public void TryAdvance_NonLocal_FromConfirmation_AdvancesToWebhookSubStep()
    {
        using var step = new ExposureModeStepViewModel();
        step.OnEnter(Context, NavigationDirection.Forward);
        step.SelectedMode = ExposureMode.TailscaleFunnel;
        step.TryAdvance(); // → confirmation (sub-step 1)

        var result = step.TryAdvance();

        Assert.True(result);
        Assert.Equal(2, step.CurrentSubStep); // webhook toggle
    }

    [Fact]
    public void TryAdvance_FromWebhookSubStep_ReturnsFalse()
    {
        using var step = new ExposureModeStepViewModel();
        step.OnEnter(Context, NavigationDirection.Forward);
        step.SelectedMode = ExposureMode.TailscaleFunnel;
        step.TryAdvance(); // → confirmation
        step.TryAdvance(); // → webhook toggle

        var result = step.TryAdvance();

        Assert.False(result); // step complete
    }

    [Fact]
    public void TryAdvance_Local_FromWebhookSubStep_ReturnsFalse()
    {
        using var step = new ExposureModeStepViewModel();
        step.OnEnter(Context, NavigationDirection.Forward);
        step.SelectedMode = ExposureMode.Local;
        step.TryAdvance(); // → webhook toggle

        var result = step.TryAdvance();

        Assert.False(result); // step complete
    }

    [Fact]
    public void TryGoBack_FromWebhookSubStep_ReturnsTrue()
    {
        using var step = new ExposureModeStepViewModel();
        step.OnEnter(Context, NavigationDirection.Forward);
        step.SelectedMode = ExposureMode.TailscaleServe;
        step.TryAdvance(); // → confirmation
        step.TryAdvance(); // → webhook toggle

        var result = step.TryGoBack();

        Assert.True(result);
        Assert.Equal(1, step.CurrentSubStep); // back to confirmation
    }

    [Fact]
    public void TryGoBack_FromConfirmation_ReturnsTrue()
    {
        using var step = new ExposureModeStepViewModel();
        step.OnEnter(Context, NavigationDirection.Forward);
        step.SelectedMode = ExposureMode.TailscaleServe;
        step.TryAdvance(); // → confirmation

        var result = step.TryGoBack();

        Assert.True(result);
        Assert.Equal(0, step.CurrentSubStep);
    }

    [Fact]
    public void TryGoBack_FromSubStep0_ReturnsFalse()
    {
        using var step = new ExposureModeStepViewModel();
        step.OnEnter(Context, NavigationDirection.Forward);

        var result = step.TryGoBack();

        Assert.False(result);
    }

    [Theory]
    [InlineData(ExposureMode.TailscaleFunnel, true)]
    [InlineData(ExposureMode.CloudflareTunnel, true)]
    [InlineData(ExposureMode.TailscaleServe, false)]
    [InlineData(ExposureMode.Local, false)]
    public void IsHighRisk_MatchesExpectation(ExposureMode mode, bool expected)
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = mode;

        Assert.Equal(expected, step.IsHighRisk);
    }

    [Theory]
    [InlineData(ExposureMode.Local, 2, 1)]
    [InlineData(ExposureMode.TailscaleServe, 3, 2)]
    [InlineData(ExposureMode.TailscaleFunnel, 3, 2)]
    [InlineData(ExposureMode.CloudflareTunnel, 3, 2)]
    public void SubStepCount_And_WebhookSubStep_MatchMode(ExposureMode mode, int expectedSubStepCount, int expectedWebhookSubStep)
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = mode;

        Assert.Equal(expectedSubStepCount, step.SubStepCount);
        Assert.Equal(expectedWebhookSubStep, step.WebhookSubStep);
    }

    // ── Bootstrap device pairing (#540) ──────────────────────────────────────

    [Fact]
    public void ContributeSecrets_NonLocal_AddsDeviceToken()
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.TailscaleFunnel;

        var builder = new WizardSecretsBuilder(Context.Paths);
        step.ContributeSecrets(builder);

        Assert.NotNull(step.BootstrapRawToken);
        Assert.NotNull(step.BootstrapDevice);
        Assert.Equal(Environment.MachineName, step.BootstrapDevice.Name);
    }

    [Fact]
    public void ContributeSecrets_Local_DoesNotAddDeviceToken()
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.Local;

        var builder = new WizardSecretsBuilder(Context.Paths);
        step.ContributeSecrets(builder);

        Assert.Null(step.BootstrapRawToken);
        Assert.Null(step.BootstrapDevice);
    }

    [Fact]
    public void ContributeSecrets_ExistingDeviceToken_DoesNotGenerateBootstrapState()
    {
        ConfigFileHelper.WriteSecretsFile(Context.Paths, new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["DeviceToken"] = "existing-token"
        });

        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.TailscaleServe;

        var builder = new WizardSecretsBuilder(Context.Paths);
        step.ContributeSecrets(builder);

        Assert.Null(step.BootstrapRawToken);
        Assert.Null(step.BootstrapDevice);
    }

    [Fact]
    public void ContributeSecrets_CompletedBootstrap_DoesNotGenerateBootstrapState()
    {
        new BootstrapStateStore(Context.Paths).MarkCompleted(TimeProvider.System);

        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.TailscaleServe;

        var builder = new WizardSecretsBuilder(Context.Paths);
        step.ContributeSecrets(builder);

        Assert.Null(step.BootstrapRawToken);
        Assert.Null(step.BootstrapDevice);
    }

    [Fact]
    public void WriteBootstrapDevice_NonLocal_WritesDevicesJson()
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.TailscaleServe;

        var builder = new WizardSecretsBuilder(Context.Paths);
        step.ContributeSecrets(builder);
        step.WriteBootstrapDevice(Context.Paths);

        Assert.True(File.Exists(Context.Paths.DevicesPath));

        var json = File.ReadAllText(Context.Paths.DevicesPath);
        var devices = JsonSerializer.Deserialize<List<PairedDevice>>(json);
        Assert.NotNull(devices);
        Assert.Single(devices);
        Assert.Equal(Environment.MachineName, devices[0].Name);
        Assert.True(devices[0].IsBootstrapDevice);
    }

    [Fact]
    public void WriteBootstrapDevice_Local_DoesNotWriteFile()
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.Local;

        var builder = new WizardSecretsBuilder(Context.Paths);
        step.ContributeSecrets(builder);
        step.WriteBootstrapDevice(Context.Paths);

        Assert.False(File.Exists(Context.Paths.DevicesPath));
    }

    [Fact]
    public void WriteBootstrapDevice_DoesNotOverwriteExistingDevicesFile()
    {
        File.WriteAllText(Context.Paths.DevicesPath, "[]");

        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.TailscaleServe;

        var builder = new WizardSecretsBuilder(Context.Paths);
        step.ContributeSecrets(builder);
        step.WriteBootstrapDevice(Context.Paths);

        Assert.Equal("[]", File.ReadAllText(Context.Paths.DevicesPath));
    }

    [Fact]
    public void WriteBootstrapDevice_WithExistingDeviceToken_DoesNotWriteMismatchedBootstrapDevice()
    {
        ConfigFileHelper.WriteSecretsFile(Context.Paths, new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["DeviceToken"] = "existing-token"
        });

        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.TailscaleServe;

        var builder = new WizardSecretsBuilder(Context.Paths);
        step.ContributeSecrets(builder);
        step.WriteBootstrapDevice(Context.Paths);

        Assert.False(File.Exists(Context.Paths.DevicesPath));
    }

    [Fact]
    public void WriteBootstrapDevice_TokenVerifiesAgainstDevice()
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.CloudflareTunnel;

        var builder = new WizardSecretsBuilder(Context.Paths);
        step.ContributeSecrets(builder);

        // Re-compute the hash from the raw token and device salt to verify consistency
        var rawToken = step.BootstrapRawToken!;
        var device = step.BootstrapDevice!;

        var tokenBytes = Base64Url.DecodeFromChars(rawToken);
        var saltBytes = Convert.FromHexString(device.Salt);
        Span<byte> combined = stackalloc byte[tokenBytes.Length + saltBytes.Length];
        tokenBytes.CopyTo(combined);
        saltBytes.CopyTo(combined[tokenBytes.Length..]);
        var expectedHash = Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant();

        Assert.Equal(expectedHash, device.TokenHash);
    }
}
