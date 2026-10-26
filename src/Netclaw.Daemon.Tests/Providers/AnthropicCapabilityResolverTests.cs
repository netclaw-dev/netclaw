// -----------------------------------------------------------------------
// <copyright file="AnthropicCapabilityResolverTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Providers.Anthropic;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

/// <summary>
/// The Anthropic Models API model object (field names from SDK 12.50.0 <c>ModelInfo</c>). Medium is
/// not supported in this fixture, so the row proves that each level comes from its own flag.
/// </summary>
public sealed class AnthropicCapabilityResolverTests
{
    private const string ModelJson = """
    {
      "id": "claude-opus-5-5",
      "type": "model",
      "display_name": "Claude Opus 5.5",
      "created_at": "2026-06-01T00:00:00Z",
      "max_input_tokens": 1000000,
      "max_tokens": 128000,
      "capabilities": {
        "thinking": { "supported": true, "types": { "adaptive": { "supported": true }, "enabled": { "supported": false } } },
        "effort": {
          "supported": true,
          "low": { "supported": true },
          "medium": { "supported": false },
          "high": { "supported": true },
          "xhigh": { "supported": true },
          "max": { "supported": true }
        }
      }
    }
    """;

    // An older model has effort flags but no adaptive thinking mode. Netclaw sends the levels in
    // adaptive mode, so that model gets only Off (or no level).
    [Fact]
    public void effort_without_adaptive_offers_only_off()
    {
        var olderModel = ModelJson
            .Replace("\"id\": \"claude-opus-5-5\"", "\"id\": \"claude-older\"", StringComparison.Ordinal)
            .Replace("\"adaptive\": { \"supported\": true }", "\"adaptive\": { \"supported\": false }", StringComparison.Ordinal)
            .Replace("\"enabled\": { \"supported\": false }", "\"enabled\": { \"supported\": true }", StringComparison.Ordinal);
        Assert.Contains("\"adaptive\": { \"supported\": false }", olderModel, StringComparison.Ordinal);

        var result = AnthropicCapabilityResolver.ParseModel(olderModel, "claude-older");

        Assert.NotNull(result);
        Assert.DoesNotContain(result.Thinking.Levels, level => level != ThinkingLevel.Off);
    }

    [Theory]
    [InlineData("effort_flags_map_to_levels")]
    [InlineData("max_tokens_maps_to_max_output")]
    public void ParseModel_ReadsThinkingCapability(string row)
    {
        var result = AnthropicCapabilityResolver.ParseModel(ModelJson, "claude-opus-5-5");

        Assert.NotNull(result);
        if (row == "max_tokens_maps_to_max_output")
        {
            Assert.Equal(128_000, result.MaxOutputTokens);
            return;
        }

        // Off depends on how the API reports a model that rejects thinking.type disabled
        // (design Open Question 3), so this row checks only the effort levels.
        Assert.Equal(ThinkingSupportKind.Controllable, result.Thinking.Kind);
        Assert.Equal(
            [ThinkingLevel.Low, ThinkingLevel.High, ThinkingLevel.Max],
            result.Thinking.Levels.Where(level => level != ThinkingLevel.Off));
    }
}
