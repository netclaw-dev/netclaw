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
    private static long _turnsCompletedTotal;
    private static long _memoriesFormedTotal;
    private static long _memoriesRecalledTotal;
    private static long _skillsLoadedTotal;

    public sealed record Snapshot(
        long InputTokensTotal,
        long OutputTokensTotal,
        long TurnsCompletedTotal,
        long MemoriesFormedTotal,
        long MemoriesRecalledTotal,
        long SkillsLoadedTotal);

    public static void RecordUsage(long inputTokens, long outputTokens)
    {
        Interlocked.Add(ref _inputTokensTotal, inputTokens);
        Interlocked.Add(ref _outputTokensTotal, outputTokens);
        InputTokensConsumed.Add(inputTokens);
        OutputTokensConsumed.Add(outputTokens);
    }

    public static void RecordTurnCompleted()
    {
        Interlocked.Increment(ref _turnsCompletedTotal);
        TurnsCompleted.Add(1);
    }

    public static void RecordMemoriesFormed(int count)
    {
        Interlocked.Add(ref _memoriesFormedTotal, count);
    }

    public static void RecordMemoriesRecalled(int count)
    {
        Interlocked.Add(ref _memoriesRecalledTotal, count);
    }

    public static void RecordSkillsLoaded(int count)
    {
        Interlocked.Add(ref _skillsLoadedTotal, count);
    }

    public static Snapshot GetSnapshot()
        => new(
            InputTokensTotal: Interlocked.Read(ref _inputTokensTotal),
            OutputTokensTotal: Interlocked.Read(ref _outputTokensTotal),
            TurnsCompletedTotal: Interlocked.Read(ref _turnsCompletedTotal),
            MemoriesFormedTotal: Interlocked.Read(ref _memoriesFormedTotal),
            MemoriesRecalledTotal: Interlocked.Read(ref _memoriesRecalledTotal),
            SkillsLoadedTotal: Interlocked.Read(ref _skillsLoadedTotal));

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _inputTokensTotal, 0);
        Interlocked.Exchange(ref _outputTokensTotal, 0);
        Interlocked.Exchange(ref _turnsCompletedTotal, 0);
        Interlocked.Exchange(ref _memoriesFormedTotal, 0);
        Interlocked.Exchange(ref _memoriesRecalledTotal, 0);
        Interlocked.Exchange(ref _skillsLoadedTotal, 0);
    }
}
