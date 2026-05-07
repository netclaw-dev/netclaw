// -----------------------------------------------------------------------
// <copyright file="SearchRetryHelpers.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using Netclaw.Configuration;

namespace Netclaw.Search;

/// <summary>
/// Shared HTTP helpers used by multiple search backends:
/// <see cref="ParseRetryAfter"/> for honoring server-supplied retry hints,
/// and <see cref="UserAgent"/> for identifying outbound search traffic.
/// </summary>
internal static class SearchRetryHelpers
{
    /// <summary>
    /// Parses the Retry-After response header into a wait duration.
    /// Falls back to exponential backoff (5s, 10s, 20s) when the header is absent or in the past.
    /// </summary>
    internal static TimeSpan ParseRetryAfter(RetryConditionHeaderValue? retryAfter, int attempt, TimeProvider timeProvider)
    {
        if (retryAfter is not null)
        {
            if (retryAfter.Delta.HasValue)
                return retryAfter.Delta.Value;

            if (retryAfter.Date.HasValue)
            {
                var remaining = retryAfter.Date.Value - timeProvider.GetUtcNow();
                if (remaining > TimeSpan.Zero)
                    return remaining;
            }
        }

        // Exponential backoff: 5s, 10s, 20s
        return TimeSpan.FromSeconds(5 * Math.Pow(2, attempt));
    }

    /// <summary>
    /// User-Agent value sent by every outbound search request. Format:
    /// <c>Netclaw/{version} (+https://netclaw.dev)</c>. Cached at type initialization.
    /// </summary>
    internal static string UserAgent { get; } =
        $"Netclaw/{BuildInfo.GetVersion(typeof(SearchRetryHelpers).Assembly)} (+https://netclaw.dev)";
}
