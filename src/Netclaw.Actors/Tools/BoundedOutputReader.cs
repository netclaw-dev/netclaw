// -----------------------------------------------------------------------
// <copyright file="BoundedOutputReader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Buffers;
using System.Text;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Bounds external tool output (process pipes, files) into a head+tail window of
/// a fixed character budget, in bounded memory regardless of total output size.
/// Extracted from <c>ShellTool.BoundedDrainAsync</c> (#1293) so the ring/window
/// logic is reviewed and fixed once and reused by <c>shell_execute</c>,
/// <c>background_job</c>, and <c>file_read</c>.
/// </summary>
/// <remarks>
/// The reader is a pure leaf: it does no redaction and no file IO — callers
/// redact (<c>SecretOutputRedactor</c>) and spill. Allocation is
/// O(budget), not O(total output): the scratch read buffer is pooled, the tail
/// ring is allocated only once the head fills, and reads go through the
/// <see cref="ValueTask{T}"/> overload so a pipe that already has data buffered
/// completes synchronously without a per-chunk <see cref="Task"/> allocation.
/// </remarks>
internal static class BoundedOutputReader
{
    /// <summary>
    /// Drains <paramref name="reader"/> into a head+tail window bounded by
    /// <paramref name="budget"/> chars. Chars beyond the budget are discarded but
    /// the source continues to be read so a still-running child never deadlocks on
    /// a full pipe buffer. A non-positive <paramref name="budget"/> disables the
    /// cap (reads the whole stream). Returns the captured text and whether it was
    /// truncated.
    /// </summary>
    public static async Task<(string Text, bool Truncated)> DrainToWindowAsync(
        TextReader reader, int budget, CancellationToken ct)
    {
        if (budget <= 0)
        {
            var all = await reader.ReadToEndAsync(ct);
            return (all, false);
        }

        var acc = new BoundedOutputAccumulator(budget);

        var buf = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buf.AsMemory(), ct)) > 0)
                acc.Append(buf.AsSpan(0, read));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buf, clearArray: true);
        }

        return acc.Finish();
    }

    /// <summary>
    /// Head+tail window of an already-in-memory string to <paramref name="budget"/>
    /// chars. Returns the string unchanged when it already fits (or the budget is
    /// non-positive). Used to derive the inline window from a larger (redacted)
    /// capture — see <c>ToolOutputSpill</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="budget"/> bounds the retained <i>content</i>, not the returned
    /// string length: a truncated result is <c>budget + Separator.Length</c> chars
    /// (the head, the "…" separator, and the tail). Callers enforcing a hard
    /// character ceiling must account for the separator.
    /// </remarks>
    public static string Window(string text, int budget)
    {
        if (budget <= 0 || text.Length <= budget)
            return text;

        var headCap = budget / 2 + budget % 2;
        var tailCap = budget / 2;
        return string.Concat(text.AsSpan(0, headCap), Separator, text.AsSpan(text.Length - tailCap));
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
/// Stateful head+tail accumulator that accepts incremental <c>Append</c> calls
/// and produces the same bounded window as <see cref="BoundedOutputReader.DrainToWindowAsync"/>.
/// Used by the streaming <c>ShellTool</c> path where pipe chunks must be fed to
/// both the activity channel and the bounded capture simultaneously.
/// </summary>
internal sealed class BoundedOutputAccumulator
{
    private readonly int _budget;
    private readonly int _headCap;
    private readonly int _tailCap;
    private readonly StringBuilder _head;
    private char[]? _tailBuf;
    private int _tailStart;
    private int _tailLen;
    private long _totalChars;

    public BoundedOutputAccumulator(int budget)
    {
        _budget = budget;
        _headCap = budget / 2 + budget % 2;
        _tailCap = budget / 2;
        _head = new StringBuilder(Math.Min(_headCap, 4096));
    }

    public void Append(ReadOnlySpan<char> chunk)
    {
        _totalChars += chunk.Length;
        var span = chunk;

        if (_head.Length < _headCap)
        {
            var headChunk = Math.Min(_headCap - _head.Length, span.Length);
            _head.Append(span[..headChunk]);
            span = span[headChunk..];
        }

        if (span.IsEmpty || _tailCap == 0)
            return;

        _tailBuf ??= new char[_tailCap];
        BoundedOutputReader.AppendToTailRing(_tailBuf, span, ref _tailStart, ref _tailLen);
    }

    public (string Text, bool Truncated) Finish()
    {
        var truncated = _totalChars > _budget;
        if (truncated)
            _head.Append(BoundedOutputReader.Separator);
        if (_tailBuf is not null && _tailLen > 0)
            BoundedOutputReader.AppendRing(_head, _tailBuf, _tailStart, _tailLen);
        return (_head.ToString(), truncated);
    }
}
