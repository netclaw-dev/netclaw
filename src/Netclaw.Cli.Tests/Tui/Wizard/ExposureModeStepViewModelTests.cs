using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class ExposureModeStepViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WizardContext _context;

    public ExposureModeStepViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        var paths = new NetclawPaths(_tempDir);
        paths.EnsureDirectoriesExist();

        _context = new WizardContext
        {
            Paths = paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = () => { }
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    // ── ContributeConfig ──────────────────────────────────────────────────────

    [Fact]
    public void ContributeConfig_Local_OmitsDaemonSection()
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.Local;

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.Null(builder.Daemon);
    }

    [Fact]
    public void ContributeConfig_TailscaleServe_WritesDaemonSection()
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.TailscaleServe;

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Daemon);
        Assert.Equal(ExposureMode.TailscaleServe, builder.Daemon.ExposureMode);
    }

    [Fact]
    public void ContributeConfig_TailscaleFunnel_WritesDaemonSection()
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.TailscaleFunnel;

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Daemon);
        Assert.Equal(ExposureMode.TailscaleFunnel, builder.Daemon.ExposureMode);
    }

    [Fact]
    public void ContributeConfig_CloudflareTunnel_WritesDaemonSection()
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.CloudflareTunnel;

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Daemon);
        Assert.Equal(ExposureMode.CloudflareTunnel, builder.Daemon.ExposureMode);
    }

    [Fact]
    public void ContributeConfig_DefaultMode_OmitsDaemonSection()
    {
        using var step = new ExposureModeStepViewModel();
        // Don't set SelectedMode — should default to Local

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.Null(builder.Daemon);
    }

    // ── BuildConfigDictionary integration ────────────────────────────────────

    [Fact]
    public void BuildConfigDictionary_TailscaleServe_WritesKebabCaseWireValue()
    {
        var builder = new WizardConfigBuilder(_context.Paths)
        {
            Daemon = new DaemonConfigSection { ExposureMode = ExposureMode.TailscaleServe }
        };

        var config = builder.BuildConfigDictionary();

        Assert.True(config.ContainsKey("Daemon"));
        var daemon = (System.Collections.Generic.Dictionary<string, object>)config["Daemon"];
        Assert.Equal("tailscale-serve", daemon["ExposureMode"]);
    }

    [Fact]
    public void BuildConfigDictionary_TailscaleFunnel_WritesKebabCaseWireValue()
    {
        var builder = new WizardConfigBuilder(_context.Paths)
        {
            Daemon = new DaemonConfigSection { ExposureMode = ExposureMode.TailscaleFunnel }
        };

        var config = builder.BuildConfigDictionary();

        Assert.True(config.ContainsKey("Daemon"));
        var daemon = (System.Collections.Generic.Dictionary<string, object>)config["Daemon"];
        Assert.Equal("tailscale-funnel", daemon["ExposureMode"]);
    }

    [Fact]
    public void BuildConfigDictionary_CloudflareTunnel_WritesKebabCaseWireValue()
    {
        var builder = new WizardConfigBuilder(_context.Paths)
        {
            Daemon = new DaemonConfigSection { ExposureMode = ExposureMode.CloudflareTunnel }
        };

        var config = builder.BuildConfigDictionary();

        Assert.True(config.ContainsKey("Daemon"));
        var daemon = (System.Collections.Generic.Dictionary<string, object>)config["Daemon"];
        Assert.Equal("cloudflare-tunnel", daemon["ExposureMode"]);
    }

    [Fact]
    public void BuildConfigDictionary_Local_OmitsDaemonKey()
    {
        var builder = new WizardConfigBuilder(_context.Paths)
        {
            Daemon = new DaemonConfigSection { ExposureMode = ExposureMode.Local }
        };

        var config = builder.BuildConfigDictionary();

        Assert.False(config.ContainsKey("Daemon"));
    }

    [Fact]
    public void BuildConfigDictionary_NullDaemon_OmitsDaemonKey()
    {
        var builder = new WizardConfigBuilder(_context.Paths);
        // Daemon is null by default

        var config = builder.BuildConfigDictionary();

        Assert.False(config.ContainsKey("Daemon"));
    }

    // ── ContributeConfig — Webhooks ─────────────────────────────────────────

    [Fact]
    public void ContributeConfig_WebhooksEnabled_WritesWebhooksSection()
    {
        using var step = new ExposureModeStepViewModel();
        step.WebhooksEnabled = true;

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Webhooks);
        Assert.True(builder.Webhooks.Enabled);
    }

    [Fact]
    public void ContributeConfig_WebhooksDisabled_OmitsWebhooksSection()
    {
        using var step = new ExposureModeStepViewModel();
        // WebhooksEnabled defaults to false

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.Null(builder.Webhooks);
    }

    // ── BuildConfigDictionary — Webhooks ─────────────────────────────────────

    [Fact]
    public void BuildConfigDictionary_WebhooksEnabled_WritesEnabledTrue()
    {
        var builder = new WizardConfigBuilder(_context.Paths)
        {
            Webhooks = new WebhooksConfigSection { Enabled = true }
        };

        var config = builder.BuildConfigDictionary();

        Assert.True(config.ContainsKey("Webhooks"));
        var webhooks = (Dictionary<string, object>)config["Webhooks"];
        Assert.Equal(true, webhooks["Enabled"]);
    }

    [Fact]
    public void BuildConfigDictionary_WebhooksDisabled_OmitsWebhooksKey()
    {
        var builder = new WizardConfigBuilder(_context.Paths)
        {
            Webhooks = new WebhooksConfigSection { Enabled = false }
        };

        var config = builder.BuildConfigDictionary();

        Assert.False(config.ContainsKey("Webhooks"));
    }

    [Fact]
    public void BuildConfigDictionary_NullWebhooks_OmitsWebhooksKey()
    {
        var builder = new WizardConfigBuilder(_context.Paths);

        var config = builder.BuildConfigDictionary();

        Assert.False(config.ContainsKey("Webhooks"));
    }

    // ── Sub-step navigation ───────────────────────────────────────────────────

    [Fact]
    public void TryAdvance_LocalMode_AdvancesToWebhookSubStep()
    {
        using var step = new ExposureModeStepViewModel();
        step.OnEnter(_context, NavigationDirection.Forward);
        step.SelectedMode = ExposureMode.Local;

        var result = step.TryAdvance();

        Assert.True(result);
        Assert.Equal(1, step.CurrentSubStep); // webhook toggle
    }

    [Fact]
    public void TryAdvance_TailscaleServe_AdvancesToConfirmation()
    {
        using var step = new ExposureModeStepViewModel();
        step.OnEnter(_context, NavigationDirection.Forward);
        step.SelectedMode = ExposureMode.TailscaleServe;

        var result = step.TryAdvance();

        Assert.True(result);
        Assert.Equal(1, step.CurrentSubStep); // confirmation
    }

    [Fact]
    public void TryAdvance_TailscaleFunnel_AdvancesToConfirmation()
    {
        using var step = new ExposureModeStepViewModel();
        step.OnEnter(_context, NavigationDirection.Forward);
        step.SelectedMode = ExposureMode.TailscaleFunnel;

        var result = step.TryAdvance();

        Assert.True(result);
        Assert.Equal(1, step.CurrentSubStep); // confirmation
    }

    [Fact]
    public void TryAdvance_NonLocal_FromConfirmation_AdvancesToWebhookSubStep()
    {
        using var step = new ExposureModeStepViewModel();
        step.OnEnter(_context, NavigationDirection.Forward);
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
        step.OnEnter(_context, NavigationDirection.Forward);
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
        step.OnEnter(_context, NavigationDirection.Forward);
        step.SelectedMode = ExposureMode.Local;
        step.TryAdvance(); // → webhook toggle

        var result = step.TryAdvance();

        Assert.False(result); // step complete
    }

    [Fact]
    public void TryGoBack_FromWebhookSubStep_ReturnsTrue()
    {
        using var step = new ExposureModeStepViewModel();
        step.OnEnter(_context, NavigationDirection.Forward);
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
        step.OnEnter(_context, NavigationDirection.Forward);
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
        step.OnEnter(_context, NavigationDirection.Forward);

        var result = step.TryGoBack();

        Assert.False(result);
    }

    [Fact]
    public void IsHighRisk_TailscaleFunnel_IsTrue()
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.TailscaleFunnel;

        Assert.True(step.IsHighRisk);
    }

    [Fact]
    public void IsHighRisk_CloudflareTunnel_IsTrue()
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.CloudflareTunnel;

        Assert.True(step.IsHighRisk);
    }

    [Fact]
    public void IsHighRisk_TailscaleServe_IsFalse()
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.TailscaleServe;

        Assert.False(step.IsHighRisk);
    }

    [Fact]
    public void IsHighRisk_Local_IsFalse()
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.Local;

        Assert.False(step.IsHighRisk);
    }

    [Fact]
    public void SubStepCount_Local_IsTwo()
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.Local;

        Assert.Equal(2, step.SubStepCount);
    }

    [Fact]
    public void SubStepCount_NonLocal_IsThree()
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.TailscaleFunnel;

        Assert.Equal(3, step.SubStepCount);
    }

    [Fact]
    public void WebhookSubStep_Local_IsOne()
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.Local;

        Assert.Equal(1, step.WebhookSubStep);
    }

    [Fact]
    public void WebhookSubStep_NonLocal_IsTwo()
    {
        using var step = new ExposureModeStepViewModel();
        step.SelectedMode = ExposureMode.TailscaleFunnel;

        Assert.Equal(2, step.WebhookSubStep);
    }
}
