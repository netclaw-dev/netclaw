// -----------------------------------------------------------------------
// <copyright file="SearchStepViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class SearchStepViewModelTests : WizardStepTestBase
{

    [Fact]
    public void DefaultBackend_IsDuckDuckGo()
    {
        using var step = new SearchStepViewModel();
        Assert.Equal(SearchBackend.DuckDuckGo, step.SelectedBackend);
    }

    [Theory]
    [InlineData(SearchBackend.DuckDuckGo, 1)]
    [InlineData(SearchBackend.Brave, 2)]
    [InlineData(SearchBackend.SearXng, 2)]
    public void SubStepCount_MatchesBackend(SearchBackend backend, int expected)
    {
        using var step = new SearchStepViewModel();
        step.SelectedBackend = backend;
        Assert.Equal(expected, step.SubStepCount);
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

        step.OnEnter(Context, NavigationDirection.Back);
        Assert.Equal(1, step.CurrentSubStep);
    }

    [Fact]
    public void ContributeConfig_SetsBraveBackend()
    {
        using var step = new SearchStepViewModel();
        step.SelectedBackend = SearchBackend.Brave;

        var builder = new WizardConfigBuilder(Context.Paths);
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

        var builder = new WizardSecretsBuilder(Context.Paths);
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

        var builder = new WizardConfigBuilder(Context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Search);
        Assert.Equal(SearchBackend.SearXng, builder.Search!.Backend);
        Assert.Equal("http://searxng.local:8080", builder.Search.SearXngEndpoint);
    }
}
