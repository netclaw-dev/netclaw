using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class SearchStepViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WizardContext _context;

    public SearchStepViewModelTests()
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
    public void DefaultBackend_IsDuckDuckGo()
    {
        using var step = new SearchStepViewModel();
        Assert.Equal(SearchBackend.DuckDuckGo, step.SelectedBackend);
    }

    [Fact]
    public void SubStepCount_IsOne_ForDuckDuckGo()
    {
        using var step = new SearchStepViewModel();
        step.SelectedBackend = SearchBackend.DuckDuckGo;
        Assert.Equal(1, step.SubStepCount);
    }

    [Fact]
    public void SubStepCount_IsTwo_ForBrave()
    {
        using var step = new SearchStepViewModel();
        step.SelectedBackend = SearchBackend.Brave;
        Assert.Equal(2, step.SubStepCount);
    }

    [Fact]
    public void TryAdvance_ReturnsFalse_ForDuckDuckGo()
    {
        using var step = new SearchStepViewModel();
        step.SelectedBackend = SearchBackend.DuckDuckGo;
        Assert.False(step.TryAdvance());
    }

    [Fact]
    public void TryAdvance_AdvancesToCredentials_ForBrave()
    {
        using var step = new SearchStepViewModel();
        step.SelectedBackend = SearchBackend.Brave;
        Assert.True(step.TryAdvance());
        Assert.Equal(1, step.CurrentSubStep);
    }

    [Fact]
    public void TryGoBack_FromCredentials_ReturnsToBackendSelection()
    {
        using var step = new SearchStepViewModel();
        step.SelectedBackend = SearchBackend.Brave;
        step.TryAdvance(); // → sub-step 1

        Assert.True(step.TryGoBack());
        Assert.Equal(0, step.CurrentSubStep);
    }

    [Fact]
    public void OnEnter_Back_ResumesAtLastSubStep()
    {
        using var step = new SearchStepViewModel();
        step.SelectedBackend = SearchBackend.Brave;
        step.TryAdvance(); // → sub-step 1

        step.OnEnter(_context, NavigationDirection.Back);
        Assert.Equal(1, step.CurrentSubStep);
    }

    [Fact]
    public void ContributeConfig_SetsBraveBackend()
    {
        using var step = new SearchStepViewModel();
        step.SelectedBackend = SearchBackend.Brave;

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Search);
        Assert.Equal(SearchBackend.Brave, builder.Search!.Backend);
    }

    [Fact]
    public void ContributeSecrets_AddsBraveApiKey()
    {
        using var step = new SearchStepViewModel();
        step.SelectedBackend = SearchBackend.Brave;
        step.BraveApiKey = "BSA-test-key";

        var builder = new WizardSecretsBuilder(_context.Paths);
        step.ContributeSecrets(builder);

        // Secrets builder doesn't expose contents directly, but we can verify
        // it doesn't throw. Full integration test covers file output.
    }

    [Fact]
    public void ContributeConfig_SetsSearXngEndpoint()
    {
        using var step = new SearchStepViewModel();
        step.SelectedBackend = SearchBackend.SearXng;
        step.SearXngEndpoint = "http://searxng.local:8080";

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Search);
        Assert.Equal(SearchBackend.SearXng, builder.Search!.Backend);
        Assert.Equal("http://searxng.local:8080", builder.Search.SearXngEndpoint);
    }
}
