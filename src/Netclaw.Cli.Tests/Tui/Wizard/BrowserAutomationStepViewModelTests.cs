using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class BrowserAutomationStepViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WizardContext _context;

    public BrowserAutomationStepViewModelTests()
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

    [Fact]
    public void SubStepCount_IsOne_WhenDisabled()
    {
        using var step = new BrowserAutomationStepViewModel(false, "test");
        step.Enabled = false;
        Assert.Equal(1, step.SubStepCount);
    }

    [Fact]
    public void SubStepCount_IsTwo_WhenEnabled()
    {
        using var step = new BrowserAutomationStepViewModel(false, "test");
        step.Enabled = true;
        Assert.Equal(2, step.SubStepCount);
    }

    [Fact]
    public void TryAdvance_ReturnsFalse_WhenDisabled()
    {
        using var step = new BrowserAutomationStepViewModel(false, "test");
        step.Enabled = false;
        Assert.False(step.TryAdvance());
    }

    [Fact]
    public void TryAdvance_AdvancesToBackendSelection_WhenEnabled()
    {
        using var step = new BrowserAutomationStepViewModel(false, "test");
        step.Enabled = true;
        Assert.True(step.TryAdvance());
        Assert.Equal(1, step.CurrentSubStep);
    }

    [Fact]
    public void TryGoBack_FromBackend_ReturnsToEnable()
    {
        using var step = new BrowserAutomationStepViewModel(false, "test");
        step.Enabled = true;
        step.TryAdvance(); // → sub-step 1

        Assert.True(step.TryGoBack());
        Assert.Equal(0, step.CurrentSubStep);
    }

    [Fact]
    public void OnEnter_Back_ResumesAtLastSubStep()
    {
        using var step = new BrowserAutomationStepViewModel(false, "test");
        step.Enabled = true;
        step.TryAdvance(); // → sub-step 1

        step.OnEnter(_context, NavigationDirection.Back);
        Assert.Equal(1, step.CurrentSubStep);
    }

    [Fact]
    public void ContributeConfig_SetsBackend_WhenEnabled()
    {
        using var step = new BrowserAutomationStepViewModel(false, "test");
        step.Enabled = true;
        step.SelectedBackend = BrowserAutomationBackend.Playwright;

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.BrowserAutomation);
        Assert.True(builder.BrowserAutomation!.Enabled);
        Assert.Equal(BrowserAutomationBackend.Playwright, builder.BrowserAutomation.Backend);
    }

    [Fact]
    public void ContributeConfig_NoSection_WhenDisabled()
    {
        using var step = new BrowserAutomationStepViewModel(false, "test");
        step.Enabled = false;

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.Null(builder.BrowserAutomation);
    }

    [Fact]
    public void DefaultBackend_IsPlaywright()
    {
        using var step = new BrowserAutomationStepViewModel(false, "test");
        Assert.Equal(BrowserAutomationBackend.Playwright, step.SelectedBackend);
    }
}
