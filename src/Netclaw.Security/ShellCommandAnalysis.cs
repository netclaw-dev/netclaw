// -----------------------------------------------------------------------
// <copyright file="ShellCommandAnalysis.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Tools;
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
    private readonly BashInitialStateMode? _screenState;

    public ShellCommandAnalyzer(ShellExecutionEnvironment environment)
        : this(environment, screenState: null)
    {
    }

    private ShellCommandAnalyzer(
        ShellExecutionEnvironment environment,
        BashInitialStateMode? screenState)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _screenState = screenState;
    }

    private static readonly BashInitialStateMode[] ScreenStates =
    [
        BashInitialStateMode.IsolatedNonInteractive,
        BashInitialStateMode.FreshNonInteractiveNoStartup
    ];

    /// <summary>
    /// Analyzes a command for a call with no managed temporary location. The parser
    /// still uses the launch facts that the shell environment sets on every process.
    /// </summary>
    public ShellCommandAnalysis Analyze(string command, string? workingDirectory = null)
        => Analyze(command, workingDirectory, temporary: null);

    /// <summary>
    /// Analyzes a command with the launch facts of one call. The facts include the
    /// managed temporary variables when the call has a temporary location.
    /// </summary>
    public ShellCommandAnalysis Analyze(
        string command,
        string? workingDirectory,
        ManagedTemporaryLocation? temporary)
    {
        var commands = new List<CommandOccurrence>();
        var denyOnlyClauses = new List<Clause>();
        var knownRegionArguments = new HashSet<ClauseElement>(
            ReferenceEqualityComparer.Instance);
        var syntaxProofComplete = true;
        var failure = Analyze(
            command,
            workingDirectory,
            _screenState is null ? _environment.CreateLaunchEnvironment(temporary) : null,
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
            syntaxProofComplete)
        {
            ManagedTemporary = temporary,
            ScreenClauses = _screenState is null
                            && _environment.Grammar == ShellGrammar.Bash
                            && (failure != ShellAnalysisFailure.None || commands.Count == 0)
                ? CollectScreenClauses(command, workingDirectory)
                : []
        };
    }

    /// <summary>
    /// Parses unresolved Bash source again for hard deny only. The screen first
    /// parses the whole source, then each list element alone, with an assumed
    /// bounded initial state. The clauses never become approval candidates.
    /// </summary>
    /// <remarks>
    /// ShellSyntaxTree 0.4.0-beta.5 rejects a background list, so each element
    /// of <c>echo ok &amp; sudo ls</c> meets hard deny only through this screen.
    /// A list element that is still unparseable adds no clause.
    /// </remarks>
    private List<Clause> CollectScreenClauses(string command, string? workingDirectory)
    {
        var clauses = new List<Clause>();
        if (TryCollectScreenClauses(command, workingDirectory, clauses))
            return clauses;

        foreach (var element in SplitListElements(command))
            TryCollectScreenClauses(element, workingDirectory, clauses);

        return clauses;
    }

    private bool TryCollectScreenClauses(string source, string? workingDirectory, List<Clause> clauses)
    {
        foreach (var state in ScreenStates)
        {
            var screened = new ShellCommandAnalyzer(_environment, state).Analyze(source, workingDirectory);
            if (screened.Commands.Count == 0)
                continue;

            clauses.AddRange(screened.Commands.Select(static occurrence => occurrence.Clause));
            return true;
        }

        return false;
    }

    private ShellAnalysisFailure Analyze(
        string command,
        string? workingDirectory,
        ShellLaunchEnvironment? launchEnvironment,
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
            && _screenState is null
            && FindListOperators(command).Any(static list => list.IsBackground))
            return ShellAnalysisFailure.Unresolved;

        ParsedCommand parsed;
        try
        {
            parsed = _screenState is { } state
                ? _environment.ParseForProhibitionScreen(command, workingDirectory, state)
                : _environment.ParseForApproval(
                    command,
                    workingDirectory,
                    publishAuthoredSourceFacts: depth == 0,
                    launchEnvironment);
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
            syntaxProofComplete &= ShellCommandAnalysis.AssignmentSyntaxReconciliation.TryCreate(
                    command,
                    parsed.Commands,
                    out var assignmentSyntax)
                && ShellCommandAnalysis.TryCollectKnownExecutionRegionArguments(
                    parsed.Syntax,
                    knownRegionArguments,
                    assignmentSyntax)
                && assignmentSyntax.AllConsumed;
            return ShellAnalysisFailure.None;
        }

        var wrapperSources = parsed.Commands
            .Where(static occurrence => IsUnexpandedWrapperClause(occurrence.Clause))
            .Select(FindWrapperSource)
            .ToList();
        var hasDecodedWrapper = parsed.Commands
            .Any(static occurrence => occurrence.Clause.IsCommandStringWrapped);
        if (wrapperSources.Count == 0
            || wrapperSources.All(static source => source is WrapperSource.Missing) && !hasDecodedWrapper)
        {
            commands.AddRange(parsed.Commands);
            return ShellAnalysisFailure.None;
        }

        // Every bundled wrapper needs its own child source. Source that mixes
        // a bundled wrapper with a wrapper that the parser decoded stays
        // unresolved.
        if (hasDecodedWrapper
            || wrapperSources.Any(static source => source is WrapperSource.Missing))
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
        var sourceIndex = 0;
        foreach (var occurrence in parsed.Commands)
        {
            if (!IsUnexpandedWrapperClause(occurrence.Clause))
            {
                commands.Add(occurrence);
                continue;
            }

            var childSource = wrapperSources[sourceIndex++];

            if (!IsTransparentShellDispatch(occurrence.Clause))
                commands.Add(occurrence);

            // A dynamic child source has no exact text to parse.
            if (childSource is not WrapperSource.Exact exactSource
                || !TryResolveWrapperWorkingDirectory(
                    occurrence,
                    workingDirectory,
                    out var innerWorkingDirectory))
            {
                return ShellAnalysisFailure.Unresolved;
            }

            var innerCommandStart = commands.Count;
            // SECURITY: the launcher sets the launch facts on the outer shell only. A child
            // shell can read startup files (bash -lc reads the login profile) that change
            // HOME or TMPDIR, so the child source gets no launch facts.
            var failure = Analyze(
                exactSource.Source,
                innerWorkingDirectory,
                launchEnvironment: null,
                depth + 1,
                commands,
                denyOnlyClauses,
                knownRegionArguments,
                ref syntaxProofComplete);
            if (failure != ShellAnalysisFailure.None)
                return failure;

            // SECURITY: an assignment prefix on the wrapper, for example
            // GIT_SSH_COMMAND=... bash -lc "git push", reaches the child
            // environment. The child candidates do not carry that assignment
            // in their identity, so a plain grant for the child would cover
            // the call. Keep the source unresolved. The check runs after the
            // child analysis, so the hard-deny screen still sees the child.
            if (occurrence.Assignments.Count > 0
                || commands.Skip(innerCommandStart).Any(static inner => inner.Assignments.Count > 0))
                return ShellAnalysisFailure.Unresolved;
        }

        return ShellAnalysisFailure.None;
    }

    /// <summary>
    /// The child source of a bundled wrapper: the parser value of the argument
    /// after the first short option with <c>c</c> that follows a POSIX shell word.
    /// </summary>
    /// <remarks>
    /// SECURITY: the child source must be the decoded value that the shell
    /// passes to the wrapper. A raw-text split that ignores escapes can end
    /// the child early, for example at <c>\"</c>, and hide the commands after
    /// that point from approval and hard-deny policy.
    /// </remarks>
    private abstract record WrapperSource
    {
        private WrapperSource()
        {
        }

        internal sealed record Missing : WrapperSource;

        internal sealed record Dynamic : WrapperSource;

        internal sealed record Exact(string Source) : WrapperSource;
    }

    private static WrapperSource FindWrapperSource(CommandOccurrence occurrence)
    {
        var words = occurrence.Clause.Verb.Tokens
            .Select(static token => (Raw: token, Value: (ShellValueDomain?)null))
            .Concat(occurrence.Arguments
                .Where(static argument => !argument.Argument.IsCwdAttribution)
                .Select(static argument => (Raw: argument.Argument.Raw, Value: (ShellValueDomain?)argument.Value)))
            .ToList();
        var invoker = words.FindIndex(static word => IsShellInvokerToken(word.Raw));
        for (var index = invoker + 1; invoker >= 0 && index < words.Count - 1; index++)
        {
            if (IsShortCommandOption(words[index].Raw))
            {
                return words[index + 1].Value is ShellValueDomain.Exact exact
                    ? new WrapperSource.Exact(exact.Value)
                    : new WrapperSource.Dynamic();
            }
        }

        return new WrapperSource.Missing();
    }

    private static bool IsShortCommandOption(string raw)
        => raw.Length > 1
           && raw[0] == '-'
           && !raw.StartsWith("--", StringComparison.Ordinal)
           && raw.AsSpan(1).IndexOf('c') >= 0;

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

        return clause.Args.Any(static arg => IsShortCommandOption(arg.Raw));
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
                LegacyShellTextScan.TrimShellPunctuation(clause.Verb.Tokens[0]),
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

            var token = LegacyShellTextScan.TrimShellPunctuation(arg.Raw);
            if (token is "--" or "-p")
                continue;

            return IsShellInvokerToken(token);
        }

        return false;
    }

    private static bool IsShellInvokerToken(string token)
        => ShellVerbPolicyData.PosixShellInvokers.Contains(
            LegacyShellTextScan.TrimShellPunctuation(token));

    /// <summary>
    /// Splits Bash source at each unquoted list operator: <c>;</c>, <c>&amp;&amp;</c>,
    /// <c>||</c>, and a background <c>&amp;</c>. Only the hard-deny screen uses the elements.
    /// </summary>
    private static List<string> SplitListElements(string command)
    {
        var elements = new List<string>();
        var start = 0;
        foreach (var list in FindListOperators(command))
        {
            elements.Add(command[start..list.Index]);
            start = list.Index + list.Length;
        }

        elements.Add(command[start..]);
        return elements;
    }

    private static List<(int Index, int Length, bool IsBackground)> FindListOperators(string command)
    {
        var operators = new List<(int Index, int Length, bool IsBackground)>();
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

            if (quote is not null)
                continue;

            var previous = i > 0 ? command[i - 1] : '\0';
            var next = i + 1 < command.Length ? command[i + 1] : '\0';
            if (ch == ';')
            {
                operators.Add((i, 1, false));
            }
            else if (ch is '&' or '|' && next == ch)
            {
                operators.Add((i, 2, false));
                i++;
            }
            else if (ch == '&' && previous is not ('&' or '>') && next != '>')
            {
                operators.Add((i, 1, true));
            }
        }

        return operators;
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

    /// <summary>
    /// Gets the managed temporary location whose variables the parser used, or
    /// <see langword="null"/> when the call had none. A later parse of part of this
    /// source uses the same location.
    /// </summary>
    internal ManagedTemporaryLocation? ManagedTemporary { get; init; }

    public IReadOnlyList<CommandOccurrence> Commands { get; }

    internal IReadOnlyList<Clause> DenyOnlyClauses { get; }

    /// <summary>
    /// The clauses of the hard-deny screen for unresolved Bash source. They
    /// never become approval candidates.
    /// </summary>
    internal IReadOnlyList<Clause> ScreenClauses { get; init; } = [];

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
                && HasUnsupportedArgumentDomain(argument)
                && !IsUnknownOutputData(command, argument))
            // A glob in a directory segment can hide traversal or a symlink.
            // Only a leaf glob has a fixed directory scope.
            || command.Clause.Args.Any(arg =>
                ShellGlobPath.HasUnresolvedDescendantScope(arg, Environment.PathStyle))
            || HasUnresolvedRedirect(command);

    internal static bool TryCollectKnownExecutionRegionArguments(
        ShellSyntaxNode node,
        ISet<ClauseElement> arguments,
        AssignmentSyntaxReconciliation assignmentSyntax)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(assignmentSyntax);

        return node switch
        {
            ShellBlockSyntax block => block.Statements.All(statement =>
                TryCollectKnownExecutionRegionArguments(statement, arguments, assignmentSyntax)),
            SimpleCommandSyntax command => command.ExecutionRegions.All(region =>
                    TryCollectKnownExecutionRegionArguments(region, arguments, assignmentSyntax))
                && command.Substitutions.All(substitution =>
                    TryCollectKnownExecutionRegionArguments(substitution, arguments, assignmentSyntax)),
            PipelineSyntax pipeline => pipeline.Stages.All(stage =>
                TryCollectKnownExecutionRegionArguments(stage, arguments, assignmentSyntax)),
            CommandListSyntax list => list.Items.All(item =>
                TryCollectKnownExecutionRegionArguments(item.Command, arguments, assignmentSyntax)),
            GroupSyntax group => TryCollectKnownExecutionRegionArguments(
                group.Body,
                arguments,
                assignmentSyntax),
            ForEachSyntax loop => TryCollectKnownExecutionRegionArguments(
                    loop.IteratorCommands,
                    arguments,
                    assignmentSyntax)
                && TryCollectKnownExecutionRegionArguments(loop.Body, arguments, assignmentSyntax),
            CommandSubstitutionSyntax substitution => TryCollectKnownExecutionRegionArguments(
                substitution.Body,
                arguments,
                assignmentSyntax),
            ExecutionRegionSyntax region => TryCollectKnownExecutionRegion(
                region,
                arguments,
                assignmentSyntax),
            _ => assignmentSyntax.TryConsume(node)
        };
    }

    private static bool TryCollectKnownExecutionRegion(
        ExecutionRegionSyntax region,
        ISet<ClauseElement> arguments,
        AssignmentSyntaxReconciliation assignmentSyntax)
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

        if (!TryCollectKnownExecutionRegionArguments(
                region.Body,
                arguments,
                assignmentSyntax))
            return false;

        if (region.Origin == ExecutionRegionOrigin.CommandArgument)
            arguments.Add(region.HostArgument!);

        return true;
    }

    /// <summary>
    /// Reconciles the non-exhaustive beta.4 syntax view with public assignment facts.
    /// An exact shell-state source span can discharge one otherwise unknown syntax node.
    /// </summary>
    internal sealed class AssignmentSyntaxReconciliation
    {
        private readonly string _source;
        private readonly Dictionary<(int Start, int Length), AssignmentIdentity> _unconsumed;

        private AssignmentSyntaxReconciliation(
            string source,
            Dictionary<(int Start, int Length), AssignmentIdentity> unconsumed)
        {
            _source = source;
            _unconsumed = unconsumed;
        }

        internal bool AllConsumed => _unconsumed.Count == 0;

        internal static bool TryCreate(
            string source,
            IReadOnlyList<CommandOccurrence> commands,
            out AssignmentSyntaxReconciliation reconciliation)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(commands);
            var spans = new Dictionary<(int Start, int Length), AssignmentIdentity>();
            foreach (var assignment in commands
                         .SelectMany(static command => command.Assignments)
                         .Where(static assignment =>
                             assignment.Scope == ShellVariableAssignmentScope.ShellState))
            {
                if (assignment.SourceStart < 0
                    || assignment.SourceLength <= 0
                    || assignment.SourceStart > source.Length - assignment.SourceLength
                    || assignment.AuthoredValue is not ShellValueDomain.Exact authored
                    || assignment.EffectiveValue is not ShellValueDomain.Exact effective)
                {
                    reconciliation = null!;
                    return false;
                }

                var span = (assignment.SourceStart, assignment.SourceLength);
                var identity = new AssignmentIdentity(
                    assignment.Name,
                    authored.Value,
                    effective.Value,
                    assignment.MayAffectProcessEnvironment,
                    source.Substring(span.SourceStart, span.SourceLength));
                if (spans.TryGetValue(span, out var existing))
                {
                    if (existing != identity)
                    {
                        reconciliation = null!;
                        return false;
                    }

                    continue;
                }

                spans.Add(span, identity);
            }

            reconciliation = new AssignmentSyntaxReconciliation(source, spans);
            return true;
        }

        internal bool TryConsume(ShellSyntaxNode node)
        {
            if (node.SourceStart is not { } start
                || node.SourceLength is not { } length
                || start < 0
                || length <= 0
                || start > _source.Length - length
                || !_unconsumed.Remove((start, length), out var assignment))
            {
                return false;
            }

            return _source.AsSpan(start, length)
                .SequenceEqual(assignment.Source.AsSpan());
        }

        private sealed record AssignmentIdentity(
            string Name,
            string AuthoredValue,
            string EffectiveValue,
            bool MayAffectProcessEnvironment,
            string Source);
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

    private static bool IsUnknownOutputData(
        CommandOccurrence command,
        AnalyzedArgument argument)
    {
        // The parser proves the verb and every child command before this check.
        // A bare status value cannot add an option or a path to an output command.
        return command.Redirects.Count == 0
               && !command.Clause.Verb.IsDynamic
               && command.Clause.Verb.Tokens.Count == 1
               && ShellVerbPolicyData.SingleTokenSideEffectVerbs.Contains(command.Clause.Verb.Tokens[0])
               && argument.Argument.Kind == ArgKind.EnvVar
               && !argument.Argument.IsPath
               && argument.Argument.Raw == "$?"
               && argument.Value is ShellValueDomain.Unknown
               && argument.AuthoredFileSystemValue is ShellValueDomain.Unknown
               && argument.AuthoredNonFileSystemValue is ShellValueDomain.Unknown;
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
                LegacyShellTextScan.TrimShellPunctuation(clause.Verb.Tokens[0]),
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
                LegacyShellTextScan.TrimShellPunctuation(clause.Verb.Tokens[0]),
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
