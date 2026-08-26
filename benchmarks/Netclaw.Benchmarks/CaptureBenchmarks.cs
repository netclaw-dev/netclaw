// -----------------------------------------------------------------------
// <copyright file="CaptureBenchmarks.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

using BenchmarkDotNet.Attributes;
using Netclaw.Actors.Tools;

namespace Netclaw.Benchmarks;

/// <summary>
/// Exercises the production capture path used by shell/background_job/file_read:
/// drain a stream to the capture ceiling, then derive the small inline window
/// (what <c>ToolOutputSpill</c> does before redaction). The point is to confirm
/// allocation is O(capture ceiling), not O(total output) — peak allocation stays
/// flat whether the source emits 1K chars or 50M.
/// </summary>
[MemoryDiagnoser]
public class CaptureBenchmarks
{
    private const int CaptureMax = 256_000;  // ToolConfig.MaxOutputChars default
    private const int InlineBudget = 2_000;  // SessionTuning.MaxInlineToolResultChars default

    [Params(1_000, 256_000, 50_000_000)]
    public int TotalChars;

    [Benchmark]
    public async Task<int> Capture_then_inline_window()
    {
        var reader = new SyntheticCharReader(TotalChars);
        var (captured, _, _) = await BoundedOutputReader.DrainToWindowAsync(reader, CaptureMax, CancellationToken.None);
        var inline = BoundedOutputReader.Window(captured, InlineBudget);
        return inline.Length;
    }
}
