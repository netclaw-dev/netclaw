// -----------------------------------------------------------------------
// <copyright file="ShellCommandAnalysis.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using ShellSyntaxTree;

namespace Netclaw.Security;

/// <summary>
/// Parses Bash commands and expands nested shell command strings.
/// Approval and hard-deny policies share this analysis.
/// </summary>
internal sealed class ShellCommandAnalyzer
{
    private const int MaxWrapperDepth = 8;

    public static readonly ShellCommandAnalyzer Bash = new();

    private ShellCommandAnalyzer()
    {
    }

    public ShellCommandAnalysis Analyze(string command, string? workingDirectory = null)
    {
        var clauses = new List<Clause>();
        var failure = Analyze(command, workingDirectory, depth: 0, clauses);
        return new ShellCommandAnalysis(clauses, failure);
    }

    private static ShellAnalysisFailure Analyze(
        string command,
        string? workingDirectory,
        int depth,
        List<Clause> clauses)
    {
        if (depth > MaxWrapperDepth)
            return ShellAnalysisFailure.Unresolved;

        // ShellSyntaxTree 0.2 does not expose a background-list tail.
        // Treat that parser boundary as unresolved so the tail cannot inherit
        // the first command's safe-verb or stored-approval result.
        if (ContainsBackgroundListOperator(command))
            return ShellAnalysisFailure.Unresolved;

        ParsedCommand parsed;
        try
        {
            var parser = string.IsNullOrWhiteSpace(workingDirectory)
                ? new BashParser()
                : new BashParser(new BashParserOptions { WorkingDirectory = workingDirectory });
            parsed = parser.Parse(command);
        }
        catch
        {
            return ShellAnalysisFailure.Unresolved;
        }

        if (parsed.IsUnparseable || parsed.Clauses.Count == 0)
            return ShellAnalysisFailure.Unresolved;

        var innerCommands = PosixShellApprovalSemantics.Instance.ExtractInnerCommands(command);
        var hasUnexpandedWrapper = parsed.Clauses.Any(IsUnexpandedWrapperClause);
        if (innerCommands.Count == 0 || !hasUnexpandedWrapper)
        {
            clauses.AddRange(parsed.Clauses);
            return ShellAnalysisFailure.None;
        }

        // A direct shell wrapper adds no independent authority scope.
        // Prefix wrappers such as env and timeout remain visible.
        clauses.AddRange(parsed.Clauses.Where(clause =>
            !IsUnexpandedWrapperClause(clause) || HasShellInvokerInArguments(clause)));

        foreach (var innerCommand in innerCommands)
        {
            var failure = Analyze(innerCommand, workingDirectory, depth + 1, clauses);
            if (failure != ShellAnalysisFailure.None)
                return failure;
        }

        return ShellAnalysisFailure.None;
    }

    private static bool IsUnexpandedWrapperClause(Clause clause)
    {
        if (clause.Verb.Tokens.Count == 0 || clause.Args.Count == 0)
            return false;

        if (!clause.Verb.Tokens.Any(IsShellInvokerToken) && !HasShellInvokerInArguments(clause))
            return false;

        return clause.Args.Any(static arg =>
            arg.Raw.Length > 1
            && arg.Raw[0] == '-'
            && !arg.Raw.StartsWith("--", StringComparison.Ordinal)
            && arg.Raw.AsSpan(1).IndexOf('c') >= 0);
    }

    private static bool HasShellInvokerInArguments(Clause clause)
        => clause.Args.Any(static arg =>
            arg.Kind != ArgKind.DynamicSkip && IsShellInvokerToken(arg.Raw));

    private static bool IsShellInvokerToken(string token)
        => PosixShellApprovalSemantics.IsPosixShellInvoker(
            ShellTokenizer.TrimShellPunctuation(token));

    private static bool ContainsBackgroundListOperator(string command)
    {
        char? quote = null;
        var escaped = false;

        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (ch == '\\' && quote != '\'')
            {
                escaped = true;
                continue;
            }

            if (ch is '\'' or '"')
            {
                if (quote is null)
                    quote = ch;
                else if (quote == ch)
                    quote = null;

                continue;
            }

            if (quote is not null || ch != '&')
                continue;

            var previous = i > 0 ? command[i - 1] : '\0';
            var next = i + 1 < command.Length ? command[i + 1] : '\0';
            if (previous is '&' or '>' || next is '&' or '>')
                continue;

            return true;
        }

        return false;
    }
}

internal enum ShellAnalysisFailure
{
    None,
    Unresolved
}

internal static class ShellGlobPath
{
    public static bool HasUnresolvedDescendantScope(Arg arg)
    {
        if (!arg.IsPath || arg.Kind != ArgKind.Glob)
            return false;

        var firstGlob = arg.Raw.IndexOfAny(['*', '?', '[']);
        return firstGlob >= 0
            && arg.Raw.IndexOf('/', firstGlob + 1) >= 0;
    }
}

internal sealed record ShellCommandAnalysis(
    IReadOnlyList<Clause> Clauses,
    ShellAnalysisFailure Failure)
{
    public bool HasDynamicSyntax => Clauses.Any(static clause =>
        clause.Verb.IsDynamic
        || clause.Args.Any(static arg => arg.Kind == ArgKind.DynamicSkip)
        || clause.Args.Any(static arg =>
            arg.IsPath
            && arg.Kind != ArgKind.Glob
            && string.IsNullOrWhiteSpace(arg.Resolved))
        // A glob in a directory segment can hide traversal or a symlink.
        // Only a leaf glob has a fixed directory scope.
        || clause.Args.Any(ShellGlobPath.HasUnresolvedDescendantScope)
        // An fd-dup target (&1, &2, &-) is a static file-descriptor number,
        // not a dynamic token: ShellSyntaxTree marks it IsDynamicSkip to mean
        // "do not path-resolve", but it carries no unresolved syntax and no
        // filesystem scope (ResolveRedirectDirectory skips &-prefixed targets).
        // Treating it as dynamic fails the whole command closed to an approval
        // prompt for every `2>&1`-shaped command, even fully safe ones.
        || clause.Redirects.Any(static redirect =>
            redirect.IsDynamicSkip
            && !IsStaticFileDescriptor(redirect.Target)));

    /// <summary>
    /// Returns true only for the static file-descriptor targets that
    /// ShellSyntaxTree 0.2 recognizes: &amp;N, &amp;N-, and &amp;-.
    /// </summary>
    private static bool IsStaticFileDescriptor(string target)
    {
        if (target == "&-")
            return true;

        if (target.Length < 2 || target[0] != '&')
            return false;

        var index = 1;
        while (index < target.Length && char.IsAsciiDigit(target[index]))
            index++;

        if (index == 1)
            return false;

        return index == target.Length
               || index == target.Length - 1 && target[index] == '-';
    }
}
