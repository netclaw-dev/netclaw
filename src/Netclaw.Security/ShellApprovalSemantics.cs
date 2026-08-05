// -----------------------------------------------------------------------
// <copyright file="ShellApprovalSemantics.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using ShellSyntaxTree;

namespace Netclaw.Security;

internal interface IShellApprovalSemantics
{
    IReadOnlyList<string> SplitCompoundCommand(string command);

    string ExtractVerbChain(string command, int maxDepth);

    string? ExtractFirstPathArgument(string command);

    IReadOnlyList<string> ExtractInnerCommands(string command);

    bool LooksLikePath(string token);

    string NormalizeApprovalUnit(string command, string? workingDirectory);

    string? NormalizePathToken(string path, string? workingDirectory);
}

internal static class ShellApprovalSemantics
{
    private static readonly IShellApprovalSemantics Posix = PosixShellApprovalSemantics.Instance;
    private static readonly IShellApprovalSemantics Windows = WindowsShellApprovalSemantics.Instance;

    public static IShellApprovalSemantics Current { get; } = OperatingSystem.IsWindows()
        ? Windows
        : Posix;

    public static IShellApprovalSemantics ForCommand(string? command)
    {
        if (!OperatingSystem.IsWindows())
            return Posix;

        if (string.IsNullOrWhiteSpace(command))
            return Windows;

        var tokens = ShellTokenizer.Tokenize(command).ToList();
        if (tokens.Count == 0)
            return Windows;

        var first = ShellTokenizer.TrimShellPunctuation(tokens[0]);
        if (PosixShellApprovalSemantics.IsPosixShellInvoker(first))
            return Posix;

        if (WindowsShellApprovalSemantics.IsWindowsShellInvoker(first))
            return Windows;

        foreach (var token in tokens)
        {
            var trimmed = ShellTokenizer.TrimShellPunctuation(token);
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('-'))
                continue;

            if (LooksLikePosixCommandPath(trimmed))
                return Posix;

            if (Windows.LooksLikePath(trimmed))
                return Windows;
        }

        return Windows;
    }

    private static bool LooksLikePosixCommandPath(string token)
    {
        if (token.Contains("://", StringComparison.Ordinal))
            return false;

        if (token.Equals("/c", StringComparison.OrdinalIgnoreCase)
            || token.Equals("/k", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Posix.LooksLikePath(token);
    }
}

internal abstract class ShellApprovalSemanticsBase : IShellApprovalSemantics
{
    public abstract IReadOnlyList<string> SplitCompoundCommand(string command);

    public abstract IReadOnlyList<string> ExtractInnerCommands(string command);

    public abstract bool LooksLikePath(string token);

    protected abstract bool IsShellSeparator(char ch);

    protected abstract bool IsAnchoredPath(string token);

    public string ExtractVerbChain(string command, int maxDepth)
    {
        if (string.IsNullOrWhiteSpace(command))
            return string.Empty;

        // Greedy verb-chain extraction via ShellSyntaxTree's BashParser:
        // extends through every "verb-like" token (no slash, no dot, no
        // flag prefix) until it hits a path or flag. This makes
        // multi-token CLI subcommands like `freshdesk ticket list`,
        // `git worktree list`, and `git push origin main` extract their
        // full chain so the approval prompt and persisted patterns stay
        // narrow and operator-meaningful.
        var greedy = TryGreedyExtract(command);
        if (string.IsNullOrEmpty(greedy))
            return string.Empty;

        // Apply the path-aware/side-effect short-circuit at depth 1.
        // BashParser doesn't know that grep/cat/find/ls take a search
        // pattern or path as their first positional arg (not a
        // subcommand), so left to its own devices it would bake the
        // call-specific arg into the verb chain — `grep secret`
        // instead of `grep`, `echo done` instead of `echo`. The
        // post-check restores the v2 invariant for these verb
        // families.
        var shortCircuited = ShellTokenizer.ApplyVerbShortCircuit(greedy);
        if (!string.Equals(shortCircuited, greedy, StringComparison.Ordinal))
            return shortCircuited;

        // Honor the explicit maxDepth cap as a hard upper bound so
        // callers asking for fewer tokens still get them. Default
        // callers pass int.MaxValue (effectively "no cap beyond
        // greedy + path-aware short-circuit").
        if (maxDepth <= 0)
            return string.Empty;

        var parts = greedy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= maxDepth)
            return greedy;

        return string.Join(' ', parts.Take(maxDepth));
    }

    private static string TryGreedyExtract(string command)
    {
        try
        {
            var parser = new BashParser();
            var result = parser.Parse(command);
            if (result.IsUnparseable || result.Clauses.Count == 0)
                return string.Empty;

            return result.Clauses[0].Verb.Joined ?? string.Empty;
        }
        catch
        {
            // Defensive: malformed input that slips past IsUnparseable
            // shouldn't take down the approval flow. Fail-empty so the
            // caller treats this as a messy command (no patterns
            // proposed, Once-only prompt).
            return string.Empty;
        }
    }

    public string? ExtractFirstPathArgument(string command)
    {
        // Per the path-extraction spec, only conservatively path-anchored
        // tokens count: starts with /, ~/, ./, ../ or equals ~, ., ..
        // Tokens that merely contain a slash (URLs, regexes, docker tags,
        // git refs) are intentionally NOT extracted — false positives here
        // would silently expand or contract trust scope.
        foreach (var token in ShellTokenizer.Tokenize(command))
        {
            var trimmed = ShellTokenizer.TrimShellPunctuation(token);
            if (trimmed.Length == 0)
                continue;
            if (trimmed.StartsWith('-'))
                continue;
            if (ShellTokenizer.IsPathToken(trimmed))
                return ShellTokenizer.ApplyFileParentRule(trimmed);
        }

        return null;
    }

    public string NormalizeApprovalUnit(string command, string? workingDirectory)
    {
        var tokens = ShellTokenizer.Tokenize(command).ToList();
        if (tokens.Count == 0)
            return string.Empty;

        var normalizedTokens = new List<string>(tokens.Count);
        foreach (var token in tokens)
        {
            if (!LooksLikePath(token))
            {
                normalizedTokens.Add(token);
                continue;
            }

            var normalized = NormalizePathToken(token, workingDirectory);
            normalizedTokens.Add(normalized ?? token);
        }

        return string.Join(' ', normalizedTokens);
    }

    public virtual string? NormalizePathToken(string path, string? workingDirectory)
        => PathUtility.ExpandAndNormalize(path, workingDirectory);

    protected virtual bool LooksLikeArgument(string token)
    {
        return ContainsShellPathSeparator(token)
            || token.StartsWith('~')
            || token.StartsWith('.')
            || token.Contains("://", StringComparison.Ordinal)
            || token.Contains(':', StringComparison.Ordinal)
            || token.StartsWith('$')
            || token.StartsWith('%')
            || token.Contains('*', StringComparison.Ordinal);
    }

    protected bool ContainsShellPathSeparator(string token)
    {
        foreach (var ch in token)
        {
            if (IsShellSeparator(ch))
                return true;
        }

        return false;
    }

    protected bool HasTraversalComponent(string token)
    {
        return token.Contains("/../", StringComparison.Ordinal)
            || token.EndsWith("/..", StringComparison.Ordinal)
            || token.Contains("\\..\\", StringComparison.Ordinal)
            || token.EndsWith("\\..", StringComparison.Ordinal);
    }

    protected static bool HasFileExtensionInLastComponent(string token)
    {
        var lastComponent = Path.GetFileName(token);
        if (string.IsNullOrWhiteSpace(lastComponent))
            return false;

        var ext = Path.GetExtension(lastComponent);
        return ext.Length > 1;
    }

    protected int GetFirstShellSeparatorIndex(string token)
    {
        for (var i = 0; i < token.Length; i++)
        {
            if (IsShellSeparator(token[i]))
                return i;
        }

        return -1;
    }

    protected static IReadOnlyList<string> SplitCompoundCommand(string command, bool splitOnSemicolon, bool splitOnSingleAmpersand)
    {
        if (string.IsNullOrWhiteSpace(command))
            return [];

        var segments = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var span = command.AsSpan();

        for (var i = 0; i < span.Length; i++)
        {
            var ch = span[i];

            if (quote is null && (ch == '\'' || ch == '"'))
            {
                quote = ch;
                current.Append(ch);
                continue;
            }

            if (quote is not null && ch == quote)
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

            if (i + 1 < span.Length)
            {
                var twoChar = span.Slice(i, 2);
                if (twoChar is "&&" or "||")
                {
                    FlushSegment(current, segments);
                    i++;
                    continue;
                }
            }

            if (splitOnSingleAmpersand && ch == '&')
            {
                FlushSegment(current, segments);
                continue;
            }

            if (splitOnSemicolon && ch == ';')
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
}

internal sealed class PosixShellApprovalSemantics : ShellApprovalSemanticsBase
{
    public static readonly PosixShellApprovalSemantics Instance = new();

    public override IReadOnlyList<string> SplitCompoundCommand(string command)
        => SplitCompoundCommand(command, splitOnSemicolon: true, splitOnSingleAmpersand: false);

    public override IReadOnlyList<string> ExtractInnerCommands(string command)
    {
        var tokens = ShellTokenizer.Tokenize(command).ToList();
        var results = new List<string>();

        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var verb = ShellTokenizer.TrimShellPunctuation(tokens[i]);
            if (!IsPosixShellInvoker(verb))
                continue;

            for (var j = i + 1; j < tokens.Count - 1; j++)
            {
                if (!IsShellCommandFlag(tokens[j]))
                    continue;

                results.Add(tokens[j + 1]);
                break;
            }
        }

        return results;
    }

    public override bool LooksLikePath(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.StartsWith('-'))
            return false;

        if (token.Contains("://", StringComparison.Ordinal))
            return false;

        if (IsAnchoredPath(token))
            return true;

        if (!ContainsShellPathSeparator(token))
            return false;

        var firstSlash = GetFirstShellSeparatorIndex(token);
        if (token.IndexOf(':', StringComparison.Ordinal) is var colonIdx && colonIdx >= 0 && colonIdx < firstSlash)
            return false;

        if (token.StartsWith('@') && token.IndexOf('/', 1) == token.LastIndexOf('/'))
            return false;

        if ((token.StartsWith("s/", StringComparison.Ordinal) || token.StartsWith("y/", StringComparison.Ordinal))
            && CountChar(token, '/') >= 3)
        {
            return false;
        }

        return HasTraversalComponent(token) || HasFileExtensionInLastComponent(token);
    }

    public override string? NormalizePathToken(string path, string? workingDirectory)
    {
        var expanded = PathUtility.ExpandHome(path);

        if (LooksLikePosixAbsoluteShellPath(expanded))
            return NormalizePosixShellPath(expanded);

        return PathUtility.ExpandAndNormalize(expanded, workingDirectory);
    }

    protected override bool IsShellSeparator(char ch) => ch == '/';

    protected override bool IsAnchoredPath(string token)
    {
        return token.Length > 0 && token[0] == '/'
            || token.StartsWith("./", StringComparison.Ordinal)
            || token.StartsWith("../", StringComparison.Ordinal)
            || token.StartsWith('~')
            || token.StartsWith("$HOME", StringComparison.Ordinal)
            || token.StartsWith("${HOME}", StringComparison.Ordinal);
    }

    internal static bool IsPosixShellInvoker(string verb)
    {
        return verb is "bash" or "sh" or "/bin/bash" or "/bin/sh"
            or "/usr/bin/bash" or "/usr/bin/sh" or "zsh" or "/bin/zsh";
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

    private static bool IsShellCommandFlag(string token)
    {
        if (token.Length == 0 || token[0] != '-' || token.StartsWith("--", StringComparison.Ordinal))
            return false;

        return token.AsSpan(1).IndexOf('c') >= 0;
    }

    private static int CountChar(string value, char target)
    {
        var count = 0;
        foreach (var c in value)
        {
            if (c == target)
                count++;
        }

        return count;
    }
}

internal sealed class WindowsShellApprovalSemantics : ShellApprovalSemanticsBase
{
    public static readonly WindowsShellApprovalSemantics Instance = new();

    public override IReadOnlyList<string> SplitCompoundCommand(string command)
        // Windows approval splitting handles both cmd.exe control operators (`&`, `&&`, `||`)
        // and PowerShell's `;` because nested PowerShell invocations are common under `cmd /c`.
        => SplitCompoundCommand(command, splitOnSemicolon: true, splitOnSingleAmpersand: true);

    public override IReadOnlyList<string> ExtractInnerCommands(string command)
    {
        var tokens = ShellTokenizer.Tokenize(command).ToList();
        var results = new List<string>();

        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var verb = ShellTokenizer.TrimShellPunctuation(tokens[i]);
            if (IsCmdInvoker(verb))
            {
                if (i + 1 < tokens.Count && IsCmdCommandFlag(tokens[i + 1]) && i + 2 < tokens.Count)
                    results.Add(tokens[i + 2]);

                continue;
            }

            if (IsPowerShellInvoker(verb)
                && i + 1 < tokens.Count
                && IsPowerShellCommandFlag(tokens[i + 1])
                && i + 2 < tokens.Count)
            {
                results.Add(tokens[i + 2]);
            }
        }

        return results;
    }

    public override bool LooksLikePath(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.StartsWith('-'))
            return false;

        if (token.Contains("://", StringComparison.Ordinal))
            return false;

        if (IsAnchoredPath(token))
            return true;

        if (!ContainsShellPathSeparator(token))
            return false;

        var firstSeparator = GetFirstShellSeparatorIndex(token);
        if (token.IndexOf(':', StringComparison.Ordinal) is var colonIdx && colonIdx >= 0 && colonIdx < firstSeparator)
        {
            var isDrivePrefix = colonIdx == 1 && char.IsAsciiLetter(token[0]);
            if (!isDrivePrefix)
                return false;
        }

        if (token.StartsWith('@') && token.IndexOf('/', 1) == token.LastIndexOf('/'))
            return false;

        if ((token.StartsWith("s/", StringComparison.Ordinal) || token.StartsWith("y/", StringComparison.Ordinal))
            && CountChar(token, '/') >= 3)
        {
            return false;
        }

        if (token.Contains('\\', StringComparison.Ordinal))
            return true;

        return HasTraversalComponent(token) || HasFileExtensionInLastComponent(token);
    }

    protected override bool IsShellSeparator(char ch) => ch is '/' or '\\';

    protected override bool IsAnchoredPath(string token)
    {
        return IsWindowsRootedPath(token)
            || token.StartsWith("./", StringComparison.Ordinal)
            || token.StartsWith("../", StringComparison.Ordinal)
            || token.StartsWith(@".\", StringComparison.Ordinal)
            || token.StartsWith(@"..\", StringComparison.Ordinal)
            || token.StartsWith('~')
            || token.StartsWith("$HOME", StringComparison.Ordinal)
            || token.StartsWith("${HOME}", StringComparison.Ordinal)
            || token.StartsWith("%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    public override string? NormalizePathToken(string path, string? workingDirectory)
    {
        var expanded = PathUtility.ExpandHome(path);

        return PathUtility.ExpandAndNormalize(expanded, workingDirectory);
    }

    internal static bool IsWindowsShellInvoker(string verb)
    {
        return IsCmdInvoker(verb) || IsPowerShellInvoker(verb);
    }

    private static bool IsCmdInvoker(string verb)
        => verb.Equals("cmd", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindowsRootedPath(string token)
    {
        if (token.StartsWith("\\\\", StringComparison.Ordinal))
            return true;

        return token.Length >= 3
            && char.IsAsciiLetter(token[0])
            && token[1] == ':'
            && (token[2] == '\\' || token[2] == '/');
    }

    private static bool IsPowerShellInvoker(string verb)
    {
        return verb.Equals("powershell", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCmdCommandFlag(string token)
    {
        return token.Equals("/c", StringComparison.OrdinalIgnoreCase)
            || token.Equals("/k", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPowerShellCommandFlag(string token)
    {
        return token.Equals("-c", StringComparison.OrdinalIgnoreCase)
            || token.Equals("-command", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountChar(string value, char target)
    {
        var count = 0;
        foreach (var c in value)
        {
            if (c == target)
                count++;
        }

        return count;
    }
}
