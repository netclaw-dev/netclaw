// -----------------------------------------------------------------------
// <copyright file="LegacyShellTextScan.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;

namespace Netclaw.Security;

/// <summary>
/// Raw-text helpers for shell text. They serve the hard-deny scan of unresolved
/// input, the protected-path text check of every command, the parse of configured
/// <c>HardDenyPatterns</c>, quote removal on approval-unit words, and a
/// punctuation trim. ShellSyntaxTree supplies every other shell fact.
/// </summary>
/// <remarks>
/// Owner decision (2026-09-30, authorization PR 4): this class stays as policy for
/// input that the parser cannot read. The raw-text scan decides first, so every
/// denial of the earlier releases stays. A parser screen then checks each Bash
/// list element (the stricter cases were kept; the looser cases were rejected).
/// None of these helpers can allow a command, create an approval candidate, or
/// create a reusable grant.
/// </remarks>
internal static class LegacyShellTextScan
{
    private static readonly HashSet<string> BashControlFlowKeywords = new(StringComparer.Ordinal)
    {
        "for", "while", "do", "done", "then", "fi", "case", "esac"
    };

    /// <summary>Splits text on whitespace outside quotes and removes the quote marks.</summary>
    internal static IEnumerable<string> Tokenize(string command)
    {
        var current = new StringBuilder();
        char? quote = null;

        foreach (var ch in command)
        {
            if (quote is null && ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (quote is not null && ch == quote)
            {
                quote = null;
                continue;
            }

            if (quote is null && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    internal static string TrimShellPunctuation(string token)
        => token.Trim().TrimStart(';', '|', '&').TrimEnd(';', '|', '&');

    /// <summary>
    /// Returns every list element and every child command of a wrapper, in
    /// source order. Text with a control-flow keyword or an unbalanced quote or
    /// bracket has no elements.
    /// </summary>
    internal static IReadOnlyList<string> GetAllCommandSegments(string command)
    {
        var segments = new List<string>();
        if (IsMessyCompoundCommand(command))
            return segments;

        foreach (var segment in SplitCompoundCommand(command, ForCommand(command)))
        {
            segments.Add(segment);
            foreach (var inner in ExtractInnerCommands(segment, ForCommand(segment)))
                segments.AddRange(GetAllCommandSegments(inner));
        }

        return segments;
    }

    private static bool IsMessyCompoundCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        char? quote = null;
        var parens = 0;
        var brackets = 0;
        var braces = 0;
        var word = new StringBuilder();

        foreach (var ch in command)
        {
            if (quote is null && ch is '\'' or '"')
            {
                if (FlushWordAsKeyword(word)) return true;
                quote = ch;
                continue;
            }

            if (quote == ch)
            {
                quote = null;
                continue;
            }

            if (quote is not null)
                continue;

            switch (ch)
            {
                case '(':
                    parens++;
                    if (FlushWordAsKeyword(word)) return true;
                    continue;
                case ')':
                    if (--parens < 0 || FlushWordAsKeyword(word)) return true;
                    continue;
                case '[':
                    brackets++;
                    if (FlushWordAsKeyword(word)) return true;
                    continue;
                case ']':
                    if (--brackets < 0 || FlushWordAsKeyword(word)) return true;
                    continue;
                case '{':
                    braces++;
                    if (FlushWordAsKeyword(word)) return true;
                    continue;
                case '}':
                    if (--braces < 0 || FlushWordAsKeyword(word)) return true;
                    continue;
            }

            if (char.IsWhiteSpace(ch) || ch is ';' or '|' or '&')
            {
                if (FlushWordAsKeyword(word)) return true;
                continue;
            }

            word.Append(ch);
        }

        if (FlushWordAsKeyword(word)) return true;

        return quote is not null || parens != 0 || brackets != 0 || braces != 0;
    }

    private static bool FlushWordAsKeyword(StringBuilder word)
    {
        if (word.Length == 0)
            return false;

        var match = BashControlFlowKeywords.Contains(word.ToString());
        word.Clear();
        return match;
    }

    private static List<string> SplitCompoundCommand(string command, ShellPathStyle pathStyle)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var span = command.AsSpan();

        for (var i = 0; i < span.Length; i++)
        {
            var ch = span[i];
            if (quote is null && ch is '\'' or '"')
            {
                quote = ch;
            }
            else if (quote == ch)
            {
                quote = null;
            }
            else if (quote is null && i + 1 < span.Length && span.Slice(i, 2) is "&&" or "||")
            {
                FlushSegment(current, segments);
                i++;
                continue;
            }
            else if (quote is null && (ch == ';' || pathStyle == ShellPathStyle.Windows && ch == '&'))
            {
                FlushSegment(current, segments);
                continue;
            }

            current.Append(ch);
        }

        FlushSegment(current, segments);
        return segments;
    }

    private static void FlushSegment(StringBuilder current, List<string> segments)
    {
        var trimmed = current.ToString().Trim();
        if (trimmed.Length > 0)
            segments.Add(trimmed);

        current.Clear();
    }

    private static List<string> ExtractInnerCommands(string command, ShellPathStyle pathStyle)
    {
        var tokens = Tokenize(command).ToList();
        var results = new List<string>();
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var verb = TrimShellPunctuation(tokens[i]);
            if (pathStyle == ShellPathStyle.Posix)
            {
                if (!ShellVerbPolicyData.PosixShellInvokers.Contains(verb))
                    continue;

                for (var j = i + 1; j < tokens.Count - 1; j++)
                {
                    if (tokens[j].Length > 1
                        && tokens[j][0] == '-'
                        && !tokens[j].StartsWith("--", StringComparison.Ordinal)
                        && tokens[j].AsSpan(1).IndexOf('c') >= 0)
                    {
                        results.Add(tokens[j + 1]);
                        break;
                    }
                }
            }
            else if (IsCmdInvoker(verb))
            {
                if (i + 2 < tokens.Count && tokens[i + 1] is "/c" or "/C" or "/k" or "/K")
                    results.Add(tokens[i + 2]);
            }
            else if (i + 2 < tokens.Count
                     && IsPowerShellInvoker(verb)
                     && (tokens[i + 1].Equals("-c", StringComparison.OrdinalIgnoreCase)
                         || tokens[i + 1].Equals("-command", StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(tokens[i + 2]);
            }
        }

        return results;
    }

    /// <summary>
    /// Selects the path style of raw text: POSIX on a POSIX host; on Windows,
    /// from the first shell word or the first path-like word.
    /// </summary>
    private static ShellPathStyle ForCommand(string command)
    {
        if (!OperatingSystem.IsWindows())
            return ShellPathStyle.Posix;

        var tokens = Tokenize(command).ToList();
        if (tokens.Count == 0)
            return ShellPathStyle.Windows;

        var first = TrimShellPunctuation(tokens[0]);
        if (ShellVerbPolicyData.PosixShellInvokers.Contains(first))
            return ShellPathStyle.Posix;

        if (IsCmdInvoker(first) || IsPowerShellInvoker(first))
            return ShellPathStyle.Windows;

        foreach (var token in tokens)
        {
            var trimmed = TrimShellPunctuation(token);
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('-'))
                continue;

            if (!trimmed.Contains("://", StringComparison.Ordinal)
                && !trimmed.Equals("/c", StringComparison.OrdinalIgnoreCase)
                && !trimmed.Equals("/k", StringComparison.OrdinalIgnoreCase)
                && ShellApprovalMatcher.LooksLikeUnitPath(trimmed, ShellPathStyle.Posix))
            {
                return ShellPathStyle.Posix;
            }

            if (ShellApprovalMatcher.LooksLikeUnitPath(trimmed, ShellPathStyle.Windows))
                return ShellPathStyle.Windows;
        }

        return ShellPathStyle.Windows;
    }

    private static bool IsCmdInvoker(string verb)
        => verb.Equals("cmd", StringComparison.OrdinalIgnoreCase)
           || verb.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsPowerShellInvoker(string verb)
        => verb.Equals("powershell", StringComparison.OrdinalIgnoreCase)
           || verb.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
           || verb.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
           || verb.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);
}
