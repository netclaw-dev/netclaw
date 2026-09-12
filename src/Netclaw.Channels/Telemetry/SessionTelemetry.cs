// -----------------------------------------------------------------------
// <copyright file="SessionTelemetry.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics.Metrics;

namespace Netclaw.Channels.Telemetry;

/// <summary>
/// OpenTelemetry counter instrumentation for session-level metrics.
/// Pushes deltas into OTel <see cref="Counter{T}"/> instruments only —
/// accumulation and snapshots are owned by the DailyStatsActor.
/// </summary>
public static class SessionTelemetry
{
    public const string MeterName = "Netclaw.Sessions";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> InputTokensConsumed =
        Meter.CreateCounter<long>("netclaw.session.tokens.input");

    private static readonly Counter<long> OutputTokensConsumed =
        Meter.CreateCounter<long>("netclaw.session.tokens.output");

    private static readonly Counter<long> TurnsCompleted =
        Meter.CreateCounter<long>("netclaw.session.turns.completed");

    public static void RecordUsage(long inputTokens, long outputTokens)
    {
        InputTokensConsumed.Add(inputTokens);
        OutputTokensConsumed.Add(outputTokens);
    }

    public static void RecordTurnCompleted()
    {
        TurnsCompleted.Add(1);
    }
}
