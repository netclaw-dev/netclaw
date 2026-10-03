// -----------------------------------------------------------------------
// <copyright file="OpenAiCodexCapabilityResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Providers;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using Netclaw.Providers.OpenAi;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class OpenAiCodexCapabilityResolverTests
{
    private const string CodexModelId = "gpt-5.6-sol";
    private const string ProviderName = "codex-provider";

    [Fact]
    public void ConfiguredProbeResolvesToTheOAuthRefreshingProviderProbe()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new NetclawPaths(Path.Combine(
            Path.GetTempPath(), $"netclaw-codex-probe-{Guid.NewGuid():N}")));
        services.AddLlmProviders();

        using var serviceProvider = services.BuildServiceProvider();

        var configuredProbe = serviceProvider.GetRequiredService<IConfiguredProviderProbe>();

        Assert.Same(serviceProvider.GetRequiredService<ProviderOAuthRefreshingProbe>(), configuredProbe);
        Assert.Same(serviceProvider.GetRequiredService<IProviderProbe>(), configuredProbe);
    }

    [Fact]
    public async Task CodexOAuthContextWinsBeforeOracleAndConfiguredValueStillWins()
    {
        var provider = new ProviderEntry
        {
            Type = "openai",
            AuthMethod = AuthMethod.OAuthDevice,
        };
        var probe = new FakeConfiguredProviderProbe(new ProviderProbeResult(true, null,
        [
            new DiscoveredModel
            {
                ModelId = new ModelId(CodexModelId),
                ContextWindowTokens = 272_000,
            },
        ]));
        var codex = CreateResolver(probe, provider);
        var oracle = new FakeResolver(new ResolvedModelCapabilities(
            CodexModelId,
            ModelModality.Text | ModelModality.Image,
            ModelModality.Text,
            1_050_000));
        var composite = new CompositeCapabilityResolver(
            [codex, oracle],
            NullLogger<CompositeCapabilityResolver>.Instance,
            _ => "openai");

        var detected = await composite.ResolveAsync(CodexModelId, TestContext.Current.CancellationToken);

        Assert.NotNull(detected);
        Assert.Equal(272_000, detected.ContextWindowTokens);
        Assert.Equal(ModelModality.Text | ModelModality.Image, detected.InputModalities);
        Assert.Equal(ModelModality.Text, detected.OutputModalities);
        Assert.Equal(ProviderName, probe.ProviderName);
        Assert.Same(provider, probe.ProviderEntry);

        var configured = ModelCapabilityResolution.ResolveModelCapabilities(
            new ModelSelection { Main = new ModelReference { ContextWindow = 128_000 } },
            detected);

        Assert.Equal(128_000, configured.ContextWindowTokens);
    }

    [Theory]
    [InlineData("openai", AuthMethod.ApiKey)]
    [InlineData("openai-compatible", AuthMethod.OAuthDevice)]
    public async Task ResolverSkipsProvidersOutsideOpenAiOAuth(
        string providerType,
        AuthMethod authMethod)
    {
        var probe = new FakeConfiguredProviderProbe(new ProviderProbeResult(true, null, []));
        var resolver = CreateResolver(probe, new ProviderEntry
        {
            Type = providerType,
            AuthMethod = authMethod,
        });

        var result = await resolver.ResolveAsync(CodexModelId, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.False(probe.WasCalled);
    }

    [Fact]
    public async Task UnsuccessfulProbeLeavesModelForOracleFallback()
    {
        var probe = new FakeConfiguredProviderProbe(new ProviderProbeResult(false, "offline", []));
        var resolver = CreateResolver(probe, new ProviderEntry
        {
            Type = "openai",
            AuthMethod = AuthMethod.OAuthPkce,
        });

        var result = await resolver.ResolveAsync(CodexModelId, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.True(probe.WasCalled);
    }

    [Fact]
    public async Task SuccessfulProbeWithoutRequestedModelReturnsNull()
    {
        var probe = new FakeConfiguredProviderProbe(new ProviderProbeResult(true, null,
        [
            new DiscoveredModel
            {
                ModelId = new ModelId("gpt-5.6"),
                ContextWindowTokens = 272_000,
            },
        ]));
        var resolver = CreateResolver(probe, new ProviderEntry
        {
            Type = "openai",
            AuthMethod = AuthMethod.OAuthDevice,
        });

        var result = await resolver.ResolveAsync(CodexModelId, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.True(probe.WasCalled);
    }

    private static OpenAiCodexCapabilityResolver CreateResolver(
        FakeConfiguredProviderProbe probe,
        ProviderEntry provider)
        => new(
            probe,
            new Dictionary<string, ProviderEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [ProviderName] = provider,
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [CodexModelId] = ProviderName,
            });

    private sealed class FakeConfiguredProviderProbe(ProviderProbeResult result)
        : IConfiguredProviderProbe
    {
        public bool WasCalled { get; private set; }
        public string? ProviderName { get; private set; }
        public ProviderEntry? ProviderEntry { get; private set; }

        public Task<ProviderProbeResult> ProbeConfiguredAsync(
            string providerName,
            ProviderEntry entry,
            CancellationToken ct = default)
        {
            WasCalled = true;
            ProviderName = providerName;
            ProviderEntry = entry;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeResolver(ResolvedModelCapabilities result) : IModelCapabilityResolver
    {
        public Task<ResolvedModelCapabilities?> ResolveAsync(
            string modelId,
            CancellationToken ct = default)
            => Task.FromResult<ResolvedModelCapabilities?>(result);
    }
}
