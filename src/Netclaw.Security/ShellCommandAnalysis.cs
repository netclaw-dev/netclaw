// -----------------------------------------------------------------------
// <copyright file="ShellCommandAnalysis.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using ShellSyntaxTree;

namespace Netclaw.Security;

/// <summary>
/// Parses commands with one canonical shell environment and expands the extra
/// bundled Bash wrapper forms that remain outside ShellSyntaxTree's contract.
/// Approval and hard-deny policies share this analysis.
/// </summary>
internal sealed class ShellCommandAnalyzer
{
    private const int MaxWrapperDepth = 8;
    private readonly ShellExecutionEnvironment _environment;

    public ShellCommandAnalyzer(ShellExecutionEnvironment environment)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public ShellCommandAnalysis Analyze(string command, string? workingDirectory = null)
    {
        var commands = new List<CommandOccurrence>();
        var denyOnlyClauses = new List<Clause>();
        var knownRegionArguments = new HashSet<ClauseElement>(
            ReferenceEqualityComparer.Instance);
        var syntaxProofComplete = true;
        var failure = Analyze(
            command,
            workingDirectory,
            depth: 0,
            commands,
            denyOnlyClauses,
            knownRegionArguments,
            ref syntaxProofComplete);
        return new ShellCommandAnalysis(
            _environment,
            command,
            workingDirectory,
            commands,
            denyOnlyClauses,
            failure,
            knownRegionArguments,
            syntaxProofComplete);
    }

    private ShellAnalysisFailure Analyze(
        string command,
        string? workingDirectory,
        int depth,
        List<CommandOccurrence> commands,
        List<Clause> denyOnlyClauses,
        HashSet<ClauseElement> knownRegionArguments,
        ref bool syntaxProofComplete)
    {
        if (depth > MaxWrapperDepth)
            return ShellAnalysisFailure.Unresolved;

        // Stable v0.3 excludes background lists. Keep this guard until the
        // parser exposes their concurrency and shell-state boundaries.
        if (_environment.Grammar == ShellGrammar.Bash
            && ContainsBackgroundListOperator(command))
            return ShellAnalysisFailure.Unresolved;

        ParsedCommand parsed;
        try
        {
            parsed = _environment.ParseForApproval(
                command,
                workingDirectory,
                publishAuthoredSourceFacts: depth == 0);
        }
        catch
        {
            return ShellAnalysisFailure.Unresolved;
        }

        if (parsed.IsUnparseable)
        {
            if (_environment.Grammar == ShellGrammar.PowerShell)
            {
                ShellCommandAnalysis.CollectSourceAuthenticDenyOnlyClauses(
                    parsed.Syntax,
                    command,
                    denyOnlyClauses);
            }

            return ShellAnalysisFailure.Unresolved;
        }

        if (parsed.Commands.Count == 0)
            return ShellAnalysisFailure.Unresolved;

        if (_environment.Grammar == ShellGrammar.PowerShell)
        {
            commands.AddRange(parsed.Commands);
            syntaxProofComplete &= ShellCommandAnalysis.TryCollectKnownExecutionRegionArguments(
                parsed.Syntax,
                knownRegionArguments);
            return ShellAnalysisFailure.None;
        }

        var innerCommands = ShellApprovalSemantics.ExtractInnerCommands(
            command,
            ShellPathStyle.Posix);
        var unexpandedWrappers = parsed.Commands
            .Where(static occurrence => IsUnexpandedWrapperClause(occurrence.Clause))
            .ToList();
        if (innerCommands.Count == 0 || unexpandedWrappers.Count == 0)
        {
            commands.AddRange(parsed.Commands);
            return ShellAnalysisFailure.None;
        }

        if (innerCommands.Count != unexpandedWrappers.Count)
        {
            // Preserve the prior defense scan when wrapper extraction is incomplete.
            commands.AddRange(parsed.Commands.Where(static occurrence =>
                !IsUnexpandedWrapperClause(occurrence.Clause)
                || !IsTransparentShellDispatch(occurrence.Clause)));
            return ShellAnalysisFailure.Unresolved;
        }

        // The v0.3 parser owns contracted wrapper forms. This fallback keeps
        // Netclaw's extra bundled bash -lc form. Expand each wrapper at its
        // parser-owned position so every consumer sees execution order. Remove
        // only a direct shell dispatch; retain prefix executables such as sudo,
        // env, and nohup for hard-deny and approval policy.
        var innerIndex = 0;
        foreach (var occurrence in parsed.Commands)
        {
            if (!IsUnexpandedWrapperClause(occurrence.Clause))
            {
                commands.Add(occurrence);
                continue;
            }

            if (!IsTransparentShellDispatch(occurrence.Clause))
                commands.Add(occurrence);

            if (!TryResolveWrapperWorkingDirectory(
                    occurrence,
                    workingDirectory,
                    out var innerWorkingDirectory))
            {
                return ShellAnalysisFailure.Unresolved;
            }

            var failure = Analyze(
                innerCommands[innerIndex++],
                innerWorkingDirectory,
                depth + 1,
                commands,
                denyOnlyClauses,
                knownRegionArguments,
                ref syntaxProofComplete);
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

        if (occurrence.WorkingDirectory is ShellValueDomain.Exact exact
            && !string.IsNullOrWhiteSpace(exact.Value))
        {
            workingDirectory = exact.Value;
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
        => ShellApprovalSemantics.IsPosixShellInvoker(
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
    public static bool HasUnresolvedDescendantScope(
        Arg arg,
        ShellPathStyle pathStyle)
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
        var scope = pathStyle == ShellPathStyle.Windows
            ? arg.Raw.TrimEnd('/', '\\')
            : arg.Raw.TrimEnd('/');
        var firstGlob = scope.IndexOfAny(['*', '?', '[']);
        if (firstGlob < 0)
            return false;

        return pathStyle == ShellPathStyle.Windows
            ? scope.AsSpan(firstGlob + 1).IndexOfAny('/', '\\') >= 0
            : scope.IndexOf('/', firstGlob + 1) >= 0;
    }
}

public sealed record ShellCommandAnalysis
{
    private const long MaximumReviewedIntegerRangeCardinality = 4096;

    internal ShellCommandAnalysis(
        ShellExecutionEnvironment environment,
        string source,
        string? workingDirectory,
        IReadOnlyList<CommandOccurrence> commands,
        IReadOnlyList<Clause> denyOnlyClauses,
        ShellAnalysisFailure failure,
        IReadOnlySet<ClauseElement> knownRegionArguments,
        bool syntaxProofComplete)
    {
        Environment = environment;
        Source = source;
        WorkingDirectory = workingDirectory;
        Commands = commands.ToImmutableArray();
        DenyOnlyClauses = denyOnlyClauses.ToImmutableArray();
        Failure = failure;
        HasDynamicSyntax = !syntaxProofComplete
            || Commands.Any(command =>
                CommandHasDynamicSyntax(command, knownRegionArguments));
        RequiresExactTreeApproval = ShellFileSystemTreeAccessPolicy.RequiresExactApproval(
            environment,
            Commands);
    }

    public string Source { get; }

    public string? WorkingDirectory { get; }

    public IReadOnlyList<CommandOccurrence> Commands { get; }

    internal IReadOnlyList<Clause> DenyOnlyClauses { get; }

    public bool IsResolved => Failure == ShellAnalysisFailure.None && Commands.Count > 0;

    public bool HasDynamicSyntax { get; }

    /// <summary>
    /// Gets whether a filesystem tree effect requires one exact approval.
    /// This fact is separate from shell syntax completeness.
    /// </summary>
    internal bool RequiresExactTreeApproval { get; }

    internal ShellExecutionEnvironment Environment { get; }

    internal ShellAnalysisFailure Failure { get; }

    internal static void CollectSourceAuthenticDenyOnlyClauses(
        ShellSyntaxNode node,
        string source,
        ICollection<Clause> clauses)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clauses);

        var seen = new HashSet<Clause>(ReferenceEqualityComparer.Instance);
        CollectSourceAuthenticDenyOnlyClauses(node, source, clauses, seen);
    }

    private static void CollectSourceAuthenticDenyOnlyClauses(
        ShellSyntaxNode node,
        string source,
        ICollection<Clause> clauses,
        ISet<Clause> seen)
    {
        switch (node)
        {
            case ShellBlockSyntax block:
                foreach (var statement in block.Statements)
                    CollectSourceAuthenticDenyOnlyClauses(statement, source, clauses, seen);
                break;
            case SimpleCommandSyntax command:
                if (seen.Add(command.Clause)
                    && IsSourceAuthenticDenyOnlyClause(command, source))
                {
                    clauses.Add(command.Clause);
                }

                foreach (var region in command.ExecutionRegions)
                    CollectSourceAuthenticDenyOnlyClauses(region, source, clauses, seen);
                foreach (var substitution in command.Substitutions)
                    CollectSourceAuthenticDenyOnlyClauses(substitution, source, clauses, seen);
                break;
            case PipelineSyntax pipeline:
                foreach (var stage in pipeline.Stages)
                    CollectSourceAuthenticDenyOnlyClauses(stage, source, clauses, seen);
                break;
            case CommandListSyntax list:
                foreach (var item in list.Items)
                    CollectSourceAuthenticDenyOnlyClauses(item.Command, source, clauses, seen);
                break;
            case GroupSyntax group:
                CollectSourceAuthenticDenyOnlyClauses(group.Body, source, clauses, seen);
                break;
            case ForEachSyntax loop:
                CollectSourceAuthenticDenyOnlyClauses(loop.IteratorCommands, source, clauses, seen);
                CollectSourceAuthenticDenyOnlyClauses(loop.Body, source, clauses, seen);
                break;
            case CommandSubstitutionSyntax substitution:
                CollectSourceAuthenticDenyOnlyClauses(substitution.Body, source, clauses, seen);
                break;
            case ExecutionRegionSyntax region:
                CollectSourceAuthenticDenyOnlyClauses(region.Body, source, clauses, seen);
                break;
        }
    }

    private static bool IsSourceAuthenticDenyOnlyClause(
        SimpleCommandSyntax command,
        string source)
    {
        var clause = command.Clause;
        if (clause.Verb.IsDynamic
            || clause.Verb.Tokens.Count == 0
            || clause.Elements.Count == 0
            || !HasValidSourceProvenance(command, source.Length))
        {
            return false;
        }

        var verbIndex = 0;
        foreach (var element in clause.Elements)
        {
            if (!Enum.IsDefined(element.Role)
                || !Enum.IsDefined(element.Kind)
                || !HasExactSourceIdentity(
                    element,
                    command,
                    source,
                    clause.IsCommandStringWrapped))
            {
                return false;
            }

            if (element.Role == ClauseElementRole.Verb)
            {
                if (element.Kind != ArgKind.Literal
                    || verbIndex >= clause.Verb.Tokens.Count
                    || element.PrecedingVerbElementCount != verbIndex
                    || !string.Equals(
                        element.Value,
                        clause.Verb.Tokens[verbIndex],
                        StringComparison.Ordinal))
                {
                    return false;
                }

                verbIndex++;
            }
        }

        return verbIndex == clause.Verb.Tokens.Count;
    }

    private static bool HasValidSourceProvenance(
        ShellSyntaxNode node,
        int sourceLength)
    {
        if (node is SimpleCommandSyntax
            {
                Clause.IsCommandStringWrapped: true,
                SourceStart: null,
                SourceLength: null
            })
        {
            return true;
        }

        if (node.SourceStart is not int start
            || node.SourceLength is not int length
            || start < 0
            || length < 0)
        {
            return false;
        }

        return length <= sourceLength && start <= sourceLength - length;
    }

    private static bool HasExactSourceIdentity(
        ClauseElement element,
        ShellSyntaxNode owner,
        string source,
        bool isCommandStringWrapped)
    {
        if (isCommandStringWrapped
            && owner.SourceStart is null
            && owner.SourceLength is null)
        {
            return element.SourceStart is null && element.SourceLength is null;
        }

        if (element.SourceStart is not int start
            || element.SourceLength is not int length
            || owner.SourceStart is not int ownerStart
            || owner.SourceLength is not int ownerLength
            || start < 0
            || length < 0
            || length != element.Raw.Length
            || start < ownerStart
            || length > source.Length
            || start > source.Length - length
            || start + length > ownerStart + ownerLength)
        {
            return false;
        }

        return source.AsSpan(start, length)
            .SequenceEqual(element.Raw.AsSpan());
    }

    private bool CommandHasDynamicSyntax(
        CommandOccurrence command,
        IReadOnlySet<ClauseElement> accountedRegionArguments)
        => !command.IsComplete
            || !Enum.IsDefined(command.ImmediateRole)
            || command.ImmediateRole == CommandOccurrenceRole.Unknown
            || command.Ancestry.Any(static frame =>
                !IsKnownAncestor(frame.Ancestor)
                || !Enum.IsDefined(frame.Region)
                || frame.Region == CommandAncestryRegion.Unknown)
            || HasUnsupportedWorkingDirectory(command.WorkingDirectory)
            || command.Clause.Verb.IsDynamic
            || command.Clause.Args.Any(arg =>
                arg.Kind == ArgKind.DynamicSkip
                && !arg.IsCwdAttribution
                && !IsAccountedExecutionRegionArgument(
                    command,
                    arg,
                    accountedRegionArguments)
                && !HasBoundedAuthoredFileSystemValue(command, arg)
                && !HasAuditedNonFileSystemValue(command, arg))
            || command.Clause.Args.Any(static arg =>
                arg.IsPath
                && arg.Kind != ArgKind.Glob
                && string.IsNullOrWhiteSpace(arg.Resolved))
            || command.Arguments.Any(argument =>
                !IsAccountedExecutionRegionArgument(
                    argument,
                    accountedRegionArguments)
                && HasUnsupportedArgumentDomain(argument))
            // A glob in a directory segment can hide traversal or a symlink.
            // Only a leaf glob has a fixed directory scope.
            || command.Clause.Args.Any(arg =>
                ShellGlobPath.HasUnresolvedDescendantScope(arg, Environment.PathStyle))
            || HasUnresolvedRedirect(command);

    internal static bool TryCollectKnownExecutionRegionArguments(
        ShellSyntaxNode node,
        ISet<ClauseElement> arguments)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(arguments);

        return node switch
        {
            ShellBlockSyntax block => block.Statements.All(statement =>
                TryCollectKnownExecutionRegionArguments(statement, arguments)),
            SimpleCommandSyntax command => command.ExecutionRegions.All(region =>
                    TryCollectKnownExecutionRegionArguments(region, arguments))
                && command.Substitutions.All(substitution =>
                    TryCollectKnownExecutionRegionArguments(substitution, arguments)),
            PipelineSyntax pipeline => pipeline.Stages.All(stage =>
                TryCollectKnownExecutionRegionArguments(stage, arguments)),
            CommandListSyntax list => list.Items.All(item =>
                TryCollectKnownExecutionRegionArguments(item.Command, arguments)),
            GroupSyntax group => TryCollectKnownExecutionRegionArguments(
                group.Body,
                arguments),
            ForEachSyntax loop => TryCollectKnownExecutionRegionArguments(
                    loop.IteratorCommands,
                    arguments)
                && TryCollectKnownExecutionRegionArguments(loop.Body, arguments),
            CommandSubstitutionSyntax substitution => TryCollectKnownExecutionRegionArguments(
                substitution.Body,
                arguments),
            ExecutionRegionSyntax region => TryCollectKnownExecutionRegion(
                region,
                arguments),
            _ => false
        };
    }

    private static bool TryCollectKnownExecutionRegion(
        ExecutionRegionSyntax region,
        ISet<ClauseElement> arguments)
    {
        if (!Enum.IsDefined(region.Origin)
            || region.Origin == ExecutionRegionOrigin.Unknown
            || !Enum.IsDefined(region.Phase)
            || region.Phase == ExecutionRegionPhase.Unknown
            || !Enum.IsDefined(region.Timing)
            || region.Timing == ExecutionRegionTiming.Unknown
            || !Enum.IsDefined(region.Cardinality)
            || region.Cardinality == ExecutionRegionCardinality.Unknown
            || (region.Origin == ExecutionRegionOrigin.CommandArgument
                && region.HostArgument is null))
        {
            return false;
        }

        if (!TryCollectKnownExecutionRegionArguments(region.Body, arguments))
            return false;

        if (region.Origin == ExecutionRegionOrigin.CommandArgument)
            arguments.Add(region.HostArgument!);

        return true;
    }

    private static bool IsAccountedExecutionRegionArgument(
        CommandOccurrence command,
        Arg argument,
        IReadOnlySet<ClauseElement> accountedRegionArguments)
        => command.Arguments.Any(analyzed =>
            ReferenceEquals(analyzed.Argument, argument)
            && IsAccountedExecutionRegionArgument(
                analyzed,
                accountedRegionArguments));

    private static bool IsAccountedExecutionRegionArgument(
        AnalyzedArgument argument,
        IReadOnlySet<ClauseElement> accountedRegionArguments)
        => argument.Argument.Kind == ArgKind.DynamicSkip
            && accountedRegionArguments.Contains(argument.Element);

    private static bool HasBoundedAuthoredFileSystemValue(
        CommandOccurrence command,
        Arg argument)
        => command.Arguments.Any(analyzed =>
            ReferenceEquals(analyzed.Argument, argument)
            && analyzed.AuthoredFileSystemValue is ShellValueDomain.Exact
                or ShellValueDomain.FiniteSet);

    private static bool HasAuditedNonFileSystemValue(
        CommandOccurrence command,
        Arg argument)
        => command.Arguments.Any(analyzed =>
            ReferenceEquals(analyzed.Argument, argument)
            && HasAuditedNonFileSystemValue(analyzed));

    internal static bool HasAuditedNonFileSystemValue(AnalyzedArgument argument)
    {
        if (argument.Argument.IsPath
            || argument.AuthoredFileSystemValue is not ShellValueDomain.Unknown)
        {
            return false;
        }

        return argument.AuthoredNonFileSystemValue switch
        {
            ShellValueDomain.Exact => true,
            ShellValueDomain.FiniteSet finite => IsValidFiniteSet(finite),
            ShellValueDomain.OrderedList list => IsValidOrderedList(list),
            ShellValueDomain.Unknown => HasMatchingIntegerRange(argument),
            _ => false
        };
    }

    private static bool HasMatchingIntegerRange(AnalyzedArgument argument)
        => argument.Value is ShellValueDomain.IntegerRange value
           && argument.AuthoredValue is ShellValueDomain.IntegerRange authored
           && value.MinimumInclusive == authored.MinimumInclusive
           && value.MaximumInclusive == authored.MaximumInclusive
           && value.MinimumInclusive >= 0
           && value.MaximumInclusive <= int.MaxValue
           && value.MaximumInclusive >= value.MinimumInclusive
           && value.MaximumInclusive - value.MinimumInclusive
               < MaximumReviewedIntegerRangeCardinality;

    private static bool IsValidFiniteSet(ShellValueDomain.FiniteSet finite)
        => finite.Values.Count is >= 2 and <= 32
           && finite.Values.All(static value => value is not null)
           && finite.Values.Distinct(StringComparer.Ordinal).Count() == finite.Values.Count;

    private static bool IsValidOrderedList(ShellValueDomain.OrderedList list)
        => list.Values.Count is >= 2 and <= 32
           && list.Values.All(static value => value is not null);

    private static bool IsKnownAncestor(ShellSyntaxNode ancestor)
        => ancestor is ShellBlockSyntax
            or SimpleCommandSyntax
            or PipelineSyntax
            or CommandListSyntax
            or GroupSyntax
            or ForEachSyntax
            or CommandSubstitutionSyntax
            or ExecutionRegionSyntax;

    private static bool HasUnsupportedWorkingDirectory(ShellValueDomain workingDirectory)
        => workingDirectory switch
        {
            ShellValueDomain.Unknown => false,
            ShellValueDomain.Exact exact => string.IsNullOrWhiteSpace(exact.Value),
            _ => true
        };

    private static bool HasUnsupportedArgumentDomain(AnalyzedArgument argument)
    {
        if (argument.AuthoredFileSystemValue is not ShellValueDomain.Unknown
            and not ShellValueDomain.Exact
            and not ShellValueDomain.FiniteSet)
        {
            return true;
        }

        if (argument.AuthoredNonFileSystemValue is not ShellValueDomain.Unknown
            and not ShellValueDomain.Exact
            and not ShellValueDomain.FiniteSet
            and not ShellValueDomain.OrderedList)
        {
            return true;
        }

        if (argument.AuthoredFileSystemValue is not ShellValueDomain.Unknown
            && argument.AuthoredNonFileSystemValue is not ShellValueDomain.Unknown)
        {
            return true;
        }

        var value = argument.Value;
        if (value is ShellValueDomain.Unknown)
        {
            if (argument.AuthoredFileSystemValue is not ShellValueDomain.Unknown)
            {
                value = argument.AuthoredFileSystemValue;
            }
            else if (!argument.Argument.IsPath
                     && argument.AuthoredNonFileSystemValue is not ShellValueDomain.Unknown)
            {
                value = argument.AuthoredNonFileSystemValue;
            }
            else if (!argument.Argument.IsPath
                     && argument.AuthoredValue is not ShellValueDomain.Unknown)
            {
                value = argument.AuthoredValue;
            }
        }

        return value switch
        {
            // A raw authored glob has no one runtime value. Netclaw applies
            // its fixed covering-scope checks to the source Arg below.
            ShellValueDomain.Unknown => argument.Argument.Kind != ArgKind.Glob,
            ShellValueDomain.Exact => false,
            ShellValueDomain.FiniteSet finite => finite.Values.Count is < 2 or > 32
                || finite.Values.Any(static value => value is null)
                || finite.Values.Distinct(StringComparer.Ordinal).Count() != finite.Values.Count,
            ShellValueDomain.OrderedList list => list.Values.Count is < 2 or > 32
                || list.Values.Any(static value => value is null),
            // ShellSyntaxTree proves these domains are bounded. They remain
            // data only and cannot establish path or execution authority.
            ShellValueDomain.IntegerRange => argument.Argument.IsPath,
            ShellValueDomain.Concatenation => argument.Argument.IsPath,
            ShellValueDomain.PathPattern pattern =>
                string.IsNullOrWhiteSpace(pattern.Pattern)
                || string.IsNullOrWhiteSpace(pattern.CoveringDirectory),
            _ => true
        };
    }

    private static bool HasUnresolvedRedirect(CommandOccurrence occurrence)
        => occurrence.Redirects.Any(redirect => HasUnresolvedRedirect(occurrence, redirect));

    private static bool HasUnresolvedRedirect(
        CommandOccurrence occurrence,
        RedirectAnalysis redirect)
    {
        if (!redirect.IsComplete || !IsKnownRedirectSource(redirect.Source))
        {
            return true;
        }

        return redirect switch
        {
            HereDocumentRedirectAnalysis heredoc =>
                !HasBoundedDataOnlyStdin(occurrence, heredoc),
            HereStringRedirectAnalysis hereString =>
                !HasBoundedDataOnlyStdin(occurrence, hereString),
            DescriptorDuplicateRedirectAnalysis duplicate =>
                duplicate.TargetDescriptor < 0,
            DescriptorMoveRedirectAnalysis move => move.TargetDescriptor < 0,
            DescriptorCloseRedirectAnalysis => false,
            FileRedirectAnalysis file => !IsKnownFileRedirectMode(file.Mode)
                || !HasBoundedPathTarget(file.Target),
            UnresolvedRedirectAnalysis => true,
            _ => true
        };
    }

    private static bool HasBoundedDataOnlyStdin(
        CommandOccurrence occurrence,
        HereDocumentRedirectAnalysis redirect)
    {
        var clause = occurrence.Clause;
        if (!IsStandardInputSource(redirect.Source)
            || clause.Verb.Tokens.Count != 1
            || !string.Equals(
                ShellTokenizer.TrimShellPunctuation(clause.Verb.Tokens[0]),
                "cat",
                StringComparison.Ordinal)
            || clause.Args.Any(static arg => !arg.IsCwdAttribution))
        {
            return false;
        }

        return HasLiteralHereDocument(
            redirect.Document,
            clause.IsCommandStringWrapped);
    }

    private static bool HasBoundedDataOnlyStdin(
        CommandOccurrence occurrence,
        HereStringRedirectAnalysis redirect)
    {
        var clause = occurrence.Clause;
        return IsStandardInputSource(redirect.Source)
            && clause.Verb.Tokens.Count == 1
            && string.Equals(
                ShellTokenizer.TrimShellPunctuation(clause.Verb.Tokens[0]),
                "cat",
                StringComparison.Ordinal)
            && !clause.Args.Any(static arg => !arg.IsCwdAttribution)
            && HasBoundedData(redirect.Data);
    }

    private static bool IsKnownRedirectSource(RedirectSource source)
        => source is RedirectSource.Default
            or RedirectSource.Descriptor { Value: >= 0 }
            or RedirectSource.PowerShellAllStreams;

    private static bool IsKnownFileRedirectMode(FileRedirectMode mode)
        => mode is FileRedirectMode.Input
            or FileRedirectMode.Output
            or FileRedirectMode.Append
            or FileRedirectMode.CombinedOutput
            or FileRedirectMode.CombinedOutputAppend;

    private static bool IsStandardInputSource(RedirectSource source)
        => source is RedirectSource.Default
            or RedirectSource.Descriptor { Value: 0 };

    private static bool HasLiteralHereDocument(
        HereDocumentAnalysis hereDocument,
        bool allowUnavailableSourceSpans)
        => hereDocument.Delimiter is not null
            && hereDocument.Body is not null
            && hereDocument.IsComplete
            && Enum.IsDefined(hereDocument.ExpansionMode)
            && hereDocument.ExpansionMode == HereDocumentExpansionMode.Literal
            && HasValidSourceFragment(
                hereDocument.Delimiter,
                allowUnavailableSourceSpans)
            && HasValidSourceFragment(
                hereDocument.Body,
                allowUnavailableSourceSpans);

    private static bool HasValidSourceFragment(
        ShellSourceFragment fragment,
        bool allowUnavailableSourceSpan)
    {
        if (fragment.Raw is null)
            return false;

        if (fragment.SourceStart is null && fragment.SourceLength is null)
            return allowUnavailableSourceSpan;

        return fragment.SourceStart >= 0 && fragment.SourceLength >= 0;
    }

    private static bool HasBoundedData(ShellValueDomain data)
        => data switch
        {
            ShellValueDomain.Exact exact => exact.Value is not null,
            ShellValueDomain.FiniteSet finite => finite.Values.Count is >= 2 and <= 32
                && finite.Values.All(static value => value is not null)
                && finite.Values.Distinct(StringComparer.Ordinal).Count() == finite.Values.Count,
            _ => false
        };

    private static bool HasBoundedPathTarget(ShellValueDomain target)
        => target switch
        {
            ShellValueDomain.Exact exact => !string.IsNullOrWhiteSpace(exact.Value),
            ShellValueDomain.FiniteSet finite => finite.Values.Count is >= 2 and <= 32
                && finite.Values.All(static value => !string.IsNullOrWhiteSpace(value)),
            ShellValueDomain.PathPattern pattern =>
                !string.IsNullOrWhiteSpace(pattern.Pattern)
                && !string.IsNullOrWhiteSpace(pattern.CoveringDirectory),
            _ => false
        };
}
