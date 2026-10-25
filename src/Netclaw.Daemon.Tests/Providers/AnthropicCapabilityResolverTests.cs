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
