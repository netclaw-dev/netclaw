// -----------------------------------------------------------------------
// <copyright file="BoundedOutputReaderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class BoundedOutputReaderTests
{
    // ── DrainToWindowAsync ──

    [Fact]
    public async Task DrainToWindow_short_output_returned_verbatim()
    {
        var input = "hello world";
        var (text, truncated, cancelled) = await BoundedOutputReader.DrainToWindowAsync(new StringReader(input), 100, CancellationToken.None);
        Assert.Equal(input, text);
        Assert.False(truncated);
        Assert.False(cancelled);
    }

    [Fact]
    public async Task DrainToWindow_empty_input_returns_empty()
    {
        var (text, truncated, cancelled) = await BoundedOutputReader.DrainToWindowAsync(new StringReader(""), 100, CancellationToken.None);
        Assert.Equal("", text);
        Assert.False(truncated);
        Assert.False(cancelled);
    }

    [Fact]
    public async Task DrainToWindow_output_exactly_at_cap_not_truncated()
    {
        var input = new string('a', 100);
        var (text, truncated, cancelled) = await BoundedOutputReader.DrainToWindowAsync(new StringReader(input), 100, CancellationToken.None);
        Assert.Equal(input, text);
        Assert.False(truncated);
        Assert.False(cancelled);
    }

    [Fact]
    public async Task DrainToWindow_long_output_truncated_with_head_and_tail()
    {
        // 100-char head marker + separator + 100-char tail marker, with filler in the middle
        var head = new string('H', 100);
        var middle = new string('M', 5000);
        var tail = new string('T', 100);
        var input = head + middle + tail;

        var (text, truncated, cancelled) = await BoundedOutputReader.DrainToWindowAsync(new StringReader(input), 200, CancellationToken.None);

        Assert.True(truncated);
        Assert.False(cancelled); // budget cut, not a cancelled/grace cut
        Assert.StartsWith(new string('H', 100), text);  // head preserved
        Assert.EndsWith(new string('T', 100), text);    // tail preserved
        Assert.Contains("...", text);                    // separator present
        Assert.DoesNotContain("M", text);                // middle discarded
    }

    [Fact]
    public async Task DrainToWindow_head_and_tail_split_evenly()
    {
        // budget=10 → headCap=5, tailCap=5
        var input = "AAAAAXXXXXXBBBBB"; // 16 chars: 5 head, 6 overflow discard, 5 tail
        var (text, truncated, cancelled) = await BoundedOutputReader.DrainToWindowAsync(new StringReader(input), 10, CancellationToken.None);

        Assert.True(truncated);
        Assert.False(cancelled);
        Assert.StartsWith("AAAAA", text);
        Assert.EndsWith("BBBBB", text);
    }

    [Fact]
    public async Task DrainToWindow_disabled_cap_returns_full_output()
    {
        var input = new string('x', 10_000);
        var (text, truncated, cancelled) = await BoundedOutputReader.DrainToWindowAsync(new StringReader(input), 0, CancellationToken.None);
        Assert.Equal(input, text);
        Assert.False(truncated);
        Assert.False(cancelled);
    }

    [Fact]
    public async Task DrainToWindow_tail_ring_wraps_across_small_chunks()
    {
        // Drives the ring's wraparound + start-advance path that the StringReader
        // tests skip: each read delivers a chunk smaller than tailCap, so the tail
        // window is rebuilt incrementally and must wrap rather than reset wholesale.
        // budget=10 → headCap=5 ("ABCDE"), tailCap=5; last 5 of "FGHIJKLMNO" = "KLMNO".
        var reader = new ChunkedReader("ABCDEFGHIJKLMNO", chunkSize: 3);

        var (text, truncated, cancelled) = await BoundedOutputReader.DrainToWindowAsync(reader, 10, CancellationToken.None);

        Assert.True(truncated);
        Assert.False(cancelled);
        Assert.Equal($"ABCDE{Environment.NewLine}...{Environment.NewLine}KLMNO", text);
    }

    [Fact]
    public async Task DrainToWindow_cancelled_before_eof_reports_cancelled_true()
    {
        // A source that never reaches EOF, like a pipe that a background child
        // still holds open. The token must end the read before the source does.
        // The return value must show this cut, not report a clean read, so a
        // caller can flag a capture that might be partial.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var (text, truncated, cancelled) = await BoundedOutputReader.DrainToWindowAsync(
            new NeverEndingReader(), 100, cts.Token);

        Assert.True(cancelled);
        Assert.Equal("", text);
    }

    // ── Window (pure string head+tail) ──

    [Fact]
    public void Window_under_budget_returned_unchanged()
    {
        Assert.Equal("short", BoundedOutputReader.Window("short", 100));
    }

    [Fact]
    public void Window_over_budget_keeps_head_and_tail()
    {
        var input = new string('H', 50) + new string('M', 500) + new string('T', 50);
        var result = BoundedOutputReader.Window(input, 100);

        Assert.StartsWith(new string('H', 50), result);
        Assert.EndsWith(new string('T', 50), result);
        Assert.DoesNotContain("M", result);
    }

    // ── BoundedOutputAccumulator ──

    [Fact]
    public void Accumulator_short_input_returned_verbatim()
    {
        var acc = new BoundedOutputAccumulator(100);
        acc.Append("hello world".AsSpan());
        var (text, truncated) = acc.Finish();
        Assert.Equal("hello world", text);
        Assert.False(truncated);
    }

    [Fact]
    public void Accumulator_long_input_truncates_with_head_and_tail()
    {
        var acc = new BoundedOutputAccumulator(200);
        acc.Append(new string('H', 100).AsSpan());
        acc.Append(new string('M', 5000).AsSpan());
        acc.Append(new string('T', 100).AsSpan());
        var (text, truncated) = acc.Finish();

        Assert.True(truncated);
        Assert.StartsWith(new string('H', 100), text);
        Assert.EndsWith(new string('T', 100), text);
        Assert.Contains("...", text);
        Assert.DoesNotContain("M", text);
    }

    [Fact]
    public void Accumulator_many_small_chunks_wraps_tail_ring()
    {
        var acc = new BoundedOutputAccumulator(10);
        acc.Append("ABC".AsSpan());
        acc.Append("DEF".AsSpan());
        acc.Append("GHI".AsSpan());
        acc.Append("JKL".AsSpan());
        acc.Append("MNO".AsSpan());
        var (text, truncated) = acc.Finish();

        Assert.True(truncated);
        Assert.Equal($"ABCDE{Environment.NewLine}...{Environment.NewLine}KLMNO", text);
    }

    [Fact]
    public async Task Accumulator_matches_drain_output()
    {
        var input = new string('H', 100) + new string('M', 5000) + new string('T', 100);
        const int budget = 200;

        var (drainText, drainTruncated, _) = await BoundedOutputReader.DrainToWindowAsync(
            new StringReader(input), budget, CancellationToken.None);

        var acc = new BoundedOutputAccumulator(budget);
        acc.Append(input.AsSpan());
        var (accText, accTruncated) = acc.Finish();

        Assert.Equal(drainText, accText);
        Assert.Equal(drainTruncated, accTruncated);
    }

    // Hands out at most chunkSize chars per read so tests can exercise the tail
    // ring's incremental wrap path — real pipe reads arrive in arbitrary slices,
    // not the single 4KB gulp a StringReader gives.
    private sealed class ChunkedReader(string data, int chunkSize) : TextReader
    {
        private int _pos;

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            var remaining = data.Length - _pos;
            if (remaining <= 0)
                return ValueTask.FromResult(0);

            var n = Math.Min(Math.Min(chunkSize, buffer.Length), remaining);
            data.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return ValueTask.FromResult(n);
        }
    }

    // Stands in for a pipe that a background child still holds open: it never
    // returns and never reaches EOF. Only the caller's token can end a read.
    private sealed class NeverEndingReader : TextReader
    {
        [SlopwatchSuppress("SW004", "Infinite delay is the test fixture, not a timing guess: it stands in for a pipe that never reaches EOF, and only the caller's token can end the read.")]
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0; // unreachable: Task.Delay throws once cancellationToken fires
        }
    }
}
