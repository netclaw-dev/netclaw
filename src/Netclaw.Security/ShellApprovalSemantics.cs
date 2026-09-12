// -----------------------------------------------------------------------
// <copyright file="ShellApprovalSemantics.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using ShellSyntaxTree;

namespace Netclaw.Security;

internal static class ShellApprovalSemantics
{
    internal static ShellPathStyle ForCommand(string? command)
    {
        if (!OperatingSystem.IsWindows())
            return ShellPathStyle.Posix;

        if (string.IsNullOrWhiteSpace(command))
            return ShellPathStyle.Windows;

        var tokens = ShellTokenizer.Tokenize(command).ToList();
        if (tokens.Count == 0)
            return ShellPathStyle.Windows;

        var first = ShellTokenizer.TrimShellPunctuation(tokens[0]);
        if (IsPosixShellInvoker(first))
            return ShellPathStyle.Posix;

        if (IsWindowsShellInvoker(first))
            return ShellPathStyle.Windows;

        foreach (var token in tokens)
        {
            var trimmed = ShellTokenizer.TrimShellPunctuation(token);
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('-'))
                continue;

            if (LooksLikePosixCommandPath(trimmed))
                return ShellPathStyle.Posix;

            if (LooksLikePath(trimmed, ShellPathStyle.Windows))
                return ShellPathStyle.Windows;
        }

        return ShellPathStyle.Windows;
    }

    internal static IReadOnlyList<string> SplitCompoundCommand(
        string command,
        ShellPathStyle pathStyle)
    {
        var splitOnSingleAmpersand = IsWindows(pathStyle);
        if (string.IsNullOrWhiteSpace(command))
            return [];

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
                current.Append(ch);
                continue;
            }

            if (quote == ch)
            {
                quote = null;
                current.Append(ch);
                continue;
            }

            if (quote is not null)
            {
                current.Append(ch);
                continue;
            }

            if (i + 1 < span.Length && span.Slice(i, 2) is "&&" or "||")
            {
                FlushSegment(current, segments);
                i++;
                continue;
            }

            if (ch == ';' || splitOnSingleAmpersand && ch == '&')
            {
                FlushSegment(current, segments);
                continue;
            }

            current.Append(ch);
        }

        FlushSegment(current, segments);
        return segments;
    }

    internal static string ExtractVerbChain(string command, int maxDepth)
    {
        if (string.IsNullOrWhiteSpace(command))
            return string.Empty;

        var greedy = TryGreedyExtract(command);
        if (string.IsNullOrEmpty(greedy))
            return string.Empty;

        var shortCircuited = ShellTokenizer.ApplyVerbShortCircuit(greedy);
        if (!string.Equals(shortCircuited, greedy, StringComparison.Ordinal))
            return shortCircuited;

        if (maxDepth <= 0)
            return string.Empty;

        var parts = greedy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= maxDepth
            ? greedy
            : string.Join(' ', parts.Take(maxDepth));
    }

    internal static string? ExtractFirstPathArgument(string command)
    {
        foreach (var token in ShellTokenizer.Tokenize(command))
        {
            var trimmed = ShellTokenizer.TrimShellPunctuation(token);
            if (trimmed.Length > 0
                && !trimmed.StartsWith('-')
                && ShellTokenizer.IsPathToken(trimmed))
            {
                return ShellTokenizer.ApplyFileParentRule(trimmed);
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> ExtractInnerCommands(
        string command,
        ShellPathStyle pathStyle)
    {
        var isWindows = IsWindows(pathStyle);
        var tokens = ShellTokenizer.Tokenize(command).ToList();
        var results = new List<string>();
        if (!isWindows)
        {
            for (var i = 0; i < tokens.Count - 1; i++)
            {
                if (!IsPosixShellInvoker(ShellTokenizer.TrimShellPunctuation(tokens[i])))
                    continue;

                for (var j = i + 1; j < tokens.Count - 1; j++)
                {
                    if (!IsPosixCommandFlag(tokens[j]))
                        continue;

                    results.Add(tokens[j + 1]);
                    break;
                }
            }

            return results;
        }

        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var verb = ShellTokenizer.TrimShellPunctuation(tokens[i]);
            if (IsCmdInvoker(verb))
            {
                if (i + 2 < tokens.Count && IsCmdCommandFlag(tokens[i + 1]))
                    results.Add(tokens[i + 2]);

                continue;
            }

            if (i + 2 < tokens.Count
                && IsPowerShellInvoker(verb)
                && IsPowerShellCommandFlag(tokens[i + 1]))
            {
                results.Add(tokens[i + 2]);
            }
        }

        return results;
    }

    internal static bool LooksLikePath(string token, ShellPathStyle pathStyle)
    {
        var isWindows = IsWindows(pathStyle);
        if (string.IsNullOrWhiteSpace(token)
            || token.StartsWith('-')
            || token.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        if (IsAnchoredPath(token, isWindows))
            return true;

        var firstSeparator = GetFirstShellSeparatorIndex(token, isWindows);
        if (firstSeparator < 0)
            return false;

        var colonIndex = token.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex >= 0 && colonIndex < firstSeparator
            && (!isWindows || colonIndex != 1 || !char.IsAsciiLetter(token[0])))
        {
            return false;
        }

        if (token.StartsWith('@') && token.IndexOf('/', 1) == token.LastIndexOf('/'))
            return false;

        if ((token.StartsWith("s/", StringComparison.Ordinal)
             || token.StartsWith("y/", StringComparison.Ordinal))
            && CountChar(token, '/') >= 3)
        {
            return false;
        }

        if (isWindows && token.Contains('\\', StringComparison.Ordinal))
            return true;

        return HasTraversalComponent(token) || HasFileExtensionInLastComponent(token);
    }

    internal static string NormalizeApprovalUnit(
        string command,
        string? workingDirectory,
        ShellPathStyle pathStyle)
    {
        _ = IsWindows(pathStyle);
        var tokens = ShellTokenizer.Tokenize(command).ToList();
        if (tokens.Count == 0)
            return string.Empty;

        var normalizedTokens = new List<string>(tokens.Count);
        foreach (var token in tokens)
        {
            if (!LooksLikePath(token, pathStyle))
            {
                normalizedTokens.Add(token);
                continue;
            }

            var normalized = NormalizePathToken(token, workingDirectory, pathStyle);
            normalizedTokens.Add(normalized ?? token);
        }

        return string.Join(' ', normalizedTokens);
    }

    internal static string? NormalizePathToken(
        string path,
        string? workingDirectory,
        ShellPathStyle pathStyle)
    {
        var isWindows = IsWindows(pathStyle);
        var expanded = PathUtility.ExpandHome(path);
        if (!isWindows && LooksLikePosixAbsoluteShellPath(expanded))
            return NormalizePosixShellPath(expanded);

        return PathUtility.ExpandAndNormalize(expanded, workingDirectory);
    }

    internal static bool IsPosixShellInvoker(string verb)
    {
        return verb is "bash" or "sh" or "dash" or "ash" or "ksh" or "mksh" or "zsh"
            or "/bin/bash" or "/bin/sh" or "/bin/dash" or "/bin/ash"
            or "/bin/ksh" or "/bin/mksh" or "/bin/zsh"
            or "/usr/bin/bash" or "/usr/bin/sh" or "/usr/bin/dash" or "/usr/bin/ash"
            or "/usr/bin/ksh" or "/usr/bin/mksh" or "/usr/bin/zsh";
    }

    private static bool IsWindows(ShellPathStyle pathStyle) => pathStyle switch
    {
        ShellPathStyle.Posix => false,
        ShellPathStyle.Windows => true,
        _ => throw new ArgumentOutOfRangeException(
            nameof(pathStyle),
            pathStyle,
            "Unknown shell path style.")
    };

    private static string TryGreedyExtract(string command)
    {
        try
        {
            var result = new BashParser().Parse(command);
            return result.IsUnparseable || result.Clauses.Count == 0
                ? string.Empty
                : result.Clauses[0].Verb.Joined ?? string.Empty;
        }
        catch
        {
            // The compatibility API must not derive a reusable phrase from malformed input.
            return string.Empty;
        }
    }

    private static void FlushSegment(StringBuilder current, List<string> segments)
    {
        var trimmed = current.ToString().Trim();
        if (trimmed.Length > 0)
            segments.Add(trimmed);

        current.Clear();
    }

    private static bool LooksLikePosixCommandPath(string token)
    {
        return !token.Contains("://", StringComparison.Ordinal)
               && !token.Equals("/c", StringComparison.OrdinalIgnoreCase)
               && !token.Equals("/k", StringComparison.OrdinalIgnoreCase)
               && LooksLikePath(token, ShellPathStyle.Posix);
    }

    private static bool IsAnchoredPath(string token, bool isWindows)
    {
        return !isWindows && token.Length > 0 && token[0] == '/'
               || IsPortableAnchoredPath(token)
               || isWindows
               && (IsWindowsRootedPath(token)
                   || token.StartsWith(@".\", StringComparison.Ordinal)
                   || token.StartsWith(@"..\", StringComparison.Ordinal)
                   || token.StartsWith("%USERPROFILE%", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPortableAnchoredPath(string token)
    {
        return token.StartsWith("./", StringComparison.Ordinal)
               || token.StartsWith("../", StringComparison.Ordinal)
               || token.StartsWith('~')
               || token.StartsWith("$HOME", StringComparison.Ordinal)
               || token.StartsWith("${HOME}", StringComparison.Ordinal);
    }

    private static int GetFirstShellSeparatorIndex(string token, bool isWindows)
    {
        for (var i = 0; i < token.Length; i++)
        {
            if (token[i] == '/' || isWindows && token[i] == '\\')
                return i;
        }

        return -1;
    }

    private static bool HasTraversalComponent(string token)
    {
        return token.Contains("/../", StringComparison.Ordinal)
               || token.EndsWith("/..", StringComparison.Ordinal)
               || token.Contains("\\..\\", StringComparison.Ordinal)
               || token.EndsWith("\\..", StringComparison.Ordinal);
    }

    private static bool HasFileExtensionInLastComponent(string token)
    {
        var lastComponent = Path.GetFileName(token);
        return !string.IsNullOrWhiteSpace(lastComponent)
               && Path.GetExtension(lastComponent).Length > 1;
    }

    private static bool LooksLikePosixAbsoluteShellPath(string path)
    {
        return path.Length > 0 && path[0] == '/'
               && !path.StartsWith("//", StringComparison.Ordinal)
               && path.IndexOf('\\', StringComparison.Ordinal) < 0
               && !path.Contains("://", StringComparison.Ordinal);
    }

    private static string NormalizePosixShellPath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var normalized = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment == ".")
                continue;

            if (segment == "..")
            {
                if (normalized.Count > 0)
                    normalized.RemoveAt(normalized.Count - 1);

                continue;
            }

            normalized.Add(segment);
        }

        return normalized.Count == 0 ? "/" : "/" + string.Join('/', normalized);
    }

    private static bool IsWindowsShellInvoker(string verb) =>
        IsCmdInvoker(verb) || IsPowerShellInvoker(verb);

    private static bool IsCmdInvoker(string verb) =>
        verb.Equals("cmd", StringComparison.OrdinalIgnoreCase)
        || verb.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsPowerShellInvoker(string verb) =>
        verb.Equals("powershell", StringComparison.OrdinalIgnoreCase)
        || verb.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
        || verb.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
        || verb.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowsRootedPath(string token)
    {
        return token.StartsWith("\\\\", StringComparison.Ordinal)
               || token.Length >= 3
               && char.IsAsciiLetter(token[0])
               && token[1] == ':'
               && token[2] is '\\' or '/';
    }

    private static bool IsPosixCommandFlag(string token)
    {
        return token.Length > 1
               && token[0] == '-'
               && !token.StartsWith("--", StringComparison.Ordinal)
               && token.AsSpan(1).IndexOf('c') >= 0;
    }

    private static bool IsCmdCommandFlag(string token) =>
        token.Equals("/c", StringComparison.OrdinalIgnoreCase)
        || token.Equals("/k", StringComparison.OrdinalIgnoreCase);

    private static bool IsPowerShellCommandFlag(string token) =>
        token.Equals("-c", StringComparison.OrdinalIgnoreCase)
        || token.Equals("-command", StringComparison.OrdinalIgnoreCase);

    private static int CountChar(string value, char target)
    {
        var count = 0;
        foreach (var character in value)
        {
            if (character == target)
                count++;
        }

        return count;
    }
}
