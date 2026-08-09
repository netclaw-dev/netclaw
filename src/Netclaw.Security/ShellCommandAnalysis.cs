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
        var commands = new List<CommandOccurrence>();
        var failure = Analyze(command, workingDirectory, depth: 0, commands);
        return new ShellCommandAnalysis(commands, failure);
    }

    private static ShellAnalysisFailure Analyze(
        string command,
        string? workingDirectory,
        int depth,
        List<CommandOccurrence> commands)
    {
        if (depth > MaxWrapperDepth)
            return ShellAnalysisFailure.Unresolved;

        // Stable v0.3 excludes background lists. Keep this guard until the
        // parser exposes their concurrency and shell-state boundaries.
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

        if (parsed.IsUnparseable || parsed.Commands.Count == 0)
            return ShellAnalysisFailure.Unresolved;

        var innerCommands = PosixShellApprovalSemantics.Instance.ExtractInnerCommands(command);
        var unexpandedWrappers = parsed.Commands
            .Where(static occurrence => IsUnexpandedWrapperClause(occurrence.Clause))
            .ToList();
        if (innerCommands.Count == 0 || unexpandedWrappers.Count == 0)
        {
            commands.AddRange(parsed.Commands);
            return ShellAnalysisFailure.None;
        }

        // The v0.3 parser owns contracted wrapper forms. This fallback keeps
        // Netclaw's extra bundled bash -lc form. Remove only a direct shell
        // dispatch: prefix executables such as sudo, env, and nohup remain
        // visible to hard-deny and approval policy.
        commands.AddRange(parsed.Commands.Where(static occurrence =>
            !IsUnexpandedWrapperClause(occurrence.Clause)
            || !IsTransparentShellDispatch(occurrence.Clause)));

        if (innerCommands.Count != unexpandedWrappers.Count)
            return ShellAnalysisFailure.Unresolved;

        for (var i = 0; i < innerCommands.Count; i++)
        {
            if (!TryResolveWrapperWorkingDirectory(
                    unexpandedWrappers[i],
                    workingDirectory,
                    out var innerWorkingDirectory))
            {
                return ShellAnalysisFailure.Unresolved;
            }

            var failure = Analyze(
                innerCommands[i],
                innerWorkingDirectory,
                depth + 1,
                commands);
            if (failure != ShellAnalysisFailure.None)
                return failure;
        }

        return ShellAnalysisFailure.None;
    }

    private static bool TryResolveWrapperWorkingDirectory(
        CommandOccurrence occurrence,
        string? inheritedWorkingDirectory,
        out string? workingDirectory)
    {
        var cwdAttribution = occurrence.Clause.Args
            .FirstOrDefault(static arg => arg.IsCwdAttribution);
        if (cwdAttribution is null)
        {
            workingDirectory = inheritedWorkingDirectory;
            return true;
        }

        if (occurrence.WorkingDirectory.Kind == ShellValueDomainKind.Exact
            && occurrence.WorkingDirectory.Values.Count == 1
            && !string.IsNullOrWhiteSpace(occurrence.WorkingDirectory.Values[0]))
        {
            workingDirectory = occurrence.WorkingDirectory.Values[0];
            return true;
        }

        if (!string.IsNullOrWhiteSpace(cwdAttribution.Resolved))
        {
            workingDirectory = cwdAttribution.Resolved;
            return true;
        }

        workingDirectory = null;
        return false;
    }

    private static bool IsUnexpandedWrapperClause(Clause clause)
    {
        if (clause.Verb.Tokens.Count == 0 || clause.Args.Count == 0)
            return false;

        if (!clause.Verb.Tokens.Any(IsShellInvokerToken)
            && !HasShellInvokerInArguments(clause))
        {
            return false;
        }

        return clause.Args.Any(static arg =>
            arg.Raw.Length > 1
            && arg.Raw[0] == '-'
            && !arg.Raw.StartsWith("--", StringComparison.Ordinal)
            && arg.Raw.AsSpan(1).IndexOf('c') >= 0);
    }

    private static bool HasShellInvokerInArguments(Clause clause)
        => clause.Args.Any(static arg =>
            arg.Kind != ArgKind.DynamicSkip && IsShellInvokerToken(arg.Raw));

    private static bool IsTransparentShellDispatch(Clause clause)
    {
        if (clause.Verb.Tokens.Count == 0)
            return false;

        if (IsShellInvokerToken(clause.Verb.Tokens[0]))
            return true;

        if (!string.Equals(
                ShellTokenizer.TrimShellPunctuation(clause.Verb.Tokens[0]),
                "command",
                StringComparison.Ordinal))
        {
            return false;
        }

        if (clause.Verb.Tokens.Count > 1)
            return IsShellInvokerToken(clause.Verb.Tokens[1]);

        foreach (var arg in clause.Args.Where(static arg => !arg.IsCwdAttribution))
        {
            if (arg.Kind == ArgKind.DynamicSkip)
                return false;

            var token = ShellTokenizer.TrimShellPunctuation(arg.Raw);
            if (token is "--" or "-p")
                continue;

            return IsShellInvokerToken(token);
        }

        return false;
    }

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

        // A trailing slash is a directory-only type filter (foo/*/), not a
        // descendant path segment: every match is still a direct child of the
        // covering directory, exactly like the leaf glob foo/*. Strip it before
        // the scan so the directory-listing idiom keeps a fixed, persistable
        // scope instead of degrading to a one-shot "complex command". A real
        // segment after the wildcard (foo/*/x, foo/*/*) keeps its separator and
        // stays unresolved.
        var scope = arg.Raw.TrimEnd('/');
        var firstGlob = scope.IndexOfAny(['*', '?', '[']);
        return firstGlob >= 0
            && scope.IndexOf('/', firstGlob + 1) >= 0;
    }
}

internal sealed record ShellCommandAnalysis(
    IReadOnlyList<CommandOccurrence> Commands,
    ShellAnalysisFailure Failure)
{
    public bool HasDynamicSyntax => Commands.Any(static command =>
        !command.IsComplete
        || !Enum.IsDefined(command.ImmediateRole)
        || command.ImmediateRole == CommandOccurrenceRole.Unknown
        || command.Ancestry.Any(static frame =>
            !Enum.IsDefined(frame.AncestorKind)
            || frame.AncestorKind == ShellSyntaxKind.Unknown
            || !Enum.IsDefined(frame.Region)
            || frame.Region == CommandAncestryRegion.Unknown)
        || HasUnsupportedWorkingDirectory(command.WorkingDirectory)
        || command.Clause.Verb.IsDynamic
        || command.Clause.Args.Any(static arg =>
            arg.Kind == ArgKind.DynamicSkip && !arg.IsCwdAttribution)
        || command.Clause.Args.Any(static arg =>
            arg.IsPath
            && arg.Kind != ArgKind.Glob
            && string.IsNullOrWhiteSpace(arg.Resolved))
        // A glob in a directory segment can hide traversal or a symlink.
        // Only a leaf glob has a fixed directory scope.
        || command.Clause.Args.Any(ShellGlobPath.HasUnresolvedDescendantScope)
        || command.Redirects.Any(HasUnresolvedRedirect));

    private static bool HasUnsupportedWorkingDirectory(ShellValueDomain workingDirectory)
    {
        if (!Enum.IsDefined(workingDirectory.Kind))
            return true;

        return workingDirectory.Kind switch
        {
            ShellValueDomainKind.Unknown => workingDirectory.Values.Count != 0
                || workingDirectory.Pattern is not null
                || workingDirectory.CoveringDirectory is not null,
            ShellValueDomainKind.Exact => workingDirectory.Values.Count != 1
                || string.IsNullOrWhiteSpace(workingDirectory.Values[0])
                || workingDirectory.Pattern is not null
                || workingDirectory.CoveringDirectory is not null,
            _ => true
        };
    }

    private static bool HasUnresolvedRedirect(RedirectAnalysis redirect)
    {
        if (!redirect.IsComplete
            || !Enum.IsDefined(redirect.Source.Kind)
            || redirect.Source.Kind == RedirectSourceKind.Unknown
            || !Enum.IsDefined(redirect.Operation)
            || redirect.Operation == RedirectOperation.Unknown)
        {
            return true;
        }

        var hasValidSource = redirect.Source.Kind switch
        {
            RedirectSourceKind.Default => redirect.Source.Descriptor is null,
            RedirectSourceKind.Descriptor => redirect.Source.Descriptor is >= 0,
            _ => false
        };
        if (!hasValidSource)
        {
            return true;
        }

        return redirect.Operation switch
        {
            // Netclaw keeps shell-fed stdin approval-sensitive. The parser can
            // prove its syntax without proving executable-specific data use.
            RedirectOperation.HereDocument or RedirectOperation.HereString => true,
            RedirectOperation.DescriptorDuplicate or RedirectOperation.DescriptorMove =>
                redirect.IsPathRelevant || redirect.TargetDescriptor is < 0 or null,
            RedirectOperation.DescriptorClose =>
                redirect.IsPathRelevant || redirect.TargetDescriptor is not null,
            RedirectOperation.FileInput
                or RedirectOperation.FileOutput
                or RedirectOperation.FileAppend
                or RedirectOperation.CombinedOutput
                or RedirectOperation.CombinedOutputAppend =>
                !redirect.IsPathRelevant || !HasBoundedPathTarget(redirect.Target),
            _ => true
        };
    }

    private static bool HasBoundedPathTarget(ShellValueDomain target)
        => target.Kind switch
        {
            ShellValueDomainKind.Exact => target.Values.Count == 1
                && !string.IsNullOrWhiteSpace(target.Values[0]),
            ShellValueDomainKind.FiniteSet => target.Values.Count is >= 2 and <= 32
                && target.Values.All(static value => !string.IsNullOrWhiteSpace(value)),
            ShellValueDomainKind.Pattern => !string.IsNullOrWhiteSpace(target.Pattern)
                && !string.IsNullOrWhiteSpace(target.CoveringDirectory),
            _ => false
        };
}
