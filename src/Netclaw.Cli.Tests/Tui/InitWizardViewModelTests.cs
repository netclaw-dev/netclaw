using System.Text.Json;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Tests for <see cref="InitWizardViewModel"/> state machine, back-navigation
/// clearing rules, and config file writing.
/// </summary>
public sealed class InitWizardViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;

    public InitWizardViewModelTests()
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
    public void StartsAtProviderStep()
    {
        using var vm = CreateViewModel();
        Assert.Equal(WizardStep.Provider, vm.CurrentStep.Value);
    }

    [Fact]
    public void GoNext_AdvancesStep()
    {
        using var vm = CreateViewModel();
        Assert.Equal(WizardStep.Provider, vm.CurrentStep.Value);

        vm.GoNext();
        Assert.Equal(WizardStep.Slack, vm.CurrentStep.Value);

        vm.GoNext();
        Assert.Equal(WizardStep.Acl, vm.CurrentStep.Value);

        vm.GoNext();
        Assert.Equal(WizardStep.Mcp, vm.CurrentStep.Value);

        vm.GoNext();
        Assert.Equal(WizardStep.Exposure, vm.CurrentStep.Value);

        vm.GoNext();
        Assert.Equal(WizardStep.HealthCheck, vm.CurrentStep.Value);
    }

    [Fact]
    public void GoBack_ReturnsToPreviousStep()
    {
        using var vm = CreateViewModel();
        vm.GoNext(); // → Slack
        vm.GoNext(); // → Acl

        vm.GoBack(); // → Slack
        Assert.Equal(WizardStep.Slack, vm.CurrentStep.Value);

        vm.GoBack(); // → Provider
        Assert.Equal(WizardStep.Provider, vm.CurrentStep.Value);
    }

    [Fact]
    public void GoBack_FromProvider_ClearsAuthState()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "openai";
        vm.SelectedAuthMethod = AuthMethod.ApiKey;
        vm.ApiKeyInput = "sk-test-key";
        vm.EndpointInput = "https://custom.endpoint";

        vm.GoNext(); // → Slack
        vm.GoBack(); // → Provider (should clear auth state)

        Assert.Equal(AuthMethod.None, vm.SelectedAuthMethod);
        Assert.Null(vm.ApiKeyInput);
        Assert.Null(vm.EndpointInput);
    }

    [Fact]
    public void ClearFromProvider_ClearsAuthAndCredentials()
    {
        using var vm = CreateViewModel();
        vm.SelectedAuthMethod = AuthMethod.ApiKey;
        vm.ApiKeyInput = "sk-test";
        vm.EndpointInput = "http://localhost:11434";

        vm.ClearFromProvider();

        Assert.Equal(AuthMethod.None, vm.SelectedAuthMethod);
        Assert.Null(vm.ApiKeyInput);
        Assert.Null(vm.EndpointInput);
    }

    [Fact]
    public async Task HealthCheck_WritesOllamaConfig()
    {
        using var vm = CreateViewModel();

        vm.SelectedProviderType = "ollama";
        vm.SelectedAuthMethod = AuthMethod.None;
        vm.EndpointInput = "http://big-gpu:11434";
        vm.SlackEnabled = false;

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext(); // triggers health check

        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsComplete.Value);

        // Verify netclaw.json
        Assert.True(File.Exists(_paths.NetclawConfigPath));
        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));

        Assert.True(config.RootElement.TryGetProperty("Providers", out var providers));
        Assert.True(providers.TryGetProperty("ollama", out var ollamaEntry));
        Assert.Equal("ollama", ollamaEntry.GetProperty("Type").GetString());
        Assert.Equal("http://big-gpu:11434", ollamaEntry.GetProperty("Endpoint").GetString());

        // Ollama has no API key — secrets.json should not have provider secrets
        if (File.Exists(_paths.SecretsPath))
        {
            var secrets = JsonDocument.Parse(File.ReadAllText(_paths.SecretsPath));
            Assert.False(secrets.RootElement.TryGetProperty("Providers", out _),
                "Ollama should not have provider secrets");
        }
    }

    [Fact]
    public async Task HealthCheck_WritesApiKeyToSecrets()
    {
        using var vm = CreateViewModel();

        vm.SelectedProviderType = "openrouter";
        vm.SelectedAuthMethod = AuthMethod.ApiKey;
        vm.ApiKeyInput = "sk-or-test-1234567890";
        vm.SlackEnabled = false;

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();

        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsComplete.Value);

        // secrets.json contains the API key
        Assert.True(File.Exists(_paths.SecretsPath));
        var secrets = JsonDocument.Parse(File.ReadAllText(_paths.SecretsPath));
        Assert.True(secrets.RootElement.TryGetProperty("Providers", out var providers));
        Assert.True(providers.TryGetProperty("openrouter", out var entry));
        Assert.Equal("sk-or-test-1234567890", entry.GetProperty("ApiKey").GetString());

        // netclaw.json must NOT contain the API key
        Assert.DoesNotContain("sk-or-test", File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task HealthCheck_WritesSlackTokensToSecrets()
    {
        using var vm = CreateViewModel();

        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = true;
        vm.SlackBotToken = "xoxb-test-bot-token";
        vm.SlackAppToken = "xapp-test-app-token";

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();

        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsComplete.Value);

        var secrets = JsonDocument.Parse(File.ReadAllText(_paths.SecretsPath));
        Assert.True(secrets.RootElement.TryGetProperty("Slack", out var slack));
        Assert.Equal("xoxb-test-bot-token", slack.GetProperty("BotToken").GetString());
        Assert.Equal("xapp-test-app-token", slack.GetProperty("AppToken").GetString());

        // Tokens must NOT appear in netclaw.json
        var configJson = File.ReadAllText(_paths.NetclawConfigPath);
        Assert.DoesNotContain("xoxb-test", configJson);
        Assert.DoesNotContain("xapp-test", configJson);
    }

    [Fact]
    public async Task HealthCheck_ReportsProviderValidation()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "anthropic";
        vm.SelectedAuthMethod = AuthMethod.ApiKey;
        vm.ApiKeyInput = "sk-ant-test-key";

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();

        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsComplete.Value);
        Assert.NotEmpty(vm.HealthCheckResults);

        var providerCheck = vm.HealthCheckResults
            .FirstOrDefault(h => h.Label.Contains("LLM provider", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(providerCheck);
        Assert.True(providerCheck.Passed);
    }

    [Fact]
    public async Task HealthCheck_ReportsSlackDisabled()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = false;

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();

        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        var slackCheck = vm.HealthCheckResults
            .FirstOrDefault(h => h.Label.Contains("Slack", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(slackCheck);
        Assert.True(slackCheck.Passed);
        Assert.Contains("disabled", slackCheck.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TotalSteps_IsSix()
    {
        Assert.Equal(6, InitWizardViewModel.TotalSteps);
    }

    private InitWizardViewModel CreateViewModel()
    {
        return new InitWizardViewModel(_paths);
    }
}
