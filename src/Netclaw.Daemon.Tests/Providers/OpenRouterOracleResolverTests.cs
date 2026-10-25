// -----------------------------------------------------------------------
// <copyright file="OpenRouterOracleResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Providers.OpenRouter;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class OpenRouterOracleResolverTests
{
    [Fact]
    public void ParseCatalog_MultimodalModel()
    {
        const string json = """
        {
          "data": [
            {
              "id": "anthropic/claude-sonnet-4",
              "architecture": {
                "input_modalities": ["text", "image"],
                "output_modalities": ["text"]
              }
            }
          ]
        }
        """;

        var catalog = OpenRouterOracleResolver.ParseCatalog(json);

        Assert.True(catalog.ContainsKey("anthropic/claude-sonnet-4"));
        var caps = catalog["anthropic/claude-sonnet-4"];
        Assert.Equal(ModelModality.Text | ModelModality.Image, caps.InputModalities);
        Assert.Equal(ModelModality.Text, caps.OutputModalities);
    }

    [Fact]
    public void ParseCatalog_TextOnlyModel()
    {
        const string json = """
        {
          "data": [
            {
              "id": "mistralai/mistral-7b",
              "architecture": {
                "input_modalities": ["text"],
                "output_modalities": ["text"]
              }
            }
          ]
        }
        """;

        var catalog = OpenRouterOracleResolver.ParseCatalog(json);

        var caps = catalog["mistralai/mistral-7b"];
        Assert.Equal(ModelModality.Text, caps.InputModalities);
        Assert.Equal(ModelModality.Text, caps.OutputModalities);
    }

    [Fact]
    public void ParseCatalog_FullMultimodal()
    {
        const string json = """
        {
          "data": [
            {
              "id": "openai/gpt-4o",
              "architecture": {
                "input_modalities": ["text", "image", "audio"],
                "output_modalities": ["text", "audio"]
              }
            }
          ]
        }
        """;

        var catalog = OpenRouterOracleResolver.ParseCatalog(json);

        var caps = catalog["openai/gpt-4o"];
        Assert.Equal(ModelModality.Text | ModelModality.Image | ModelModality.Audio, caps.InputModalities);
        Assert.Equal(ModelModality.Text | ModelModality.Audio, caps.OutputModalities);
    }

    [Fact]
    public void ParseCatalog_NoArchitecture_DefaultsToText()
    {
        const string json = """
        {
          "data": [
            {
              "id": "some/model"
            }
          ]
        }
        """;

        var catalog = OpenRouterOracleResolver.ParseCatalog(json);

        var caps = catalog["some/model"];
        Assert.Equal(ModelModality.Text, caps.InputModalities);
        Assert.Equal(ModelModality.Text, caps.OutputModalities);
    }

    [Fact]
    public void ParseCatalog_ContextLength_Parsed()
    {
        const string json = """
        {
          "data": [
            {
              "id": "qwen/qwen3.5-35b-a3b",
              "context_length": 262144,
              "architecture": {
                "input_modalities": ["text"],
                "output_modalities": ["text"]
              }
            }
          ]
        }
        """;

        var catalog = OpenRouterOracleResolver.ParseCatalog(json);

        var caps = catalog["qwen/qwen3.5-35b-a3b"];
        Assert.Equal(262_144, caps.ContextWindowTokens);
    }

    [Fact]
    public void ParseCatalog_NoContextLength_ReturnsNull()
    {
        const string json = """
        {
          "data": [
            {
              "id": "some/model",
              "architecture": {
                "input_modalities": ["text"],
                "output_modalities": ["text"]
              }
            }
          ]
        }
        """;

        var catalog = OpenRouterOracleResolver.ParseCatalog(json);

        Assert.Null(catalog["some/model"].ContextWindowTokens);
    }

    [Fact]
    public void ParseCatalog_ZeroContextLength_ReturnsNull()
    {
        const string json = """
        {
          "data": [
            {
              "id": "some/model",
              "context_length": 0,
              "architecture": {
                "input_modalities": ["text"],
                "output_modalities": ["text"]
              }
            }
          ]
        }
        """;

        var catalog = OpenRouterOracleResolver.ParseCatalog(json);

        Assert.Null(catalog["some/model"].ContextWindowTokens);
    }

    [Fact]
    public void ParseCatalog_EmptyData()
    {
        const string json = """{"data": []}""";

        var catalog = OpenRouterOracleResolver.ParseCatalog(json);

        Assert.Empty(catalog);
    }

    [Fact]
    public void ParseModalityArray_UnknownValues_Ignored()
    {
        const string json = """["text", "image", "hologram"]""";
        var element = System.Text.Json.JsonDocument.Parse(json).RootElement;

        var result = OpenRouterOracleResolver.ParseModalityArray(element);

        Assert.Equal(ModelModality.Text | ModelModality.Image, result);
    }


    // Pinned from the live catalog (2026-10-25): openai/gpt-5, qwen/qwen3-32b, deepseek/deepseek-chat.
    private const string ThinkingCatalog = """
    {
      "data": [
        {
          "id": "openai/gpt-5",
          "supported_parameters": ["max_completion_tokens", "max_tokens", "reasoning", "reasoning_effort", "tools"],
          "reasoning": { "mandatory": true, "supported_efforts": ["high", "medium", "low", "minimal"], "default_effort": "medium" },
          "top_provider": { "context_length": 400000, "max_completion_tokens": 128000, "is_moderated": true }
        },
        {
          "id": "qwen/qwen3-32b",
          "supported_parameters": ["max_tokens", "reasoning", "tools"],
          "reasoning": { "mandatory": false },
          "top_provider": { "context_length": 40960, "max_completion_tokens": 16384, "is_moderated": false }
        },
        {
          "id": "deepseek/deepseek-chat",
          "supported_parameters": ["max_tokens", "tools"],
          "reasoning": null,
          "top_provider": { "context_length": 128000, "max_completion_tokens": 16000, "is_moderated": false }
        }
      ]
    }
    """;

    [Theory]
    [InlineData("mandatory_with_efforts_maps_to_controllable_without_off")]
    [InlineData("no_reasoning_parameter_maps_to_none")]
    [InlineData("reasoning_without_efforts_maps_to_controllable_with_off")]
    [InlineData("top_provider_max_completion_tokens_maps_to_max_output")]
    public void ParseCatalog_ReadsThinkingCapability(string row)
    {
        var catalog = OpenRouterOracleResolver.ParseCatalog(ThinkingCatalog);

        switch (row)
        {
            case "mandatory_with_efforts_maps_to_controllable_without_off":
                // "minimal" has no Netclaw level; a mandatory model cannot turn thinking off.
                Assert.Equal(
                    ThinkingSupport.Controllable(ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High),
                    catalog["openai/gpt-5"].Thinking);
                break;
            case "no_reasoning_parameter_maps_to_none":
                Assert.Equal(ThinkingSupport.None, catalog["deepseek/deepseek-chat"].Thinking);
                break;
            case "reasoning_without_efforts_maps_to_controllable_with_off":
                Assert.Equal(
                    ThinkingSupport.Controllable(ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.Medium, ThinkingLevel.High, ThinkingLevel.Max),
                    catalog["qwen/qwen3-32b"].Thinking);
                break;
            case "top_provider_max_completion_tokens_maps_to_max_output":
                Assert.Equal(128_000, catalog["openai/gpt-5"].MaxOutputTokens);
                Assert.Equal(16_384, catalog["qwen/qwen3-32b"].MaxOutputTokens);
                break;
            default:
                Assert.Fail($"unknown row {row}");
                break;
        }
    }
}
