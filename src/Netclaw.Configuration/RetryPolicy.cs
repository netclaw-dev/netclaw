// -----------------------------------------------------------------------
// <copyright file="RetryPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;

namespace Netclaw.Configuration;

/// <summary>
/// Configuration for retry behavior on transient LLM provider failures.
/// </summary>
public sealed record RetryPolicy
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Determines whether the given exception is transient and should be retried.
    /// Retries on: status-less network failures, 408/429/5xx responses,
    /// and timeout-style cancellations.
    /// </summary>
    public bool ShouldRetry(Exception ex, int attempt)
    {
        if (attempt >= MaxRetries)
            return false;

        return ex switch
        {
            HttpRequestException { StatusCode: null } => true,
            HttpRequestException httpEx => httpEx.StatusCode is
                HttpStatusCode.RequestTimeout or
                HttpStatusCode.TooManyRequests or
                HttpStatusCode.InternalServerError or
                HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or
                HttpStatusCode.GatewayTimeout,
            TaskCanceledException => true,
            TimeoutException => true,
            _ => false
        };
    }

    /// <summary>
    /// Returns the delay before the next retry attempt using exponential backoff with jitter.
    /// </summary>
    public TimeSpan GetDelay(int attempt)
    {
        var exponential = TimeSpan.FromTicks(BaseDelay.Ticks * (1L << attempt));
        var capped = exponential > MaxDelay ? MaxDelay : exponential;
        // Add ±25% jitter to avoid thundering herd
        var jitter = 0.75 + Random.Shared.NextDouble() * 0.5;
        return TimeSpan.FromTicks((long)(capped.Ticks * jitter));
    }
}
