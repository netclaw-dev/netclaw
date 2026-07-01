// -----------------------------------------------------------------------
// <copyright file="DaemonProviderServiceExtensionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class DaemonProviderServiceExtensionsTests
{
    [Fact]
    public void NoProviderConfigured_RegistersNoOpChatClientProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

        var providers = new Dictionary<string, ProviderEntry>();
        var models = new ModelSelection();
        var validation = ProviderRuntimeValidation.Evaluate(
            providers,
            models,
            ProviderRuntimeConfiguration.FromExplicitRoles(providers, main: false, fallback: false, compaction: false));

        Assert.Equal(ProviderRuntimeStatus.NoProviderConfigured, validation.Status);

        services.AddDaemonLlmProviders(providers, models, validation);

        using var sp = services.BuildServiceProvider();
        var chatProvider = sp.GetRequiredService<IChatClientProvider>();

        Assert.IsType<NoOpChatClientProvider>(chatProvider);
        Assert.True(chatProvider.IsDegraded);
    }

    [Fact]
    public void NoProviderConfigured_DoesNotRegisterRealPluginFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

        var validation = ProviderRuntimeValidation.Evaluate(
            new Dictionary<string, ProviderEntry>(),
            new ModelSelection(),
            ProviderRuntimeConfiguration.FromExplicitRoles(
                new Dictionary<string, ProviderEntry>(), main: false, fallback: false, compaction: false));

        services.AddDaemonLlmProviders(
            new Dictionary<string, ProviderEntry>(),
            new ModelSelection(),
            validation);

        using var sp = services.BuildServiceProvider();

        // ProviderPluginFactory is only registered on the valid path.
        Assert.Null(sp.GetService<ProviderPluginFactory>());
    }

    [Fact]
    public void NoProviderConfigured_KeepsProviderDescriptorsAvailableForRecovery()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(new NetclawPaths(Path.Combine(
            Path.GetTempPath(),
            $"netclaw-provider-tests-{Guid.NewGuid():N}")));

        var validation = ProviderRuntimeValidation.Evaluate(
            new Dictionary<string, ProviderEntry>(),
            new ModelSelection(),
            ProviderRuntimeConfiguration.FromExplicitRoles(
                new Dictionary<string, ProviderEntry>(), main: false, fallback: false, compaction: false));

        services.AddDaemonLlmProviders(
            new Dictionary<string, ProviderEntry>(),
            new ModelSelection(),
            validation);

        using var sp = services.BuildServiceProvider();

        var registry = sp.GetRequiredService<ProviderDescriptorRegistry>();
        Assert.Contains("openrouter", registry.KnownTypeKeys);
    }

    [Fact]
    public void UnknownProviderReference_RegistersNoOpInsteadOfCrashing()
    {
        // Regression for the operator typo scenario (Models:Main.Provider
        // "ollama-local1" vs configured "ollama-local") that previously
        // crashed the daemon with a raw ProviderPluginFactory stack trace.
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

        var providers = new Dictionary<string, ProviderEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["github-copilot"] = new ProviderEntry { Type = "github-copilot" },
            ["my-openrouter"] = new ProviderEntry { Type = "openrouter" },
            ["ollama-local"] = new ProviderEntry { Type = "ollama" },
        };
        var models = new ModelSelection
        {
            Main = new ModelReference { Provider = "ollama-local1", ModelId = "qwen3:30b" },
        };
        var validation = ProviderRuntimeValidation.Evaluate(
            providers,
            models,
            ProviderRuntimeConfiguration.FromExplicitRoles(providers, main: true, fallback: false, compaction: false));

        services.AddDaemonLlmProviders(providers, models, validation);

        using var sp = services.BuildServiceProvider();

        // Must not throw — degraded startup is the contract.
        var chatProvider = sp.GetRequiredService<IChatClientProvider>();
        Assert.IsType<NoOpChatClientProvider>(chatProvider);
        Assert.True(chatProvider.IsDegraded);
    }

    [Fact]
    public async Task NoOpProvider_ResponseStartsWithFixedBanner()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

        var validation = ProviderRuntimeValidation.Evaluate(
            new Dictionary<string, ProviderEntry>(),
            new ModelSelection(),
            ProviderRuntimeConfiguration.FromExplicitRoles(
                new Dictionary<string, ProviderEntry>(), main: false, fallback: false, compaction: false));

        services.AddDaemonLlmProviders(
            new Dictionary<string, ProviderEntry>(),
            new ModelSelection(),
            validation);

        using var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IChatClientProvider>().GetClient(ModelRole.Main);

        var response = await client.GetResponseAsync(
            new[] { new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "hi") },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.StartsWith(NoOpChatClient.LeadingPhrase, response.Text);
    }
}
