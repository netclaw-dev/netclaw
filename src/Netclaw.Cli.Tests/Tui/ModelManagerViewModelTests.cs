// -----------------------------------------------------------------------
// <copyright file="ModelManagerViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using R3;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

public sealed class ModelManagerViewModelTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeProviderProbe _fakeProbe = new();

    public ModelManagerViewModelTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void StartsAtRoleOverview()
    {
        using var vm = CreateViewModel();
        Assert.Equal(ModelManagerState.RoleOverview, vm.CurrentState.Value);
    }

    [Fact]
    public void StartAssignment_NoProviders_SetsStatusMessage()
    {
        using var vm = CreateViewModel();
        vm.Refresh();

        vm.StartAssignment("Main");
        Assert.Contains("No providers configured", vm.StatusMessage.Value);
    }

    [Fact]
    public void StartAssignment_SingleProvider_AutoSelectsAndDiscovers()
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
        vm.Refresh();
        Assert.Single(vm.Providers);

        vm.StartAssignment("Main");

        // Single provider auto-selected, goes to discover
        Assert.Equal(ModelManagerState.DiscoverModels, vm.CurrentState.Value);
        Assert.Equal("my-ollama", vm.SelectedProvider);
    }

    [Fact]
    public async Task StartAssignment_OAuthProvider_UsesOAuthAccessTokenForDiscovery()
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
        vm.Refresh();

        vm.StartAssignment("Main");
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("oauth-access-token", _fakeProbe.LastApiKey);
    }

    [Fact]
    public void StartAssignment_MultipleProviders_GoesToSelectProvider()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["my-ollama"] = new Dictionary<string, object> { ["Type"] = "ollama" },
                ["my-openrouter"] = new Dictionary<string, object> { ["Type"] = "openrouter" }
            }
        });

        using var vm = CreateViewModel();
        vm.Refresh();
        Assert.Equal(2, vm.Providers.Count);

        vm.StartAssignment("Main");
        Assert.Equal(ModelManagerState.SelectProvider, vm.CurrentState.Value);
    }

    [Fact]
    public async Task ConfirmAssignment_WritesCorrectConfig()
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
        vm.Refresh();

        // Simulate the full assignment flow
        vm.StartAssignment("Main");
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        vm.SelectModel("model-a");
        Assert.Equal(ModelManagerState.ConfirmAssignment, vm.CurrentState.Value);

        vm.ConfirmAssignment();
        Assert.Equal(ModelManagerState.RoleOverview, vm.CurrentState.Value);

        // Verify config
        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var main = config.RootElement.GetProperty("Models").GetProperty("Main");
        Assert.Equal("my-ollama", main.GetProperty("Provider").GetString());
        Assert.Equal("model-a", main.GetProperty("ModelId").GetString());
        Assert.Equal("Live", main.GetProperty("Provenance").GetString());
        Assert.Equal("Text", main.GetProperty("InputModalities").GetString());
        Assert.Equal("Text", main.GetProperty("OutputModalities").GetString());
    }

    [Fact]
    public async Task ConfirmAssignment_DiscoveredModelWithMetadata_WritesMetadata()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["openai-codex"] = new Dictionary<string, object>
                {
                    ["Type"] = "openai",
                    ["AuthMethod"] = "OAuthDevice"
                }
            }
        });
        _fakeProbe.NextResult = new ProviderProbeResult(true, null,
        [
            new DiscoveredModel
            {
                ModelId = new Netclaw.Configuration.ModelId("gpt-new-codex"),
                ContextWindowTokens = 512000,
                InputModalities = ModelModality.Text | ModelModality.Image,
                OutputModalities = ModelModality.Text,
            }
        ]);

        using var vm = CreateViewModel();
        vm.Refresh();
        vm.StartAssignment("Main");
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        vm.SelectModel("gpt-new-codex");
        vm.ConfirmAssignment();

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var main = config.RootElement.GetProperty("Models").GetProperty("Main");
        Assert.Equal("Live", main.GetProperty("Provenance").GetString());
        Assert.Equal(512000, main.GetProperty("ContextWindow").GetInt32());
        Assert.Equal("Text, Image", main.GetProperty("InputModalities").GetString());
        Assert.Equal("Text", main.GetProperty("OutputModalities").GetString());
    }

    [Fact]
    public async Task StartAssignment_WhenProbeThrows_ReportsFailureAndStopsProbing()
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

        _fakeProbe.ExceptionToThrow = new InvalidOperationException("simulated probe failure");

        using var vm = CreateViewModel();
        vm.Refresh();

        vm.StartAssignment("Main");
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(vm.IsProbing.Value);
        Assert.NotNull(vm.ProbeResult.Value);
        Assert.False(vm.ProbeResult.Value!.Success);
        Assert.Contains("simulated probe failure", vm.ProbeResult.Value.ErrorMessage);
    }

    [Fact]
    public async Task StartAssignment_PublishesResultAfterIsProbingClears()
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

        _fakeProbe.NextResult = new ProviderProbeResult(false, "synthetic failure", []);

        using var vm = CreateViewModel();
        vm.Refresh();

        bool? isProbingAtResultPublish = null;
        using var sub = vm.ProbeResult.Subscribe(result =>
        {
            if (result is not null)
                isProbingAtResultPublish = vm.IsProbing.Value;
        });

        vm.StartAssignment("Main");
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(false, isProbingAtResultPublish);
        Assert.False(vm.IsProbing.Value);
    }

    [Fact]
    public void ClearRole_Main_IsRejected()
    {
        using var vm = CreateViewModel();
        vm.ClearRole("Main");
        Assert.Contains("Cannot clear", vm.StatusMessage.Value);
    }

    [Fact]
    public void ClearRole_Fallback_RemovesFromConfig()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Models"] = new Dictionary<string, object>
            {
                ["Main"] = new Dictionary<string, object>
                {
                    ["Provider"] = "my-ollama",
                    ["ModelId"] = "qwen3:30b"
                },
                ["Fallback"] = new Dictionary<string, object>
                {
                    ["Provider"] = "my-ollama",
                    ["ModelId"] = "qwen3:8b"
                }
            }
        });

        using var vm = CreateViewModel();
        vm.ClearRole("Fallback");

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var models = config.RootElement.GetProperty("Models");
        Assert.True(models.TryGetProperty("Main", out _));
        Assert.False(models.TryGetProperty("Fallback", out _));
    }

    [Fact]
    public void GoBack_FromSelectProvider_ReturnsToRoleOverview()
    {
        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object>
            {
                ["p1"] = new Dictionary<string, object> { ["Type"] = "ollama" },
                ["p2"] = new Dictionary<string, object> { ["Type"] = "openrouter" }
            }
        });

        using var vm = CreateViewModel();
        vm.Refresh();
        vm.StartAssignment("Main");
        Assert.Equal(ModelManagerState.SelectProvider, vm.CurrentState.Value);

        vm.GoBack();
        Assert.Equal(ModelManagerState.RoleOverview, vm.CurrentState.Value);
    }

    [Fact]
    public void Refresh_PopulatesDisplayNameFromRegistry()
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

        var registry = Netclaw.Cli.Provider.ProviderCommand.CreateDefaultRegistry();
        using var vm = new ModelManagerViewModel(_paths, _fakeProbe, registry);
        vm.Refresh();

        Assert.Single(vm.Providers);
        Assert.Equal("my-vllm", vm.Providers[0].Name);
        Assert.Equal("llama.cpp / vLLM", vm.Providers[0].DisplayName);
    }

    [Fact]
    public void Refresh_FallsBackToTypeWhenNoRegistry()
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
        vm.Refresh();

        Assert.Single(vm.Providers);
        Assert.Equal("ollama", vm.Providers[0].DisplayName);
    }

    private ModelManagerViewModel CreateViewModel()
    {
        return new ModelManagerViewModel(_paths, _fakeProbe);
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
