// -----------------------------------------------------------------------
// <copyright file="ShellCommandAnalysis.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Configuration;
using Netclaw.Security.Authorization.Filesystem;
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
        var provesNoCommand = false;
        var failure = Analyze(
            command,
            workingDirectory,
            _screenState is null ? _environment.CreateLaunchEnvironment(temporary) : null,
            depth: 0,
            commands,
            denyOnlyClauses,
            knownRegionArguments,
            ref syntaxProofComplete,
            ref provesNoCommand);
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
            ProvesNoCommand = provesNoCommand,
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

    // SECURITY: a parse can stop part way, for example at a bash -lc child whose
    // directory is unknown after a cd that can fail. Its commands so far still
    // meet the screen, but the rest of the source is not seen. Only a complete
    // parse ends the search; otherwise each list element is screened again.
    private bool TryCollectScreenClauses(string source, string? workingDirectory, List<Clause> clauses)
    {
        foreach (var state in ScreenStates)
        {
            var screened = new ShellCommandAnalyzer(_environment, state).Analyze(source, workingDirectory);
            clauses.AddRange(screened.Commands.Select(static occurrence => occurrence.Clause));
            if (screened.Failure == ShellAnalysisFailure.None)
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
        ref bool syntaxProofComplete,
        ref bool provesNoCommand)
    {
        if (depth > MaxWrapperDepth)
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
        {
            // Owner decision (October 2026): a Bash source that parses with no
            // command runs no program, for example an assignment (x=1) or a
            // comment. The analysis stays unresolved for every other rule. Only
            // the authorizer reads this fact. The caller of a wrapper child
            // drops it. The hard-deny screen, PowerShell, and a blank source
            // never get it.
            provesNoCommand = _screenState is null
                              && _environment.Grammar == ShellGrammar.Bash
                              && !string.IsNullOrWhiteSpace(command);
            return ShellAnalysisFailure.Unresolved;
        }

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
            var childProvesNoCommand = false;
            var failure = Analyze(
                exactSource.Source,
                innerWorkingDirectory,
                launchEnvironment: null,
                depth + 1,
                commands,
                denyOnlyClauses,
                knownRegionArguments,
                ref syntaxProofComplete,
                ref childProvesNoCommand);
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

    private static List<(int Index, int Length)> FindListOperators(string command)
    {
        var operators = new List<(int Index, int Length)>();
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
                operators.Add((i, 1));
            }
            else if (ch is '&' or '|' && next == ch)
            {
                operators.Add((i, 2));
                i++;
            }
            else if (ch == '&' && previous is not ('&' or '>') && next != '>')
            {
                operators.Add((i, 1));
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

/// <summary>How much of one command occurrence the parser could not prove.</summary>
internal enum ShellUnresolvedPart
{
    /// <summary>The parser proves the whole command.</summary>
    None = 0,

    /// <summary>
    /// Only an operand value is unknown. The program word, the command
    /// structure, the working directory, and each redirect are proved.
    /// </summary>
    Operand = 1,

    /// <summary>
    /// The program word, the structure, the directory, a redirect, or the
    /// scope of a glob with a wildcard in a directory segment is unknown. A glob
    /// can name a protected path that no screen checks yet, so the D1 rule for
    /// an unknown operand does not apply to it.
    /// </summary>
    Command = 2,
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
        SyntaxProofComplete = syntaxProofComplete;
        var unresolvedParts = new Dictionary<CommandOccurrence, ShellUnresolvedPart>(
            ReferenceEqualityComparer.Instance);
        var expansionOnly = new HashSet<CommandOccurrence>(ReferenceEqualityComparer.Instance);
        var runsNoProgram = new HashSet<CommandOccurrence>(ReferenceEqualityComparer.Instance);
        foreach (var command in Commands)
        {
            var part = ClassifyUnresolvedPart(command, knownRegionArguments);
            // The expansion rule applies after the other causes. When it is the
            // only cause of an exact command, a rewrite of the words can resolve
            // the command, so the coordinator can keep its rewrite correction.
            if (part != ShellUnresolvedPart.Command && HasUnboundedPathnameExpansion(command))
            {
                part = ShellUnresolvedPart.Command;
                expansionOnly.Add(command);
            }

            if (part != ShellUnresolvedPart.None)
                unresolvedParts[command] = part;
            else if (RunsNoProgramWhenProved(command))
                runsNoProgram.Add(command);
        }

        _unresolvedParts = unresolvedParts;
        _expansionOnly = expansionOnly;
        _runsNoProgram = runsNoProgram;
        HasDynamicSyntax = !syntaxProofComplete || unresolvedParts.Count > 0;
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

    /// <summary>
    /// Gets whether any part of the source is unresolved. Advice for the whole
    /// call reads it. Approval reads <see cref="GetUnresolvedPart"/> for each
    /// command, so one unresolved command does not hide the others.
    /// </summary>
    public bool HasDynamicSyntax { get; }

    /// <summary>
    /// Gets whether the PowerShell assignment and execution-region proof is
    /// complete. Bash sources always have a complete proof here.
    /// </summary>
    internal bool SyntaxProofComplete { get; }

    private readonly IReadOnlyDictionary<CommandOccurrence, ShellUnresolvedPart> _unresolvedParts;

    /// <summary>Returns how much of one command of this analysis the parser could not prove.</summary>
    internal ShellUnresolvedPart GetUnresolvedPart(CommandOccurrence command)
        => _unresolvedParts.TryGetValue(command, out var part) ? part : ShellUnresolvedPart.None;

    private readonly IReadOnlySet<CommandOccurrence> _expansionOnly;

    private readonly IReadOnlySet<CommandOccurrence> _runsNoProgram;

    /// <summary>
    /// True when the Bash source parsed completely and holds no command, for
    /// example an assignment (<c>x=1</c>) or a comment. Such a call runs no
    /// program. <see cref="IsResolved"/> stays false for every other rule.
    /// </summary>
    internal bool ProvesNoCommand { get; init; }

    /// <summary>
    /// Returns true when the parser proves that the command runs no program:
    /// a command with only redirects (<c>&gt; file</c>), or a Bash data command
    /// (<see cref="ShellVerbPolicyData.IsDataCommand"/>). Its only effects
    /// outside the shell are its redirects, and each redirect target is one
    /// proved file (see <see cref="HasPlainFileRedirects"/>).
    /// </summary>
    /// <remarks>
    /// SECURITY: owner decision (October 2026). Such a command gets no grant
    /// candidate. The authorizer judges each redirect target with the file
    /// rules of the audience, and never prompts for the command. A data
    /// command that a shell-state assignment reaches qualifies only when its
    /// operands are proved data, as for the approval exemption (F3). A
    /// command substitution in an operand is its own occurrence and keeps its
    /// own decision.
    /// </remarks>
    internal bool RunsNoProgram(CommandOccurrence command)
        => _runsNoProgram.Contains(command);

    /// <summary>
    /// Returns true when the only cause that makes the command exact is a word
    /// that Bash can glob, with an unknown value
    /// (<see cref="HasUnboundedPathnameExpansion(CommandOccurrence)"/>). The
    /// command stays exact, so no grant and no reviewed phrase covers it.
    /// </summary>
    internal bool IsUnresolvedOnlyByPathnameExpansion(CommandOccurrence command)
        => _expansionOnly.Contains(command);

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
            case ConditionLoopSyntax loop:
                CollectSourceAuthenticDenyOnlyClauses(loop.Condition, source, clauses, seen);
                CollectSourceAuthenticDenyOnlyClauses(loop.Body, source, clauses, seen);
                break;
            case ConditionalSyntax conditional:
                foreach (var branch in conditional.Branches)
                {
                    CollectSourceAuthenticDenyOnlyClauses(branch.Condition, source, clauses, seen);
                    CollectSourceAuthenticDenyOnlyClauses(branch.Body, source, clauses, seen);
                }

                if (conditional.Else is { } otherwise)
                    CollectSourceAuthenticDenyOnlyClauses(otherwise, source, clauses, seen);
                break;
            case CaseSyntax caseStatement:
                foreach (var item in caseStatement.Items)
                    CollectSourceAuthenticDenyOnlyClauses(item.Body, source, clauses, seen);
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

    private ShellUnresolvedPart ClassifyUnresolvedPart(
        CommandOccurrence command,
        IReadOnlySet<ClauseElement> accountedRegionArguments)
    {
        // ShellSyntaxTree marks a command with only redirects as incomplete,
        // because it has no command word. Netclaw proves the rest of it here.
        if (IsRedirectOnlyCommand(command))
        {
            return Environment.Grammar == ShellGrammar.Bash
                   && HasPlainFileRedirects(command)
                ? ShellUnresolvedPart.None
                : ShellUnresolvedPart.Command;
        }

        if (!HasKnownStructure(command)
            || HasUnsupportedWorkingDirectory(command.WorkingDirectory)
            || command.Clause.Verb.IsDynamic
            || HasDynamicProgramWord(command)
            || HasUnresolvedRedirect(command))
        {
            return ShellUnresolvedPart.Command;
        }

        // A data command runs no program, so the file rules judge its redirect
        // targets (RunsNoProgram). A target that is not one proved file (a
        // glob, a set of values, or a Bash special device) cannot get that
        // judgment, so the command is exact: the prompt shows its full text.
        if (Environment.Grammar == ShellGrammar.Bash
            && command.Redirects.Count > 0
            && IsBashDataCommand(command)
            && !HasPlainFileRedirects(command))
        {
            return ShellUnresolvedPart.Command;
        }

        // A glob in a directory segment can hide traversal or a symlink. It has
        // a fixed reach only with the parser glob fact (ShellSyntaxTree
        // 0.4.0-beta.11): the covering directory, to the segment depth.
        // SECURITY: when such a glob can expand to a word that starts with "-",
        // the expansion can add an option. Decision D1 then applies: the word is
        // an unknown operand. A leaf glob keeps its earlier scope rule.
        var globMayAddOption = false;
        foreach (var arg in command.Clause.Args)
        {
            if (!ShellGlobPath.HasUnresolvedDescendantScope(arg, Environment.PathStyle))
                continue;

            if (ShellGlobScope.FindGlobPattern(command, arg) is not { } pattern)
                return ShellUnresolvedPart.Command;

            globMayAddOption |= pattern.Glob!.MayStartWithDash;
        }

        if (HasOnlyDataOperands(command))
            return ShellUnresolvedPart.None;

        // A test builtin whose operands are not data can run a subscript.
        return globMayAddOption
               || HasUnresolvedOperand(command, accountedRegionArguments)
               || HasTestBuiltinVerb(command)
            ? ShellUnresolvedPart.Operand
            : ShellUnresolvedPart.None;
    }

    /// <summary>
    /// Returns true when the parser proves the structure of one command: the
    /// command is complete, and its role and each ancestor are known.
    /// </summary>
    /// <remarks>
    /// A literal twin has the structure of one top-level command, so the
    /// structure of its source command must pass this check.
    /// </remarks>
    internal static bool HasKnownStructure(CommandOccurrence command)
        => command.IsComplete
           && HasKnownRoleAndAncestors(command);

    private static bool HasKnownRoleAndAncestors(CommandOccurrence command)
        => Enum.IsDefined(command.ImmediateRole)
           && command.ImmediateRole != CommandOccurrenceRole.Unknown
           && !command.Ancestry.Any(static frame =>
               !IsKnownAncestor(frame.Ancestor)
               || !Enum.IsDefined(frame.Region)
               || frame.Region == CommandAncestryRegion.Unknown);

    /// <summary>
    /// Returns true when the command has no command word and no assignment:
    /// each element is a redirect (<c>&gt; file</c>, <c>&lt; file</c>). Bash
    /// opens each redirect target and runs no program.
    /// </summary>
    /// <remarks>
    /// The rule composes general parser facts: no verb token, a verb that is
    /// not dynamic, and the role of each element. The structure (role and
    /// ancestors) must be known, as for <see cref="HasKnownStructure"/>.
    /// </remarks>
    internal static bool IsRedirectOnlyCommand(CommandOccurrence command)
        => command.Clause.Verb.Tokens.Count == 0
           && !command.Clause.Verb.IsDynamic
           && command.Clause.Elements.Count > 0
           && command.Clause.Elements.All(static element => element.Role == ClauseElementRole.Redirect)
           && command.Assignments.Count == 0
           && command.Arguments.Count == 0
           && command.FileSystemTreeAccesses.Count == 0
           && command.Redirects.Count > 0
           && command.Redirects.Count == command.Clause.Redirects.Count
           && HasKnownRoleAndAncestors(command);

    // Owner decision (October 2026): a data command with redirects runs no
    // program. ClassifyUnresolvedPart proved the whole command (part None),
    // and with it each redirect target (HasPlainFileRedirects). The grammar
    // check stays: PowerShell has an echo alias, and its redirects get no proof.
    private bool RunsNoProgramWhenProved(CommandOccurrence command)
        => Environment.Grammar == ShellGrammar.Bash
           && (IsRedirectOnlyCommand(command)
               || IsBashDataCommand(command)
               && (command.Assignments.Count == 0
                   || HasProvedDataOperands(command, isTestBuiltin: HasTestBuiltinVerb(command))));

    /// <summary>
    /// Returns true when each redirect of a Bash command is proved and each file
    /// redirect target is one plain file: an exact absolute POSIX value. A
    /// descriptor copy, move, or close (<c>2&gt;&amp;1</c>) opens no file.
    /// </summary>
    /// <remarks>
    /// SECURITY: Bash gives some paths under <c>/dev/</c> a meaning that is not
    /// a file: <c>/dev/tcp/host/port</c> and <c>/dev/udp/host/port</c> open a
    /// network connection, and <c>/dev/fd/N</c> copies a descriptor. The file
    /// rules cannot judge such a target, so <c>/dev/null</c> is the only path
    /// below <c>/dev/</c> that qualifies. A here document or a here string is
    /// not a file redirect. It passes only as fixed text on stdin
    /// (<see cref="HasFixedTextStdin(CommandOccurrence, HereDocumentRedirectAnalysis)"/>),
    /// which opens no file, as a descriptor copy opens none. Each other here
    /// document or here string fails the proof check. The check reads the canonical form of the value, so
    /// <c>/dev/./tcp</c> and <c>//dev/tcp</c> also fail. Bash gives a special
    /// meaning only to a word that starts with the literal name; a <c>..</c>
    /// that leaves <c>/dev/</c> puts the rest of the word in the port, which
    /// Bash rejects.
    /// </remarks>
    private static bool HasPlainFileRedirects(CommandOccurrence command)
        => command.Redirects.All(redirect =>
               !HasUnresolvedRedirect(command, redirect)
               && redirect switch
               {
                   FileRedirectAnalysis file => IsPlainFileTarget(file),
                   DescriptorDuplicateRedirectAnalysis or DescriptorMoveRedirectAnalysis or DescriptorCloseRedirectAnalysis => true,
                   HereDocumentRedirectAnalysis or HereStringRedirectAnalysis => true,
                   _ => false
               });

    private static bool IsPlainFileTarget(FileRedirectAnalysis redirect)
    {
        if (redirect.Target is not ShellValueDomain.Exact { Value: { Length: > 0 } value }
            || !value.StartsWith('/')
            || !CanonicalPath.TryCreate(value, relativeBase: null, ShellPathStyle.Posix, out var path))
        {
            return false;
        }

        return path.Value == "/dev/null"
               || !path.Value.StartsWith("/dev/", StringComparison.Ordinal);
    }

    // ShellSyntaxTree 0.4.0-beta.17 gives no command words for a bracket
    // pattern in the program word, such as ["ci","build"], but it reports the
    // word as literal. Bash expands the pattern, so the program is not fixed.
    // With no other word, only the program word can make the words unknown, so
    // the command stays unresolved, as it was with beta.10. Other program words
    // with unknown words (a brace text, a tilde path) keep their decision.
    private static bool HasDynamicProgramWord(CommandOccurrence command)
    {
        var elements = command.Clause.Elements;
        return command.CommandWords is ShellCommandWords.Unknown
               && elements.Count > 0
               && elements[0] is { Role: ClauseElementRole.Verb, Kind: ArgKind.Literal }
               && elements[0].Value.Contains('[', StringComparison.Ordinal)
               && elements.Skip(1).All(static element => element.Role == ClauseElementRole.Redirect);
    }

    private static bool HasUnresolvedOperand(
        CommandOccurrence command,
        IReadOnlySet<ClauseElement> accountedRegionArguments)
        => command.Clause.Args.Any(arg =>
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
                && (HasUnsupportedArgumentDomain(argument) || IsUnscopedVariableWord(argument))
                && !IsUnknownOutputData(command, argument));

    /// <summary>
    /// Returns true when a variable word is not a path word, so it has no path
    /// scope.
    /// </summary>
    /// <remarks>
    /// SECURITY: Netclaw computes a path scope only from a path word that the
    /// parser resolves, from a file word, and from a typed filesystem value. A
    /// variable word such as <c>"$d"</c> is not a path word, also when the
    /// parser proves its value. In
    /// <c>for d in ../x; do dotnet build "$d"; done</c> or
    /// <c>d=../x; dotnet build "$d"</c>, the value <c>../x</c> is outside the
    /// folder, but the candidate keeps only the working directory scope. Such a
    /// word is an unknown operand, so decision D1 lets only a safe phrase or a
    /// grant for anywhere cover the command. ShellSyntaxTree 0.4.0-beta.19
    /// gives a typed filesystem or data value only to a
    /// <see cref="ArgKind.DynamicSkip"/> word, which keeps its own rule above.
    /// A variable word that the parser marks as a path keeps its path scope,
    /// or the path rule above makes it unknown when it has no resolved value.
    /// A word whose proved value is an integer range (<c>"$?"</c>) is data, as
    /// in <see cref="HasUnsupportedArgumentDomain(AnalyzedArgument)"/>.
    /// </remarks>
    private static bool IsUnscopedVariableWord(AnalyzedArgument argument)
        => argument.Argument.Kind == ArgKind.EnvVar
           && !argument.Argument.IsPath
           && argument.Value is not ShellValueDomain.IntegerRange;

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
            ConditionLoopSyntax loop => Enum.IsDefined(loop.LoopKind)
                && loop.LoopKind != ConditionLoopKind.Unknown
                && TryCollectKnownExecutionRegionArguments(loop.Condition, arguments, assignmentSyntax)
                && TryCollectKnownExecutionRegionArguments(loop.Body, arguments, assignmentSyntax),
            ConditionalSyntax conditional => conditional.Branches.Count > 0
                && conditional.Branches.All(branch =>
                    TryCollectKnownExecutionRegionArguments(branch.Condition, arguments, assignmentSyntax)
                    && TryCollectKnownExecutionRegionArguments(branch.Body, arguments, assignmentSyntax))
                && (conditional.Else is null
                    || TryCollectKnownExecutionRegionArguments(conditional.Else, arguments, assignmentSyntax)),
            CaseSyntax caseStatement => caseStatement.Items.All(item =>
                TryCollectKnownExecutionRegionArguments(item.Body, arguments, assignmentSyntax)),
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

    // ShellSyntaxTree 0.4.0-beta.13 adds while, until, if, and case nodes, and
    // beta.14 adds the background group. The state pass joins the facts of each
    // path, so a command inside them has the same proof as a command at the top
    // level. An unknown node or group kind still fails closed.
    private static bool IsKnownAncestor(ShellSyntaxNode ancestor)
        => ancestor is ShellBlockSyntax
            or SimpleCommandSyntax
            or PipelineSyntax
            or CommandListSyntax
            or GroupSyntax
            {
                GroupKind: ShellGroupKind.CurrentScope
                    or ShellGroupKind.IsolatedScope
                    or ShellGroupKind.Background
            }
            or ForEachSyntax
            or ConditionLoopSyntax { LoopKind: ConditionLoopKind.While or ConditionLoopKind.Until }
            or ConditionalSyntax
            or ConditionalBranchSyntax
            or CaseSyntax
            or CaseItemSyntax
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

    /// <summary>
    /// Returns true when every operand of the command is data: an output
    /// command (<c>echo</c>, <c>printf</c>, <c>:</c>, <c>true</c>, <c>false</c>)
    /// prints or ignores its operands, and a test builtin (<c>test</c>,
    /// <c>[</c>) compares them.
    /// </summary>
    /// <remarks>
    /// SECURITY: a dynamic operand of such a command reaches stdout or the exit
    /// status only. It is not the program word, and it is not a redirect target:
    /// <see cref="HasUnresolvedRedirect(CommandOccurrence)"/> checks each
    /// redirect target separately. A command substitution inside an operand is
    /// its own occurrence with its own candidate, so the rule hides no command.
    /// ShellSyntaxTree accepts a dynamic printf operand only after a literal
    /// format, and it rejects <c>printf -v</c>, so no dynamic value reaches the
    /// printf format or a shell variable. A test builtin can evaluate an array
    /// subscript in an operand, so its operands must also pass
    /// <see cref="HasBoundedNameSafeValue(AnalyzedArgument)"/>. The rule is Bash
    /// only: in PowerShell these words are aliases or external programs with
    /// their own parameters. The first verb token decides, because the parser
    /// folds a plain operand into the verb (<c>echo yes</c>, <c>[ abc</c>).
    /// </remarks>
    private bool HasOnlyDataOperands(CommandOccurrence command)
        => Environment.Grammar == ShellGrammar.Bash
           && command.Clause.Verb.Tokens is [var verb, ..]
           && ShellVerbPolicyData.IsDataCommand(verb, ApprovalShell.Bash)
           && (!HasTestBuiltinVerb(command)
               || HasProvedDataOperands(command, isTestBuiltin: true));

    private bool HasTestBuiltinVerb(CommandOccurrence command)
        => Environment.Grammar == ShellGrammar.Bash
           && command.Clause.Verb.Tokens is [var verb, ..]
           && ShellVerbPolicyData.BashTestBuiltins.Contains(verb);

    /// <summary>
    /// Returns true when the parser proves every value of the operand (an exact
    /// value or a finite set) and no value has a <c>[</c>.
    /// </summary>
    /// <remarks>
    /// SECURITY: <c>[ -v 'a[$(cmd)]' ]</c> runs <c>cmd</c>, because Bash
    /// evaluates the subscript as arithmetic. Arithmetic also evaluates the
    /// value of a variable that it names, so <c>[ -v 'a[x]' ]</c> runs a
    /// substitution in the value of <c>x</c>. A name without <c>[</c> has no
    /// subscript, and the other test operators do not evaluate their operands.
    /// An unknown value, a glob match, or a file name can hold any text, so
    /// such an operand is not data. The rule reads only typed values. It does
    /// not parse the test operators.
    /// </remarks>
    private static bool HasBoundedNameSafeValue(AnalyzedArgument argument)
        => argument.Value switch
        {
            ShellValueDomain.Exact exact => HasNoSubscript(exact.Value),
            ShellValueDomain.FiniteSet finite => finite.Values.All(HasNoSubscript),
            _ => false
        };

    private static bool HasNoSubscript(string? value)
        => value is not null && !value.Contains('[', StringComparison.Ordinal);

    /// <summary>
    /// Returns true when each operand of a Bash data command is proved data. An
    /// output operand needs a word that Bash cannot glob, or a proved authored
    /// value (an exact value or a finite set) with no glob character. A test
    /// operand needs
    /// <see cref="HasBoundedNameSafeValue(AnalyzedArgument)"/>.
    /// </summary>
    /// <remarks>
    /// SECURITY: an unquoted word with an unknown value, such as <c>$n</c> or
    /// <c>../"$d"/*</c>, gets pathname expansion. ShellSyntaxTree gives no path
    /// for such a word, so the protected-path screen cannot see what it lists.
    /// ShellSyntaxTree 0.4.0-beta.19 reports
    /// <see cref="AnalyzedArgument.MayPathnameExpand"/> from the authored word,
    /// so a quoted part such as <c>pre"$n"</c> is data. A quoted unknown value is still not data for a test
    /// builtin, because a <c>-v</c> subscript can run code.
    /// </remarks>
    internal static bool HasProvedDataOperands(CommandOccurrence command, bool isTestBuiltin)
        => command.Arguments.All(argument => isTestBuiltin
            ? HasBoundedNameSafeValue(argument)
            : !argument.MayPathnameExpand
              || HasGlobFreeAuthoredValue(argument));

    /// <summary>
    /// Returns true when a Bash operand with an unknown value can undergo
    /// pathname expansion at run time.
    /// </summary>
    /// <remarks>
    /// SECURITY: ShellSyntaxTree 0.4.0-beta.19 reports
    /// <see cref="AnalyzedArgument.MayPathnameExpand"/> from the authored word.
    /// A word such as <c>"${d}ret"/*</c>, <c>$n</c>, or
    /// <c>~/.netclaw/{keys,config}/key-1.xml</c> has no proved value and no
    /// glob scope, so it can list or read the names in any directory, also a
    /// protected one. Such a command gets one exact candidate with
    /// <c>Once</c> and <c>Deny</c> only; no grant and no reviewed phrase covers
    /// it, and an unattended run denies it. A proved glob scope
    /// (<see cref="ShellValueDomain.PathPattern"/>) keeps decision D5, and a
    /// quoted unknown value keeps decision D1.
    /// Owner decision (#2349): a Bash data command keeps its earlier rule. An
    /// output command (<c>echo</c>, <c>printf</c>) prints its operands, so the
    /// worst case is file names in the output, never file contents. A test
    /// builtin keeps the proved-value rule of
    /// <see cref="HasBoundedNameSafeValue(AnalyzedArgument)"/>.
    /// The rule does not read <c>MayFieldSplit</c>. A word that can split but
    /// cannot glob is a quoted <c>"$@"</c> or a bounded arithmetic word. Field
    /// splitting only cuts a value into more words, and each word keeps the
    /// check of a normal operand: an unknown value gets decision D1, as a
    /// quoted <c>"$x"</c> does. Splitting cannot add a path that the D1 gap
    /// does not already accept. The agent cannot set <c>$@</c> without consent:
    /// <c>set --</c> needs consent, and a function definition fails closed.
    /// </remarks>
    private bool HasUnboundedPathnameExpansion(CommandOccurrence command)
        => Environment.Grammar == ShellGrammar.Bash
           && !IsBashDataCommand(command)
           && command.Arguments.Any(IsUnboundedPathnameExpansionWord);

    private static bool IsUnboundedPathnameExpansionWord(AnalyzedArgument argument)
        => argument.MayPathnameExpand
           && argument.Value is ShellValueDomain.Unknown
           && argument.Argument.Kind != ArgKind.Glob
           && !HasGlobFreeAuthoredValue(argument)
           && !IsStatusWord(argument);

    private static bool IsBashDataCommand(CommandOccurrence command)
        => command.Clause.Verb.Tokens is [var verb, ..]
           && ShellVerbPolicyData.IsDataCommand(verb, ApprovalShell.Bash);

    // `$?` is an exit status: an integer with no glob character.
    private static bool IsStatusWord(AnalyzedArgument argument)
        => argument.Argument.Kind == ArgKind.EnvVar
           && argument.Argument.Raw == "$?";

    /// <summary>
    /// Returns true when the parser proves each authored value of the word
    /// before splitting and pathname expansion, and no value has a glob
    /// character. Bash then has nothing to expand, as for <c>$r</c> in
    /// <c>for r in 1 2; do gh run view $r; done</c>.
    /// </summary>
    internal static bool HasGlobFreeAuthoredValue(AnalyzedArgument argument)
        => argument.AuthoredValue switch
        {
            ShellValueDomain.Exact exact => HasNoGlobCharacter(exact.Value),
            ShellValueDomain.FiniteSet finite => finite.Values.All(HasNoGlobCharacter),
            _ => false
        };

    private static bool HasNoGlobCharacter(string? value)
        => value is not null && value.IndexOfAny(['*', '?', '[']) < 0;

    /// <summary>
    /// Returns the source text of each word that makes a Bash command exact by
    /// the pathname-expansion rule
    /// (<see cref="HasUnboundedPathnameExpansion(CommandOccurrence)"/>).
    /// </summary>
    /// <remarks>
    /// The approval coordinator names these words in its quote correction. In
    /// double quotes, such a word gets no pathname expansion and no field
    /// splitting, so its unknown value is one operand (decision D1). The list
    /// grants no authority.
    /// </remarks>
    internal static IReadOnlyList<string> GetUnboundedPathnameExpansionWords(CommandOccurrence command)
        => command.Arguments
            .Where(IsUnboundedPathnameExpansionWord)
            .Select(static argument => argument.Argument.Raw)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static bool IsUnknownOutputData(
        CommandOccurrence command,
        AnalyzedArgument argument)
    {
        // The parser proves the verb and every child command before this check.
        // A bare status value cannot add an option or a path to an output command.
        // PowerShell keeps this rule; Bash output operands use HasOnlyDataOperands.
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
                !HasFixedTextStdin(occurrence, heredoc),
            HereStringRedirectAnalysis hereString =>
                !HasFixedTextStdin(occurrence, hereString),
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

    /// <summary>
    /// Returns true when a heredoc gives fixed text on stdin to a command that
    /// can take it as data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A heredoc with a quoted delimiter does not expand its body. Netclaw
    /// treats the text as it treats text from a pipe: it reads no path and no
    /// command from it. The command keeps its normal candidate: a grant for
    /// <c>python3</c> covers <c>python3 - &lt;&lt;'EOF'</c> as it covers
    /// <c>python3 -c '...'</c>. Each interpreter rule that applies to the
    /// argument form also applies to this form.
    /// </para>
    /// <para>
    /// SECURITY: an unquoted delimiter expands the body. ShellSyntaxTree marks
    /// such a heredoc <c>Expand</c> and does not prove that the body has no
    /// expansion, so it stays unresolved. Some receivers also stay unresolved:
    /// see <see cref="CanTakeFixedStdinText(CommandOccurrence)"/>.
    /// </para>
    /// </remarks>
    private static bool HasFixedTextStdin(
        CommandOccurrence occurrence,
        HereDocumentRedirectAnalysis redirect)
        => IsStandardInputSource(redirect.Source)
            && CanTakeFixedStdinText(occurrence)
            && HasLiteralHereDocument(
                redirect.Document,
                occurrence.Clause.IsCommandStringWrapped);

    /// <summary>
    /// Returns true when a here string gives a proved value on stdin to a
    /// command that can take it as data. The rule is the heredoc rule of
    /// <see cref="HasFixedTextStdin(CommandOccurrence, HereDocumentRedirectAnalysis)"/>.
    /// </summary>
    /// <remarks>
    /// SECURITY: ShellSyntaxTree gives an <c>Exact</c> or <c>FiniteSet</c>
    /// value only when it proves the word. A word with an unknown variable or
    /// a command substitution has an unknown value and stays unresolved.
    /// </remarks>
    private static bool HasFixedTextStdin(
        CommandOccurrence occurrence,
        HereStringRedirectAnalysis redirect)
        => IsStandardInputSource(redirect.Source)
            && CanTakeFixedStdinText(occurrence)
            && HasBoundedData(redirect.Data);

    /// <summary>
    /// Returns true when the command has known command words and no word that
    /// can name a shell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A command with Unknown command words has no grant identity, for example
    /// <c>python3 - "$f"</c> in a loop. Its literal twins (F1) cannot carry a
    /// heredoc, so the resolved command would get a rewrite correction that no
    /// rewrite can satisfy. Such a command keeps the one exact candidate.
    /// </para>
    /// <para>
    /// SECURITY: a shell reads stdin as a script. For <c>bash -c '...'</c>,
    /// Netclaw analyzes the script as child commands, so the hard-deny and
    /// path rules see each command. Netclaw does not analyze the text of a
    /// heredoc or a here string as a script. Thus such text to a shell stays
    /// unresolved, and a grant for the shell does not cover it. Text from a
    /// pipe (<c>printf ... | bash</c>) is outside this rule.
    /// </para>
    /// <para>
    /// The file name of each verb word decides (<c>bash</c>, <c>./bash</c>,
    /// <c>/usr/local/bin/bash</c>, <c>env sh</c>, <c>xargs bash</c>). An
    /// argument with a proved value that is a shell file name
    /// (<c>timeout 5 /opt/x/bash</c>), and an argument with no proved value,
    /// also keep the command unresolved. The existing <c>-c</c> wrapper rule
    /// reads a shell word in an argument in the same way. A shell can also
    /// be one word inside an argument: <c>env -S 'bash -s'</c>,
    /// <c>ssh host 'bash -s'</c>, <c>flock x -c 'bash -s'</c>. Netclaw does
    /// not read the private grammar of a program, so each part of a proved
    /// value between white space gets the same file name test. The cost is
    /// that <c>grep bash &lt;&lt;'EOF'</c> and
    /// <c>grep 'run bash now' &lt;&lt;'EOF'</c> are also unresolved; this is
    /// the safe direction. The shell names are policy data.
    /// </para>
    /// </remarks>
    private static bool CanTakeFixedStdinText(CommandOccurrence occurrence)
        => occurrence.CommandWords is ShellCommandWords.Known
            && !HasShellReceiver(occurrence);

    private static bool HasShellReceiver(CommandOccurrence occurrence)
        => occurrence.Clause.Verb.Tokens.Any(ShellVerbPolicyData.IsScriptShellProgram)
            || occurrence.Arguments.Any(static argument =>
                !argument.Argument.IsCwdAttribution
                && MayNameScriptShell(argument.Value));

    private static bool MayNameScriptShell(ShellValueDomain value)
        => value switch
        {
            ShellValueDomain.Exact exact => exact.Value is null
                || HasScriptShellWord(exact.Value),
            ShellValueDomain.FiniteSet finite => finite.Values.Any(static item =>
                item is null || HasScriptShellWord(item)),
            _ => true
        };

    // Owner decision 2026-10-08: each part of the value between white space
    // gets the test, so a shell inside one argument stays strict. To test only
    // the whole value, return ShellVerbPolicyData.IsScriptShellProgram(value).
    private static bool HasScriptShellWord(string value)
        => value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Any(ShellVerbPolicyData.IsScriptShellProgram);

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
