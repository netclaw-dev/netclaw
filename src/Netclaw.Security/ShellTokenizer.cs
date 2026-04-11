using System.Text;

namespace Netclaw.Security;

/// <summary>
/// Shared tokenizer for shell command strings. Extracts tokens from commands,
/// splits compound commands on operators, and recursively extracts inner
/// commands from bash -c / sh -c wrappers.
/// </summary>
public static class ShellTokenizer
{
    private static readonly string[] CompoundOperators = ["&&", "||"];

    /// <summary>
    /// Tokenizes a shell command string, respecting single and double quotes.
    /// Strips quote delimiters from tokens.
    /// </summary>
    public static IEnumerable<string> Tokenize(string command)
    {
        var current = new StringBuilder();
        char? quote = null;

        foreach (var ch in command)
        {
            if (quote is null && (ch == '\'' || ch == '"'))
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

    /// <summary>
    /// Splits a compound command on <c>&&</c>, <c>||</c>, <c>;</c>, and <c>|</c>
    /// operators, returning each individual command segment trimmed.
    /// Pipe (<c>|</c>) is treated as a segment boundary because each side
    /// may invoke a different program.
    /// </summary>
    public static IReadOnlyList<string> SplitCompoundCommand(string command)
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

            // Track quoting so we don't split inside strings
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

            // Check for two-char operators: && and ||
            if (i + 1 < span.Length)
            {
                var twoChar = span.Slice(i, 2);
                if (twoChar is "&&" or "||")
                {
                    FlushSegment(current, segments);
                    i++; // skip second char
                    continue;
                }
            }

            // Single-char operators: ; and |
            if (ch is ';' or '|')
            {
                FlushSegment(current, segments);
                continue;
            }

            current.Append(ch);
        }

        FlushSegment(current, segments);
        return segments;
    }

    /// <summary>
    /// Extracts the verb chain (command name + subcommands) from a tokenized
    /// command. Stops at the first token that looks like a flag (starts with -)
    /// or an argument (path, URL, etc.), and caps at <paramref name="maxDepth"/>
    /// tokens (default: 2) to avoid capturing positional arguments as subcommands.
    /// </summary>
    public static string ExtractVerbChain(string command, int maxDepth = 2)
    {
        var tokens = Tokenize(command).ToList();
        if (tokens.Count == 0)
            return string.Empty;

        var verbParts = new List<string>();
        foreach (var token in tokens)
        {
            if (verbParts.Count >= maxDepth)
                break;

            var trimmed = TrimShellPunctuation(token);
            if (trimmed.Length == 0)
                continue;

            if (trimmed.StartsWith('-'))
                break;

            if (LooksLikeArgument(trimmed))
                break;

            verbParts.Add(trimmed);
        }

        return string.Join(' ', verbParts);
    }

    /// <summary>
    /// Extracts inner commands from bash -c / sh -c wrappers. Returns the
    /// inner command strings for recursive scanning. Returns an empty list
    /// if the command does not use a shell wrapper.
    /// </summary>
    public static IReadOnlyList<string> ExtractInnerCommands(string command)
    {
        var tokens = Tokenize(command).ToList();
        var results = new List<string>();

        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var verb = TrimShellPunctuation(tokens[i]);
            if (!IsShellInvoker(verb))
                continue;

            // Look for -c flag
            if (i + 1 < tokens.Count && IsShellCommandFlag(tokens[i + 1]) && i + 2 < tokens.Count)
            {
                results.Add(tokens[i + 2]);
            }
        }

        return results;
    }

    /// <summary>
    /// Returns all command strings that should be evaluated, including the
    /// top-level compound segments and any recursively extracted inner commands
    /// from bash -c / sh -c wrappers.
    /// </summary>
    public static IReadOnlyList<string> GetAllCommandSegments(string command)
    {
        var allSegments = new List<string>();
        var topLevel = SplitCompoundCommand(command);

        foreach (var segment in topLevel)
        {
            allSegments.Add(segment);

            var innerCommands = ExtractInnerCommands(segment);
            foreach (var inner in innerCommands)
            {
                // Recursively get segments from inner commands
                allSegments.AddRange(GetAllCommandSegments(inner));
            }
        }

        return allSegments;
    }

    private static bool IsShellInvoker(string verb)
    {
        return verb is "bash" or "sh" or "/bin/bash" or "/bin/sh"
            or "/usr/bin/bash" or "/usr/bin/sh" or "zsh" or "/bin/zsh";
    }

    private static bool IsShellCommandFlag(string token)
    {
        if (token.Length == 0 || token[0] != '-' || token.StartsWith("--", StringComparison.Ordinal))
            return false;

        return token.AsSpan(1).IndexOf('c') >= 0;
    }

    private static bool LooksLikeArgument(string token)
    {
        // Paths, URLs, filenames, dotfiles, home-relative
        return token.Contains('/', StringComparison.Ordinal)
            || token.Contains('\\', StringComparison.Ordinal)
            || token.StartsWith('~')
            || token.StartsWith('.')
            || token.Contains("://", StringComparison.Ordinal)
            || token.Contains(':', StringComparison.Ordinal)
            // Environment variable references
            || token.StartsWith('$')
            // Glob patterns
            || token.Contains('*', StringComparison.Ordinal);
    }

    internal static string TrimShellPunctuation(string token)
    {
        return token.Trim().TrimStart(';', '|', '&').TrimEnd(';', '|', '&');
    }

    private static void FlushSegment(StringBuilder current, List<string> segments)
    {
        var trimmed = current.ToString().Trim();
        if (trimmed.Length > 0)
            segments.Add(trimmed);
        current.Clear();
    }
}
