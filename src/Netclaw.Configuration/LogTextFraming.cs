// -----------------------------------------------------------------------
// <copyright file="LogTextFraming.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------

namespace Netclaw.Configuration;

/// <summary>
/// Keeps untrusted text from forging a record in a text log (CWE-117). The daemon log and the
/// session logs share it, so one rule decides what a record boundary looks like.
/// </summary>
public static class LogTextFraming
{
    /// <summary>
    /// Starts every line of a record after the first. Each record header starts in column 0,
    /// with a digit in the daemon log (<c>17:00:38.175 [INF]</c>) or with <c>[</c> in a session
    /// transcript (<c>[2026-10-29T17:00:38.1750000+00:00] User:</c>), so a line that starts
    /// with spaces cannot be read as a new record.
    /// </summary>
    public const string ContinuationPrefix = "  ";

    /// <summary>
    /// Turns every line break (CR, LF, CRLF, NEL, FF, U+2028, U+2029) into
    /// <see cref="Environment.NewLine"/>, and escapes each other control character except tab,
    /// and each bidi control, as <c>\uXXXX</c>. Escaping makes terminal escape sequences and
    /// bidi reordering inert for anyone who reads the log.
    /// </summary>
    public static string Normalize(string text)
        => TextControlCharacters.EscapeAsUnicode(text.ReplaceLineEndings(Environment.NewLine), IsUnsafeInLog);

    /// <summary>
    /// Normalizes <paramref name="text"/> and puts <see cref="ContinuationPrefix"/> at the start of
    /// every line after the first.
    /// </summary>
    public static string Frame(string text)
    {
        var normalized = Normalize(text);
        return normalized.Contains(Environment.NewLine, StringComparison.Ordinal)
            ? normalized.Replace(Environment.NewLine, Environment.NewLine + ContinuationPrefix, StringComparison.Ordinal)
            : normalized;
    }

    private static bool IsUnsafeInLog(char character)
        => (char.IsControl(character) && character is not '\t' and not '\r' and not '\n')
            || TextControlCharacters.IsBidiControl(character);
}
