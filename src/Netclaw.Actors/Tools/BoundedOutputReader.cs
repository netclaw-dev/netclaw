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
            // Explicit opt-out: a non-positive budget disables truncation and
            // reads the whole stream. Callers that bound via config never pass
            // this; it exists for deliberate full-capture callers.
            var all = await reader.ReadToEndAsync(ct);
            return (all, false);
        }

        // Split the budget: first half for the head, second half for the tail.
        // Odd budget gives the extra char to the head. Computed without (budget +
        // 1) so a near-int.MaxValue budget can't overflow to a negative headCap.
        var headCap = budget / 2 + budget % 2;
        var tailCap = budget / 2;

        var head = new StringBuilder(Math.Min(headCap, 4096));

        // Tail ring buffer, allocated lazily on first overflow past the head. The
        // common case — output under the cap — never fills the head, so it never
        // pays for the tail window at all.
        char[]? tailBuf = null;
        var tailStart = 0;  // index of the oldest retained char in the ring
        var tailLen = 0;    // chars currently retained (<= tailCap)

        long totalChars = 0; // total chars seen across all reads; long so a multi-GB
                             // flood can't overflow the truncation check to a false negative

        // Transient scratch buffer for the read loop: pooled so a long drain
        // doesn't allocate it per call and it never lands on the LOH.
        var buf = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            int read;
            // ReadAsync(Memory<char>) returns a non-allocating ValueTask when the
            // read completes synchronously (data already buffered) — unlike the
            // Task<int> char[] overload, which allocates once per chunk.
            while ((read = await reader.ReadAsync(buf.AsMemory(), ct)) > 0)
            {
                totalChars += read;
                var span = buf.AsSpan(0, read);

                if (head.Length < headCap)
                {
                    var headChunk = Math.Min(headCap - head.Length, span.Length);
                    head.Append(span[..headChunk]);
                    span = span[headChunk..];
                }

                if (span.IsEmpty || tailCap == 0)
                    continue;

                tailBuf ??= new char[tailCap];
                AppendToTailRing(tailBuf, span, ref tailStart, ref tailLen);
            }
        }
        finally
        {
            // clearArray: the scratch buffer held raw output (possibly secrets);
            // wipe it before returning to the shared pool.
            ArrayPool<char>.Shared.Return(buf, clearArray: true);
        }

        // Truncation only when total chars exceeded the full budget (head+tail),
        // meaning some middle chars were discarded.
        var truncated = totalChars > budget;

        // Reconstruct in place on `head`: when truncated, the discarded middle is
        // marked with a separator; otherwise head + tail is the full output.
        if (truncated)
            head.Append(Separator);
        if (tailBuf is not null && tailLen > 0)
            AppendRing(head, tailBuf, tailStart, tailLen);
        return (head.ToString(), truncated);
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

    // Separator marking the discarded middle of a truncated head+tail window.
    // Uses the platform newline so it matches the line endings the rest of the
    // captured output is assembled with (e.g. StringBuilder.AppendLine).
    private static readonly string Separator = $"{Environment.NewLine}...{Environment.NewLine}";

    /// <summary>
    /// Writes <paramref name="span"/> into a ring buffer that retains only the
    /// most recent <c>ring.Length</c> chars. Uses block copies (at most two per
    /// call) rather than a per-char loop, so draining a very chatty child stays
    /// cheap regardless of how much it prints.
    /// </summary>
    private static void AppendToTailRing(char[] ring, ReadOnlySpan<char> span, ref int start, ref int len)
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

    /// <summary>Appends a ring buffer's retained chars, oldest-first, to <paramref name="sb"/>.</summary>
    private static void AppendRing(StringBuilder sb, char[] ring, int start, int len)
    {
        var first = Math.Min(len, ring.Length - start);
        sb.Append(ring, start, first);
        if (first < len)
            sb.Append(ring, 0, len - first);
    }
}
