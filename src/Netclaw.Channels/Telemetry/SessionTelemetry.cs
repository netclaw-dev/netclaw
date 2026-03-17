using System.Diagnostics.Metrics;
using System.Threading;

namespace Netclaw.Channels.Telemetry;

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

    private static long _inputTokensTotal;
    private static long _outputTokensTotal;
    private static long _cachedInputTokensTotal;
    private static long _turnsCompletedTotal;

    public sealed record Snapshot(
        long InputTokensTotal,
        long OutputTokensTotal,
        long CachedInputTokensTotal,
        long TurnsCompletedTotal);

    public static void RecordUsage(long inputTokens, long outputTokens, long cachedInputTokens)
    {
        Interlocked.Add(ref _inputTokensTotal, inputTokens);
        Interlocked.Add(ref _outputTokensTotal, outputTokens);
        Interlocked.Add(ref _cachedInputTokensTotal, cachedInputTokens);
        InputTokensConsumed.Add(inputTokens);
        OutputTokensConsumed.Add(outputTokens);
    }

    public static void RecordTurnCompleted()
    {
        Interlocked.Increment(ref _turnsCompletedTotal);
        TurnsCompleted.Add(1);
    }

    public static Snapshot GetSnapshot()
        => new(
            InputTokensTotal: Interlocked.Read(ref _inputTokensTotal),
            OutputTokensTotal: Interlocked.Read(ref _outputTokensTotal),
            CachedInputTokensTotal: Interlocked.Read(ref _cachedInputTokensTotal),
            TurnsCompletedTotal: Interlocked.Read(ref _turnsCompletedTotal));

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _inputTokensTotal, 0);
        Interlocked.Exchange(ref _outputTokensTotal, 0);
        Interlocked.Exchange(ref _cachedInputTokensTotal, 0);
        Interlocked.Exchange(ref _turnsCompletedTotal, 0);
    }
}
