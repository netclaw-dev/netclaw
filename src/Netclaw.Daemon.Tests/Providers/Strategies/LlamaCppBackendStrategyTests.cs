// -----------------------------------------------------------------------
// <copyright file="LlamaCppBackendStrategyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration;
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers.Strategies;

public sealed class LlamaCppBackendStrategyTests
{
    private const string ModelsJsonWithMetaCtx = """
    {
      "object": "list",
      "data": [
        {
          "id": "Qwen3.6-27B-MTP-UD-Q4_K_XL.gguf",
          "meta": { "n_ctx": 131072, "n_ctx_train": 262144 }
        }
      ]
    }
    """;

    [Fact]
    public void Matches_PropsPresent()
    {
        using var models = JsonDocument.Parse("""{"object":"list","data":[]}""");
        using var props = JsonDocument.Parse("{}");
        var probe = new BackendProbe("any-model", models.RootElement, props.RootElement);
        Assert.True(new LlamaCppBackendStrategy().Matches(probe));
    }

    [Fact]
    public void Matches_MetaContext_PresentEvenWithoutProps()
    {
        using var models = JsonDocument.Parse(ModelsJsonWithMetaCtx);
        var probe = new BackendProbe("Qwen3.6-27B-MTP-UD-Q4_K_XL.gguf", models.RootElement, PropsRoot: null);
        Assert.True(new LlamaCppBackendStrategy().Matches(probe));
    }

    [Theory]
    [InlineData("{\"default_generation_settings\":{\"n_ctx\":65536},\"modalities\":{\"vision\":true}}")]
    [InlineData("{\"default_generation_settings\":{\"params\":{\"n_ctx\":65536}},\"modalities\":{\"vision\":true}}")]
    public void Parse_PrefersPropsNCtxOverMeta(string propsJson)
    {
        using var models = JsonDocument.Parse(ModelsJsonWithMetaCtx);
        using var props = JsonDocument.Parse(propsJson);
        var probe = new BackendProbe("Qwen3.6-27B-MTP-UD-Q4_K_XL.gguf", models.RootElement, props.RootElement);

        var result = new LlamaCppBackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Equal(65_536, result.ContextWindowTokens); // /props overrides
        Assert.Equal(ModelModality.Text | ModelModality.Image, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    [Theory]
    [InlineData("{\"default_generation_settings\":{\"n_ctx\":0},\"modalities\":{\"vision\":true}}")]
    [InlineData("{\"default_generation_settings\":{\"params\":{\"n_ctx\":0}},\"modalities\":{\"vision\":true}}")]
    public void Parse_IgnoresZeroPropsNCtx_UsesMetaContext(string propsJson)
    {
        using var models = JsonDocument.Parse(ModelsJsonWithMetaCtx);
        using var props = JsonDocument.Parse(propsJson);
        var probe = new BackendProbe("Qwen3.6-27B-MTP-UD-Q4_K_XL.gguf", models.RootElement, props.RootElement);

        var result = new LlamaCppBackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Equal(131_072, result.ContextWindowTokens);
        Assert.Equal(ModelModality.Text | ModelModality.Image, result.InputModalities);
    }

    [Fact]
    public void Parse_UsesMetaNCtx_WhenPropsAbsent()
    {
        using var models = JsonDocument.Parse(ModelsJsonWithMetaCtx);
        var probe = new BackendProbe("Qwen3.6-27B-MTP-UD-Q4_K_XL.gguf", models.RootElement, PropsRoot: null);

        var result = new LlamaCppBackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Equal(131_072, result.ContextWindowTokens);
        Assert.Null(result.InputModalities);
        Assert.Null(result.OutputModalities);
    }

    [Fact]
    public void Parse_ZeroMetaNCtx_ReturnsUnknownContextEvenWhenMetaTrainPresent()
    {
        const string modelsJson = """
        {
          "object": "list",
          "data": [
            {
              "id": "Qwen3.6-27B-MTP-UD-Q4_K_XL.gguf",
              "meta": { "n_ctx": 0, "n_ctx_train": 262144 }
            }
          ]
        }
        """;
        using var models = JsonDocument.Parse(modelsJson);
        var probe = new BackendProbe("Qwen3.6-27B-MTP-UD-Q4_K_XL.gguf", models.RootElement, PropsRoot: null);

        var result = new LlamaCppBackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Null(result.ContextWindowTokens);
        Assert.Null(result.InputModalities);
        Assert.Null(result.OutputModalities);
    }

    [Fact]
    public void Parse_UsesMetaTrainContext_WhenMetaNCtxAbsent()
    {
        const string modelsJson = """
        {
          "object": "list",
          "data": [
            {
              "id": "Qwen3.6-27B-MTP-UD-Q4_K_XL.gguf",
              "meta": { "n_ctx_train": 262144 }
            }
          ]
        }
        """;
        using var models = JsonDocument.Parse(modelsJson);
        var probe = new BackendProbe("Qwen3.6-27B-MTP-UD-Q4_K_XL.gguf", models.RootElement, PropsRoot: null);

        var result = new LlamaCppBackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Equal(262_144, result.ContextWindowTokens);
    }

    [Fact]
    public void Parse_AllContextMetadataZero_ReturnsNullContext()
    {
        const string modelsJson = """
        {
          "object": "list",
          "data": [
            {
              "id": "Qwen3.6-27B-MTP-UD-Q4_K_XL.gguf",
              "meta": { "n_ctx": 0, "n_ctx_train": 0 }
            }
          ]
        }
        """;
        using var models = JsonDocument.Parse(modelsJson);
        using var props = JsonDocument.Parse("""{"default_generation_settings":{"n_ctx":0}}""");
        var probe = new BackendProbe("Qwen3.6-27B-MTP-UD-Q4_K_XL.gguf", models.RootElement, props.RootElement);

        var result = new LlamaCppBackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Null(result.ContextWindowTokens);
        Assert.Null(result.InputModalities);
        Assert.Null(result.OutputModalities);
    }

    [Fact]
    public void Parse_RouterPropsWithVisionFalse_DoesNotSetTextOnlyModalities()
    {
        using var models = JsonDocument.Parse(ModelsJsonWithMetaCtx);
        const string propsJson = """
        {
          "role": "router",
          "default_generation_settings": { "n_ctx": 0 },
          "modalities": { "vision": false }
        }
        """;
        using var props = JsonDocument.Parse(propsJson);
        var probe = new BackendProbe("Qwen3.6-27B-MTP-UD-Q4_K_XL.gguf", models.RootElement, props.RootElement);

        var result = new LlamaCppBackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Equal(131_072, result.ContextWindowTokens);
        Assert.Null(result.InputModalities);
        Assert.Null(result.OutputModalities);
    }

    [Fact]
    public void Parse_VisionDisabled_StaysTextOnly()
    {
        using var models = JsonDocument.Parse(ModelsJsonWithMetaCtx);
        using var props = JsonDocument.Parse("""{"modalities":{"vision":false}}""");
        var probe = new BackendProbe("Qwen3.5", models.RootElement, props.RootElement);

        var result = new LlamaCppBackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Text, result.InputModalities);
    }
}
