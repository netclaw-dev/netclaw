// -----------------------------------------------------------------------
// <copyright file="DefaultOutputLimitTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// The D4 table. The default is min(32000, model max, window - prompt - margin), with
/// margin = max(1024, window / 20) and a floor of 1024. An explicit value is sent as is.
/// </summary>
public sealed class DefaultOutputLimitTests
{
    [Theory]
    [InlineData("short_prompt_sends_32000_default", null, null, 5_000, 131_072, null, 32_000, "Default", false)]
    // 131072 - 110000 - 6553 = 14519
    [InlineData("prompt_near_window_sends_14519_default_clamped", null, null, 110_000, 131_072, null, 14_519, "DefaultClamped", false)]
    // 32768 - 31000 - 1638 = 130, below the floor
    [InlineData("prompt_past_floor_sends_1024_and_warns", null, null, 31_000, 32_768, null, 1_024, "DefaultClamped", true)]
    [InlineData("unknown_window_sends_32000_default", null, null, 5_000, null, null, 32_000, "Default", false)]
    [InlineData("model_max_16384_sends_16384_default_clamped", null, null, 5_000, 131_072, 16_384, 16_384, "DefaultClamped", false)]
    // A model max of 0 is an unknown sentinel, not a limit.
    [InlineData("zero_model_max_counts_as_unknown", null, null, 5_000, 131_072, 0, 32_000, "Default", false)]
    [InlineData("explicit_configured_value_is_not_clamped", null, 64_000, 110_000, 131_072, 64_000, 64_000, "Configured", false)]
    [InlineData("call_site_value_is_not_clamped", 4_096, 32_000, 31_000, 32_768, 2_048, 4_096, "Call", false)]
    public void Computes_default(
        string row,
        int? callValue,
        int? configuredValue,
        int promptTokens,
        int? contextWindow,
        int? modelMax,
        int expectedValue,
        string expectedSource,
        bool expectedFloor)
    {
        var limit = OutputLimitResolver.Resolve(callValue, configuredValue, promptTokens, contextWindow, modelMax);

        Assert.True(
            limit == new OutputLimit(expectedValue, Enum.Parse<OutputLimitSource>(expectedSource), expectedFloor),
            $"{row}: got {limit}");
    }
}
