// -----------------------------------------------------------------------
// <copyright file="OpenAiCompatibleCapabilityResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

/// <summary>
/// High-level dispatch tests for <see cref="OpenAiCompatibleCapabilityResolver"/>.
/// Per-backend parsing is covered in <c>VllmBackendStrategyTests</c> and
/// <c>LlamaCppBackendStrategyTests</c>.
/// </summary>
public sealed class OpenAiCompatibleCapabilityResolverTests
{
    [Theory]
    [InlineData("{\"id\":\"Qwen3.6-27B-MTP-UD-Q4_K_XL.gguf\",\"meta\":{\"n_ctx\":131072,\"n_ctx_train\":262144}}", 131_072)]
    [InlineData("{\"id\":\"Qwen/Qwen3.6-VL-30B-FP8\",\"max_model_len\":256000}", 256_000)]
    [InlineData("{\"id\":\"deepseek-v4-flash\",\"owned_by\":\"ds4.c\",\"context_length\":262144}", 262_144)]
    [InlineData("{\"id\":\"deepseek-v4-pro\",\"owned_by\":\"ds4.c\",\"top_provider\":{\"context_length\":196608}}", 196_608)]
    public void ParseModels_ReadsSelfHostedContextMetadata(string modelJson, int expectedContextWindow)
    {
        var json = $$"""
        {
          "object": "list",
          "data": [ {{modelJson}} ]
        }
        """;

        var result = OpenAiCompatibleDescriptor.ParseModels(json);

        var model = Assert.Single(result.Models);
        Assert.Equal(expectedContextWindow, model.ContextWindowTokens);
    }

    [Theory]
    [InlineData("{\"id\":\"router-model\",\"meta\":{\"n_ctx\":0}}")]
    [InlineData("{\"id\":\"router-model\",\"max_model_len\":0}")]
    public void ParseModels_TreatsZeroContextMetadataAsUnknown(string modelJson)
    {
        var json = $$"""
        {
          "object": "list",
          "data": [ {{modelJson}} ]
        }
        """;

        var result = OpenAiCompatibleDescriptor.ParseModels(json);

        var model = Assert.Single(result.Models);
        Assert.Null(model.ContextWindowTokens);
    }

    [Fact]
    public void ParseModels_ZeroNCtxDoesNotFallBackToTrainContext()
    {
        const string json = """
        {
          "object": "list",
          "data": [
            {
              "id": "router-model",
              "meta": { "n_ctx": 0, "n_ctx_train": 262144 }
            }
          ]
        }
        """;

        var result = OpenAiCompatibleDescriptor.ParseModels(json);

        var model = Assert.Single(result.Models);
        Assert.Null(model.ContextWindowTokens);
    }

    [Fact]
    public void ResolveFromProbe_VllmShape_DispatchesToVllmStrategy()
    {
        const string modelsJson = """
        {
          "object": "list",
          "data": [
            {
              "id": "Qwen/Qwen3.6-VL-30B-FP8",
              "object": "model",
              "owned_by": "vllm",
              "max_model_len": 256000
            }
          ]
        }
        """;

        var result = OpenAiCompatibleCapabilityResolver.ResolveFromProbe(
            "Qwen/Qwen3.6-VL-30B-FP8", modelsJson, propsJson: null);

        Assert.NotNull(result);
        Assert.Equal(256_000, result.ContextWindowTokens);
        // vLLM exposes no modality info; HF resolver fills these downstream.
        Assert.Null(result.InputModalities);
        Assert.Null(result.OutputModalities);
    }

    [Fact]
    public void ResolveFromProbe_LlamaCppShape_DispatchesToLlamaCppStrategy()
    {
        const string modelsJson = """
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
        const string propsJson = """
        {
          "default_generation_settings": { "n_ctx": 131072 },
          "modalities": { "vision": true }
        }
        """;

        var result = OpenAiCompatibleCapabilityResolver.ResolveFromProbe(
            "Qwen3.6-27B-MTP-UD-Q4_K_XL.gguf", modelsJson, propsJson);

        Assert.NotNull(result);
        // /props.n_ctx wins as the runtime-effective context window.
        Assert.Equal(131_072, result.ContextWindowTokens);
        Assert.Equal(ModelModality.Text | ModelModality.Image, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    [Fact]
    public void ResolveFromProbe_LlamaCppRouterPropsZero_ReturnsUnknownContext()
    {
        const string modelsJson = """
        {
          "object": "list",
          "data": [
            {
              "id": "router-model",
              "object": "model",
              "owned_by": "llamacpp"
            }
          ]
        }
        """;
        const string propsJson = """
        {
          "role": "router",
          "default_generation_settings": {
            "params": {},
            "n_ctx": 0
          },
          "modalities": {
            "vision": false
          }
        }
        """;

        var result = OpenAiCompatibleCapabilityResolver.ResolveFromProbe(
            "router-model", modelsJson, propsJson);

        Assert.NotNull(result);
        Assert.Null(result.ContextWindowTokens);
        Assert.Null(result.InputModalities);
        Assert.Null(result.OutputModalities);
    }

    [Fact]
    public void ResolveFromProbe_Ds4Shape_DispatchesToDs4Strategy_EvenWithProps()
    {
        const string modelsJson = """
        {
          "object": "list",
          "data": [
            {
              "id": "deepseek-v4-flash",
              "object": "model",
              "owned_by": "ds4.c",
              "context_length": 262144,
              "top_provider": { "context_length": 262144 }
            }
          ]
        }
        """;
        // A proxy in front of ds4 may answer /props; the ds4 strategy is
        // ordered before llama.cpp so the owned_by signal still wins.
        const string propsJson = """
        { "default_generation_settings": { "n_ctx": 4096 } }
        """;

        var result = OpenAiCompatibleCapabilityResolver.ResolveFromProbe(
            "deepseek-v4-flash", modelsJson, propsJson);

        Assert.NotNull(result);
        Assert.Equal(262_144, result.ContextWindowTokens);
        Assert.Equal(ModelModality.Text, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    [Fact]
    public void ResolveFromProbe_Ds4Shape_DispatchesToDs4Strategy_EvenWithMaxModelLen()
    {
        const string modelsJson = """
        {
          "object": "list",
          "data": [
            {
              "id": "deepseek-v4-flash",
              "object": "model",
              "owned_by": "ds4.c",
              "max_model_len": 4096,
              "context_length": 262144
            }
          ]
        }
        """;

        var result = OpenAiCompatibleCapabilityResolver.ResolveFromProbe(
            "deepseek-v4-flash", modelsJson, propsJson: null);

        Assert.NotNull(result);
        Assert.Equal(262_144, result.ContextWindowTokens);
        Assert.Equal(ModelModality.Text, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    [Fact]
    public void ResolveFromProbe_UnknownShape_FallsThroughToGenericStrategy()
    {
        const string modelsJson = """
        { "object": "list", "data": [ { "id": "mystery-model" } ] }
        """;

        var result = OpenAiCompatibleCapabilityResolver.ResolveFromProbe(
            "mystery-model", modelsJson, propsJson: null);

        Assert.NotNull(result);
        Assert.Null(result.InputModalities);
        Assert.Null(result.OutputModalities);
        Assert.Null(result.ContextWindowTokens);
    }


    [Theory]
    [InlineData("llama_cpp_supports_reasoning_effort_maps_to_levels")]
    [InlineData("llama_cpp_enable_thinking_marker_maps_to_off_only")]
    [InlineData("vllm_maps_to_unknown")]
    public void ResolveFromProbe_ReadsThinkingCapability(string row)
    {
        const string llamaModels = """
        { "object": "list", "data": [ { "id": "qwen3.gguf", "meta": { "n_ctx": 131072 } } ] }
        """;
        const string vllmModels = """
        { "object": "list", "data": [ { "id": "qwen3", "object": "model", "owned_by": "vllm", "max_model_len": 131072 } ] }
        """;
        const string capsProps = """
        {
          "default_generation_settings": { "n_ctx": 131072 },
          "chat_template": "{% if enable_thinking %}<think>{% endif %}",
          "chat_template_caps": { "supports_tools": true, "supports_reasoning_effort": true }
        }
        """;
        const string markerProps = """
        {
          "default_generation_settings": { "n_ctx": 131072 },
          "chat_template": "{% if enable_thinking is defined and not enable_thinking %}<think></think>{% endif %}",
          "chat_template_caps": { "supports_tools": true, "supports_reasoning_effort": false }
        }
        """;

        var (modelId, models, props, expected) = row switch
        {
            "llama_cpp_supports_reasoning_effort_maps_to_levels" => ("qwen3.gguf", llamaModels, capsProps,
                ThinkingSupport.Controllable(ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High)),
            "llama_cpp_enable_thinking_marker_maps_to_off_only" => ("qwen3.gguf", llamaModels, markerProps,
                ThinkingSupport.Controllable(ThinkingLevel.Off)),
            _ => ("qwen3", vllmModels, (string?)null, ThinkingSupport.Unknown),
        };

        var result = OpenAiCompatibleCapabilityResolver.ResolveFromProbe(modelId, models, props);

        Assert.NotNull(result);
        Assert.Equal(expected, result.Thinking);
    }
}
