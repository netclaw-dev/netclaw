using System.Text.Json;
using Netclaw.Cli.Provider;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers.OAuth;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

public sealed class ProviderManagerViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;
    private readonly FakeProviderProbe _fakeProbe = new();

    public ProviderManagerViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    /// <summary>
    /// Simulates activation without requiring a bound Page (which provides the Input stream).
    /// Calls RefreshDisplayProviders + ProbeAllConfiguredAsync directly.
    /// </summary>
    private async Task ActivateAndProbeAsync(ProviderManagerViewModel vm)
    {
        vm.RefreshDisplayProviders();
        await vm.ProbeAllConfiguredAsync();
    }

    [Fact]
    public void StartsAtLoadingState()
    {
        using var vm = CreateViewModel();
        Assert.Equal(ProviderManagerState.Loading, vm.CurrentState.Value);
    }

    [Fact]
    public void DisplayProviders_ShowsAllKnownTypes()
    {
        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();

        Assert.Equal(4, vm.DisplayProviders.Count);
        foreach (var type in new[] { "ollama", "openai", "anthropic", "openrouter" })
        {
            Assert.Contains(vm.DisplayProviders, p => p.ProviderType == type);
        }
    }

    [Fact]
    public void DisplayProviders_AllUnconfiguredByDefault()
    {
        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();

        Assert.All(vm.DisplayProviders, p =>
        {
            Assert.False(p.IsConfigured);
            Assert.Null(p.ConfiguredName);
            Assert.Null(p.Entry);
            Assert.StartsWith("(", p.DisplayEndpoint);
        });
    }

    [Fact]
    public void DisplayProviders_MergesConfiguredWithKnown()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-openrouter"] = new Dictionary<string, object>
                {
                    ["Type"] = "openrouter",
                    ["Endpoint"] = "https://openrouter.ai/api/v1",
                    ["AuthMethod"] = "ApiKey"
                }
            }
        });

        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();

        // All 4 types present
        Assert.Equal(4, vm.DisplayProviders.Count);

        // openrouter is configured
        var openrouter = vm.DisplayProviders.First(p => p.ProviderType == "openrouter");
        Assert.True(openrouter.IsConfigured);
        Assert.Equal("my-openrouter", openrouter.ConfiguredName);
        Assert.NotNull(openrouter.Entry);
        Assert.Equal("ApiKey", openrouter.DisplayAuth);

        // others are not configured
        var ollama = vm.DisplayProviders.First(p => p.ProviderType == "ollama");
        Assert.False(ollama.IsConfigured);
    }

    [Fact]
    public async Task EagerProbe_ProbesAllConfigured()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-openrouter"] = new Dictionary<string, object>
                {
                    ["Type"] = "openrouter",
                    ["Endpoint"] = "https://openrouter.ai/api/v1",
                    ["AuthMethod"] = "ApiKey"
                },
                ["my-ollama"] = new Dictionary<string, object>
                {
                    ["Type"] = "ollama",
                    ["Endpoint"] = "http://localhost:11434"
                }
            }
        });

        using var vm = CreateViewModel();
        await ActivateAndProbeAsync(vm);

        // Both configured providers should have been probed
        Assert.Contains("openrouter", _fakeProbe.ProbedTypes);
        Assert.Contains("ollama", _fakeProbe.ProbedTypes);
        Assert.Equal(2, _fakeProbe.ProbeCallCount);
    }

    [Fact]
    public async Task EagerProbe_UsesOAuthAccessTokenWhenApiKeyMissing()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-openai"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai",
                    ["Endpoint"] = "https://api.openai.com",
                    ["AuthMethod"] = "OAuthDevice"
                }
            }
        });

        WriteSecrets(new Dictionary<string, object>
        {
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-openai"] = new Dictionary<string, object>
                {
                    ["OAuthAccessToken"] = "oauth-access-token"
                }
            }
        });

        using var vm = CreateViewModel();
        await ActivateAndProbeAsync(vm);

        Assert.Equal("openai", _fakeProbe.LastProviderType);
        Assert.Equal("oauth-access-token", _fakeProbe.LastApiKey);
    }

    [Fact]
    public async Task EagerProbe_CompletesToListState()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-ollama"] = new Dictionary<string, object>
                {
                    ["Type"] = "ollama",
                    ["Endpoint"] = "http://localhost:11434"
                }
            }
        });

        using var vm = CreateViewModel();
        await ActivateAndProbeAsync(vm);

        Assert.Equal(ProviderManagerState.List, vm.CurrentState.Value);
    }

    [Fact]
    public async Task EagerProbe_SetsHealthOnItems()
    {
        _fakeProbe.TypeResults["openrouter"] = new ProviderProbeResult(true, null,
            [new DiscoveredModel { ModelId = "gpt-4" }]);
        _fakeProbe.TypeResults["ollama"] = new ProviderProbeResult(false, "Connection refused", []);

        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-openrouter"] = new Dictionary<string, object>
                {
                    ["Type"] = "openrouter",
                    ["Endpoint"] = "https://openrouter.ai/api/v1",
                    ["AuthMethod"] = "ApiKey"
                },
                ["my-ollama"] = new Dictionary<string, object>
                {
                    ["Type"] = "ollama",
                    ["Endpoint"] = "http://localhost:11434"
                }
            }
        });

        using var vm = CreateViewModel();
        await ActivateAndProbeAsync(vm);

        var openrouter = vm.DisplayProviders.First(p => p.ProviderType == "openrouter");
        Assert.Equal(ProviderHealthStatus.Healthy, openrouter.Health);

        var ollama = vm.DisplayProviders.First(p => p.ProviderType == "ollama");
        Assert.Equal(ProviderHealthStatus.Unhealthy, ollama.Health);
    }

    [Fact]
    public async Task EagerProbe_NoConfiguredProviders_GoesDirectlyToList()
    {
        using var vm = CreateViewModel();
        await ActivateAndProbeAsync(vm);

        Assert.Equal(ProviderManagerState.List, vm.CurrentState.Value);
        Assert.Equal(0, _fakeProbe.ProbeCallCount);
    }

    [Fact]
    public void ActivateSelectedProvider_Unconfigured_StartsAddFlow()
    {
        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();
        vm.CurrentState.Value = ProviderManagerState.List;

        // Find an unconfigured provider (all are unconfigured with empty config)
        var ollamaIndex = vm.DisplayProviders.FindIndex(p => p.ProviderType == "ollama");
        vm.SelectedProviderIndex = ollamaIndex;
        vm.ActivateSelectedProvider();

        // Ollama has [None] auth, so goes straight to AddCredentials
        Assert.Equal(ProviderManagerState.AddCredentials, vm.CurrentState.Value);
        Assert.Equal("ollama", vm.NewProviderType);
    }

    [Fact]
    public void ActivateSelectedProvider_Unconfigured_ApiKeyProvider_GoesToAuthSelect()
    {
        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();
        vm.CurrentState.Value = ProviderManagerState.List;

        var anthropicIndex = vm.DisplayProviders.FindIndex(p => p.ProviderType == "anthropic");
        vm.SelectedProviderIndex = anthropicIndex;
        vm.ActivateSelectedProvider();

        Assert.Equal(ProviderManagerState.AddSelectAuth, vm.CurrentState.Value);
        Assert.Equal("anthropic", vm.NewProviderType);
    }

    [Fact]
    public async Task ActivateSelectedProvider_Healthy_TransitionsToDetails()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-openrouter"] = new Dictionary<string, object>
                {
                    ["Type"] = "openrouter",
                    ["Endpoint"] = "https://openrouter.ai/api/v1",
                    ["AuthMethod"] = "ApiKey"
                }
            }
        });

        using var vm = CreateViewModel();
        await ActivateAndProbeAsync(vm);

        var openrouterIndex = vm.DisplayProviders.FindIndex(p => p.ProviderType == "openrouter");
        vm.SelectedProviderIndex = openrouterIndex;
        vm.ActivateSelectedProvider();

        Assert.Equal(ProviderManagerState.Details, vm.CurrentState.Value);
        Assert.NotNull(vm.DetailProvider);
        Assert.Equal("openrouter", vm.DetailProvider.ProviderType);
    }

    [Fact]
    public async Task ActivateSelectedProvider_Unhealthy_TransitionsToFixCredentials()
    {
        _fakeProbe.NextResult = new ProviderProbeResult(false, "Unauthorized", []);

        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-openrouter"] = new Dictionary<string, object>
                {
                    ["Type"] = "openrouter",
                    ["Endpoint"] = "https://openrouter.ai/api/v1",
                    ["AuthMethod"] = "ApiKey"
                }
            }
        });

        using var vm = CreateViewModel();
        await ActivateAndProbeAsync(vm);

        var openrouterIndex = vm.DisplayProviders.FindIndex(p => p.ProviderType == "openrouter");
        vm.SelectedProviderIndex = openrouterIndex;
        vm.ActivateSelectedProvider();

        Assert.Equal(ProviderManagerState.FixCredentials, vm.CurrentState.Value);
        Assert.NotNull(vm.DetailProvider);
        Assert.True(vm.IsFixFlow);
    }

    [Fact]
    public async Task FixCredentials_Success_ReturnsToList()
    {
        // Start with a failed probe
        _fakeProbe.TypeResults["openrouter"] = new ProviderProbeResult(false, "Unauthorized", []);

        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-openrouter"] = new Dictionary<string, object>
                {
                    ["Type"] = "openrouter",
                    ["Endpoint"] = "https://openrouter.ai/api/v1",
                    ["AuthMethod"] = "ApiKey"
                }
            }
        });

        using var vm = CreateViewModel();
        await ActivateAndProbeAsync(vm);

        // Navigate to fix credentials
        var idx = vm.DisplayProviders.FindIndex(p => p.ProviderType == "openrouter");
        vm.SelectedProviderIndex = idx;
        vm.ActivateSelectedProvider();
        Assert.Equal(ProviderManagerState.FixCredentials, vm.CurrentState.Value);

        // Now the re-probe should succeed — update the per-type result
        _fakeProbe.TypeResults["openrouter"] = new ProviderProbeResult(true, null,
            [new DiscoveredModel { ModelId = "model-a" }]);

        vm.FixApiKey = "sk-new-key";
        vm.SubmitFixCredentials();

        // Wait for the single-provider probe to complete
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5));
        // Fix flow triggers RefreshAndProbeAll — wait for the eager re-probe too
        await vm.EagerProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ProviderManagerState.List, vm.CurrentState.Value);
        Assert.False(vm.IsFixFlow);
    }

    [Fact]
    public async Task Details_RemoveAction_TransitionsToRemoveConfirm()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-openrouter"] = new Dictionary<string, object>
                {
                    ["Type"] = "openrouter",
                    ["Endpoint"] = "https://openrouter.ai/api/v1",
                    ["AuthMethod"] = "ApiKey"
                }
            }
        });

        using var vm = CreateViewModel();
        await ActivateAndProbeAsync(vm);

        // Navigate to details
        var idx = vm.DisplayProviders.FindIndex(p => p.ProviderType == "openrouter");
        vm.SelectedProviderIndex = idx;
        vm.ActivateSelectedProvider();
        Assert.Equal(ProviderManagerState.Details, vm.CurrentState.Value);

        // Remove action
        vm.StartRemove();
        Assert.Equal(ProviderManagerState.RemoveConfirm, vm.CurrentState.Value);
        Assert.Equal("my-openrouter", vm.RemoveProviderName);
    }

    [Fact]
    public async Task AddProvider_WritesCorrectConfigStructure()
    {
        using var vm = CreateViewModel();
        await ActivateAndProbeAsync(vm);

        // Start add flow for openrouter (unconfigured)
        var idx = vm.DisplayProviders.FindIndex(p => p.ProviderType == "openrouter");
        vm.SelectedProviderIndex = idx;
        vm.ActivateSelectedProvider();

        vm.SelectAuthMethod(AuthMethod.ApiKey);
        vm.NewApiKey = "sk-test-key";
        vm.SubmitCredentials();

        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        vm.ConfirmAdd();

        // ConfirmAdd triggers RefreshAndProbeAll — wait for re-probe
        await vm.EagerProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ProviderManagerState.List, vm.CurrentState.Value);

        // Verify config file
        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        Assert.True(config.RootElement.TryGetProperty("Providers", out var providers));
        var providerNames = providers.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Single(providerNames);
        Assert.StartsWith("my-openrouter", providerNames[0]);

        // Verify secrets file
        var secrets = JsonDocument.Parse(File.ReadAllText(_paths.SecretsPath));
        Assert.True(secrets.RootElement.TryGetProperty("Providers", out _));
    }

    [Fact]
    public async Task SubmitCredentials_WhenProbeThrows_ReportsFailureAndStopsProbing()
    {
        _fakeProbe.ExceptionToThrow = new InvalidOperationException("simulated probe failure");

        using var vm = CreateViewModel();
        await ActivateAndProbeAsync(vm);

        var idx = vm.DisplayProviders.FindIndex(p => p.ProviderType == "openrouter");
        vm.SelectedProviderIndex = idx;
        vm.ActivateSelectedProvider();
        vm.SelectAuthMethod(AuthMethod.ApiKey);

        vm.NewApiKey = "sk-test";
        vm.SubmitCredentials();
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsProbing.Value);
        Assert.NotNull(vm.ProbeResult.Value);
        Assert.False(vm.ProbeResult.Value!.Success);
        Assert.Contains("simulated probe failure", vm.ProbeResult.Value.ErrorMessage);
    }

    [Fact]
    public async Task ConfirmAdd_OAuth_TokensPersistOnlyOnSave()
    {
        using var vm = CreateViewModel();
        vm.NewProviderName = "my-openai";
        vm.NewProviderType = "openai";
        vm.NewAuthMethod = AuthMethod.OAuthDevice;
        vm.NewEndpoint = "https://api.openai.com";
        vm.OAuthResult = new OAuthDeviceFlowResult(
            new SensitiveString("oauth-access-token"),
            new SensitiveString("oauth-refresh-token"),
            DateTimeOffset.UtcNow.AddHours(1));

        Assert.False(File.Exists(_paths.SecretsPath));

        vm.ConfirmAdd();
        await vm.EagerProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(File.Exists(_paths.SecretsPath));

        var secrets = JsonDocument.Parse(File.ReadAllText(_paths.SecretsPath));
        var provider = secrets.RootElement
            .GetProperty("Providers")
            .GetProperty("my-openai");

        Assert.StartsWith("ENC:", provider.GetProperty("OAuthAccessToken").GetString());
        Assert.StartsWith("ENC:", provider.GetProperty("OAuthRefreshToken").GetString());
    }

    [Fact]
    public async Task RemoveProvider_ReferencedByModelRole_IsRejected()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-ollama"] = new Dictionary<string, object>
                {
                    ["Type"] = "ollama",
                    ["Endpoint"] = "http://localhost:11434"
                }
            },
            ["Models"] = new Dictionary<string, object>
            {
                ["Main"] = new Dictionary<string, object>
                {
                    ["Provider"] = "my-ollama",
                    ["ModelId"] = "qwen3:30b"
                }
            }
        });

        using var vm = CreateViewModel();
        await ActivateAndProbeAsync(vm);

        // Navigate to details for ollama
        var idx = vm.DisplayProviders.FindIndex(p => p.ProviderType == "ollama");
        vm.SelectedProviderIndex = idx;
        vm.ActivateSelectedProvider();

        // Remove action
        vm.StartRemove();

        Assert.Equal(ProviderManagerState.RemoveConfirm, vm.CurrentState.Value);
        Assert.NotEmpty(vm.RemoveBlockingRoles);
        Assert.Contains("Main", vm.RemoveBlockingRoles);
    }

    [Fact]
    public void GoBack_FromAddSelectAuth_ReturnsToList()
    {
        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();
        vm.CurrentState.Value = ProviderManagerState.List;

        var idx = vm.DisplayProviders.FindIndex(p => p.ProviderType == "anthropic");
        vm.SelectedProviderIndex = idx;
        vm.ActivateSelectedProvider();
        Assert.Equal(ProviderManagerState.AddSelectAuth, vm.CurrentState.Value);

        vm.GoBack();
        Assert.Equal(ProviderManagerState.List, vm.CurrentState.Value);
    }

    [Fact]
    public void GoBack_FromDetails_ReturnsToList()
    {
        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();

        // Manually set up a details state
        var item = new ProviderDisplayItem
        {
            ProviderType = "openrouter",
            IsConfigured = true,
            ConfiguredName = "my-openrouter",
            Health = ProviderHealthStatus.Healthy
        };
        vm.DetailProvider = item;
        vm.CurrentState.Value = ProviderManagerState.Details;

        vm.GoBack();
        Assert.Equal(ProviderManagerState.List, vm.CurrentState.Value);
        Assert.Null(vm.DetailProvider);
    }

    [Fact]
    public void GoBack_FromFixCredentials_ReturnsToList()
    {
        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();

        vm.CurrentState.Value = ProviderManagerState.FixCredentials;

        vm.GoBack();
        Assert.Equal(ProviderManagerState.List, vm.CurrentState.Value);
    }

    [Fact]
    public void GoBack_FromList_ShutdownSignal()
    {
        using var vm = CreateViewModel();
        vm.CurrentState.Value = ProviderManagerState.List;
        // GoBack from list should call Shutdown (which we can't easily test without a host,
        // but we can verify it doesn't crash)
        vm.GoBack();
    }

    private ProviderManagerViewModel CreateViewModel()
    {
        return new ProviderManagerViewModel(_paths, ProviderCommand.CreateDefaultRegistry(), _fakeProbe);
    }

    private void WriteConfig(Dictionary<string, object> data)
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void WriteSecrets(Dictionary<string, object> data)
    {
        File.WriteAllText(_paths.SecretsPath,
            JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }
}
