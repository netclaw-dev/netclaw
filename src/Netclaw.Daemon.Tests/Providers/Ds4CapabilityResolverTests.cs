// -----------------------------------------------------------------------
// <copyright file="Ds4CapabilityResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Providers.Ds4;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

/// <summary>
/// Parsing tests for <see cref="Ds4CapabilityResolver"/>. Verifies context
/// window extraction from ds4's OpenRouter-shaped <c>/v1/models</c> listing and
/// the text-only modality reported for DeepSeek V4 models.
/// </summary>
public sealed class Ds4CapabilityResolverTests
{
    private const string ModelsJson = """
    {
      "object": "list",
      "data": [
        { "id": "deepseek-v4-flash", "context_length": 262144,
          "top_provider": { "context_length": 131072 } },
        { "id": "deepseek-v4-pro", "top_provider": { "context_length": 196608 } }
      ]
    }
    """;

    [Fact]
    public void ParseModels_ResolvesContextAndTextModalities()
    {
        var result = Ds4CapabilityResolver.ParseModels(ModelsJson, "deepseek-v4-flash");

        Assert.NotNull(result);
        Assert.Equal(262_144, result!.ContextWindowTokens);
        Assert.Equal(ModelModality.Text, result.InputModalities);
        Assert.Equal(ModelModality.Text, result.OutputModalities);
    }

    [Fact]
    public void ParseModels_MatchesModelIdCaseInsensitively()
    {
        var result = Ds4CapabilityResolver.ParseModels(ModelsJson, "DEEPSEEK-V4-PRO");

        Assert.NotNull(result);
        Assert.Equal(196_608, result!.ContextWindowTokens);
    }

    [Fact]
    public void ParseModels_ReturnsNullWhenModelMissing()
    {
        var result = Ds4CapabilityResolver.ParseModels(ModelsJson, "gpt-4");

        Assert.Null(result);
    }
}
