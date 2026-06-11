// -----------------------------------------------------------------------
// <copyright file="ShellDrainBenchmarks.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using BenchmarkDotNet.Attributes;
using Netclaw.Actors.Tools;

namespace Netclaw.Benchmarks;

/// <summary>
/// Isolates the shell-output drain algorithm from the rest of <c>ShellTool</c>
/// (no process spawn, no real pipe I/O) so the numbers reflect only the cost of
/// turning a child's stdout stream into a bounded, redaction-ready string.
///
/// The comparison is the regression fix for #1293:
///   <see cref="BoundedOutputReader.DrainToWindowAsync"/> (head+tail ring buffer,
///   capped at read time) versus the old path — <see cref="System.IO.TextReader.ReadToEndAsync()"/>
///   followed by <see cref="ShellTool.TruncateOutput"/>, which materialised the
///   entire output before applying the cap. The story we want the numbers to tell
///   is allocation shape: the old path is O(total output) and lands on the LOH for
///   anything large; the new path is O(cap) regardless of how chatty the child is.
/// </summary>
[MemoryDiagnoser]
public class ShellDrainBenchmarks
{
    // Production default for ToolConfig.MaxOutputChars. Kept as a literal here so
    // the benchmark exercises the real cap without taking a Configuration dependency
    // at measurement time.
    private const int Cap = 32_000;

    /// <summary>
    /// Total characters the synthetic child "writes". Chosen to straddle the cap:
    /// below it (no truncation), exactly at it, modestly over (LOH territory), and
    /// far over (the pathological autonomous-log-pull case that triggered #1293).
    /// </summary>
    [Params(1_000, 32_000, 1_000_000, 50_000_000)]
    public int TotalChars;

    [Benchmark(Baseline = true, Description = "ReadToEnd + TruncateOutput (pre-#1293)")]
    public async Task<int> ReadToEnd_ThenTruncate()
    {
        // Reader is constructed per-invocation because it is stateful (consumed as
        // it is read). Its own allocation is a few dozen bytes and identical across
        // both benchmarks, so it does not distort the head-to-head allocation story.
        var reader = new SyntheticCharReader(TotalChars);
        var all = await reader.ReadToEndAsync();
        return ShellTool.TruncateOutput(all, Cap).Length;
    }

    [Benchmark(Description = "BoundedDrainAsync (post-#1293)")]
    public async Task<int> BoundedDrain()
    {
        var reader = new SyntheticCharReader(TotalChars);
        var (text, _) = await BoundedOutputReader.DrainToWindowAsync(reader, Cap, CancellationToken.None);
        return text.Length;
    }
}
