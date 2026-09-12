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
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui.Wizard;

public sealed class ProviderStepViewModelTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly WizardContext _context;
    private readonly FakeProviderProbe _fakeProbe = new();
    private readonly ProviderDescriptorRegistry _registry = ProviderCommand.CreateDefaultRegistry();

    public ProviderStepViewModelTests()
    {
        var paths = new NetclawPaths(_dir.Path);
        paths.EnsureDirectoriesExist();
        _context = new WizardContext
        {
            Paths = paths,
            Registry = _registry,
            RequestRedraw = () => { }
        };
    }

    public void Dispose() => _dir.Dispose();

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
    public void TryGoBack_FromGitHubCopilotModelSelection_GoesToAuthHostChoice()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe)
        {
            SelectedProviderType = "github-copilot",
            SelectedAuthMethod = AuthMethod.OAuthDevice,
        };
        step.SetSubStep(4);

        Assert.True(step.TryGoBack());

        Assert.Equal(7, step.CurrentSubStep);
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
    public void OnEnter_Back_AfterGitHubCopilotEnterpriseFlow_ResumesAtModelSelection()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe)
        {
            SelectedProviderType = "github-copilot",
            SelectedAuthMethod = AuthMethod.OAuthDevice,
        };

        step.SetSubStep(7);
        step.SetSubStep(8);
        step.SetSubStep(9);
        step.SetSubStep(5);
        step.SetSubStep(3);
        step.SetSubStep(4);

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
            new DiscoveredModel { ModelId = new Netclaw.Configuration.ModelId("llama3:latest") },
            new DiscoveredModel { ModelId = new Netclaw.Configuration.ModelId("qwen3:30b") }
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
    public async Task Superseded_probe_completion_does_not_cancel_the_replacement_probe()
    {
        var ct = TestContext.Current.CancellationToken;
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        step.SelectedProviderType = "ollama";
        step.EndpointInput = "http://localhost:11434";
        _fakeProbe.Gate = new TaskCompletionSource();
        _fakeProbe.NextResult = new ProviderProbeResult(true, null,
            [new DiscoveredModel { ModelId = new Netclaw.Configuration.ModelId("m") }]);

        step.StartProbe();                  // probe A — blocks on the gate
        var probeA = step.ProbeCompletion!;
        step.StartProbe();                  // cancels A, starts probe B (also blocks on the gate)
        var probeB = step.ProbeCompletion!;

        // Probe A was cancelled by the second StartProbe; let its finally run. With the bug, that
        // finally tore down the shared _probeCts field — which now holds probe B's live CTS.
        await probeA.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // Releasing the gate, probe B must complete SUCCESSFULLY: its CTS was not cancelled or
        // disposed by the superseded probe's finally.
        _fakeProbe.Gate.SetResult();
        await probeB.WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.True(step.ProbeResult.Value!.Success);
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
    public void ContributeConfig_SelectedDiscoveredModel_OmitsCapabilityOverrides()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        step.SelectedProviderType = "OpenAI";
        step.SelectedAuthMethod = AuthMethod.OAuthDevice;
        step.SelectedModelId = "gpt-new-codex";
        step.DiscoveredModels.Add(new DiscoveredModel
        {
            ModelId = new Netclaw.Configuration.ModelId("gpt-new-codex"),
            ContextWindowTokens = 512000,
            InputModalities = ModelModality.Text | ModelModality.Image,
            OutputModalities = ModelModality.Text,
        });

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Model);
        Assert.Equal(ModelDiscoverySource.Live, builder.Model!.Provenance);

        var config = builder.BuildConfigDictionary();
        var models = (Dictionary<string, object>)config["Models"];
        var roles = (Dictionary<string, object>)models["Roles"];
        var definitions = (Dictionary<string, object>)models["Definitions"];
        var main = (Dictionary<string, object>)definitions[(string)roles["Main"]];
        Assert.False(main.ContainsKey("ContextWindow"));
        Assert.False(main.ContainsKey("InputModalities"));
        Assert.False(main.ContainsKey("OutputModalities"));
    }

    [Theory]
    [InlineData("ContextWindow", "65536")]
    [InlineData("InputModalities", "Text, Image")]
    [InlineData("OutputModalities", "Text, Audio")]
    public void ContributeConfig_SameModelWithoutCapabilityControls_PreservesStoredCapability(
        string propertyName,
        string expectedValue)
    {
        File.WriteAllText(_context.Paths.NetclawConfigPath, System.Text.Json.JsonSerializer.Serialize(
            new Dictionary<string, object>
            {
                ["configVersion"] = 1,
                ["Providers"] = new Dictionary<string, object>
                {
                    ["openai"] = new Dictionary<string, object>
                    {
                        ["Type"] = "openai",
                        ["AuthMethod"] = "OAuthDevice"
                    }
                },
                ["Models"] = new Dictionary<string, object>
                {
                    ["Main"] = new Dictionary<string, object>
                    {
                        ["Provider"] = "openai",
                        ["ModelId"] = "gpt-new-codex",
                        ["ContextWindow"] = 65536,
                        ["InputModalities"] = "Text, Image",
                        ["OutputModalities"] = "Text, Audio"
                    }
                }
            }));

        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        step.SelectedProviderType = "OpenAI";
        step.SelectedAuthMethod = AuthMethod.OAuthDevice;
        step.SelectedModelId = "gpt-new-codex";
        step.DiscoveredModels.Add(new DiscoveredModel
        {
            ModelId = new Netclaw.Configuration.ModelId("gpt-new-codex"),
            ContextWindowTokens = 512000,
            InputModalities = ModelModality.Text,
            OutputModalities = ModelModality.Text,
        });

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        var config = builder.BuildConfigDictionary();
        var models = (Dictionary<string, object>)config["Models"];
        var roles = (Dictionary<string, object>)models["Roles"];
        var definitions = (Dictionary<string, object>)models["Definitions"];
        var main = (Dictionary<string, object>)definitions[(string)roles["Main"]];
        Assert.Equal(expectedValue, main[propertyName].ToString());
    }

    [Fact]
    public void GitHubCopilotPublicHost_ContributeConfig_EmitsNoVendorOptions()
    {
        var previous = Environment.GetEnvironmentVariable("GH_HOST");
        try
        {
            Environment.SetEnvironmentVariable("GH_HOST", "enterprise.example.com");
            using var step = new ProviderStepViewModel(_registry, _fakeProbe);
            step.SelectedProviderType = "github-copilot";
            step.SelectedAuthMethod = AuthMethod.OAuthDevice;
            step.SelectGitHubCopilotAuthHost(GitHubCopilotAuthHostMode.GitHubCom);

            var builder = new WizardConfigBuilder(_context.Paths);
            step.ContributeConfig(builder);

            Assert.NotNull(builder.Provider);
            Assert.Equal("github-copilot", builder.Provider!.TypeKey);
            Assert.Null(builder.Provider.VendorOptions);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_HOST", previous);
        }
    }

    [Fact]
    public void GitHubCopilotEnterpriseHostOnly_ContributeConfig_EmitsDerivedVendorOptions()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        step.SelectedProviderType = "github-copilot";
        step.SelectedAuthMethod = AuthMethod.OAuthDevice;

        Assert.True(step.TrySetGitHubCopilotEnterpriseHost("ghe.example.com", out var hostError), hostError);
        Assert.True(step.TryStartGitHubCopilotEnterpriseOAuth(null, out var apiError), apiError);

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Provider?.VendorOptions);
        Assert.Equal("https://ghe.example.com", builder.Provider!.VendorOptions!["GitHubHost"]);
        Assert.Equal("https://ghe.example.com/api/v3", builder.Provider.VendorOptions["GitHubApiBase"]);
    }

    [Fact]
    public void GitHubCopilotEnterpriseExplicitApiBase_ContributeConfig_EmitsCanonicalVendorOptions()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        step.SelectedProviderType = "github-copilot";
        step.SelectedAuthMethod = AuthMethod.OAuthDevice;

        Assert.True(step.TrySetGitHubCopilotEnterpriseHost("https://example.ghe.com", out var hostError), hostError);
        Assert.True(step.TryStartGitHubCopilotEnterpriseOAuth("https://api.example.ghe.com/", out var apiError), apiError);

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Provider?.VendorOptions);
        Assert.Equal("https://example.ghe.com", builder.Provider!.VendorOptions!["GitHubHost"]);
        Assert.Equal("https://api.example.ghe.com", builder.Provider.VendorOptions["GitHubApiBase"]);
    }

    [Fact]
    public void GitHubCopilotEnterpriseHostChange_ClearsStaleExplicitApiBase()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe)
        {
            SelectedProviderType = "github-copilot",
            SelectedAuthMethod = AuthMethod.OAuthDevice,
            GitHubCopilotHostInput = "https://old.ghe.example.com",
            GitHubCopilotApiBaseInput = "https://api.old.ghe.example.com",
            VendorOptions = new Dictionary<string, object?>
            {
                ["GitHubHost"] = "https://old.ghe.example.com",
                ["GitHubApiBase"] = "https://api.old.ghe.example.com",
            },
        };

        Assert.True(step.TrySetGitHubCopilotEnterpriseHost("https://new.ghe.example.com", out var error), error);

        Assert.Equal("https://new.ghe.example.com", step.GitHubCopilotHostInput);
        Assert.Null(step.GitHubCopilotApiBaseInput);
        Assert.Null(step.VendorOptions);
    }

    [Fact]
    public void GitHubCopilotEnterpriseInputs_RejectInvalidValuesBeforeVendorOptions()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        step.SelectedProviderType = "github-copilot";
        step.SelectedAuthMethod = AuthMethod.OAuthDevice;

        Assert.False(step.TrySetGitHubCopilotEnterpriseHost("http://ghe.example.com", out var hostError));
        Assert.Contains("HTTPS", hostError);
        Assert.Null(step.VendorOptions);

        Assert.True(step.TrySetGitHubCopilotEnterpriseHost("ghe.example.com", out hostError), hostError);
        Assert.False(step.TryStartGitHubCopilotEnterpriseOAuth("http://ghe.example.com/api/v3", out var apiError));
        Assert.Contains("HTTPS", apiError);
        Assert.Null(step.VendorOptions);
    }

    [Fact]
    public void ContributeConfig_DiscoveredModelWithoutModalities_OmitsCapabilityOverrides()
    {
        // An openai-compatible /v1/models listing reports no modalities, so the
        // discovered model leaves them unset. The wizard must NOT bake a guessed Text
        // into config — that override would beat real detection on every daemon boot
        // and silently demote a multimodal model to text-only (#1290).
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        step.SelectedProviderType = "openai-compatible";
        step.SelectedAuthMethod = AuthMethod.None;
        step.SelectedModelId = "qwen-vl";
        step.DiscoveredModels.Add(new DiscoveredModel
        {
            ModelId = new Netclaw.Configuration.ModelId("qwen-vl"),
            ContextWindowTokens = 32768,
        });

        var builder = new WizardConfigBuilder(_context.Paths);
        step.ContributeConfig(builder);

        Assert.NotNull(builder.Model);
        Assert.Equal(ModelDiscoverySource.Live, builder.Model!.Provenance);

        var config = builder.BuildConfigDictionary();
        var models = (Dictionary<string, object>)config["Models"];
        var roles = (Dictionary<string, object>)models["Roles"];
        var definitions = (Dictionary<string, object>)models["Definitions"];
        var main = (Dictionary<string, object>)definitions[(string)roles["Main"]];
        Assert.False(main.ContainsKey("ContextWindow"));
        Assert.False(main.ContainsKey("InputModalities"));
        Assert.False(main.ContainsKey("OutputModalities"));
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

    [Fact]
    public async Task ContributeHealthChecks_NoProvider_EmitsWarnItem()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe);
        // SelectedProviderType deliberately left null.

        var items = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(items, () => { });
        await step.ContributeHealthChecksAsync(runner, TestContext.Current.CancellationToken);

        var providerItem = items[0];
        Assert.True(providerItem.Passed);
        Assert.True(providerItem.IsWarning);
        Assert.Contains("No-Op", providerItem.Label);
        Assert.Contains("netclaw init", providerItem.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HealthCheckRunner_WarningItemDoesNotCountAsCleanPass()
    {
        var items = new List<HealthCheckItem>
        {
            new("No provider configured", Passed: true, IsWarning: true),
        };
        var runner = new HealthCheckRunner(items, () => { });

        Assert.False(runner.AllPassed);
    }

    [Fact]
    public async Task ContributeHealthChecks_ProviderSelected_EmitsPassItem()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe)
        {
            SelectedProviderType = "ollama",
        };

        var items = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(items, () => { });
        await step.ContributeHealthChecksAsync(runner, TestContext.Current.CancellationToken);

        var providerItem = items[0];
        Assert.True(providerItem.Passed);
        Assert.False(providerItem.IsWarning);
        Assert.Contains("LLM provider configured", providerItem.Label);

        var modelItem = items[1];
        Assert.True(modelItem.Passed);
        Assert.True(modelItem.IsWarning);
        Assert.Contains("No model selected", modelItem.Label);
        Assert.Contains("No-Op", modelItem.Label);
        Assert.False(runner.AllPassed);
    }

    [Fact]
    public async Task ContributeHealthChecks_ProviderAndModelSelected_CountsAsCleanPass()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe)
        {
            SelectedProviderType = "ollama",
            SelectedModelId = "qwen3:30b",
        };

        var items = new List<HealthCheckItem>();
        var runner = new HealthCheckRunner(items, () => { });
        await step.ContributeHealthChecksAsync(runner, TestContext.Current.CancellationToken);

        Assert.All(items, item =>
        {
            Assert.True(item.Passed);
            Assert.False(item.IsWarning);
        });
        Assert.True(runner.AllPassed);
    }

    // Reuse the existing FakeProviderProbe from the monolith tests
    private sealed class FakeProviderProbe : IProviderProbe
    {
        public ProviderProbeResult NextResult { get; set; } = new(false, "not configured", []);
        public string? LastProviderType { get; private set; }
        public string? LastApiKey { get; private set; }

        // When set, the ProviderEntry probe blocks (observing the token) until completed — used to
        // stage overlapping probes for the CTS-lifecycle race test. Null returns immediately.
        public TaskCompletionSource? Gate { get; set; }

        public Task<ProviderProbeResult> ProbeAsync(
            string providerType, string? endpoint, string? apiKey,
            CancellationToken ct = default)
        {
            LastProviderType = providerType;
            LastApiKey = apiKey;
            return Task.FromResult(NextResult);
        }

        public async Task<ProviderProbeResult> ProbeAsync(
            ProviderEntry entry, CancellationToken ct = default)
        {
            LastProviderType = entry.Type;
            LastApiKey = entry.ApiKey?.Value ?? entry.OAuthAccessToken?.Value;
            if (Gate is not null)
                await Gate.Task.WaitAsync(ct);
            return NextResult;
        }

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
