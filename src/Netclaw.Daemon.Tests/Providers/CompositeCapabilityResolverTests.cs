// -----------------------------------------------------------------------
// <copyright file="CompositeCapabilityResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Providers;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class CompositeCapabilityResolverTests
{
    [Fact]
    public async Task PartialResults_MergeAcrossResolvers()
    {
        // Models the vLLM + HuggingFace handoff: first resolver supplies
        // context window only, second resolver supplies modalities only.
        var contextOnly = new FakeResolver(
            new ResolvedModelCapabilities("test-model", null, null, 256_000));
        var modalityOnly = new FakeResolver(
            new ResolvedModelCapabilities("test-model", ModelModality.Text | ModelModality.Image, ModelModality.Text, null));

        var composite = new CompositeCapabilityResolver(
            [contextOnly, modalityOnly],
            NullLogger<CompositeCapabilityResolver>.Instance);

        var result = await composite.ResolveAsync("test-model", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Text | ModelModality.Image, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
        Assert.Equal(256_000, result.ContextWindowTokens);
    }

    [Fact]
    public async Task FirstNonNullWinsPerField()
    {
        var first = new FakeResolver(
            new ResolvedModelCapabilities("test-model", ModelModality.Text, null, 200_000));
        var second = new FakeResolver(
            new ResolvedModelCapabilities("test-model", ModelModality.Text | ModelModality.Image, ModelModality.Text, 256_000));

        var composite = new CompositeCapabilityResolver(
            [first, second],
            NullLogger<CompositeCapabilityResolver>.Instance);

        var result = await composite.ResolveAsync("test-model", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Text, result.InputModalities); // first wins
        Assert.Equal(ModelModality.Text, result.OutputModalities); // second supplies (first was null)
        Assert.Equal(200_000, result.ContextWindowTokens); // first wins
    }

    [Fact]
    public async Task NonPositiveContext_DoesNotBlockLaterResolver()
    {
        var first = new FakeResolver(
            new ResolvedModelCapabilities("test-model", ModelModality.Text, null, 0));
        var second = new FakeResolver(
            new ResolvedModelCapabilities("test-model", null, ModelModality.Text, 256_000));

        var composite = new CompositeCapabilityResolver(
            [first, second],
            NullLogger<CompositeCapabilityResolver>.Instance);

        var result = await composite.ResolveAsync("test-model", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Text, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
        Assert.Equal(256_000, result.ContextWindowTokens);
    }

    [Fact]
    public async Task AllResolvers_ReturnNull_CompositeReturnsNull()
    {
        // Defaulting now lives at the consumption boundary
        // (ModelCapabilityResolution), not in the composite.
        var first = new FakeResolver(null);
        var second = new FakeResolver(null);

        var composite = new CompositeCapabilityResolver(
            [first, second],
            NullLogger<CompositeCapabilityResolver>.Instance);

        var result = await composite.ResolveAsync("unknown-model", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task FirstResolver_Throws_ContinuesChain()
    {
        var first = new ThrowingResolver();
        var second = new FakeResolver(
            new ResolvedModelCapabilities("test-model", ModelModality.Text, ModelModality.Text, 65_536));

        var composite = new CompositeCapabilityResolver(
            [first, second],
            NullLogger<CompositeCapabilityResolver>.Instance);

        var result = await composite.ResolveAsync("test-model", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Text, result.InputModalities);
        Assert.Equal(65_536, result.ContextWindowTokens);
    }

    [Fact]
    public async Task ProviderMismatch_ResolverSkipped()
    {
        // Active provider for the model is openai-compatible; the ollama
        // resolver must not be invoked.
        var ollama = new FakeResolver(
            new ResolvedModelCapabilities("model", ModelModality.Text, ModelModality.Text, 32_768),
            providerType: "ollama");
        var openAiCompat = new FakeResolver(
            new ResolvedModelCapabilities("model", null, null, 256_000),
            providerType: "openai-compatible");

        var composite = new CompositeCapabilityResolver(
            [ollama, openAiCompat],
            NullLogger<CompositeCapabilityResolver>.Instance,
            _ => "openai-compatible");

        var result = await composite.ResolveAsync("model", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(ollama.WasCalled);
        Assert.True(openAiCompat.WasCalled);
        Assert.Equal(256_000, result.ContextWindowTokens);
    }

    [Fact]
    public async Task OracleResolver_RunsRegardlessOfProvider()
    {
        var openAiCompat = new FakeResolver(
            new ResolvedModelCapabilities("model", null, null, 256_000),
            providerType: "openai-compatible");
        var oracle = new FakeResolver(
            new ResolvedModelCapabilities("model", ModelModality.Text | ModelModality.Image, ModelModality.Text, null),
            providerType: null); // oracle

        var composite = new CompositeCapabilityResolver(
            [openAiCompat, oracle],
            NullLogger<CompositeCapabilityResolver>.Instance,
            _ => "openai-compatible");

        var result = await composite.ResolveAsync("model", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(oracle.WasCalled);
        Assert.Equal(ModelModality.Text | ModelModality.Image, result.InputModalities);
        Assert.Equal(256_000, result.ContextWindowTokens);
    }

    [Fact]
    public async Task NoActiveProviderLookup_AllResolversRun()
    {
        // Backward compatibility: when no lookup is provided, the
        // composite behaves as if every resolver is eligible.
        var ollama = new FakeResolver(
            new ResolvedModelCapabilities("model", null, null, 32_768),
            providerType: "ollama");
        var openAiCompat = new FakeResolver(null, providerType: "openai-compatible");

        var composite = new CompositeCapabilityResolver(
            [ollama, openAiCompat],
            NullLogger<CompositeCapabilityResolver>.Instance);

        var result = await composite.ResolveAsync("model", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(ollama.WasCalled);
        Assert.True(openAiCompat.WasCalled);
    }

    private sealed class FakeResolver : IModelCapabilityResolver
    {
        private readonly ResolvedModelCapabilities? _result;
        public bool WasCalled { get; private set; }
        public string? ProviderType { get; }

        public FakeResolver(ResolvedModelCapabilities? result, string? providerType = null)
        {
            _result = result;
            ProviderType = providerType;
        }

        public Task<ResolvedModelCapabilities?> ResolveAsync(
            string modelId, CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingResolver : IModelCapabilityResolver
    {
        public Task<ResolvedModelCapabilities?> ResolveAsync(
            string modelId, CancellationToken ct = default)
            => throw new HttpRequestException("Network error");
    }
}
