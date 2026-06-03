// -----------------------------------------------------------------------
// <copyright file="VllmBackendStrategyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers.Strategies;

public sealed class VllmBackendStrategyTests
{
    // Real vLLM 0.20 response shape captured from a live deployment
    // (see Aaron's 2026-05-13 field-repro comment on issue #619).
    private const string VllmModelsJson = """
    {
      "object": "list",
      "data": [
        {
          "id": "Qwen/Qwen3.6-VL-30B-FP8",
          "object": "model",
          "created": 1778684111,
          "owned_by": "vllm",
          "root": "/models/xs",
          "parent": null,
          "max_model_len": 256000
        }
      ]
    }
    """;

    [Fact]
    public void Matches_OwnedByVllm()
    {
        using var doc = JsonDocument.Parse(VllmModelsJson);
        var probe = new BackendProbe("Qwen/Qwen3.6-VL-30B-FP8", doc.RootElement, PropsRoot: null);
        Assert.True(new VllmBackendStrategy().Matches(probe));
    }

    [Fact]
    public void Matches_MaxModelLen_With404Props()
    {
        // Strip owned_by to verify the secondary signal works on its own.
        const string strippedJson = """
        { "object": "list", "data": [ { "id": "model-x", "max_model_len": 131072 } ] }
        """;
        using var doc = JsonDocument.Parse(strippedJson);
        var probe = new BackendProbe("model-x", doc.RootElement, PropsRoot: null);
        Assert.True(new VllmBackendStrategy().Matches(probe));
    }

    [Fact]
    public void Matches_False_WhenNeitherSignalPresent()
    {
        const string json = """
        { "object": "list", "data": [ { "id": "model-x" } ] }
        """;
        using var doc = JsonDocument.Parse(json);
        var probe = new BackendProbe("model-x", doc.RootElement, PropsRoot: null);
        Assert.False(new VllmBackendStrategy().Matches(probe));
    }

    [Fact]
    public void Parse_ReturnsMaxModelLenAsContext_LeavesModalitiesNull()
    {
        using var doc = JsonDocument.Parse(VllmModelsJson);
        var probe = new BackendProbe("Qwen/Qwen3.6-VL-30B-FP8", doc.RootElement, PropsRoot: null);
        var result = new VllmBackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Equal("Qwen/Qwen3.6-VL-30B-FP8", result.ModelId);
        Assert.Equal(256_000, result.ContextWindowTokens);
        // vLLM exposes no modality info; HF resolver fills these.
        Assert.Null(result.InputModalities);
        Assert.Null(result.OutputModalities);
    }

    [Fact]
    public void Parse_ZeroMaxModelLen_ReturnsUnknownContext()
    {
        const string json = """
        { "object": "list", "data": [ { "id": "model-x", "owned_by": "vllm", "max_model_len": 0 } ] }
        """;
        using var doc = JsonDocument.Parse(json);
        var probe = new BackendProbe("model-x", doc.RootElement, PropsRoot: null);

        var result = new VllmBackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Null(result.ContextWindowTokens);
    }

    [Fact]
    public void Parse_NoMatchingModelId_ReturnsNull()
    {
        // Strategy can't say anything useful when the served model is
        // not in the catalog. Returning null lets the composite continue
        // through the rest of the chain.
        using var doc = JsonDocument.Parse(VllmModelsJson);
        var probe = new BackendProbe("other-model", doc.RootElement, PropsRoot: null);
        var result = new VllmBackendStrategy().Parse(probe);

        Assert.Null(result);
    }
}
