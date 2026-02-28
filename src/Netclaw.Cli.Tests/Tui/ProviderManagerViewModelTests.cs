using System.Text.Json;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
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

    [Fact]
    public void StartsAtListState()
    {
        using var vm = CreateViewModel();
        Assert.Equal(ProviderManagerState.List, vm.CurrentState.Value);
    }

    [Fact]
    public void StartAdd_TransitionsToAddSelectType()
    {
        using var vm = CreateViewModel();
        vm.StartAdd();
        Assert.Equal(ProviderManagerState.AddSelectType, vm.CurrentState.Value);
    }

    [Fact]
    public void SelectProviderType_OllamaSkipsAuth_GoesToCredentials()
    {
        using var vm = CreateViewModel();
        vm.StartAdd();
        vm.SelectProviderType("ollama");

        Assert.Equal(ProviderManagerState.AddCredentials, vm.CurrentState.Value);
        Assert.Equal(AuthMethod.None, vm.NewAuthMethod);
    }

    [Fact]
    public void SelectProviderType_AnthropicShowsAuth()
    {
        using var vm = CreateViewModel();
        vm.StartAdd();
        vm.SelectProviderType("anthropic");

        Assert.Equal(ProviderManagerState.AddSelectAuth, vm.CurrentState.Value);
    }

    [Fact]
    public async Task AddProvider_WritesCorrectConfigStructure()
    {
        using var vm = CreateViewModel();
        vm.StartAdd();
        vm.SelectProviderType("openrouter");
        vm.SelectAuthMethod(AuthMethod.ApiKey);
        vm.NewApiKey = "sk-test-key";
        vm.SubmitCredentials();

        // Wait for probe
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        // Probe succeeded — confirm add
        vm.ConfirmAdd();

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
    public void RemoveProvider_ReferencedByModelRole_IsRejected()
    {
        // Arrange: create provider + model reference
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
        vm.RefreshProviders();
        Assert.Single(vm.Providers);

        vm.SelectedProviderIndex = 0;
        vm.StartRemove();

        Assert.Equal(ProviderManagerState.RemoveConfirm, vm.CurrentState.Value);
        Assert.NotEmpty(vm.RemoveBlockingRoles);
        Assert.Contains("Main", vm.RemoveBlockingRoles);
    }

    [Fact]
    public void GoBack_FromAddSelectAuth_ReturnsToSelectType()
    {
        using var vm = CreateViewModel();
        vm.StartAdd();
        vm.SelectProviderType("anthropic");
        Assert.Equal(ProviderManagerState.AddSelectAuth, vm.CurrentState.Value);

        vm.GoBack();
        Assert.Equal(ProviderManagerState.AddSelectType, vm.CurrentState.Value);
    }

    [Fact]
    public void GoBack_FromList_ShutdownSignal()
    {
        using var vm = CreateViewModel();
        // GoBack from list should call Shutdown (which we can't easily test without a host,
        // but we can verify it doesn't crash)
        vm.GoBack();
    }

    private ProviderManagerViewModel CreateViewModel()
    {
        return new ProviderManagerViewModel(_paths, _fakeProbe);
    }

    private void WriteConfig(Dictionary<string, object> data)
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }
}
