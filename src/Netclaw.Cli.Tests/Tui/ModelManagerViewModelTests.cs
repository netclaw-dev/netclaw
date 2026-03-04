using System.Text.Json;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

public sealed class ModelManagerViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;
    private readonly FakeProviderProbe _fakeProbe = new();

    public ModelManagerViewModelTests()
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
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

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
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        vm.SelectModel("qwen3:30b");
        Assert.Equal(ModelManagerState.ConfirmAssignment, vm.CurrentState.Value);

        vm.ConfirmAssignment();
        Assert.Equal(ModelManagerState.RoleOverview, vm.CurrentState.Value);

        // Verify config
        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var main = config.RootElement.GetProperty("Models").GetProperty("Main");
        Assert.Equal("my-ollama", main.GetProperty("Provider").GetString());
        Assert.Equal("qwen3:30b", main.GetProperty("ModelId").GetString());
        Assert.Equal("Live", main.GetProperty("Provenance").GetString());
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
