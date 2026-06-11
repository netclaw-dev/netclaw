// -----------------------------------------------------------------------
// <copyright file="ProviderManagerViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using R3;
using Netclaw.Cli.Provider;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Providers.OAuth;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

public sealed class ProviderManagerViewModelTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeProviderProbe _fakeProbe = new();

    public ProviderManagerViewModelTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

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

        Assert.Equal(7, vm.DisplayProviders.Count);
        foreach (var type in new[] { "ollama", "openai", "anthropic", "openrouter", "openai-compatible", "github-copilot", "veniceai" })
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

        // All known types present
        Assert.Equal(7, vm.DisplayProviders.Count);

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
            [new DiscoveredModel { ModelId = new Netclaw.Configuration.ModelId("gpt-4") }]);
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
    public void ActivateSelectedProvider_Unconfigured_StartsAtAddName()
    {
        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();
        vm.CurrentState.Value = ProviderManagerState.List;

        // Find an unconfigured provider (all are unconfigured with empty config)
        var ollamaIndex = vm.DisplayProviders.FindIndex(p => p.ProviderType == "ollama");
        vm.SelectedProviderIndex = ollamaIndex;
        vm.ActivateSelectedProvider();

        // Add flow always starts at AddName regardless of auth type so the
        // user can confirm or override the auto-generated provider name.
        Assert.Equal(ProviderManagerState.AddName, vm.CurrentState.Value);
        Assert.Equal("ollama", vm.NewProviderType);
        Assert.False(string.IsNullOrEmpty(vm.NewProviderName));
    }

    [Fact]
    public void AdvanceAfterName_NoAuthProvider_GoesToCredentials()
    {
        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();
        vm.CurrentState.Value = ProviderManagerState.List;

        var ollamaIndex = vm.DisplayProviders.FindIndex(p => p.ProviderType == "ollama");
        vm.SelectedProviderIndex = ollamaIndex;
        vm.ActivateSelectedProvider();
        vm.AdvanceAfterName();

        // Ollama has [None] auth, so AdvanceAfterName routes to AddCredentials.
        Assert.Equal(ProviderManagerState.AddCredentials, vm.CurrentState.Value);
    }

    [Fact]
    public void AdvanceAfterName_ApiKeyProvider_GoesToAuthSelect()
    {
        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();
        vm.CurrentState.Value = ProviderManagerState.List;

        var anthropicIndex = vm.DisplayProviders.FindIndex(p => p.ProviderType == "anthropic");
        vm.SelectedProviderIndex = anthropicIndex;
        vm.ActivateSelectedProvider();
        vm.AdvanceAfterName();

        Assert.Equal(ProviderManagerState.AddSelectAuth, vm.CurrentState.Value);
        Assert.Equal("anthropic", vm.NewProviderType);
    }

    [Fact]
    public void TrySetNewProviderName_TrimsAndAcceptsUniqueName()
    {
        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();

        Assert.True(vm.TrySetNewProviderName("  lab-a100  ", out var err));
        Assert.Equal("", err);
        Assert.Equal("lab-a100", vm.NewProviderName);
    }

    [Fact]
    public void TrySetNewProviderName_RejectsEmptyAndWhitespace()
    {
        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();

        Assert.False(vm.TrySetNewProviderName("", out var err1));
        Assert.NotEmpty(err1);

        Assert.False(vm.TrySetNewProviderName("   ", out var err2));
        Assert.NotEmpty(err2);

        Assert.False(vm.TrySetNewProviderName(null, out var err3));
        Assert.NotEmpty(err3);
    }

    [Fact]
    public void TrySetNewProviderName_RejectsCollisionCaseInsensitive()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-vllm"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "http://localhost:8080"
                }
            }
        });

        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();

        Assert.False(vm.TrySetNewProviderName("MY-VLLM", out var err));
        Assert.Contains("my-vllm", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrySetNewProviderName_OnFailure_PreservesCandidateForRedraw()
    {
        // When validation fails the user's typed text must survive on the
        // view model so the next view build re-prefills the input with what
        // they typed; otherwise their entry vanishes when ErrorMessage
        // triggers a redraw.
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-vllm"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "http://localhost:8080"
                }
            }
        });

        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();
        vm.NewProviderName = "lab-default";

        Assert.False(vm.TrySetNewProviderName("MY-VLLM", out _));

        Assert.Equal("MY-VLLM", vm.NewProviderName);
    }

    [Fact]
    public async Task ConfirmRename_OnFailure_PreservesCandidateForRedraw()
    {
        // Same redraw-preservation rule as TrySetNewProviderName, but for the
        // rename flow: a failed rename must leave the bad name on the view
        // model so the next redraw re-prefills the input.
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-vllm"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "http://localhost:8080"
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

        var idx = vm.DisplayProviders.FindIndex(p => p.ConfiguredName == "my-vllm");
        vm.DetailProvider = vm.DisplayProviders[idx];
        vm.CurrentState.Value = ProviderManagerState.RenameProvider;
        vm.RenameNewName = "my-vllm";

        // Collides with the other existing provider.
        vm.ConfirmRename("my-ollama");

        Assert.Equal(ProviderManagerState.RenameProvider, vm.CurrentState.Value);
        Assert.NotEmpty(vm.ErrorMessage.Value);
        Assert.Equal("my-ollama", vm.RenameNewName);
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
            [new DiscoveredModel { ModelId = new Netclaw.Configuration.ModelId("model-a") }]);

        vm.FixApiKey = "sk-new-key";
        vm.SubmitFixCredentials();

        // Wait for the single-provider probe to complete
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        // Fix flow triggers RefreshAndProbeAll — wait for the eager re-probe too
        await vm.EagerProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

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
        vm.AdvanceAfterName();

        vm.SelectAuthMethod(AuthMethod.ApiKey);
        vm.NewApiKey = "sk-test-key";
        vm.SubmitCredentials();

        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        vm.ConfirmAdd();

        // ConfirmAdd triggers RefreshAndProbeAll — wait for re-probe
        await vm.EagerProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

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
        vm.AdvanceAfterName();
        vm.SelectAuthMethod(AuthMethod.ApiKey);

        vm.NewApiKey = "sk-test";
        vm.SubmitCredentials();
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(vm.IsProbing.Value);
        Assert.NotNull(vm.ProbeResult.Value);
        Assert.False(vm.ProbeResult.Value!.Success);
        Assert.Contains("simulated probe failure", vm.ProbeResult.Value.ErrorMessage);
    }

    [Fact]
    public async Task SubmitCredentials_PublishesResultAfterIsProbingClears()
    {
        using var vm = CreateViewModel();
        await ActivateAndProbeAsync(vm);

        var idx = vm.DisplayProviders.FindIndex(p => p.ProviderType == "openrouter");
        vm.SelectedProviderIndex = idx;
        vm.ActivateSelectedProvider();
        vm.AdvanceAfterName();
        vm.SelectAuthMethod(AuthMethod.ApiKey);

        vm.NewApiKey = "sk-test";
        _fakeProbe.NextResult = new ProviderProbeResult(false, "synthetic failure", []);

        bool? isProbingAtResultPublish = null;
        using var sub = vm.ProbeResult.Subscribe(result =>
        {
            if (result is not null)
                isProbingAtResultPublish = vm.IsProbing.Value;
        });

        vm.SubmitCredentials();
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(false, isProbingAtResultPublish);
        Assert.False(vm.IsProbing.Value);
    }

    [Fact]
    public async Task SubmitCredentials_OAuthSuccess_PersistsProviderImmediately()
    {
        using var vm = CreateViewModel();
        vm.NewProviderName = "my-copilot";
        vm.NewProviderType = "github-copilot";
        vm.NewAuthMethod = AuthMethod.OAuthDevice;
        vm.OAuth.Result = new OAuthDeviceFlowResult(
            new SensitiveString("oauth-access-token"),
            null,
            DateTimeOffset.UtcNow.AddHours(1),
            null);

        Assert.False(File.Exists(_paths.SecretsPath));

        vm.SubmitCredentials();
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(ProviderManagerState.AddComplete, vm.CurrentState.Value);
        Assert.True(File.Exists(_paths.NetclawConfigPath));
        Assert.True(File.Exists(_paths.SecretsPath));

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var configProvider = config.RootElement
            .GetProperty("Providers")
            .GetProperty("my-copilot");
        Assert.Equal("github-copilot", configProvider.GetProperty("Type").GetString());
        Assert.Equal("OAuthDevice", configProvider.GetProperty("AuthMethod").GetString());
        Assert.Equal("https://api.githubcopilot.com", configProvider.GetProperty("Endpoint").GetString());

        var secrets = JsonDocument.Parse(File.ReadAllText(_paths.SecretsPath));
        var provider = secrets.RootElement
            .GetProperty("Providers")
            .GetProperty("my-copilot");

        Assert.StartsWith("ENC:", provider.GetProperty("OAuthAccessToken").GetString());
        Assert.False(provider.TryGetProperty("OAuthRefreshToken", out _));
        Assert.False(provider.TryGetProperty("OAuthAccountId", out _));
    }

    [Fact]
    public async Task SubmitCredentials_OAuthSuccess_PreservesExistingOAuthProviders()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["openai-codex"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai",
                    ["AuthMethod"] = "OAuthDevice",
                    ["Endpoint"] = "https://api.openai.com"
                }
            }
        });

        WriteSecrets(new Dictionary<string, object>
        {
            ["Providers"] = new Dictionary<string, object>
            {
                ["openai-codex"] = new Dictionary<string, object>
                {
                    ["OAuthAccessToken"] = "openai-access-token"
                }
            }
        });

        using var vm = CreateViewModel();
        vm.NewProviderName = "my-copilot";
        vm.NewProviderType = "github-copilot";
        vm.NewAuthMethod = AuthMethod.OAuthDevice;
        vm.OAuth.Result = new OAuthDeviceFlowResult(
            new SensitiveString("copilot-access-token"),
            null,
            DateTimeOffset.UtcNow.AddHours(1),
            null);

        vm.SubmitCredentials();
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var configProviders = config.RootElement.GetProperty("Providers");
        Assert.True(configProviders.TryGetProperty("openai-codex", out _));
        Assert.True(configProviders.TryGetProperty("my-copilot", out _));

        var secrets = JsonDocument.Parse(File.ReadAllText(_paths.SecretsPath));
        var secretProviders = secrets.RootElement.GetProperty("Providers");
        Assert.True(secretProviders.TryGetProperty("openai-codex", out _));
        Assert.True(secretProviders.TryGetProperty("my-copilot", out _));
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
    public void GoBack_FromAddName_ReturnsToList()
    {
        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();
        vm.CurrentState.Value = ProviderManagerState.List;

        var idx = vm.DisplayProviders.FindIndex(p => p.ProviderType == "anthropic");
        vm.SelectedProviderIndex = idx;
        vm.ActivateSelectedProvider();
        Assert.Equal(ProviderManagerState.AddName, vm.CurrentState.Value);

        vm.GoBack();
        Assert.Equal(ProviderManagerState.List, vm.CurrentState.Value);
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
        vm.AdvanceAfterName();
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

    [Fact]
    public void DisplayProviders_ShowsMultipleInstancesOfSameType()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-vllm-local"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "http://localhost:8080"
                },
                ["my-vllm-remote"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "http://192.168.1.50:8080"
                }
            }
        });

        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();

        // Both instances should appear
        var compatible = vm.DisplayProviders.Where(p => p.ProviderType == "openai-compatible").ToList();
        Assert.Equal(2, compatible.Count);
        Assert.Contains(compatible, p => p.ConfiguredName == "my-vllm-local");
        Assert.Contains(compatible, p => p.ConfiguredName == "my-vllm-remote");
        Assert.All(compatible, p => Assert.True(p.IsConfigured));

        // openai-compatible should NOT appear as an unconfigured placeholder
        Assert.DoesNotContain(vm.DisplayProviders,
            p => p.ProviderType == "openai-compatible" && !p.IsConfigured);

        // Other unconfigured types should still be present
        Assert.Contains(vm.DisplayProviders, p => p.ProviderType == "ollama" && !p.IsConfigured);

        // Total: 2 configured + 6 unconfigured types = 8
        Assert.Equal(8, vm.DisplayProviders.Count);
    }

    [Fact]
    public void StartAddNewProvider_TransitionsToAddSelectType()
    {
        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();
        vm.CurrentState.Value = ProviderManagerState.List;

        vm.StartAddNewProvider();

        Assert.Equal(ProviderManagerState.AddSelectType, vm.CurrentState.Value);
    }

    [Fact]
    public void GoBack_FromAddSelectType_ReturnsToList()
    {
        using var vm = CreateViewModel();
        vm.RefreshDisplayProviders();
        vm.CurrentState.Value = ProviderManagerState.AddSelectType;

        vm.GoBack();

        Assert.Equal(ProviderManagerState.List, vm.CurrentState.Value);
    }

    [Fact]
    public async Task AddProvider_UsesCustomNameWhenUserProvidesOne()
    {
        using var vm = CreateViewModel();
        await ActivateAndProbeAsync(vm);

        var idx = vm.DisplayProviders.FindIndex(p => p.ProviderType == "openrouter");
        vm.SelectedProviderIndex = idx;
        vm.ActivateSelectedProvider();

        // User edits the name on the AddName step.
        Assert.True(vm.TrySetNewProviderName("lab-a100", out _));
        vm.AdvanceAfterName();

        vm.SelectAuthMethod(AuthMethod.ApiKey);
        vm.NewApiKey = "sk-test-key";
        vm.SubmitCredentials();

        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        vm.ConfirmAdd();
        await vm.EagerProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var providerNames = config.RootElement.GetProperty("Providers").EnumerateObject()
            .Select(p => p.Name).ToList();
        Assert.Single(providerNames);
        Assert.Equal("lab-a100", providerNames[0]);
    }

    [Fact]
    public async Task StartRename_TransitionsToRenameProvider()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-vllm"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "http://localhost:8080"
                }
            }
        });

        using var vm = CreateViewModel();
        await ActivateAndProbeAsync(vm);

        var idx = vm.DisplayProviders.FindIndex(p => p.ConfiguredName == "my-vllm");
        vm.SelectedProviderIndex = idx;
        vm.ActivateSelectedProvider();
        // Force into Details (probe outcome doesn't matter here).
        vm.DetailProvider = vm.DisplayProviders[idx];

        vm.StartRename();

        Assert.Equal(ProviderManagerState.RenameProvider, vm.CurrentState.Value);
        Assert.Equal("my-vllm", vm.RenameNewName);
    }

    [Fact]
    public async Task ConfirmRename_SwapsKeyAndReturnsToList()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-vllm"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "http://localhost:8080"
                }
            }
        });

        using var vm = CreateViewModel();
        await ActivateAndProbeAsync(vm);

        var idx = vm.DisplayProviders.FindIndex(p => p.ConfiguredName == "my-vllm");
        vm.DetailProvider = vm.DisplayProviders[idx];
        vm.CurrentState.Value = ProviderManagerState.Details;

        vm.ConfirmRename("lab-a100");
        await vm.EagerProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(ProviderManagerState.List, vm.CurrentState.Value);

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var providers = config.RootElement.GetProperty("Providers");
        Assert.False(providers.TryGetProperty("my-vllm", out _));
        Assert.True(providers.TryGetProperty("lab-a100", out _));
    }

    [Fact]
    public async Task ConfirmRename_EmptyName_KeepsCurrentStateAndSetsError()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-vllm"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai-compatible",
                    ["Endpoint"] = "http://localhost:8080"
                }
            }
        });

        using var vm = CreateViewModel();
        await ActivateAndProbeAsync(vm);

        var idx = vm.DisplayProviders.FindIndex(p => p.ConfiguredName == "my-vllm");
        vm.DetailProvider = vm.DisplayProviders[idx];
        vm.CurrentState.Value = ProviderManagerState.RenameProvider;

        vm.ConfirmRename("   ");

        Assert.Equal(ProviderManagerState.RenameProvider, vm.CurrentState.Value);
        Assert.NotEmpty(vm.ErrorMessage.Value);
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
