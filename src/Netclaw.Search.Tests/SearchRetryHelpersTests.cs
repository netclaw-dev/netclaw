// -----------------------------------------------------------------------
// <copyright file="SearchRetryHelpersTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Search;
using Xunit;

namespace Netclaw.Search.Tests;

public class SearchRetryHelpersTests
{
    [Fact]
    public void ParseRetryAfter_returns_delta_from_header()
    {
        var header = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        var delay = SearchRetryHelpers.ParseRetryAfter(header, 0, TimeProvider.System);
        Assert.Equal(TimeSpan.FromSeconds(30), delay);
    }

    [Fact]
    public void ParseRetryAfter_returns_remaining_time_from_date_header()
    {
        var now = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var future = now.AddSeconds(45);
        var header = new RetryConditionHeaderValue(future);
        var fakeTime = new FakeTimeProvider(now);

        var delay = SearchRetryHelpers.ParseRetryAfter(header, 0, fakeTime);

        Assert.Equal(TimeSpan.FromSeconds(45), delay);
    }

    [Fact]
    public void ParseRetryAfter_falls_back_to_exponential_when_date_in_past()
    {
        var now = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var past = now.AddSeconds(-30);
        var header = new RetryConditionHeaderValue(past);
        var fakeTime = new FakeTimeProvider(now);

        var delay = SearchRetryHelpers.ParseRetryAfter(header, 0, fakeTime);

        Assert.Equal(TimeSpan.FromSeconds(5), delay);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    public void ParseRetryAfter_falls_back_to_exponential_backoff_when_header_absent(int attempt, int expectedSeconds)
    {
        var delay = SearchRetryHelpers.ParseRetryAfter(null, attempt, TimeProvider.System);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void UserAgent_starts_with_Netclaw_slash()
    {
        Assert.StartsWith("Netclaw/", SearchRetryHelpers.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public void UserAgent_includes_homepage_url()
    {
        Assert.Contains("netclaw.dev", SearchRetryHelpers.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public void UserAgent_does_not_contain_plus_suffix()
    {
        // The "+" in "(+https://netclaw.dev)" is part of the URL marker, but the version
        // portion (between "/" and " ") should never contain a "+...commitsha" suffix.
        var ua = SearchRetryHelpers.UserAgent;
        var slash = ua.IndexOf('/', StringComparison.Ordinal);
        var space = ua.IndexOf(' ', StringComparison.Ordinal);
        var versionPart = ua[(slash + 1)..space];
        Assert.DoesNotContain('+', versionPart);
    }

}
