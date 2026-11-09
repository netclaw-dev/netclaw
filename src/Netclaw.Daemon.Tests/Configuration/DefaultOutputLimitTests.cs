// -----------------------------------------------------------------------
// <copyright file="DefaultOutputLimitTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Daemon.Configuration;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// The default is min(32000, model max, window / 4). An explicit value is sent as is. The prompt size
/// never changes the limit.
/// </summary>
public sealed class DefaultOutputLimitTests
{
    [Theory]
    [InlineData("unknown_max_and_window_sends_32000_default", null, null, null, null, 32_000, "Default")]
    [InlineData("large_window_sends_32000_default", null, null, null, 401_408, 32_000, "Default")]
    // 128000 / 4 = 32000: the largest window that still fits the full default.
    [InlineData("window_128000_sends_32000_default", null, null, null, 128_000, 32_000, "Default")]
    [InlineData("window_32768_sends_8192_default_clamped", null, null, null, 32_768, 8_192, "DefaultClamped")]
    [InlineData("window_3_sends_1_default_clamped", null, null, null, 3, 1, "DefaultClamped")]
    [InlineData("model_max_16384_sends_16384_default_clamped", null, null, 16_384, 200_000, 16_384, "DefaultClamped")]
    [InlineData("window_below_model_max_wins", null, null, 16_384, 40_000, 10_000, "DefaultClamped")]
    [InlineData("model_max_above_default_sends_32000_default", null, null, 128_000, null, 32_000, "Default")]
    // A model max or window of 0 is an unknown sentinel, not a limit.
    [InlineData("zero_model_max_counts_as_unknown", null, null, 0, null, 32_000, "Default")]
    [InlineData("zero_window_counts_as_unknown", null, null, null, 0, 32_000, "Default")]
    [InlineData("explicit_configured_value_is_sent_as_is", null, 64_000, 16_384, 32_768, 64_000, "Configured")]
    [InlineData("call_site_value_is_sent_as_is", 4_096, 32_000, 2_048, 8_192, 4_096, "Call")]
    public void Computes_default(
        string row,
        int? callValue,
        int? configuredValue,
        int? modelMax,
        int? contextWindow,
        int expectedValue,
        string expectedSource)
    {
        var limit = OutputLimitResolver.Resolve(callValue, configuredValue, modelMax, contextWindow);

        Assert.True(
            limit == new OutputLimit(expectedValue, Enum.Parse<OutputLimitSource>(expectedSource)),
            $"{row}: got {limit}");
    }
}
