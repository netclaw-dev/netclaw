// -----------------------------------------------------------------------
// <copyright file="Ds4BackendStrategyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration;
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers.Strategies;

public sealed class Ds4BackendStrategyTests
{
    // Real ds4-server response shape (see append_model_json_values in
    // ds4_server.c): OpenRouter-shaped metadata with owned_by "ds4.c".
    private const string Ds4ModelsJson = """
    {
      "object": "list",
      "data": [
        {
          "id": "deepseek-v4-flash",
          "object": "model",
          "created": 1767225600,
          "owned_by": "ds4.c",
          "name": "DeepSeek V4 Flash",
          "context_length": 262144,
          "top_provider": {
            "context_length": 262144,
            "max_completion_tokens": 8192,
            "is_moderated": false
          },
          "supported_parameters": ["tools", "tool_choice", "stream"]
        },
        {
          "id": "deepseek-v4-pro",
          "object": "model",
          "created": 1767225600,
          "owned_by": "ds4.c",
          "name": "DeepSeek V4 PRO",
          "top_provider": { "context_length": 196608 }
        }
      ]
    }
    """;

    [Fact]
    public void Matches_OwnedByDs4()
    {
        using var doc = JsonDocument.Parse(Ds4ModelsJson);
        var probe = new BackendProbe("deepseek-v4-flash", doc.RootElement, PropsRoot: null);
        Assert.True(new Ds4BackendStrategy().Matches(probe));
    }

    [Fact]
    public void Matches_False_WhenOwnedByOther()
    {
        const string json = """
        { "object": "list", "data": [ { "id": "model-x", "owned_by": "vllm", "context_length": 131072 } ] }
        """;
        using var doc = JsonDocument.Parse(json);
        var probe = new BackendProbe("model-x", doc.RootElement, PropsRoot: null);
        Assert.False(new Ds4BackendStrategy().Matches(probe));
    }

    [Fact]
    public void Matches_False_WhenModelMissing()
    {
        using var doc = JsonDocument.Parse(Ds4ModelsJson);
        var probe = new BackendProbe("gpt-4", doc.RootElement, PropsRoot: null);
        Assert.False(new Ds4BackendStrategy().Matches(probe));
    }

    [Fact]
    public void Parse_ReadsTopLevelContextLength_AndTextModalities()
    {
        using var doc = JsonDocument.Parse(Ds4ModelsJson);
        var probe = new BackendProbe("deepseek-v4-flash", doc.RootElement, PropsRoot: null);

        var result = new Ds4BackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Equal(262_144, result.ContextWindowTokens);
        Assert.Equal(ModelModality.Text, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    [Fact]
    public void Parse_FallsBackToTopProviderContextLength()
    {
        using var doc = JsonDocument.Parse(Ds4ModelsJson);
        var probe = new BackendProbe("deepseek-v4-pro", doc.RootElement, PropsRoot: null);

        var result = new Ds4BackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Equal(196_608, result.ContextWindowTokens);
    }

    [Fact]
    public void Parse_TreatsMissingContextAsUnknown()
    {
        const string json = """
        { "object": "list", "data": [ { "id": "deepseek-v4-flash", "owned_by": "ds4.c" } ] }
        """;
        using var doc = JsonDocument.Parse(json);
        var probe = new BackendProbe("deepseek-v4-flash", doc.RootElement, PropsRoot: null);

        var result = new Ds4BackendStrategy().Parse(probe);

        Assert.NotNull(result);
        Assert.Null(result.ContextWindowTokens);
    }
}
