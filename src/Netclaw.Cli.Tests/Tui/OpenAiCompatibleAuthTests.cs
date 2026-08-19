// -----------------------------------------------------------------------
// <copyright file="OpenAiCompatibleAuthTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Cli.Provider;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Tests.Utilities;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Coverage for optional API-key auth on the openai-compatible provider:
/// auth shape declaration, auth-picker labels, wizard and provider-manager
/// state transitions, credential persistence, and a headless typed-key
/// end-to-end add flow.
/// </summary>
public sealed class OpenAiCompatibleAuthTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeProviderProbe _fakeProbe = new();
    private readonly ProviderDescriptorRegistry _registry = ProviderCommand.CreateDefaultRegistry();

    public OpenAiCompatibleAuthTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    // ── Auth shape ──

    [Fact]
    public void OpenAiCompatible_Auth_SupportsNoneAndApiKeyInOrder()
    {
        var descriptor = _registry.Get("openai-compatible");

        var auth = Assert.IsType<EndpointOrApiKeyAuth>(descriptor.Auth);
        Assert.Equal([AuthMethod.None, AuthMethod.ApiKey], auth.SupportedAuthMethods);
    }

    [Fact]
    public void BuildAuthMethodLabels_IncludesNoneForOptionalAuthProvider()
    {
        var labels = OAuthFlowViews.BuildAuthMethodLabels(_registry.Get("openai-compatible").Auth);

        Assert.Equal(["No auth (local endpoint)", "API Key"], labels);
    }

    [Fact]
    public void BuildAuthMethodLabels_ExcludesNoneForSingleMethodProviders()
    {
        Assert.Empty(OAuthFlowViews.BuildAuthMethodLabels(_registry.Get("ollama").Auth));
        Assert.Equal(["API Key"], OAuthFlowViews.BuildAuthMethodLabels(_registry.Get("anthropic").Auth));
    }

    [Fact]
    public void BuildAuthMethodLabels_UnchangedForMultiAuthWithoutNone()
    {
        var labels = OAuthFlowViews.BuildAuthMethodLabels(_registry.Get("openai").Auth);

        Assert.DoesNotContain(labels, l => l.Contains("No auth", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseAuthMethodLabel_RoundTripsNoAuthLabel()
    {
        var auth = _registry.Get("openai-compatible").Auth;

        Assert.Equal(AuthMethod.None, OAuthFlowViews.ParseAuthMethodLabel("No auth (local endpoint)", auth));
        Assert.Equal(AuthMethod.ApiKey, OAuthFlowViews.ParseAuthMethodLabel("API Key", auth));
    }

    // ── Provider manager state machine ──

    [Fact]
    public void AdvanceAfterName_OpenAiCompatible_GoesToAuthSelect()
    {
        using var vm = CreateManagerVm();
        vm.StartAddForType("openai-compatible");

        vm.AdvanceAfterName();

        Assert.Equal(ProviderManagerState.AddSelectAuth, vm.CurrentState.Value);
    }

    [Fact]
    public void SelectAuthMethod_OpenAiCompatibleNone_GoesToCredentials()
    {
        using var vm = CreateManagerVm();
        vm.StartAddForType("openai-compatible");
        vm.AdvanceAfterName();

        vm.SelectAuthMethod(AuthMethod.None);

        Assert.Equal(ProviderManagerState.AddCredentials, vm.CurrentState.Value);
    }

    [Fact]
    public void SelectAuthMethod_OpenAiCompatibleApiKey_GoesToEndpointStage()
    {
        using var vm = CreateManagerVm();
        vm.StartAddForType("openai-compatible");
        vm.AdvanceAfterName();

        vm.SelectAuthMethod(AuthMethod.ApiKey);

        Assert.Equal(ProviderManagerState.AddCredentialsEndpoint, vm.CurrentState.Value);
    }

    [Fact]
    public void SubmitEndpointCredential_SetsEndpointAndAdvancesToKeyStage()
    {
        using var vm = CreateManagerVm();
        vm.StartAddForType("openai-compatible");
        vm.SelectAuthMethod(AuthMethod.ApiKey);

        vm.SubmitEndpointCredential("http://gpu.lan:8000");

        Assert.Equal("http://gpu.lan:8000", vm.NewEndpoint);
        Assert.Equal(ProviderManagerState.AddCredentials, vm.CurrentState.Value);
    }

    [Fact]
    public void SubmitCredentials_OpenAiCompatibleApiKeyWithEmptyKey_BlocksBeforeProbe()
    {
        // Fake-failure gate: declaring ApiKey without a key must block before
        // any probe or persistence happens.
        using var vm = CreateManagerVm();
        vm.StartAddForType("openai-compatible");
        vm.SelectAuthMethod(AuthMethod.ApiKey);
        vm.SubmitEndpointCredential("http://gpu.lan:8000");
        vm.NewApiKey = null;

        vm.SubmitCredentials();

        Assert.Equal(ProviderManagerState.AddCredentials, vm.CurrentState.Value);
        Assert.Equal(0, _fakeProbe.ProbeCallCount);
        Assert.Contains("API key is required", vm.StatusMessage.Value);
        Assert.False(File.Exists(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task AddFlow_OpenAiCompatibleApiKey_PersistsMethodAndEncryptedSecret()
    {
        using var vm = CreateManagerVm();
        vm.StartAddForType("openai-compatible");
        vm.AdvanceAfterName();
        vm.SelectAuthMethod(AuthMethod.ApiKey);
        vm.SubmitEndpointCredential("http://gpu.lan:8000");
        vm.NewApiKey = "sk-gateway-key";

        vm.SubmitCredentials();
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(ProviderManagerState.AddComplete, vm.CurrentState.Value);
        Assert.Equal("sk-gateway-key", _fakeProbe.LastApiKey);

        var config = ReadConfig();
        var entry = config.GetProperty("Providers").GetProperty("my-openai-compatible");
        Assert.Equal("openai-compatible", entry.GetProperty("Type").GetString());
        Assert.Equal("ApiKey", entry.GetProperty("AuthMethod").GetString());
        Assert.Equal("http://gpu.lan:8000", entry.GetProperty("Endpoint").GetString());

        var secrets = File.ReadAllText(_paths.SecretsPath);
        Assert.Contains("ApiKey", secrets);
        Assert.Contains("ENC:", secrets);
    }

    [Fact]
    public async Task AddFlow_OpenAiCompatibleNone_PersistsNoMethodAndNoSecret()
    {
        using var vm = CreateManagerVm();
        vm.StartAddForType("openai-compatible");
        vm.AdvanceAfterName();
        vm.SelectAuthMethod(AuthMethod.None);
        vm.NewEndpoint = "http://gpu.lan:8000";

        vm.SubmitCredentials();
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(ProviderManagerState.AddComplete, vm.CurrentState.Value);
        Assert.Null(_fakeProbe.LastApiKey);

        var config = ReadConfig();
        var entry = config.GetProperty("Providers").GetProperty("my-openai-compatible");
        Assert.Equal("openai-compatible", entry.GetProperty("Type").GetString());
        Assert.Equal("http://gpu.lan:8000", entry.GetProperty("Endpoint").GetString());
        Assert.False(entry.TryGetProperty("AuthMethod", out _));

        if (File.Exists(_paths.SecretsPath))
        {
            var secrets = File.ReadAllText(_paths.SecretsPath);
            Assert.DoesNotContain("ApiKey", secrets);
        }
    }

    // ── Provider manager fix flow ──

    [Fact]
    public async Task SubmitFixCredentials_OpenAiCompatibleNoneEntry_DoesNotRequireKey()
    {
        WriteConfigProvider("my-vllm", "openai-compatible", authMethod: null);
        using var vm = CreateManagerVm();
        vm.RefreshDisplayProviders();
        vm.DetailProvider = vm.DisplayProviders.Single(p => p.ConfiguredName == "my-vllm");
        vm.StartFixCredentials(vm.DetailProvider);
        vm.FixApiKey = null;
        vm.FixEndpoint = "http://gpu.lan:8001";

        vm.SubmitFixCredentials();
        await vm.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // No key required: the fix probe ran (plus the eager re-probe after
        // success) and the fix-flow success path completed.
        Assert.True(_fakeProbe.ProbeCallCount >= 1);
        Assert.Contains("Credentials updated", vm.StatusMessage.Value);
    }

    [Fact]
    public void SubmitFixCredentials_OpenAiCompatibleApiKeyEntryWithEmptyKey_Blocks()
    {
        WriteConfigProvider("my-vllm", "openai-compatible", authMethod: "ApiKey");
        using var vm = CreateManagerVm();
        vm.RefreshDisplayProviders();
        vm.DetailProvider = vm.DisplayProviders.Single(p => p.ConfiguredName == "my-vllm");
        vm.StartFixCredentials(vm.DetailProvider);
        vm.FixApiKey = null;

        vm.SubmitFixCredentials();

        Assert.Equal(ProviderManagerState.FixCredentials, vm.CurrentState.Value);
        Assert.Equal(0, _fakeProbe.ProbeCallCount);
        Assert.Contains("API key is required", vm.StatusMessage.Value);
    }

    [Fact]
    public void SubmitFixEndpoint_ApiKeyEntry_AdvancesToKeyStage()
    {
        WriteConfigProvider("my-vllm", "openai-compatible", authMethod: "ApiKey");
        using var vm = CreateManagerVm();
        vm.RefreshDisplayProviders();
        vm.DetailProvider = vm.DisplayProviders.Single(p => p.ConfiguredName == "my-vllm");
        vm.StartFixCredentials(vm.DetailProvider);

        vm.SubmitFixEndpoint("http://gpu.lan:8001");

        Assert.Equal(ProviderManagerState.FixApiKey, vm.CurrentState.Value);
        Assert.Equal("http://gpu.lan:8001", vm.FixEndpoint);
    }

    // ── Wizard ──

    [Fact]
    public void Wizard_TryGoBack_FromApiKeyStep_ReturnsToEndpoint()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe)
        {
            SelectedProviderType = "openai-compatible",
            SelectedAuthMethod = AuthMethod.ApiKey,
        };
        step.SetSubStep(10);

        Assert.True(step.TryGoBack());
        Assert.Equal(2, step.CurrentSubStep);
    }

    [Fact]
    public async Task Wizard_Probe_OpenAiCompatibleApiKey_CarriesKeyInProbeEntry()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe)
        {
            SelectedProviderType = "openai-compatible",
            SelectedAuthMethod = AuthMethod.ApiKey,
            EndpointInput = "http://gpu.lan:8000",
            ApiKeyInput = "sk-gateway-key",
        };

        step.StartProbe();
        await step.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("openai-compatible", _fakeProbe.LastProviderType);
        Assert.Equal("sk-gateway-key", _fakeProbe.LastApiKey);
    }

    [Fact]
    public async Task Wizard_Probe_OpenAiCompatibleNone_SendsNoCredential()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe)
        {
            SelectedProviderType = "openai-compatible",
            SelectedAuthMethod = AuthMethod.None,
            EndpointInput = "http://gpu.lan:8000",
        };

        step.StartProbe();
        await step.ProbeCompletion!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("openai-compatible", _fakeProbe.LastProviderType);
        Assert.Null(_fakeProbe.LastApiKey);
    }

    [Fact]
    public void Wizard_ContributeConfig_NoneMethod_DefaultsEndpointFromDescriptor()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe)
        {
            SelectedProviderType = "openai-compatible",
            SelectedAuthMethod = AuthMethod.None,
            SelectedModelId = "qwen3:30b",
        };

        var builder = new WizardConfigBuilder(_paths);
        step.ContributeConfig(builder);

        Assert.Equal(AuthMethod.None, builder.Provider!.AuthMethod);
        Assert.Equal(_registry.Get("openai-compatible").DefaultEndpoint, builder.Provider.Endpoint);
    }

    [Fact]
    public void Wizard_ContributeConfig_ApiKeyMethod_EmitsMethodAndEndpoint()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe)
        {
            SelectedProviderType = "openai-compatible",
            SelectedAuthMethod = AuthMethod.ApiKey,
            EndpointInput = "http://gpu.lan:8000",
            ApiKeyInput = "sk-gateway-key",
        };

        var builder = new WizardConfigBuilder(_paths);
        step.ContributeConfig(builder);

        Assert.Equal(AuthMethod.ApiKey, builder.Provider!.AuthMethod);
        Assert.Equal("http://gpu.lan:8000", builder.Provider.Endpoint);
    }

    [Fact]
    public void Wizard_WriteProviderCredentials_None_WritesNoAuthMethodAndNoSecret()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe)
        {
            SelectedProviderType = "openai-compatible",
            SelectedAuthMethod = AuthMethod.None,
            EndpointInput = "http://gpu.lan:8000",
        };

        step.WriteProviderCredentials(_paths);

        var entry = ReadProviderEntry("openai-compatible");
        Assert.Equal("openai-compatible", entry.GetProperty("Type").GetString());
        Assert.Equal("http://gpu.lan:8000", entry.GetProperty("Endpoint").GetString());
        Assert.False(entry.TryGetProperty("AuthMethod", out _));

        if (File.Exists(_paths.SecretsPath))
        {
            var secrets = File.ReadAllText(_paths.SecretsPath);
            Assert.DoesNotContain("ApiKey", secrets);
        }
    }

    [Fact]
    public void Wizard_WriteProviderCredentials_ApiKey_WritesMethodAndEncryptedSecret()
    {
        using var step = new ProviderStepViewModel(_registry, _fakeProbe)
        {
            SelectedProviderType = "openai-compatible",
            SelectedAuthMethod = AuthMethod.ApiKey,
            EndpointInput = "http://gpu.lan:8000",
            ApiKeyInput = "sk-gateway-key",
        };

        step.WriteProviderCredentials(_paths);

        var entry = ReadProviderEntry("openai-compatible");
        Assert.Equal("ApiKey", entry.GetProperty("AuthMethod").GetString());

        var secrets = File.ReadAllText(_paths.SecretsPath);
        Assert.Contains("ENC:", secrets);
    }

    // ── Headless typed-key end-to-end (automation floor) ──

    [Fact]
    public async Task ManagerAddFlow_OpenAiCompatibleApiKey_TypedKeyEndToEnd()
    {
        var (terminal, app, vm, input) = CreateHeadlessApp();

        using var appCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = app.RunAsync(appCts.Token);

        try
        {
            await WaitForAsync(() => terminal.Contains("Provider Manager"), appCts.Token);

            foreach (var _ in _registry.KnownTypeKeys.TakeWhile(t => t != "openai-compatible"))
                input.EnqueueKey(ConsoleKey.DownArrow);

            // type row -> name step; accept generated name.
            input.EnqueueKey(ConsoleKey.Enter);
            input.EnqueueKey(ConsoleKey.Enter);
            // auth picker: "No auth (local endpoint)" is first, "API Key" second.
            input.EnqueueKey(ConsoleKey.DownArrow);
            input.EnqueueKey(ConsoleKey.Enter);
            // endpoint stage.
            input.EnqueuePaste("http://gpu.lan:8000");
            input.EnqueueKey(ConsoleKey.Enter);
            // key stage.
            input.EnqueuePaste("sk-gateway-key");
            input.EnqueueKey(ConsoleKey.Enter);

            await WaitForAsync(() => vm.CurrentState.Value == ProviderManagerState.AddComplete, appCts.Token);

            Assert.Equal("openai-compatible", vm.NewProviderType);
            Assert.Equal("http://gpu.lan:8000", vm.NewEndpoint);
            Assert.Equal("sk-gateway-key", vm.NewApiKey);
            Assert.True(File.Exists(_paths.NetclawConfigPath));

            var entry = ReadProviderEntry("my-openai-compatible");
            Assert.Equal("ApiKey", entry.GetProperty("AuthMethod").GetString());
        }
        finally
        {
            input.EnqueueKey(ConsoleKey.Q, control: true);
            await run.WaitAsync(appCts.Token);
        }
    }

    // ── Helpers ──

    private ProviderManagerViewModel CreateManagerVm()
    {
        var vm = new ProviderManagerViewModel(_paths, _registry, _fakeProbe);
        vm.RefreshDisplayProviders();
        return vm;
    }

    private JsonElement ReadConfig()
    {
        Assert.True(File.Exists(_paths.NetclawConfigPath));
        using var doc = JsonDocument.Parse(File.ReadAllText(_paths.NetclawConfigPath));
        return doc.RootElement.Clone();
    }

    private JsonElement ReadProviderEntry(string name)
        => ReadConfig().GetProperty("Providers").GetProperty(name);

    private void WriteConfigProvider(string name, string type, string? authMethod)
    {
        var entry = new Dictionary<string, object> { ["Type"] = type };
        if (authMethod is not null)
            entry["AuthMethod"] = authMethod;
        entry["Endpoint"] = "http://gpu.lan:8000";

        WriteConfig(new Dictionary<string, object>
        {
            ["configVersion"] = 1,
            ["Providers"] = new Dictionary<string, object> { [name] = entry }
        });
    }

    private void WriteConfig(Dictionary<string, object> data)
    {
        File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    private (VirtualTerminal Terminal, TerminaApplication App, ProviderManagerViewModel Vm, VirtualInputSource Input)
        CreateHeadlessApp()
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        ProviderManagerViewModel? capturedVm = null;

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/provider", builder =>
        {
            builder.RegisterRoute<ProviderManagerPage, ProviderManagerViewModel>(
                "/provider",
                _ => new ProviderManagerPage(),
                _ =>
                {
                    capturedVm = new ProviderManagerViewModel(_paths, _registry, _fakeProbe);
                    return capturedVm;
                });
        });

        var sp = services.BuildServiceProvider();
        return (terminal, sp.GetRequiredService<TerminaApplication>(), capturedVm!, virtualInput);
    }

    private static async Task WaitForAsync(Func<bool> predicate, CancellationToken ct)
    {
        while (!predicate())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
