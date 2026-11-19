// -----------------------------------------------------------------------
// <copyright file="GatewayRetryPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Channels;

internal readonly record struct GatewayRetrySchedule(TimeSpan DueAfter, TimeSpan NextDelay);

internal static class GatewayRetryPolicy
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StabilityWindow = TimeSpan.FromSeconds(60);

    internal static GatewayRetrySchedule NextSchedule(TimeSpan currentDelay)
    {
        var nextDelay = currentDelay == TimeSpan.Zero
            ? InitialDelay
            : TimeSpan.FromTicks(Math.Min(currentDelay.Ticks * 2, MaxDelay.Ticks));
        return new GatewayRetrySchedule(currentDelay, nextDelay);
    }

    internal static TimeSpan DelayAfterReady(
        TimeSpan currentDelay,
        DateTimeOffset readyEnteredAt,
        DateTimeOffset now)
    {
        var readyDuration = now - readyEnteredAt;
        return readyDuration >= StabilityWindow ? TimeSpan.Zero : currentDelay;
    }
}
