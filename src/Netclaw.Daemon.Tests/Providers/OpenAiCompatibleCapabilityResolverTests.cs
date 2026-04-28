// -----------------------------------------------------------------------
// <copyright file="OpenAiCompatibleCapabilityResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class OpenAiCompatibleCapabilityResolverTests
{
    [Fact]
    public void ParseModelsResponse_ExtractsContextWindow()
    {
        const string json = """
        {
          "object": "list",
          "data": [
            {
              "id": "Qwen3.5-35B-A3B-UD-Q4_K_XL.gguf",
              "meta": {
                "n_ctx_train": 262144
              }
            }
          ]
        }
        """;

        var result = OpenAiCompatibleCapabilityResolver.ParseModelsResponse(json, "Qwen3.5-35B-A3B-UD-Q4_K_XL.gguf");

        Assert.NotNull(result);
        Assert.Equal(262_144, result.ContextWindowTokens);
        Assert.Equal(ModelModality.Text, result.InputModalities);
    }

    [Fact]
    public void ParsePropsResponse_VisionEnabled_AddsImageInput()
    {
        const string json = """
        {
          "default_generation_settings": {
            "params": {
              "n_ctx": 65536
            }
          },
          "modalities": {
            "vision": true
          }
        }
        """;

        var result = OpenAiCompatibleCapabilityResolver.ParsePropsResponse(json, "Qwen3.5", 32768);

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Text | ModelModality.Image, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
        Assert.Equal(65536, result.ContextWindowTokens);
    }

    [Fact]
    public void ParsePropsResponse_VisionDisabled_StaysTextOnly()
    {
        const string json = """
        {
          "modalities": {
            "vision": false
          }
        }
        """;

        var result = OpenAiCompatibleCapabilityResolver.ParsePropsResponse(json, "Qwen3.5", 32768);

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Text, result.InputModalities);
        Assert.Equal(32768, result.ContextWindowTokens);
    }

    [Fact]
    public void ParsePropsResponse_UsesModelContextWindowWhenRuntimeValueMissing()
    {
        const string json = """
        {
          "modalities": {
            "vision": true
          }
        }
        """;

        var result = OpenAiCompatibleCapabilityResolver.ParsePropsResponse(json, "Qwen3.5", 262144);

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Text | ModelModality.Image, result.InputModalities);
        Assert.Equal(262144, result.ContextWindowTokens);
    }
}
