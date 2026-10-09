// -----------------------------------------------------------------------
// <copyright file="BoundedOutputReader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers;
using System.Text;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Bounds external tool output (process pipes, files) to the LAST
/// <c>budget</c> chars of a fixed character budget, in bounded memory regardless
/// of total output size. The dropped head is not kept inline: callers spill the
/// full result and steer the model to <c>tool_output_read</c> for it. Extracted
/// from <c>ShellTool.BoundedDrainAsync</c> (#1293) so the ring/window logic is
/// reviewed and fixed once and reused by <c>shell_execute</c>,
/// <c>background_job</c>, and <c>file_read</c>.
/// </summary>
/// <remarks>
/// The reader is a pure leaf: it does no redaction and no file IO — callers
/// redact (<c>SecretOutputRedactor</c>) and spill. Allocation is
/// O(budget), not O(total output): the scratch read buffer is pooled, the tail
/// ring is allocated only once it first receives data, and reads go through the
/// <see cref="ValueTask{T}"/> overload so a pipe that already has data buffered
/// completes synchronously without a per-chunk <see cref="Task"/> allocation.
/// </remarks>
internal static class BoundedOutputReader
{
    /// <summary>
    /// Drains <paramref name="reader"/> into a tail-only window bounded by
    /// <paramref name="budget"/> chars. Chars beyond the budget are discarded but
    /// the source continues to be read so a still-running child never deadlocks on
    /// a full pipe buffer. A non-positive <paramref name="budget"/> disables the
    /// cap (reads the whole stream). Returns the captured text, whether the budget
    /// truncated it, and whether <paramref name="ct"/> cancelled the read before
    /// the source reached EOF.
    /// </summary>
    /// <remarks>
    /// A cancelled <paramref name="ct"/> stops the read and returns whatever was
    /// captured so far, instead of throwing. Callers pass a real, boundable token
    /// (not <see cref="CancellationToken.None"/>): a process pipe reaches EOF only
    /// when every process holding its write end closes it, and a forked or
    /// backgrounded grandchild (a daemon, a `cmd &amp;` job) can hold that write
    /// end open long after the direct child process has exited. Without a way to
    /// stop the read, the drain would hang for the grandchild's full life span.
    /// When <paramref name="ct"/> cancels the read, unread data may still wait
    /// behind it. A caller that cares about a silent partial capture must check
    /// the returned <c>Cancelled</c> flag. The <c>Truncated</c> flag alone is not
    /// enough: it reports only the budget cut.
    /// </remarks>
    public static async Task<(string Text, bool Truncated, bool Cancelled)> DrainToWindowAsync(
        TextReader reader, int budget, CancellationToken ct)
    {
        if (budget <= 0)
        {
            try
            {
                var all = await reader.ReadToEndAsync(ct);
                return (all, false, false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // ReadToEndAsync has no partial-read API, so a cancellation here
                // returns nothing captured rather than hanging past the bound.
                return (string.Empty, true, true);
            }
        }

        var acc = new BoundedOutputAccumulator(budget);
        var cancelled = false;

        var buf = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buf.AsMemory(), ct)) > 0)
                acc.Append(buf.AsSpan(0, read));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The source never reached EOF within the caller's bound. Return
            // whatever was captured instead of discarding it or hanging.
            cancelled = true;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buf, clearArray: true);
        }

        var (text, truncated) = acc.Finish();
        return (text, truncated, cancelled);
    }

    /// <summary>
    /// Tail-only window of an already-in-memory string: the LAST
    /// <paramref name="budget"/> chars plus a leading separator. Returns the
    /// string unchanged when it already fits (or the budget is non-positive).
    /// Used to derive the inline window from a larger (redacted) capture — see
    /// <c>ToolOutputSpill</c>. The dropped head is only in the spill file; the
    /// model pulls it back with <c>tool_output_read</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="budget"/> bounds the retained <i>content</i>, not the returned
    /// string length: a truncated result is <c>budget + Separator.Length</c> chars
    /// (the separator and the tail). Callers enforcing a hard character ceiling
    /// must account for the separator.
    /// </remarks>
    public static string Window(string text, int budget)
    {
        if (budget <= 0 || text.Length <= budget)
            return text;

        return string.Concat(Separator, text.AsSpan(text.Length - budget));
    }

    internal static readonly string Separator = $"{Environment.NewLine}...{Environment.NewLine}";

    /// <summary>
    /// Writes <paramref name="span"/> into a ring buffer that retains only the
    /// most recent <c>ring.Length</c> chars. Uses block copies (at most two per
    /// call) rather than a per-char loop, so draining a very chatty child stays
    /// cheap regardless of how much it prints.
    /// </summary>
    internal static void AppendToTailRing(char[] ring, ReadOnlySpan<char> span, ref int start, ref int len)
    {
        var cap = ring.Length;

        if (span.Length >= cap)
        {
            // This span alone fills (or overfills) the window: only its last `cap`
            // chars can survive. One contiguous copy, ring reset.
            span[^cap..].CopyTo(ring);
            start = 0;
            len = cap;
            return;
        }

        var writePos = (start + len) % cap;
        var first = Math.Min(span.Length, cap - writePos);
        span[..first].CopyTo(ring.AsSpan(writePos));
        if (first < span.Length)
            span[first..].CopyTo(ring); // remainder wraps to the front

        var newLen = len + span.Length;
        if (newLen > cap)
        {
            // Overwrote the oldest chars: advance start past them.
            start = (start + (newLen - cap)) % cap;
            len = cap;
        }
        else
        {
            len = newLen;
        }
    }

    internal static void AppendRing(StringBuilder sb, char[] ring, int start, int len)
    {
        var first = Math.Min(len, ring.Length - start);
        sb.Append(ring, start, first);
        if (first < len)
            sb.Append(ring, 0, len - first);
    }
}

/// <summary>
/// Stateful tail-only accumulator that accepts incremental <c>Append</c> calls
/// and produces the same bounded window as <see cref="BoundedOutputReader.DrainToWindowAsync"/>:
/// the last <c>budget</c> chars, with a leading separator when truncated. Used by
/// the streaming <c>ShellTool</c> path where pipe chunks must be fed to both the
/// activity channel and the bounded capture simultaneously.
/// </summary>
internal sealed class BoundedOutputAccumulator
{
    private readonly int _budget;
    private char[]? _tailBuf;
    private int _tailStart;
    private int _tailLen;
    private long _totalChars;

    public BoundedOutputAccumulator(int budget)
    {
        _budget = budget;
    }

    public void Append(ReadOnlySpan<char> chunk)
    {
        _totalChars += chunk.Length;
        if (chunk.IsEmpty || _budget <= 0)
            return;

        _tailBuf ??= new char[_budget];
        BoundedOutputReader.AppendToTailRing(_tailBuf, chunk, ref _tailStart, ref _tailLen);
    }

    public (string Text, bool Truncated) Finish()
    {
        var truncated = _totalChars > _budget;
        if (_tailBuf is null || _tailLen == 0)
            return (string.Empty, truncated);

        var sb = new StringBuilder(_tailLen + (truncated ? BoundedOutputReader.Separator.Length : 0));
        if (truncated)
            sb.Append(BoundedOutputReader.Separator);
        BoundedOutputReader.AppendRing(sb, _tailBuf, _tailStart, _tailLen);
        return (sb.ToString(), truncated);
    }
}
