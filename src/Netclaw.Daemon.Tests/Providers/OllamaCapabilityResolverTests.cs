// -----------------------------------------------------------------------
// <copyright file="OllamaCapabilityResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class OllamaCapabilityResolverTests
{
    [Fact]
    public void ParseShowResponse_Qwen35_ContextWindow()
    {
        const string json = """
        {
          "model_info": {
            "general.architecture": "qwen35",
            "qwen35.context_length": 262144,
            "qwen35.embedding_length": 3072
          }
        }
        """;

        var result = OllamaCapabilityResolver.ParseShowResponse(json, "qwen3.5:9b");

        Assert.NotNull(result);
        Assert.Equal(262_144, result.ContextWindowTokens);
        Assert.Equal("qwen3.5:9b", result.ModelId);
    }

    [Fact]
    public void ParseShowResponse_VisionModel()
    {
        const string json = """
        {
          "model_info": {
            "general.architecture": "llava",
            "llava.context_length": 4096,
            "llava.vision.block_count": 23
          }
        }
        """;

        var result = OllamaCapabilityResolver.ParseShowResponse(json, "llava:13b");

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Text | ModelModality.Image, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
        Assert.Equal(4096, result.ContextWindowTokens);
    }

    [Fact]
    public void ParseShowResponse_TextOnly()
    {
        const string json = """
        {
          "model_info": {
            "general.architecture": "qwen35",
            "qwen35.context_length": 131072
          }
        }
        """;

        var result = OllamaCapabilityResolver.ParseShowResponse(json, "qwen3.5:30b");

        Assert.NotNull(result);
        Assert.Equal(ModelModality.Text, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    [Fact]
    public void ParseShowResponse_MissingModelInfo_ReturnsNull()
    {
        const string json = """
        {
          "license": "apache-2.0",
          "modelfile": "FROM qwen3.5:9b"
        }
        """;

        var result = OllamaCapabilityResolver.ParseShowResponse(json, "qwen3.5:9b");

        Assert.Null(result);
    }

    [Fact]
    public void ParseShowResponse_MissingContextLength()
    {
        const string json = """
        {
          "model_info": {
            "general.architecture": "qwen35",
            "qwen35.embedding_length": 3072
          }
        }
        """;

        var result = OllamaCapabilityResolver.ParseShowResponse(json, "qwen3.5:9b");

        Assert.NotNull(result);
        Assert.Null(result.ContextWindowTokens);
        Assert.Equal(ModelModality.Text, result.InputModalities);
    }

    [Fact]
    public void ParseShowResponse_MissingArchitecture()
    {
        const string json = """
        {
          "model_info": {
            "some.other.key": 42
          }
        }
        """;

        var result = OllamaCapabilityResolver.ParseShowResponse(json, "mystery-model");

        Assert.NotNull(result);
        Assert.Null(result.ContextWindowTokens);
        Assert.Equal(ModelModality.Text, result.InputModalities);
    }
}
