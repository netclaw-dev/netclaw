// -----------------------------------------------------------------------
// <copyright file="LogSafe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;

namespace Netclaw.Security;

/// <summary>
/// Makes external text safe for a single log line. Call this at every point where a value
/// from a request, a route, a config file, or a remote server reaches a log message.
/// </summary>
/// <remarks>
/// The threat is log forging (CWE-117). A newline in a logged value lets the sender add a
/// line of their own to the log file, so a later reader sees an entry that the daemon never
/// wrote. Structured logging does not escape interpolated values, so the newline survives.
/// </remarks>
public static class LogSafe
{
    /// <summary>
    /// The default maximum output length. A hostile peer can supply a very large value, and
    /// the log line does not need it.
    /// </summary>
    public const int DefaultMaxChars = 200;

    // Returns true for chars that act as a line terminator for at least one downstream
    // consumer. char.IsControl covers Cc (CR, LF, NUL, ESC). U+2028 LINE SEPARATOR and
    // U+2029 PARAGRAPH SEPARATOR are Zl and Zp, so IsControl misses them, but JSON-line
    // readers and log shippers still split on them.
    private static bool IsLineBreak(char c) => char.IsControl(c) || c is '\u2028' or '\u2029';

    /// <summary>
    /// Removes line-breaking characters from <paramref name="value"/> and trims the result to
    /// <paramref name="maxChars"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxChars"/> is zero or negative. A zero limit indexes before the first
    /// character, and an empty result is never what a caller wants from a sanitizer.
    /// </exception>
    public static string Text(string? value, int maxChars = DefaultMaxChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);

        if (string.IsNullOrEmpty(value))
            return string.Empty;

        // Fast path: no allocation when the value is already clean and short.
        var hasBreak = false;
        foreach (var ch in value)
        {
            if (IsLineBreak(ch))
            {
                hasBreak = true;
                break;
            }
        }

        if (!hasBreak)
            return TrimAtCharBoundary(value, maxChars);

        // Strip, do not replace with a space. A space would let a CR/LF payload still act as
        // a plausible field separator to a key=value log parser:
        // "reason=ok\nlevel=critical" must not become "reason=ok level=critical".
        var buffer = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!IsLineBreak(ch))
                buffer.Append(ch);
        }

        return TrimAtCharBoundary(buffer.ToString(), maxChars);
    }

    private static string TrimAtCharBoundary(string value, int maxChars)
    {
        if (value.Length <= maxChars)
            return value;

        // Do not split a surrogate pair at the cut. A dangling high surrogate makes
        // downstream UTF-8 encoders emit U+FFFD or throw.
        var cut = maxChars;
        if (char.IsHighSurrogate(value[cut - 1]))
            cut -= 1;
        return value[..cut];
    }
}
