// -----------------------------------------------------------------------
// <copyright file="ProviderStepViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Provider;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class ProviderStepViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WizardContext _context;
    private readonly FakeProviderProbe _fakeProbe = new();
    private readonly ProviderDescriptorRegistry _registry = ProviderCommand.CreateDefaultRegistry();

    public ProviderStepViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        var paths = new NetclawPaths(_tempDir);
        paths.EnsureDirectoriesExist();
        _context = new WizardContext
        {
            Paths = paths,
            Registry = _registry,
            RequestRedraw = () => { }
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [Fact]
    public void SetSubStep_AdvancesToGivenStep()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        step.SetSubStep(3);
        Assert.Equal(3, step.CurrentSubStep);
    }

    [Fact]
    public void TryGoBack_FromValidation_GoesToCredentials()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        step.SelectedAuthMethod = AuthMethod.ApiKey;
        step.SetSubStep(3); // validation

        Assert.True(step.TryGoBack());
        Assert.Equal(2, step.CurrentSubStep); // credentials
    }

    [Fact]
    public void TryGoBack_FromValidation_WithOAuthDevice_GoesToOAuth()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        step.SelectedAuthMethod = AuthMethod.OAuthDevice;
        step.SetSubStep(3);

        Assert.True(step.TryGoBack());
        Assert.Equal(5, step.CurrentSubStep); // OAuth device flow
    }

    [Fact]
    public void TryGoBack_FromModelSelection_GoesToCredentials()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        step.SetSubStep(4); // model selection

        Assert.True(step.TryGoBack());
        Assert.Equal(2, step.CurrentSubStep);
    }

    [Fact]
    public void TryGoBack_FromOAuthDevice_GoesToAuth()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        step.SetSubStep(5); // OAuth device

        Assert.True(step.TryGoBack());
        Assert.Equal(1, step.CurrentSubStep); // auth method
    }

    [Fact]
    public void TryGoBack_FromFirstStep_ReturnsFalse()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        Assert.False(step.TryGoBack());
    }

    [Fact]
    public void OnEnter_Back_ResumesAtLastSubStep()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        step.SetSubStep(4); // model selection

        step.OnEnter(_context, NavigationDirection.Back);
        Assert.Equal(4, step.CurrentSubStep);
    }

    [Fact]
    public void ClearFromProvider_ResetsAllState()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        step.SelectedProviderType = "openai";
        step.SelectedAuthMethod = AuthMethod.ApiKey;
        step.ApiKeyInput = "sk-test";
        step.EndpointInput = "https://api.openai.com";
        step.SelectedModelId = "gpt-4.1";

        step.ClearFromProvider();

        Assert.Equal(AuthMethod.None, step.SelectedAuthMethod);
        Assert.Null(step.ApiKeyInput);
        Assert.Null(step.EndpointInput);
        Assert.Null(step.SelectedModelId);
        Assert.Empty(step.DiscoveredModels);
    }

    [Fact]
    public async Task ProbeProvider_StoresDiscoveredModels()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        step.SelectedProviderType = "ollama";
        step.EndpointInput = "http://localhost:11434";

        _fakeProbe.NextResult = new ProviderProbeResult(true, null,
        [
            new DiscoveredModel { ModelId = "llama3:latest" },
            new DiscoveredModel { ModelId = "qwen3:30b" }
        ]);

        step.StartProbe();
        await step.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(step.ProbeResult.Value!.Success);
        Assert.Equal(2, step.DiscoveredModels.Count);
    }

    [Fact]
    public async Task ProbeProvider_ReportsFailure()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        step.SelectedProviderType = "openai";
        step.ApiKeyInput = "bad-key";

        _fakeProbe.NextResult = new ProviderProbeResult(false, "Invalid API key", []);

        step.StartProbe();
        await step.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(step.ProbeResult.Value!.Success);
        Assert.Contains("Invalid API key", step.ProbeResult.Value.ErrorMessage);
    }

    [Fact]
    public void ContributeConfig_SetsProviderAndModel()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        step.SelectedProviderType = "OpenAI";
        step.SelectedAuthMethod = AuthMethod.ApiKey;
        step.SelectedModelId = "gpt-4.1";

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Provider);
        Assert.Equal("openai", builder.Provider!.TypeKey);
        Assert.Equal(AuthMethod.ApiKey, builder.Provider.AuthMethod);
        Assert.NotNull(builder.Model);
        Assert.Equal("gpt-4.1", builder.Model!.ModelId);
    }

    [Fact]
    public void ContributeConfig_NoProvider_NoSection()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.Null(builder.Provider);
        Assert.Null(builder.Model);
    }

    // Reuse the existing FakeProviderProbe from the monolith tests
    private sealed class FakeProviderProbe : IProviderProbe
    {
        public ProviderProbeResult NextResult { get; set; } = new(false, "not configured", []);
        public string? LastProviderType { get; private set; }
        public string? LastApiKey { get; private set; }

        public Task<ProviderProbeResult> ProbeAsync(
            string providerType, string? endpoint, string? apiKey,
            CancellationToken ct = default)
        {
            LastProviderType = providerType;
            LastApiKey = apiKey;
            return Task.FromResult(NextResult);
        }

        public Task<ProviderProbeResult> ProbeAsync(
            ProviderEntry entry, CancellationToken ct = default)
            => Task.FromResult(NextResult);

        public Task<ProviderProbeResult> ProbeAsync(
            string providerType, string? endpoint, string? credential,
            AuthMethod authMethod, CancellationToken ct = default)
        {
            LastProviderType = providerType;
            LastApiKey = credential;
            return Task.FromResult(NextResult);
        }
    }
}
