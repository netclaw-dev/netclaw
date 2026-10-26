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
        var contextOnly = new FakeCapabilityResolver(
            new ResolvedModelCapabilities("test-model", null, null, 256_000));
        var modalityOnly = new FakeCapabilityResolver(
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
        var first = new FakeCapabilityResolver(
            new ResolvedModelCapabilities("test-model", ModelModality.Text, null, 200_000));
        var second = new FakeCapabilityResolver(
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
        var first = new FakeCapabilityResolver(
            new ResolvedModelCapabilities("test-model", ModelModality.Text, null, 0));
        var second = new FakeCapabilityResolver(
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
        var first = new FakeCapabilityResolver(null);
        var second = new FakeCapabilityResolver(null);

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
        var second = new FakeCapabilityResolver(
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
        var ollama = new FakeCapabilityResolver(
            new ResolvedModelCapabilities("model", ModelModality.Text, ModelModality.Text, 32_768),
            providerType: "ollama");
        var openAiCompat = new FakeCapabilityResolver(
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
        var openAiCompat = new FakeCapabilityResolver(
            new ResolvedModelCapabilities("model", null, null, 256_000),
            providerType: "openai-compatible");
        var oracle = new FakeCapabilityResolver(
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
        var ollama = new FakeCapabilityResolver(
            new ResolvedModelCapabilities("model", null, null, 32_768),
            providerType: "ollama");
        var openAiCompat = new FakeCapabilityResolver(null, providerType: "openai-compatible");

        var composite = new CompositeCapabilityResolver(
            [ollama, openAiCompat],
            NullLogger<CompositeCapabilityResolver>.Instance);

        var result = await composite.ResolveAsync("model", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(ollama.WasCalled);
        Assert.True(openAiCompat.WasCalled);
    }

    // The early stop of an Ollama-only setup: when the native probe knows the three original fields
    // (input, output, window), the chain makes no catalog call for the newer fields.
    [Fact]
    public async Task ollama_early_stop_makes_no_catalog_call()
    {
        var ollama = new FakeCapabilityResolver(
            new ResolvedModelCapabilities("qwen3:30b", ModelModality.Text, ModelModality.Text, 131_072), "ollama");
        var catalog = new FakeCapabilityResolver(
            new ResolvedModelCapabilities("qwen3:30b", null, null, null) { MaxOutputTokens = 16_384 });
        var composite = new CompositeCapabilityResolver(
            [ollama, catalog], NullLogger<CompositeCapabilityResolver>.Instance, _ => "ollama");

        var result = await composite.ResolveAsync("qwen3:30b", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(131_072, result.ContextWindowTokens);
        Assert.False(catalog.WasCalled, "the catalog was called after the native probe knew the three fields");
    }

    private sealed class ThrowingResolver : IModelCapabilityResolver
    {
        public Task<ResolvedModelCapabilities?> ResolveAsync(
            string modelId, CancellationToken ct = default)
            => throw new HttpRequestException("Network error");
    }


    [Fact]
    public async Task native_thinking_wins_over_catalog()
    {
        var native = new FakeCapabilityResolver(
            new ResolvedModelCapabilities("m", null, null, null)
            {
                Thinking = ThinkingSupport.Controllable(ThinkingLevel.Low, ThinkingLevel.High),
                MaxOutputTokens = 64_000,
            });
        var catalog = new FakeCapabilityResolver(
            new ResolvedModelCapabilities("m", ModelModality.Text, ModelModality.Text, 200_000)
            {
                Thinking = ThinkingSupport.Controllable(ThinkingLevel.Off, ThinkingLevel.Medium),
                MaxOutputTokens = 128_000,
            });
        var composite = new CompositeCapabilityResolver(
            [native, catalog], NullLogger<CompositeCapabilityResolver>.Instance);

        var result = await composite.ResolveAsync("m", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(ThinkingSupport.Controllable(ThinkingLevel.Low, ThinkingLevel.High), result.Thinking);
        Assert.Equal(64_000, result.MaxOutputTokens);
    }

    [Fact]
    public async Task catalog_wins_over_unknown()
    {
        // The native probe knows only the window, so the chain goes on to the catalog.
        var native = new FakeCapabilityResolver(
            new ResolvedModelCapabilities("m", null, null, 131_072));
        var catalog = new FakeCapabilityResolver(
            new ResolvedModelCapabilities("m", null, null, null)
            {
                Thinking = ThinkingSupport.None,
                MaxOutputTokens = 16_384,
            });
        var composite = new CompositeCapabilityResolver(
            [native, catalog], NullLogger<CompositeCapabilityResolver>.Instance);

        var result = await composite.ResolveAsync("m", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(ThinkingSupport.None, result.Thinking);
        Assert.Equal(16_384, result.MaxOutputTokens);
        Assert.Equal(131_072, result.ContextWindowTokens);
    }
}

/// <summary>
/// Each definition resolves against the capability chain of its own provider (review item 3 of PR
/// #2694), so a Compaction or Fallback model on another endpoint never gets Main's window or levels.
/// The rows use the same model ID on two providers, so only the provider can pick the chain.
/// </summary>
public sealed class ProviderCapabilityResolverTests
{
    private static readonly ModelReference Main = new() { Provider = "main-vllm", ModelId = "qwen3" };
    private static readonly ModelReference Other = new() { Provider = "small-ollama", ModelId = "qwen3" };

    [Theory]
    [InlineData("compaction_on_other_provider_uses_its_own_chain", "small-ollama", 8_192)]
    [InlineData("fallback_on_other_provider_uses_its_own_chain", "small-ollama", 8_192)]
    [InlineData("main_uses_its_own_chain", "main-vllm", 131_072)]
    // The resolver returns unknown for a provider without a chain; the caller then uses no model max
    // and, for a self-hosted server, no window. Startup validation rejects an unknown provider first.
    [InlineData("unknown_provider_returns_unknown", "missing", null)]
    // Provider names are configuration keys, which the binder reads without case.
    [InlineData("provider_name_case_does_not_matter", "Small-Ollama", 8_192)]
    public async Task Resolves_against_the_definitions_own_provider(string row, string provider, int? expectedWindow)
    {
        var mainChain = new FakeCapabilityResolver(new ResolvedModelCapabilities("qwen3", null, null, 131_072));
        var otherChain = new FakeCapabilityResolver(new ResolvedModelCapabilities("qwen3", null, null, 8_192));
        // An ordinal dictionary: the resolver itself must not depend on the comparer of its caller.
        var chains = new Dictionary<string, IModelCapabilityResolver>
        {
            [Main.Provider] = mainChain,
            [Other.Provider] = otherChain,
        };
        var models = row.StartsWith("fallback", StringComparison.Ordinal)
            ? new ModelSelection { Main = Main, Fallback = Other }
            : new ModelSelection { Main = Main, Compaction = Other };
        var resolver = new ProviderCapabilityResolver(chains, models);

        var result = await resolver.ResolveAsync(
            new ModelReference { Provider = provider, ModelId = "qwen3" }, TestContext.Current.CancellationToken);

        Assert.True(expectedWindow == result?.ContextWindowTokens, $"{row}: window {result?.ContextWindowTokens}");
        Assert.True((expectedWindow == 131_072) == mainChain.WasCalled, $"{row}: Main's chain called = {mainChain.WasCalled}");
    }

    // The model-ID overload serves callers that know only an ID. An ID that no role names has no
    // provider, so the answer is unknown; Main's chain must not answer for another endpoint's model.
    [Fact]
    public async Task model_id_that_no_role_names_returns_unknown()
    {
        var mainChain = new FakeCapabilityResolver(new ResolvedModelCapabilities("qwen3", null, null, 131_072));
        var resolver = new ProviderCapabilityResolver(
            new Dictionary<string, IModelCapabilityResolver> { [Main.Provider] = mainChain },
            new ModelSelection { Main = Main });

        var result = await resolver.ResolveAsync("unnamed-model", TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.False(mainChain.WasCalled, "Main's chain answered for a model that no role names");
    }
}
