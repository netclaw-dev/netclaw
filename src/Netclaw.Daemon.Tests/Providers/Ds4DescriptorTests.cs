// -----------------------------------------------------------------------
// <copyright file="Ds4DescriptorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Providers.Ds4;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

/// <summary>
/// Probe-parsing tests for <see cref="Ds4Descriptor"/>. ds4 emits
/// OpenRouter-shaped model metadata, so context-window discovery must read
/// <c>context_length</c> / <c>top_provider.context_length</c> rather than the
/// vLLM/llama.cpp fields the generic self-hosted descriptor expects.
/// </summary>
public sealed class Ds4DescriptorTests
{
    [Fact]
    public void ParseModels_ReadsTopLevelContextLength()
    {
        const string json = """
        {
          "object": "list",
          "data": [
            { "id": "deepseek-v4-flash", "context_length": 262144,
              "top_provider": { "context_length": 131072 } }
          ]
        }
        """;

        var result = Ds4Descriptor.ParseModels(json);

        var model = Assert.Single(result.Models);
        Assert.Equal("deepseek-v4-flash", model.ModelId.Value);
        // Top-level context_length wins over the nested top_provider value.
        Assert.Equal(262_144, model.ContextWindowTokens);
    }

    [Fact]
    public void ParseModels_FallsBackToTopProviderContextLength()
    {
        const string json = """
        {
          "object": "list",
          "data": [
            { "id": "deepseek-v4-pro", "top_provider": { "context_length": 196608 } }
          ]
        }
        """;

        var result = Ds4Descriptor.ParseModels(json);

        var model = Assert.Single(result.Models);
        Assert.Equal(196_608, model.ContextWindowTokens);
    }

    [Fact]
    public void ParseModels_TreatsMissingContextAsUnknown()
    {
        const string json = """
        {
          "object": "list",
          "data": [ { "id": "deepseek-v4-flash" } ]
        }
        """;

        var result = Ds4Descriptor.ParseModels(json);

        var model = Assert.Single(result.Models);
        Assert.Null(model.ContextWindowTokens);
    }
}
