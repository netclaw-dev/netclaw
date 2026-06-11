// -----------------------------------------------------------------------
// <copyright file="SyntheticCharReader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Benchmarks;

/// <summary>
/// A <see cref="TextReader"/> that synthesises a fixed number of characters
/// without ever holding the full payload in memory. It stands in for a chatty
/// child process's stdout so the drain benchmark measures the algorithm's
/// allocations and nothing else — the input itself allocates O(1), so anything
/// the <c>[MemoryDiagnoser]</c> reports belongs to the code under test.
///
/// All reads complete synchronously: the goal is to feed the drain loop as fast
/// as possible, not to model pipe latency.
/// </summary>
internal sealed class SyntheticCharReader(long total) : TextReader
{
    private const char Fill = 'x';
    private long _produced;

    public override int Read(char[] buffer, int index, int count)
    {
        var remaining = total - _produced;
        if (remaining <= 0)
            return 0;

        var n = (int)Math.Min(count, remaining);
        Array.Fill(buffer, Fill, index, n);
        _produced += n;
        return n;
    }

    public override int Read(Span<char> buffer)
    {
        var remaining = total - _produced;
        if (remaining <= 0)
            return 0;

        var n = (int)Math.Min(buffer.Length, remaining);
        buffer[..n].Fill(Fill);
        _produced += n;
        return n;
    }

    // BoundedDrainAsync reads via the ValueTask Memory<char> overload (below).
    // The char[] Task overload is kept too so any reader path resolves to the
    // synchronous fast path above and the benchmark measures the algorithm only.
    public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(char[] buffer, int index, int count)
        => Task.FromResult(Read(buffer, index, count));
}
