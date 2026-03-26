using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class SecurityPostureStepViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WizardContext _context;

    public SecurityPostureStepViewModelTests()
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
    public void OnLeave_PublishesPostureToContext()
    {
        using var step = new SecurityPostureStepViewModel();
        step.OnEnter(_context, NavigationDirection.Forward);
        step.SelectedPosture = DeploymentPosture.Team;

        step.OnLeave();

        Assert.Equal(DeploymentPosture.Team, _context.SelectedPosture);
    }

    [Fact]
    public void ContributeConfig_Personal_SetsHostAllowed()
    {
        using var step = new SecurityPostureStepViewModel();
        step.SelectedPosture = DeploymentPosture.Personal;

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.Equal(DeploymentPosture.Personal, builder.Security.DeploymentPosture);
        Assert.Equal(ShellExecutionMode.HostAllowed, builder.Security.ShellExecutionMode);
    }

    [Fact]
    public void ContributeConfig_Team_SetsShellOff()
    {
        using var step = new SecurityPostureStepViewModel();
        step.SelectedPosture = DeploymentPosture.Team;

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.Equal(DeploymentPosture.Team, builder.Security.DeploymentPosture);
        Assert.Equal(ShellExecutionMode.Off, builder.Security.ShellExecutionMode);
    }

    [Fact]
    public void ContributeConfig_Public_SetsShellOff()
    {
        using var step = new SecurityPostureStepViewModel();
        step.SelectedPosture = DeploymentPosture.Public;

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.Equal(DeploymentPosture.Public, builder.Security.DeploymentPosture);
        Assert.Equal(ShellExecutionMode.Off, builder.Security.ShellExecutionMode);
    }

    [Fact]
    public void ContributeConfig_NullPosture_DefaultsToPersonal()
    {
        using var step = new SecurityPostureStepViewModel();
        // Don't set SelectedPosture

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.Equal(DeploymentPosture.Personal, builder.Security.DeploymentPosture);
        Assert.Equal(ShellExecutionMode.HostAllowed, builder.Security.ShellExecutionMode);
    }

    [Fact]
    public void ContributeConfig_SetsToolConfig()
    {
        using var step = new SecurityPostureStepViewModel();
        step.SelectedPosture = DeploymentPosture.Personal;

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Tools);
        Assert.Equal(ShellExecutionMode.HostAllowed, builder.Tools.ShellMode);
    }
}
