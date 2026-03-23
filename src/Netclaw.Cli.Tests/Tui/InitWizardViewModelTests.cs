using System.Text.Json;
using R3;
using Netclaw.Cli.Mcp;
using Netclaw.Cli.Provider;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Tests for <see cref="InitWizardViewModel"/> state machine, back-navigation
/// clearing rules, ACL skip logic, provider probing, and config file writing.
/// </summary>
public sealed class InitWizardViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;
    private readonly FakeProviderProbe _fakeProbe = new();
    private readonly FakeSlackProbe _fakeSlackProbe = new();
    private readonly ProviderDescriptorRegistry _registry = ProviderCommand.CreateDefaultRegistry();

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
        vm.SlackEnabled = true; // enable chat services so ACL is not skipped
        Assert.Equal(WizardStep.Provider, vm.CurrentStep.Value);

        vm.GoNext();
        Assert.Equal(WizardStep.ChatServices, vm.CurrentStep.Value);

        vm.GoNext();
        Assert.Equal(WizardStep.Acl, vm.CurrentStep.Value);

        vm.GoNext();
        Assert.Equal(WizardStep.Search, vm.CurrentStep.Value);

        vm.GoNext();
        Assert.Equal(WizardStep.BrowserAutomation, vm.CurrentStep.Value);

        vm.GoNext(); // Memory is skipped
        Assert.Equal(WizardStep.Exposure, vm.CurrentStep.Value);

        vm.GoNext();
        Assert.Equal(WizardStep.Identity, vm.CurrentStep.Value);

        vm.GoNext();
        Assert.Equal(WizardStep.HealthCheck, vm.CurrentStep.Value);
    }

    [Fact]
    public void GoBack_ReturnsToPreviousStep()
    {
        using var vm = CreateViewModel();
        vm.SlackEnabled = true;
        vm.GoNext(); // → ChatServices
        vm.GoNext(); // → Acl

        vm.GoBack(); // → ChatServices
        Assert.Equal(WizardStep.ChatServices, vm.CurrentStep.Value);

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

        vm.GoNext(); // → ChatServices
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
    public void ClearFromProvider_ClearsProbeResultAndModel()
    {
        using var vm = CreateViewModel();
        vm.SelectedModelId = "test-model";
        vm.DiscoveredModels.Add(new DiscoveredModel { ModelId = "test-model" });
        vm.ProbeResult.Value = new ProviderProbeResult(true, null,
            [new DiscoveredModel { ModelId = "test-model" }]);

        vm.ClearFromProvider();

        Assert.Null(vm.SelectedModelId);
        Assert.Empty(vm.DiscoveredModels);
        Assert.Null(vm.ProbeResult.Value);
    }

    [Fact]
    public void GoNext_SkipsAcl_WhenNoChatServicesEnabled()
    {
        using var vm = CreateViewModel();
        vm.SlackEnabled = false;

        vm.GoNext(); // Provider → ChatServices
        Assert.Equal(WizardStep.ChatServices, vm.CurrentStep.Value);

        vm.GoNext(); // ChatServices → should skip ACL → Search
        Assert.Equal(WizardStep.Search, vm.CurrentStep.Value);
    }

    [Fact]
    public void GoBack_SkipsAcl_WhenNoChatServicesEnabled()
    {
        using var vm = CreateViewModel();
        vm.SlackEnabled = false;

        vm.GoNext(); // → ChatServices
        vm.GoNext(); // → Search (ACL skipped)
        Assert.Equal(WizardStep.Search, vm.CurrentStep.Value);

        vm.GoBack(); // → ChatServices (ACL skipped going back)
        Assert.Equal(WizardStep.ChatServices, vm.CurrentStep.Value);
    }

    [Fact]
    public async Task ProbeProvider_StoresDiscoveredModels()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "ollama";
        vm.EndpointInput = "http://localhost:11434";

        _fakeProbe.NextResult = new ProviderProbeResult(true, null,
        [
            new DiscoveredModel { ModelId = "llama3:latest" },
            new DiscoveredModel { ModelId = "qwen3:30b" }
        ]);

        vm.StartProbe();
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.ProbeResult.Value!.Success);
        Assert.Equal(2, vm.DiscoveredModels.Count);
        Assert.Equal("llama3:latest", vm.DiscoveredModels[0].ModelId);
        Assert.Equal("qwen3:30b", vm.DiscoveredModels[1].ModelId);
        Assert.Equal("ollama", _fakeProbe.LastProviderType);
    }

    [Fact]
    public async Task ProbeProvider_ReportsFailure()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "openrouter";
        vm.ApiKeyInput = "bad-key";

        _fakeProbe.NextResult = new ProviderProbeResult(false, "Invalid API key", []);

        vm.StartProbe();
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.ProbeResult.Value!.Success);
        Assert.Equal("Invalid API key", vm.ProbeResult.Value.ErrorMessage);
        Assert.Empty(vm.DiscoveredModels);
    }

    [Fact]
    public async Task ProbeProvider_WhenProbeThrows_ReportsFailureAndStopsProbing()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "openai";
        vm.ApiKeyInput = "oauth-token";
        _fakeProbe.ExceptionToThrow = new InvalidOperationException("simulated probe failure");

        vm.StartProbe();
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsProbing.Value);
        Assert.NotNull(vm.ProbeResult.Value);
        Assert.False(vm.ProbeResult.Value!.Success);
        Assert.Contains("simulated probe failure", vm.ProbeResult.Value.ErrorMessage);
    }

    [Fact]
    public async Task ProbeProvider_PublishesResultAfterIsProbingClears()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "openrouter";
        vm.ApiKeyInput = "sk-test";
        _fakeProbe.NextResult = new ProviderProbeResult(false, "synthetic failure", []);

        bool? isProbingAtResultPublish = null;
        using var sub = vm.ProbeResult.Subscribe(result =>
        {
            if (result is not null)
                isProbingAtResultPublish = vm.IsProbing.Value;
        });

        vm.StartProbe();
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(false, isProbingAtResultPublish);
        Assert.False(vm.IsProbing.Value);
    }

    [Fact]
    public async Task ProbeProvider_OAuth_UsesOAuthTokenWhenApiKeyInputEmpty()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "openai";
        vm.SelectedAuthMethod = AuthMethod.OAuthDevice;
        vm.ApiKeyInput = null;
        vm.OAuth.Result = new OAuthDeviceFlowResult(
            new SensitiveString("oauth-access-token"),
            new SensitiveString("oauth-refresh-token"),
            DateTimeOffset.UtcNow.AddHours(1));

        _fakeProbe.NextResult = new ProviderProbeResult(true, null,
        [
            new DiscoveredModel { ModelId = "gpt-4.1" }
        ]);

        vm.StartProbe();
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("oauth-access-token", _fakeProbe.LastApiKey);
        Assert.True(vm.ProbeResult.Value!.Success);
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
    public async Task HealthCheck_WritesRecommendedAudienceProfiles()
    {
        using var vm = CreateViewModel();

        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = false;
        vm.ExposureMode = "Local only (recommended for homelab)";

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();

        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsComplete.Value);

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var tools = config.RootElement.GetProperty("Tools");
        Assert.Equal("HostAllowed", tools.GetProperty("ShellMode").GetString());

        var profiles = tools.GetProperty("AudienceProfiles");
        var publicProfile = profiles.GetProperty("Public");
        Assert.Equal("Allowlist", publicProfile.GetProperty("ToolsMode").GetString());
        Assert.Equal("Allowlist", publicProfile.GetProperty("McpServersMode").GetString());
        Assert.Empty(publicProfile.GetProperty("AllowedMcpServers").EnumerateArray());
        Assert.Equal("Roots", publicProfile.GetProperty("ReadFiles").GetProperty("Mode").GetString());

        var teamProfile = profiles.GetProperty("Team");
        Assert.Empty(teamProfile.GetProperty("AllowedMcpServers").EnumerateArray());

        var personalProfile = profiles.GetProperty("Personal");
        Assert.Equal("All", personalProfile.GetProperty("ToolsMode").GetString());
        Assert.Equal("All", personalProfile.GetProperty("McpServersMode").GetString());
        Assert.Equal("All", personalProfile.GetProperty("ReadFiles").GetProperty("Mode").GetString());
    }

    [Fact]
    public async Task HealthCheck_RemoteExposure_DisablesHostShellByDefault()
    {
        using var vm = CreateViewModel();

        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = false;
        vm.ExposureMode = "Cloudflare Tunnel (configure later)";

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();

        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsComplete.Value);

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var tools = config.RootElement.GetProperty("Tools");
        Assert.Equal("Off", tools.GetProperty("ShellMode").GetString());
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

        // secrets.json contains the API key (encrypted at rest)
        Assert.True(File.Exists(_paths.SecretsPath));
        var secrets = JsonDocument.Parse(File.ReadAllText(_paths.SecretsPath));
        Assert.True(secrets.RootElement.TryGetProperty("Providers", out var providers));
        Assert.True(providers.TryGetProperty("openrouter", out var entry));
        var encryptedKey = entry.GetProperty("ApiKey").GetString();
        Assert.StartsWith("ENC:", encryptedKey);

        // netclaw.json must NOT contain the API key
        Assert.DoesNotContain("sk-or-test", File.ReadAllText(_paths.NetclawConfigPath));

        // Verify decryption round-trips correctly
        var loaded = Netclaw.Cli.Provider.ProviderCommand.LoadProviders(_paths);
        Assert.Equal("sk-or-test-1234567890", loaded["openrouter"].ApiKey?.Value);
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
    public async Task HealthCheck_WritesModelIdToConfig()
    {
        using var vm = CreateViewModel();

        vm.SelectedProviderType = "openrouter";
        vm.SelectedAuthMethod = AuthMethod.ApiKey;
        vm.ApiKeyInput = "sk-or-test-key";
        vm.SelectedModelId = "anthropic/claude-sonnet-4-20250514";
        vm.SlackEnabled = false;

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();

        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsComplete.Value);

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        Assert.True(config.RootElement.TryGetProperty("Models", out var models));
        Assert.True(models.TryGetProperty("Main", out var main));
        Assert.Equal("openrouter", main.GetProperty("Provider").GetString());
        Assert.Equal("anthropic/claude-sonnet-4-20250514", main.GetProperty("ModelId").GetString());
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
    public void TotalSteps_IsNine()
    {
        Assert.Equal(9, InitWizardViewModel.TotalSteps);
    }

    [Fact]
    public void ActiveStepCount_IsSeven_WhenNoChatServices()
    {
        using var vm = CreateViewModel();
        vm.SlackEnabled = false;
        Assert.Equal(7, vm.ActiveStepCount);
    }

    [Fact]
    public void ActiveStepCount_IsEight_WhenChatServicesEnabled()
    {
        using var vm = CreateViewModel();
        vm.SlackEnabled = true;
        Assert.Equal(8, vm.ActiveStepCount);
    }

    [Fact]
    public void GetDisplayStepNumber_AdjustsForSkippedAclAndMemory()
    {
        using var vm = CreateViewModel();
        vm.SlackEnabled = false;

        // Provider = 1, ChatServices = 2, Acl skipped, Search = 3,
        // BrowserAutomation = 4, Memory skipped, Exposure = 5,
        // Identity = 6, HealthCheck = 7
        Assert.Equal(1, vm.GetDisplayStepNumber(WizardStep.Provider));
        Assert.Equal(2, vm.GetDisplayStepNumber(WizardStep.ChatServices));
        Assert.Equal(3, vm.GetDisplayStepNumber(WizardStep.Search));
        Assert.Equal(4, vm.GetDisplayStepNumber(WizardStep.BrowserAutomation));
        Assert.Equal(5, vm.GetDisplayStepNumber(WizardStep.Exposure));
        Assert.Equal(6, vm.GetDisplayStepNumber(WizardStep.Identity));
        Assert.Equal(7, vm.GetDisplayStepNumber(WizardStep.HealthCheck));
    }

    [Fact]
    public async Task HealthCheck_SlackEnabled_ProbeSuccess_ShowsTeamName()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = true;
        vm.SlackBotToken = "xoxb-test-bot-token";
        vm.SlackAppToken = "xapp-test-app-token";

        _fakeSlackProbe.NextResult = new Channels.Slack.SlackProbeResult(
            true, null, "Acme Corp", new Channels.Slack.SlackUserId("U99999"));

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();

        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        var slackCheck = vm.HealthCheckResults
            .FirstOrDefault(h => h.Label.Contains("Slack", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(slackCheck);
        Assert.True(slackCheck.Passed);
        Assert.Contains("Acme Corp", slackCheck.Label, StringComparison.Ordinal);
        Assert.Equal(1, _fakeSlackProbe.ProbeCallCount);
        Assert.Equal("xoxb-test-bot-token", _fakeSlackProbe.LastBotToken);
    }

    [Fact]
    public async Task HealthCheck_SlackEnabled_ProbeFailure_ShowsError()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = true;
        vm.SlackBotToken = "xoxb-bad-token";
        vm.SlackAppToken = "xapp-test-app-token";

        _fakeSlackProbe.NextResult = new Channels.Slack.SlackProbeResult(
            false, "Bot token is invalid. Check your Slack app's Bot User OAuth Token.", null, null);

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();

        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        var slackCheck = vm.HealthCheckResults
            .FirstOrDefault(h => h.Label.Contains("Slack", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(slackCheck);
        Assert.False(slackCheck.Passed);
        Assert.Contains("invalid", slackCheck.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HealthCheck_ChannelResolutionSuccess_WritesChannelIdsToConfig()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = true;
        vm.SlackBotToken = "xoxb-test-bot-token";
        vm.SlackAppToken = "xapp-test-app-token";
        vm.SlackChannelNamesInput = "general, dev";

        _fakeSlackProbe.NextResolutionResult = new Channels.Slack.SlackChannelResolutionResult(
            true, null,
            [
                new Channels.Slack.ResolvedSlackChannel("general", "C001"),
                new Channels.Slack.ResolvedSlackChannel("dev", "C002")
            ],
            []);

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();
        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.IsComplete.Value);
        Assert.Equal(1, _fakeSlackProbe.ResolveCallCount);

        // Verify config file has channel IDs
        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        Assert.True(config.RootElement.TryGetProperty("Slack", out var slack));
        Assert.Equal("C001", slack.GetProperty("DefaultChannelId").GetString());

        var allowed = slack.GetProperty("AllowedChannelIds");
        Assert.Equal(2, allowed.GetArrayLength());
        Assert.Equal("C001", allowed[0].GetString());
        Assert.Equal("C002", allowed[1].GetString());
    }

    [Fact]
    public async Task HealthCheck_BlankChannelInput_SkipsResolution()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = true;
        vm.SlackBotToken = "xoxb-test-bot-token";
        vm.SlackAppToken = "xapp-test-app-token";
        vm.SlackChannelNamesInput = null;

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();
        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.IsComplete.Value);
        Assert.Equal(0, _fakeSlackProbe.ResolveCallCount);

        // Config should not have channel fields
        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        Assert.True(config.RootElement.TryGetProperty("Slack", out var slack));
        Assert.False(slack.TryGetProperty("AllowedChannelIds", out _));
        Assert.False(slack.TryGetProperty("DefaultChannelId", out _));
    }

    [Fact]
    public async Task HealthCheck_PartialChannelResolution_WritesOnlyResolved()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = true;
        vm.SlackBotToken = "xoxb-test-bot-token";
        vm.SlackAppToken = "xapp-test-app-token";
        vm.SlackChannelNamesInput = "general, nonexistent";

        _fakeSlackProbe.NextResolutionResult = new Channels.Slack.SlackChannelResolutionResult(
            false, null,
            [new Channels.Slack.ResolvedSlackChannel("general", "C001")],
            ["nonexistent"]);

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();
        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        // Wizard completes despite partial resolution
        Assert.True(vm.IsComplete.Value);

        // Health check shows the failure
        var channelCheck = vm.HealthCheckResults
            .FirstOrDefault(h => h.Label.Contains("Slack channels", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(channelCheck);
        Assert.False(channelCheck.Passed);
        Assert.Contains("#nonexistent", channelCheck.Label, StringComparison.Ordinal);

        // Only resolved ID written
        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var slack = config.RootElement.GetProperty("Slack");
        var allowed = slack.GetProperty("AllowedChannelIds");
        Assert.Equal(1, allowed.GetArrayLength());
        Assert.Equal("C001", allowed[0].GetString());
    }

    [Fact]
    public async Task HealthCheck_ChannelResolutionApiError_NonBlocking()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = true;
        vm.SlackBotToken = "xoxb-test-bot-token";
        vm.SlackAppToken = "xapp-test-app-token";
        vm.SlackChannelNamesInput = "general";

        _fakeSlackProbe.NextResolutionResult = new Channels.Slack.SlackChannelResolutionResult(
            false, "Bot token lacks channels:read scope.", [], ["general"]);

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();
        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        // Wizard completes despite API error
        Assert.True(vm.IsComplete.Value);

        var channelCheck = vm.HealthCheckResults
            .FirstOrDefault(h => h.Label.Contains("channel lookup", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(channelCheck);
        Assert.False(channelCheck.Passed);
        Assert.Contains("channels:read", channelCheck.Label, StringComparison.Ordinal);

        // No channel IDs in config
        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var slack = config.RootElement.GetProperty("Slack");
        Assert.False(slack.TryGetProperty("AllowedChannelIds", out _));
    }

    [Fact]
    public async Task HealthCheck_AuthFailure_SkipsChannelResolution()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = true;
        vm.SlackBotToken = "xoxb-bad-token";
        vm.SlackAppToken = "xapp-test-app-token";
        vm.SlackChannelNamesInput = "general, dev";

        _fakeSlackProbe.NextResult = new Channels.Slack.SlackProbeResult(
            false, "Bot token is invalid.", null, null);

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();
        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.IsComplete.Value);
        Assert.Equal(0, _fakeSlackProbe.ResolveCallCount);
    }

    [Fact]
    public async Task HealthCheck_WritesBraveSearchConfig()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = false;
        vm.SelectedSearchBackend = "brave";
        vm.BraveApiKeyInput = "BSA-test-key-123";

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();
        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsComplete.Value);

        // netclaw.json has Search.Backend = "brave"
        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        Assert.True(config.RootElement.TryGetProperty("Search", out var search));
        Assert.Equal("brave", search.GetProperty("Backend").GetString());

        // secrets.json has Search.BraveApiKey
        Assert.True(File.Exists(_paths.SecretsPath));
        var secrets = JsonDocument.Parse(File.ReadAllText(_paths.SecretsPath));
        Assert.True(secrets.RootElement.TryGetProperty("Search", out var searchSecrets));
        Assert.Equal("BSA-test-key-123", searchSecrets.GetProperty("BraveApiKey").GetString());

        // API key must NOT appear in netclaw.json
        Assert.DoesNotContain("BSA-test-key", File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task HealthCheck_WritesSearXngSearchConfig()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = false;
        vm.SelectedSearchBackend = "searxng";
        vm.SearXngEndpointInput = "http://searxng.local:8080";

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();
        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsComplete.Value);

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        Assert.True(config.RootElement.TryGetProperty("Search", out var search));
        Assert.Equal("searxng", search.GetProperty("Backend").GetString());
        Assert.Equal("http://searxng.local:8080", search.GetProperty("SearXngEndpoint").GetString());
    }

    [Fact]
    public async Task HealthCheck_DuckDuckGoDefault_OmitsSearchSection()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = false;
        // SelectedSearchBackend defaults to "duckduckgo"

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();
        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsComplete.Value);

        // DDG is the default — no Search section needed in config
        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        Assert.False(config.RootElement.TryGetProperty("Search", out _));
    }

    [Fact]
    public void BrowserAutomation_DefaultBackend_IsPlaywright()
    {
        using var vm = CreateViewModel();
        Assert.Equal(BrowserAutomationMcpProfiles.PlaywrightBackend, vm.SelectedBrowserAutomationBackend);
    }

    [Fact]
    public async Task HealthCheck_BrowserAutomationChrome_WritesMcpProfile()
    {
        var browserBootstrapper = new FakeBrowserAutomationBootstrapper
        {
            NextResult = new BrowserAutomationBootstrapResult(true, false, "ready")
        };
        using var vm = CreateViewModel(browserBootstrapper);

        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = false;
        vm.BrowserAutomationEnabled = true;
        vm.SelectedBrowserAutomationBackend = BrowserAutomationMcpProfiles.ChromeDevToolsBackend;

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();
        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.IsComplete.Value);

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        Assert.True(config.RootElement.TryGetProperty("McpServers", out var mcpServers));
        Assert.True(mcpServers.TryGetProperty("browser_chrome_devtools", out var browserEntry));
        Assert.Equal("stdio", browserEntry.GetProperty("Transport").GetString());
        var command = browserEntry.GetProperty("Command").GetString();
        Assert.False(string.IsNullOrWhiteSpace(command));
        Assert.EndsWith("npx", command, StringComparison.OrdinalIgnoreCase);

        var args = browserEntry.GetProperty("Arguments");
        Assert.Contains(args.EnumerateArray().Select(a => a.GetString()), a => a == "chrome-devtools-mcp@latest");
        Assert.DoesNotContain(args.EnumerateArray().Select(a => a.GetString()), a => a == "--slim");
        Assert.Contains(args.EnumerateArray().Select(a => a.GetString()), a => a == "--headless=true");
    }

    [Fact]
    public async Task HealthCheck_BrowserAutomationPlaywright_WritesMcpProfileWithContextSafeFlags()
    {
        var browserBootstrapper = new FakeBrowserAutomationBootstrapper
        {
            NextResult = new BrowserAutomationBootstrapResult(true, false, "ready")
        };
        using var vm = CreateViewModel(browserBootstrapper);

        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = false;
        vm.BrowserAutomationEnabled = true;
        vm.SelectedBrowserAutomationBackend = BrowserAutomationMcpProfiles.PlaywrightBackend;

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();
        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.IsComplete.Value);

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        var args = config.RootElement
            .GetProperty("McpServers")
            .GetProperty("browser_playwright")
            .GetProperty("Arguments")
            .EnumerateArray()
            .Select(a => a.GetString())
            .ToArray();

        Assert.Contains("@playwright/mcp@latest", args);
        Assert.Contains("--isolated", args);
        Assert.Contains("--image-responses", args);
        Assert.Contains("omit", args);
        Assert.Contains("--snapshot-mode", args);
        Assert.Contains("none", args);
        Assert.Contains("--browser", args);
        Assert.Contains(BrowserAutomationRuntimeDetector.GetPreferredPlaywrightBrowser(), args);

        var envVars = config.RootElement
            .GetProperty("McpServers")
            .GetProperty("browser_playwright")
            .GetProperty("EnvironmentVariables");
        Assert.True(envVars.TryGetProperty("PLAYWRIGHT_BROWSERS_PATH", out var browsersPath));
        Assert.False(string.IsNullOrWhiteSpace(browsersPath.GetString()));
    }

    [Fact]
    public async Task HealthCheck_BrowserAutomation_NodeMissing_PausesAndRequestsRetry()
    {
        var browserBootstrapper = new FakeBrowserAutomationBootstrapper
        {
            NextResult = new BrowserAutomationBootstrapResult(
                false,
                true,
                "Node.js is not installed.",
                "brew install node@20")
        };
        using var vm = CreateViewModel(browserBootstrapper);

        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = false;
        vm.BrowserAutomationEnabled = true;

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();
        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsComplete.Value);
        Assert.False(vm.IsHealthCheckRunning.Value);
        Assert.Contains("press Enter to retry", vm.StatusMessage.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, browserBootstrapper.CallCount);
        Assert.False(File.Exists(_paths.NetclawConfigPath) &&
                     File.ReadAllText(_paths.NetclawConfigPath).Contains("McpServers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HealthCheck_WritesDmConfigToNetclawJson()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = true;
        vm.SlackBotToken = "xoxb-test-bot-token";
        vm.SlackAppToken = "xapp-test-app-token";
        vm.SlackAllowDirectMessages = true;
        vm.SlackAllowedUserIdsInput = "U001ABC, U002DEF";

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();
        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsComplete.Value);

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        Assert.True(config.RootElement.TryGetProperty("Slack", out var slack));
        Assert.True(slack.GetProperty("AllowDirectMessages").GetBoolean());

        var allowedUsers = slack.GetProperty("AllowedUserIds");
        Assert.Equal(2, allowedUsers.GetArrayLength());
        Assert.Equal("U001ABC", allowedUsers[0].GetString());
        Assert.Equal("U002DEF", allowedUsers[1].GetString());
    }

    [Fact]
    public async Task HealthCheck_OmitsDmConfig_WhenDisabled()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = true;
        vm.SlackBotToken = "xoxb-test-bot-token";
        vm.SlackAppToken = "xapp-test-app-token";
        vm.SlackAllowDirectMessages = false;

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();
        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsComplete.Value);

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        Assert.True(config.RootElement.TryGetProperty("Slack", out var slack));
        Assert.False(slack.TryGetProperty("AllowDirectMessages", out _));
        Assert.False(slack.TryGetProperty("AllowedUserIds", out _));
    }

    [Fact]
    public async Task HealthCheck_WritesAllowedUsers_WithoutAutoEnablingDm()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = true;
        vm.SlackBotToken = "xoxb-test-bot-token";
        vm.SlackAppToken = "xapp-test-app-token";
        vm.SlackAllowDirectMessages = false; // not explicitly enabled
        vm.SlackAllowedUserIdsInput = "U001ABC";

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();
        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsComplete.Value);

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        Assert.True(config.RootElement.TryGetProperty("Slack", out var slack));
        Assert.False(slack.TryGetProperty("AllowDirectMessages", out _));

        var allowedUsers = slack.GetProperty("AllowedUserIds");
        Assert.Equal(1, allowedUsers.GetArrayLength());
        Assert.Equal("U001ABC", allowedUsers[0].GetString());
    }

    [Fact]
    public async Task HealthCheck_MemoryBackend_IsSqlite_NoProviderInConfig()
    {
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = false;

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();
        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(5));

        var config = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        // Memory section should not exist — SQLite is the implicit default
        Assert.False(config.RootElement.TryGetProperty("Memory", out _));

        // No McpServers.memorizer
        if (config.RootElement.TryGetProperty("McpServers", out var mcpServers))
            Assert.False(mcpServers.TryGetProperty("memorizer", out _));

        // Health check should show SQLite
        var memoryCheck = vm.HealthCheckResults
            .FirstOrDefault(h => h.Label.Contains("Memory", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(memoryCheck);
        Assert.True(memoryCheck.Passed);
        Assert.Contains("SQLite", memoryCheck.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthCheck_SlackProbeTimeout_ShowsTimeoutFailure()
    {
        _fakeSlackProbe.DelayBeforeResult = TimeSpan.FromMinutes(5); // way beyond 15s timeout
        using var vm = CreateViewModel();
        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = true;
        vm.SlackBotToken = "xoxb-test-bot-token";
        vm.SlackAppToken = "xapp-test-app-token";

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();

        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(vm.IsComplete.Value);

        var slackCheck = vm.HealthCheckResults
            .FirstOrDefault(h => h.Label.Contains("Slack", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(slackCheck);
        Assert.False(slackCheck.Passed);
        Assert.Contains("timed out", slackCheck.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HealthCheck_BrowserBootstrapTimeout_ShowsTimeoutFailure()
    {
        var browserBootstrapper = new FakeBrowserAutomationBootstrapper
        {
            NextResult = new BrowserAutomationBootstrapResult(true, false, "ready"),
            DelayBeforeResult = TimeSpan.FromMinutes(10) // way beyond 3m timeout
        };
        using var vm = CreateViewModel(browserBootstrapper);

        vm.SelectedProviderType = "ollama";
        vm.SlackEnabled = false;
        vm.BrowserAutomationEnabled = true;
        vm.SelectedBrowserAutomationBackend = BrowserAutomationMcpProfiles.PlaywrightBackend;

        vm.CurrentStep.Value = WizardStep.HealthCheck;
        vm.GoNext();

        await vm.HealthCheckCompletion!.WaitAsync(TimeSpan.FromMinutes(5));
        Assert.True(vm.IsComplete.Value);

        var browserCheck = vm.HealthCheckResults
            .FirstOrDefault(h => h.Label.Contains("Browser automation", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(browserCheck);
        Assert.False(browserCheck.Passed);
        Assert.Contains("timed out", browserCheck.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteIdentityFiles_GeneratesAllThreeFiles()
    {
        using var vm = CreateViewModel();
        vm.AgentName = "Hal";
        vm.CommunicationStyle = "Concise & casual";
        vm.UserName = "Dave";
        vm.UserTimezone = "America/Chicago";

        vm.WriteIdentityFiles();

        // SOUL.md
        Assert.True(File.Exists(_paths.SoulPath));
        var soul = File.ReadAllText(_paths.SoulPath);
        Assert.Contains("# You are Hal", soul, StringComparison.Ordinal);
        Assert.Contains("concise and casual", soul, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Dave", soul, StringComparison.Ordinal);
        Assert.Contains("America/Chicago", soul, StringComparison.Ordinal);
        Assert.DoesNotContain("Primary use", soul, StringComparison.OrdinalIgnoreCase);

        // AGENTS.md
        Assert.True(File.Exists(_paths.AgentsPath));
        var agents = File.ReadAllText(_paths.AgentsPath);
        Assert.Contains("Operating Rules", agents, StringComparison.Ordinal);
        Assert.Contains(_paths.IdentityDirectory, agents, StringComparison.Ordinal);
        Assert.Contains(_paths.SoulPath, agents, StringComparison.Ordinal);

        // TOOLING.md
        Assert.True(File.Exists(_paths.ToolingPath));
        var tooling = File.ReadAllText(_paths.ToolingPath);
        Assert.Contains("Environment Capabilities", tooling, StringComparison.Ordinal);
    }

    [Fact]
    public void PopulateChannelAudiences_personal_posture_dm_maps_to_personal()
    {
        using var vm = CreateViewModel();
        vm.ExposureMode = "Local only";
        vm.SlackAllowDirectMessages = true;

        vm.PopulateChannelAudiences();

        Assert.True(vm.ChannelAudiences.ContainsKey("dm"));
        Assert.Equal("personal", vm.ChannelAudiences["dm"]);
    }

    [Fact]
    public void PopulateChannelAudiences_team_posture_dm_maps_to_team()
    {
        using var vm = CreateViewModel();
        vm.ExposureMode = "Private network";
        vm.SlackAllowDirectMessages = true;

        vm.PopulateChannelAudiences();

        Assert.True(vm.ChannelAudiences.ContainsKey("dm"));
        Assert.Equal("team", vm.ChannelAudiences["dm"]);
    }

    [Fact]
    public void PopulateChannelAudiences_no_dm_enabled_has_no_dm_key()
    {
        using var vm = CreateViewModel();
        vm.ExposureMode = "Local only";
        vm.SlackAllowDirectMessages = false;

        vm.PopulateChannelAudiences();

        Assert.False(vm.ChannelAudiences.ContainsKey("dm"));
    }

    private InitWizardViewModel CreateViewModel(IBrowserAutomationBootstrapper? browserBootstrapper = null)
    {
        return new InitWizardViewModel(_paths, _registry, _fakeProbe, _fakeSlackProbe, browserBootstrapper);
    }
}

internal sealed class FakeBrowserAutomationBootstrapper : IBrowserAutomationBootstrapper
{
    public int CallCount { get; private set; }

    public BrowserAutomationBootstrapResult NextResult { get; set; } =
        new(true, false, "ready");

    /// <summary>
    /// Optional delay before returning results. Used to test timeout behavior.
    /// </summary>
    public TimeSpan? DelayBeforeResult { get; set; }

    public async Task<BrowserAutomationBootstrapResult> EnsureReadyAsync(string backend, CancellationToken ct = default)
    {
        CallCount++;
        if (DelayBeforeResult.HasValue)
#pragma warning disable SW004 // Intentional: fake service simulates latency for timeout testing
            await Task.Delay(DelayBeforeResult.Value, ct);
#pragma warning restore SW004
        return NextResult;
    }
}
