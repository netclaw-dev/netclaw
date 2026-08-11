// -----------------------------------------------------------------------
// <copyright file="IToolApprovalMatcher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections;
using System.Text;
using System.Text.Json;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Security;

/// <summary>
/// One approval candidate extracted from a tool invocation. The verb is the
/// command head plus subcommand chain (e.g., <c>find</c>, <c>git status</c>).
/// The directory identifies a path operand, a redirect parent, or an inherited
/// shell directory. A null directory uses the spawned process cwd.
/// One shell clause can produce multiple candidates when it accesses multiple
/// authorization scopes.
/// </summary>
public sealed record ApprovalCandidate(string Verb, string? Directory);

/// <summary>
/// Tool-specific pattern extraction and matching for the approval system.
/// Each tool type can provide its own matcher to define what constitutes
/// an "intent-level" pattern for approval purposes.
/// </summary>
public interface IToolApprovalMatcher
{
    /// <summary>
    /// Returns the key used to look up this invocation's approval mode in
    /// <c>ToolApprovalConfig.ToolOverrides</c>. Most matchers return the tool
    /// name unchanged; argument-aware matchers may return a context-specific
    /// key so different invocations of the same tool (e.g., a write to a
    /// control-plane file vs. a write to a user file) can be gated
    /// independently.
    /// </summary>
    string GetApprovalModeKey(ToolName toolName, IDictionary<string, object?>? arguments);

    /// <summary>
    /// Returns true if this invocation must require interactive approval on
    /// the Personal audience when no explicit approval policy is configured.
    /// Encapsulates the fail-closed decision so callers do not have to inspect
    /// tool names or approval-key string formats.
    /// </summary>
    bool IsFailClosedOnPersonal(ToolName toolName, IDictionary<string, object?>? arguments);

    /// <summary>
    /// Returns the exact display patterns shown to the user in the approval
    /// prompt body. For shell these are normalized approval units (verb
    /// chain plus any path-aware first argument); for other tools the tool
    /// name. Reused as the retry-exact key for one-shot approvals.
    /// </summary>
    IReadOnlyList<string> ExtractPatterns(ToolName toolName, IDictionary<string, object?>? arguments);

    /// <summary>
    /// Returns the candidate verb chains evaluated against persisted
    /// <see cref="ApprovalEntry"/> records by the gate. For shell these are
    /// pure verb chains (e.g., <c>git push</c>, <c>grep</c>); for other
    /// tools typically <c>[toolName.Value]</c>. Derived from
    /// <see cref="ExtractCandidates"/>.
    /// </summary>
    IReadOnlyList<string> ExtractCandidateVerbs(ToolName toolName, IDictionary<string, object?>? arguments);

    /// <summary>
    /// Returns the candidate <c>(verb, directory)</c> pairs for this tool
    /// invocation. A shell clause can emit candidates for its path operand,
    /// redirect targets, and inherited directory. A null directory uses
    /// <see cref="ToolExecutionContext.Cwd"/>.
    /// </summary>
    IReadOnlyList<ApprovalCandidate> ExtractCandidates(ToolName toolName, IDictionary<string, object?>? arguments);

    /// <summary>
    /// Returns true when every candidate verb chain finds a matching
    /// <see cref="ApprovalEntry"/> under the supplied <paramref name="cwd"/>.
    /// A folder-scoped entry matches when its directory contains the cwd and
    /// no symlink segments exist between the two; a global-wildcard entry
    /// (<c>directory: null</c>) matches any cwd.
    /// </summary>
    bool IsApproved(
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        IReadOnlyList<ApprovalEntry> approvedEntries,
        string? cwd);

    /// <summary>
    /// Returns true when the invocation cannot be cleanly split into
    /// verb-chain approval units — for shell, when the command contains bash
    /// control-flow keywords or unbalanced quotes/brackets. Approval prompts
    /// for messy invocations omit persistent-grant buttons and surface a
    /// "complex command" hint; the user can still grant a single retry via
    /// <c>Once</c>. Non-shell matchers SHALL return <c>false</c>.
    /// </summary>
    bool IsMessy(ToolName toolName, IDictionary<string, object?>? arguments);

    /// <summary>
    /// Formats the tool call for display in the approval prompt header.
    /// </summary>
    string FormatForDisplay(ToolName toolName, IDictionary<string, object?>? arguments);
}

/// <summary>
/// Shell-specific approval matcher bound to one canonical grammar. Approval
/// units and same-language child occurrences come from the selected
/// ShellSyntaxTree parser; unresolved syntax never creates a persistent grant.
/// </summary>
public sealed record ShellApprovalAnalysis(
    IReadOnlyList<string> Patterns,
    IReadOnlyList<ApprovalCandidate> Candidates,
    string DisplayText,
    bool IsMessy);

public sealed class ShellApprovalMatcher : IToolApprovalMatcher
{
    public static readonly ShellApprovalMatcher Instance = new();

    private const string PosixNullDevicePath = "/dev/null";

    private readonly ShellCommandAnalyzer _analyzer;

    public ShellApprovalMatcher()
        : this(ShellExecutionEnvironmentDefaults.Bash)
    {
    }

    public ShellApprovalMatcher(ShellExecutionEnvironment environment)
    {
        Environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _analyzer = new ShellCommandAnalyzer(environment);
    }

    public ShellExecutionEnvironment Environment { get; }

    public string GetApprovalModeKey(ToolName toolName, IDictionary<string, object?>? arguments)
        => toolName.Value;

    public bool IsFailClosedOnPersonal(ToolName toolName, IDictionary<string, object?>? arguments)
        => true;

    public IReadOnlyList<string> ExtractPatterns(ToolName toolName, IDictionary<string, object?>? arguments)
        => AnalyzeInvocation(toolName, arguments).Patterns;

    public ShellApprovalAnalysis AnalyzeInvocation(
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        ShellCommandAnalysis? analysis = null)
    {
        var command = GetCommand(arguments);
        if (string.IsNullOrWhiteSpace(command))
            return new ShellApprovalAnalysis([], [], "(empty command)", IsMessy: false);

        var workingDirectory = GetWorkingDirectory(arguments);
        analysis ??= _analyzer.Analyze(command, workingDirectory);
        ValidateAnalysis(analysis, command, workingDirectory);

        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in ExtractApprovalUnitsViaAnalysis(analysis))
        {
            var normalized = ShellTokenizer.NormalizeApprovalUnit(
                unit,
                workingDirectory,
                Environment.PathStyle);
            if (!string.IsNullOrEmpty(normalized))
                patterns.Add(normalized);
        }

        return new ShellApprovalAnalysis(
            patterns.ToList(),
            ExtractCandidatesViaAnalysis(analysis),
            FormatForDisplay(command, analysis),
            IsMessy(analysis));
    }

    public IReadOnlyList<string> ExtractCandidateVerbs(ToolName toolName, IDictionary<string, object?>? arguments)
        => ExtractCandidates(toolName, arguments)
            .Select(c => c.Verb)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<ApprovalCandidate> ExtractCandidates(ToolName toolName, IDictionary<string, object?>? arguments)
        => AnalyzeInvocation(toolName, arguments).Candidates;

    private void ValidateAnalysis(
        ShellCommandAnalysis analysis,
        string command,
        string? workingDirectory)
    {
        if (!ReferenceEquals(analysis.Environment, Environment))
            throw new ArgumentException(
                "The command analysis belongs to another shell environment.",
                nameof(analysis));
        if (!string.Equals(analysis.Source, command, StringComparison.Ordinal)
            || !string.Equals(
                analysis.WorkingDirectory,
                workingDirectory,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The command analysis does not match the submitted source and working directory.",
                nameof(analysis));
        }
    }

    private IReadOnlyList<ApprovalCandidate> ExtractCandidatesViaAnalysis(
        ShellCommandAnalysis result)
    {
        if (!result.IsResolved
            || result.HasDynamicSyntax
            || HasUnscopedPowerShellProviderOperand(result))
            return [];

        var workingDirectory = result.WorkingDirectory;

        // The prompt groups a pipe as one approval unit. Authorization still
        // checks each clause so an unsafe tail cannot hide behind a safe head.
        var seen = new HashSet<(string, string?)>();
        var candidates = new List<ApprovalCandidate>();

        foreach (var occurrence in result.Commands)
        {
            var clause = occurrence.Clause;
            // ShellSyntaxTree's greedy verb walk (SPEC §6.1) folds
            // lowercase-leading value tokens into the verb chain (`git tag
            // v0.4.2`, `git show aa211dcb`, `git checkout feature2`), while
            // digit-leading ones (`0.4.2`) stop the walk and land in Args
            // (verb stays `git tag`). Both are call-specific values, not
            // approvable intent, so strip them off the chain before gating —
            // otherwise `git tag v0.4.2` would miss a `git tag` grant that
            // `git tag 0.4.2` matches. Mirrors the value-termination in
            // ReconstructClauseText so the gate candidate and the persisted
            // pattern normalize identically.
            if (clause.Verb.IsDynamic)
                continue;

            var parsedVerb = clause.Verb.CanonicalVerb
                ?? string.Join(" ", TrimTrailingValueTokens(clause.Verb.Tokens));
            var verb = ShellTokenizer.ApplyVerbShortCircuit(parsedVerb);
            if (string.IsNullOrEmpty(verb))
                continue;

            var isSideEffectVerb = ShellTokenizer.SingleTokenSideEffectVerbs.Contains(verb);
            var directories = ResolveCommandDirectories(
                occurrence,
                verb,
                isSideEffectVerb,
                workingDirectory,
                Environment.PathStyle);
            if (directories is null)
                return [];

            foreach (var directory in directories)
            {
                var key = (verb.ToLowerInvariant(), directory);
                if (seen.Add(key))
                    candidates.Add(new ApprovalCandidate(verb, directory));
            }
        }

        return candidates;
    }

    private static IReadOnlyList<string?>? ResolveCommandDirectories(
        ShellSyntaxTree.CommandOccurrence occurrence,
        string verb,
        bool isSideEffectVerb,
        string? workingDirectory,
        ShellPathStyle pathStyle)
    {
        var clause = occurrence.Clause;
        var directories = new List<string?>();
        var cwdAttribution = clause.Args.FirstOrDefault(static arg => arg.IsCwdAttribution);
        var clauseWorkingDirectory = ExactValue(occurrence.WorkingDirectory)
            ?? cwdAttribution?.Resolved
            ?? (cwdAttribution is null ? workingDirectory : null);

        // Each parser path is an authorization scope. A grant must cover all
        // scopes, or a later external path could hide behind an earlier local
        // path. The resolved value also handles native forms such as @file.
        // ShellSyntaxTree 0.3.0 still has the #1795 classification.
        // Remove this guard after a later release contains the parser correction.
        if (!isSideEffectVerb)
        {
            foreach (var arg in clause.Args)
            {
                if (arg.IsCwdAttribution
                    || !IsAuthorizationPathArg(arg, clauseWorkingDirectory, pathStyle))
                    continue;

                if (arg.Kind == ShellSyntaxTree.ArgKind.Glob)
                {
                    var coveringDirectory = ResolveGlobCoveringDirectory(
                        arg,
                        clauseWorkingDirectory,
                        pathStyle);
                    if (coveringDirectory is null)
                        return null;

                    directories.Add(coveringDirectory);
                    continue;
                }

                // A parser path without a canonical value cannot use the broader
                // cwd grant. Return no candidates so the command fails closed.
                var resolved = arg.Resolved;
                if (string.IsNullOrWhiteSpace(resolved))
                    return null;

                directories.Add(ResolveAuthorizationScope(verb, arg, resolved, pathStyle));
            }
        }

        foreach (var redirect in occurrence.Redirects)
        {
            var redirectDirectories = ResolveRedirectDirectories(redirect, pathStyle);
            if (redirectDirectories is null)
                return null;

            directories.AddRange(redirectDirectories);
        }

        if (directories.Count == 0)
        {
            // A side-effect verb ignores cwd. Other verbs use the parser's
            // exact state proof. An unresolved synthetic attribution means a
            // preceding state change may have failed, so the outer cwd cannot
            // safely substitute for it.
            if (isSideEffectVerb)
            {
                directories.Add(null);
            }
            else if (cwdAttribution is not null)
            {
                var attributedDirectory = ExactValue(occurrence.WorkingDirectory)
                    ?? cwdAttribution.Resolved;
                if (string.IsNullOrWhiteSpace(attributedDirectory))
                    return null;

                directories.Add(attributedDirectory);
            }
            else if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                directories.Add(
                    ExactValue(occurrence.WorkingDirectory) ?? workingDirectory);
            }
            else
            {
                directories.Add(null);
            }
        }

        return directories.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string? ResolveAuthorizationScope(
        string verb,
        ShellSyntaxTree.Arg arg,
        string resolved,
        ShellPathStyle pathStyle)
    {
        var raw = arg.Raw.Trim();
        if (raw.Length >= 2 && raw[0] is '\'' or '"' && raw[^1] == raw[0])
            raw = raw[1..^1];

        var hasDirectorySyntax = raw is "." or ".."
            || raw.EndsWith("/", StringComparison.Ordinal)
            || pathStyle == ShellPathStyle.Windows
                && raw.EndsWith("\\", StringComparison.Ordinal);

        var hasDirectoryOperand = verb.Equals("find", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("cd", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("chdir", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("pushd", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("popd", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("Set-Location", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("Push-Location", StringComparison.OrdinalIgnoreCase)
            || verb.Equals("Pop-Location", StringComparison.OrdinalIgnoreCase);

        // A dotted basename can name either a file or a directory. Navigation
        // and traversal commands need the exact scope, not the file-parent
        // heuristic. The safe-space policy still rejects external and
        // symlinked paths.
        return hasDirectorySyntax || hasDirectoryOperand
            ? resolved
            : ShellTokenizer.ApplyFileParentRule(resolved, pathStyle);
    }

    private static string? ResolveGlobCoveringDirectory(
        ShellSyntaxTree.Arg arg,
        string? workingDirectory,
        ShellPathStyle pathStyle)
    {
        if (ShellGlobPath.HasUnresolvedDescendantScope(arg, pathStyle))
            return null;

        var path = arg.Raw.Trim();
        if (path.Length >= 2 && path[0] is '\'' or '"' && path[^1] == path[0])
            path = path[1..^1];

        if (arg.IsFlag)
        {
            var valueSeparator = path.IndexOf('=', StringComparison.Ordinal);
            if (valueSeparator < 0 || valueSeparator == path.Length - 1)
                return null;

            path = path[(valueSeparator + 1)..];
        }

        path = path.TrimStart('@');
        const string fileSystemPrefix = "filesystem::";
        if (path.StartsWith(fileSystemPrefix, StringComparison.OrdinalIgnoreCase))
            path = path[fileSystemPrefix.Length..];

        var firstGlob = path.IndexOfAny(['*', '?', '[']);
        if (firstGlob < 0)
            return null;

        var staticPrefix = path[..firstGlob];
        var separator = pathStyle == ShellPathStyle.Windows
            ? staticPrefix.LastIndexOfAny(['/', '\\'])
            : staticPrefix.LastIndexOf('/');
        var coveringPath = CoveringPath(staticPrefix, separator, pathStyle);

        var coveringDirectory = ShellTokenizer.NormalizePathToken(
            coveringPath,
            workingDirectory,
            pathStyle);
        if (coveringDirectory is null
            || ContainsSymlinkEntry(coveringDirectory))
        {
            return null;
        }

        return coveringDirectory;
    }

    private static string CoveringPath(
        string staticPrefix,
        int separator,
        ShellPathStyle pathStyle)
    {
        if (separator < 0)
            return ".";
        if (separator == 0)
            return staticPrefix[..1];
        if (pathStyle == ShellPathStyle.Windows
            && separator == 2
            && staticPrefix.Length >= 3
            && char.IsAsciiLetter(staticPrefix[0])
            && staticPrefix[1] == ':')
        {
            return staticPrefix[..3];
        }

        return staticPrefix[..separator];
    }

    private static bool ContainsSymlinkEntry(string directory)
    {
        if (!Directory.Exists(directory))
            return false;

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                // Netclaw does not reproduce Bash glob rules here. Unicode,
                // brackets, and escapes differ from .NET wildcard rules.
                // Any symlink makes the leaf expansion unsafe to persist.
                if (PathUtility.ContainsSymlinkSegment(directory, entry))
                    return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            // The matcher cannot prove the expansion stays in the fixed scope.
            return true;
        }
    }

    private static bool IsAuthorizationPathArg(
        ShellSyntaxTree.Arg arg,
        string? workingDirectory,
        ShellPathStyle pathStyle)
    {
        if (!arg.IsPath)
            return false;

        // ShellSyntaxTree 0.3.0 still has the #1795 classification.
        // Remove this guard after a later release contains the parser correction.
        if (arg.Raw.Length > 0 && arg.Raw.All(char.IsAsciiDigit)
            && ShouldDropNumericToken(arg, workingDirectory))
        {
            return false;
        }

        var containsSeparator = pathStyle == ShellPathStyle.Windows
            ? arg.Raw.IndexOfAny(['/', '\\']) >= 0
            : arg.Raw.Contains('/', StringComparison.Ordinal);
        if (ShellTokenizer.IsPathToken(arg.Raw, pathStyle) || !containsSeparator)
            return true;

        // An internal slash can also name a ref such as feature/x. Native
        // option and @file shapes supply the extra path evidence we need.
        if (arg.IsFlag || arg.Raw.TrimStart('\'', '"').StartsWith('@'))
            return true;

        // Collapse an ambiguous relative token to cwd only when its resolved
        // path stays there. External paths and symlink paths need exact checks.
        if (string.IsNullOrWhiteSpace(arg.Resolved)
            || string.IsNullOrWhiteSpace(workingDirectory))
        {
            return true;
        }

        try
        {
            var normalizedPath = PathUtility.Normalize(arg.Resolved);
            if (!PathUtility.IsNormalizedWithinRoot(normalizedPath, workingDirectory))
                return true;

            return PathUtility.ContainsSymlinkSegment(workingDirectory, normalizedPath);
        }
        catch (Exception ex) when (ex is ArgumentException
                                      or IOException
                                      or NotSupportedException
                                      or System.Security.SecurityException)
        {
            return true;
        }
    }

    /// <summary>
    /// Returns true only when an all-digit operand does not identify a filesystem object.
    /// A probe failure keeps the operand as a path, so the approval gate prompts.
    /// </summary>
    private static bool ShouldDropNumericToken(ShellSyntaxTree.Arg arg, string? workingDirectory)
    {
        try
        {
            var token = string.IsNullOrWhiteSpace(arg.Resolved) ? arg.Raw : arg.Resolved;

            string path;
            if (Path.IsPathRooted(token))
            {
                path = token;
            }
            else if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                path = Path.Combine(workingDirectory, token);
            }
            else
            {
                return false;
            }

            return !File.Exists(path) && !Directory.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException
                                      or IOException
                                      or NotSupportedException
                                      or UnauthorizedAccessException
                                      or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string? ExactValue(ShellSyntaxTree.ShellValueDomain domain)
        => (domain as ShellSyntaxTree.ShellValueDomain.Exact)?.Value;

    private static IReadOnlyList<string>? ResolveRedirectDirectories(
        ShellSyntaxTree.RedirectAnalysis redirect,
        ShellPathStyle pathStyle)
    {
        if (!redirect.IsComplete)
            return null;

        if (redirect is ShellSyntaxTree.UnresolvedRedirectAnalysis)
            return null;

        if (redirect is not ShellSyntaxTree.FileRedirectAnalysis file)
        {
            return redirect is ShellSyntaxTree.DescriptorDuplicateRedirectAnalysis
                or ShellSyntaxTree.DescriptorMoveRedirectAnalysis
                or ShellSyntaxTree.DescriptorCloseRedirectAnalysis
                or ShellSyntaxTree.HereDocumentRedirectAnalysis
                or ShellSyntaxTree.HereStringRedirectAnalysis
                ? []
                : null;
        }

        if (file.Target is ShellSyntaxTree.ShellValueDomain.PathPattern pattern)
        {
            var coveringDirectory = pattern.CoveringDirectory;
            return string.IsNullOrWhiteSpace(coveringDirectory)
                || ContainsSymlinkEntry(coveringDirectory)
                ? null
                : [coveringDirectory];
        }

        IReadOnlyList<string> targets = file.Target switch
        {
            ShellSyntaxTree.ShellValueDomain.Exact exact => [exact.Value],
            ShellSyntaxTree.ShellValueDomain.FiniteSet finite => finite.Values,
            _ => []
        };
        if (targets.Count == 0)
        {
            return null;
        }

        var directories = new List<string>(targets.Count);
        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target))
                return null;

            if (UsesHostPathStyle(pathStyle) && HasUnsafeHostPath(target))
                return null;

            // The resolved POSIX null device creates no reusable filesystem
            // authority. Other device paths stay strict.
            if (pathStyle == ShellPathStyle.Posix
                && string.Equals(target, PosixNullDevicePath, StringComparison.Ordinal))
            {
                continue;
            }

            var directory = GetRedirectDirectory(target, pathStyle);
            if (directory is null)
                return null;

            directories.Add(directory);
        }

        return directories;
    }

    private static string? GetRedirectDirectory(string target, ShellPathStyle pathStyle)
    {
        if (!IsRootedForPathStyle(target, pathStyle))
            return null;

        var separator = pathStyle == ShellPathStyle.Windows
            ? target.LastIndexOfAny(['/', '\\'])
            : target.LastIndexOf('/');
        if (separator < 0)
            return null;
        if (separator == 0)
            return target[..1];
        if (pathStyle == ShellPathStyle.Windows
            && separator == 2
            && char.IsAsciiLetter(target[0])
            && target[1] == ':')
        {
            return target[..3];
        }

        return target[..separator];
    }

    private static bool IsRootedForPathStyle(string path, ShellPathStyle pathStyle)
        => pathStyle switch
        {
            ShellPathStyle.Posix => path.Length > 0 && path[0] == '/',
            ShellPathStyle.Windows => (path.Length >= 3
                                       && char.IsAsciiLetter(path[0])
                                       && path[1] == ':'
                                       && path[2] is '/' or '\\')
                                      || (path.Length >= 5
                                          && path[0] is '/' or '\\'
                                          && path[1] is '/' or '\\'),
            _ => false
        };

    private static bool UsesHostPathStyle(ShellPathStyle pathStyle)
        => pathStyle == ShellPathStyle.Windows
            ? OperatingSystem.IsWindows()
            : !OperatingSystem.IsWindows();

    private static bool HasUnsafeHostPath(string target)
    {
        try
        {
            var pathRoot = Path.GetPathRoot(target);
            return string.IsNullOrWhiteSpace(pathRoot)
                   || PathUtility.ContainsSymlinkSegment(pathRoot, target);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or IOException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            return true;
        }
    }

    /// <summary>
    /// Splits the environment-bound command analysis into approval-unit strings:
    /// one unit per statement, with consecutive <c>|</c> clauses folded into
    /// the same unit so <c>cat x | wc -l</c> stays a single decision.
    /// Returns an empty list for messy, unparseable, or parser-rejected
    /// commands. This result matches the legacy <see cref="ShellTokenizer.SplitCompoundCommand"/>
    /// empty-result contract so the prompt builder offers only Once/Deny.
    /// </summary>
    private IReadOnlyList<string> ExtractApprovalUnitsViaAnalysis(
        ShellCommandAnalysis result)
    {
        // The parser is the sole structural authority. Dynamic or unresolved
        // syntax cannot produce a persistent approval unit.
        if (!result.IsResolved
            || result.HasDynamicSyntax
            || HasUnscopedPowerShellProviderOperand(result))
            return [];

        try
        {
            var units = new List<string>();
            var current = new StringBuilder();

            foreach (var occurrence in result.Commands)
            {
                var clause = occurrence.Clause;
                // AndIf / OrIf / Sequence (and the leading None clause) each
                // open a fresh approval unit; Pipe clauses fold into the unit
                // in progress. A bare newline produces Sequence, so multi-line
                // commands split here too.
                if (clause.Operator != ShellSyntaxTree.CompoundOperator.Pipe && current.Length > 0)
                {
                    units.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                    current.Append(" | ");

                current.Append(ReconstructClauseText(clause));
            }

            if (current.Length > 0)
                units.Add(current.ToString());

            return units;
        }
        catch
        {
            // Defensive: an unmapped redirect/clause shape from a future
            // ShellSyntaxTree release must not take down the approval
            // prompt. Fail-empty so the matcher treats the command as messy
            // (Once/Deny prompt only).
            return [];
        }
    }

    /// <summary>
    /// Rebuilds one clause's user-facing text from its parsed parts: verb
    /// chain, positional/flag args, and redirects. Synthetic cd-attribution
    /// args are dropped — they carry an inherited cwd, not a token the user
    /// typed. Call-specific value arguments are also excluded since they vary
    /// between invocations of the same verb chain: digit-bearing tokens
    /// (issue #1331, see <see cref="IsCallSpecificValueToken"/>), multi-line
    /// quoted strings (issue #1402, see <see cref="ContainsLineBreak"/>), and
    /// single-line quoted free text (issue #1406, see
    /// <see cref="IsQuotedFreeTextArg"/>). Once such a token is encountered,
    /// the greedy walk terminates — subsequent args (wrapped subcommands like
    /// <c>curl</c> after <c>timeout 30</c>) are outside the approval intent.
    /// The result is fed back through
    /// <see cref="ShellTokenizer.NormalizeApprovalUnit"/> for path
    /// normalization, so this only needs to emit a clean token sequence.
    /// </summary>
    private static string ReconstructClauseText(ShellSyntaxTree.Clause clause)
    {
        // Strip the trailing call-specific value tokens the greedy verb walk
        // folded into the chain (see TrimTrailingValueTokens) so the persisted
        // pattern matches the gate candidate for `git tag v0.4.2`.
        var sb = new StringBuilder(string.Join(" ", TrimTrailingValueTokens(clause.Verb.Tokens)));

        foreach (var arg in clause.Args)
        {
            if (arg.IsCwdAttribution || string.IsNullOrEmpty(arg.Raw))
                continue;

            // Issue #1331 (generalized): call-specific value args —
            // digit-bearing non-flag, non-path tokens — are a termination
            // condition. Once we hit one, subsequent args (wrapped subcommands
            // like `curl` after `timeout 30`, or trailing flags after a
            // version) are outside the approval intent.
            if (IsCallSpecificValueToken(arg.Raw))
                break;

            // Issue #1402: a multi-line quoted string (a message body, an
            // inline script) is call-specific content that varies between
            // invocations — and an embedded line break corrupts the stored
            // pattern's display. Like the digit rule above, hitting one
            // terminates the walk.
            if (ContainsLineBreak(arg.Raw))
                break;

            // Issue #1406: a single-line quoted operand whose text holds
            // internal whitespace (a commit message, a ticket body, an inline
            // note) is call-specific free text. Every unique value would
            // otherwise become a new stored pattern that re-prompts. Same
            // termination mechanism as the digit (#1331) and multi-line
            // (#1402) rules. A path arg is exempt so a quoted path with a
            // space keeps its directory scope (see IsQuotedFreeTextArg).
            if (IsQuotedFreeTextArg(arg))
                break;

            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(arg.Raw);
        }

        // Redirect targets live outside Args; the legacy tokenizer kept them
        // as plain `> /path` tokens, so preserve them in the display unit
        // and the approve-once retry key.
        foreach (var redirect in clause.Redirects)
        {
            if (string.IsNullOrEmpty(redirect.Target))
                continue;

            // Issue #1402: a quoted redirect target can carry an embedded
            // line break too (`> "$LOGDIR\nfile"`), and quote-aware path
            // normalization preserves it — same termination rule as args so
            // the break never reaches the stored pattern.
            if (ContainsLineBreak(redirect.Target))
                break;

            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(RedirectToken(redirect.Direction));
            sb.Append(' ');
            sb.Append(redirect.Target);
        }

        return sb.ToString();
    }

    /// <summary>
    /// True when <paramref name="value"/> contains an embedded line break.
    /// Checks CR as well as LF: a lone carriage return corrupts a stored
    /// pattern just like a newline, and in a terminal-rendered prompt it
    /// returns the cursor to column 0 so attacker-influenced content (e.g.
    /// quoted ticket text flowing into a tool argument) could visually
    /// overwrite the rendered command at the moment of approval.
    /// </summary>
    private static bool ContainsLineBreak(string value)
        => value.AsSpan().IndexOfAny('\r', '\n') >= 0;

    /// <summary>
    /// True when <paramref name="token"/> is a call-specific value that varies
    /// between invocations of the same verb chain. The classification is
    /// morphological — one rule, not a taxonomy of value shapes: any non-flag,
    /// non-path token containing a digit is a value. That covers versions
    /// (<c>v0.4.2</c>, <c>0.4.2</c>), SHAs (<c>aa211dcb</c>), IPs, ports,
    /// ticket IDs, and digit-bearing refs (<c>feature2</c>) — generalizing
    /// issue #1331's bare-integer rule. Flags are exempt (<c>-3</c>,
    /// <c>--max-count=10</c> carry invocation intent, not values);
    /// path-shaped tokens are exempt so digit-bearing paths
    /// (<c>/tmp/build2</c>) still reach directory scoping and the display
    /// pattern. All-alpha operands (branch names, package names) are
    /// intentionally NOT classified — no shape rule can tell them apart from
    /// subcommands, and mis-stripping a subcommand silently widens a grant.
    /// </summary>
    private static bool IsCallSpecificValueToken(string token)
    {
        if (string.IsNullOrEmpty(token) || token[0] == '-')
            return false;

        if (ShellTokenizer.IsPathToken(token))
            return false;

        foreach (var c in token)
        {
            if (char.IsAsciiDigit(c))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="arg"/> is a quote-wrapped operand whose text
    /// holds internal whitespace and is not a path (issue #1406). Such an
    /// operand is call-specific free text — a commit message, a ticket body,
    /// an inline note — that varies between invocations of the same verb
    /// chain, so it must not enter the stored approval pattern. Like the
    /// digit-bearing (#1331) and multi-line (#1402) rules, hitting one
    /// terminates the reconstruction walk.
    /// <para>
    /// The rule is deliberately narrow so it never widens a grant:
    /// </para>
    /// <list type="bullet">
    /// <item>Only a single matching quote pair qualifies. An unquoted token
    /// cannot hold internal whitespace — the shell splits it into separate
    /// args — so a single-word quoted arg (<c>git commit -m "fix"</c>) has no
    /// internal whitespace, stays in the pattern, and normalizes the same as
    /// its unquoted form.</item>
    /// <item>A path arg is exempt. Its directory is authorization state that
    /// candidate extraction resolves separately from
    /// the same parsed <c>Arg</c>, so a quoted path with a space
    /// (<c>cat "my file.txt"</c>) keeps its scope. Only a value operand, never
    /// a path, drops here.</item>
    /// </list>
    /// This rule only shapes the stored/display pattern. It does not touch the
    /// gate candidate or the live authorization decision, which re-parse each
    /// command and scope every path arg through the zone gate.
    /// </summary>
    private static bool IsQuotedFreeTextArg(ShellSyntaxTree.Arg arg)
    {
        if (arg.IsPath)
            return false;

        var raw = arg.Raw;

        // A single matching quote pair around at least one inner character.
        // The shortest droppable form is quote + whitespace + quote (length 3).
        if (raw.Length < 3)
            return false;

        var quote = raw[0];
        if (quote is not ('"' or '\'') || raw[^1] != quote)
            return false;

        for (var i = 1; i < raw.Length - 1; i++)
        {
            if (char.IsWhiteSpace(raw[i]))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Drops trailing call-specific value tokens from a parsed verb chain,
    /// always retaining at least the command word. See
    /// <see cref="IsCallSpecificValueToken"/> for why <c>git tag v0.4.2</c> and
    /// <c>git tag 0.4.2</c> must both normalize to <c>git tag</c>.
    /// </summary>
    private static IReadOnlyList<string> TrimTrailingValueTokens(IReadOnlyList<string> verbTokens)
    {
        var end = verbTokens.Count;
        while (end > 1 && IsCallSpecificValueToken(verbTokens[end - 1]))
            end--;

        return end == verbTokens.Count ? verbTokens : verbTokens.Take(end).ToList();
    }

    private static string RedirectToken(ShellSyntaxTree.RedirectDirection direction) => direction switch
    {
        ShellSyntaxTree.RedirectDirection.In => "<",
        ShellSyntaxTree.RedirectDirection.Out => ">",
        ShellSyntaxTree.RedirectDirection.Append => ">>",
        ShellSyntaxTree.RedirectDirection.ErrOut => "2>",
        ShellSyntaxTree.RedirectDirection.ErrAppend => "2>>",
        _ => throw new ArgumentOutOfRangeException(
            nameof(direction), direction,
            "Unknown ShellSyntaxTree redirect direction — a package upgrade needs a matcher update."),
    };

    public bool IsApproved(
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        IReadOnlyList<ApprovalEntry> approvedEntries,
        string? cwd)
    {
        // Fail-closed on a missing/empty Command argument: a malformed
        // shell invocation cannot be "already approved" — the agent must
        // round-trip through the gate so the operator sees what was
        // attempted.
        var command = GetCommand(arguments);
        if (string.IsNullOrWhiteSpace(command))
            return false;

        // Empty candidates include parser failures and dynamic syntax.
        // Both cases must return to the approval gate.
        var candidates = ExtractCandidates(toolName, arguments);
        if (candidates.Count == 0)
            return false;

        foreach (var candidate in candidates)
        {
            // Pure side-effect candidates (echo "X" without a path or redirect,
            // bash :, true/false) are always authorized — they're skipped on
            // persistence so the store never contains them, and the matcher
            // here mirrors that decision at evaluation time.
            if (ApprovalPatternMatching.IsPureSideEffect(candidate))
                continue;

            if (!ApprovalPatternMatching.MatchesShellApproval(
                    candidate.Verb, candidate.Directory, cwd, approvedEntries))
                return false;
        }

        return true;
    }

    public bool IsMessy(ToolName toolName, IDictionary<string, object?>? arguments)
        => AnalyzeInvocation(toolName, arguments).IsMessy;

    private bool IsMessy(ShellCommandAnalysis analysis)
    {
        if (!analysis.IsResolved
            || analysis.HasDynamicSyntax
            || HasUnscopedPowerShellProviderOperand(analysis))
            return true;

        var workingDirectory = analysis.WorkingDirectory;

        if (analysis.Commands
            .SelectMany(static command => command.Clause.Args)
            .Where(static arg => arg.IsPath && arg.Kind == ShellSyntaxTree.ArgKind.Glob)
            .Any(arg => ResolveGlobCoveringDirectory(
                arg,
                workingDirectory,
                Environment.PathStyle) is null))
        {
            return true;
        }

        if (analysis.Commands.Any(command =>
                ResolveCommandDirectories(
                    command,
                    NormalizedVerb(command),
                    IsSideEffectCommand(command),
                    workingDirectory,
                    Environment.PathStyle) is null))
        {
            return true;
        }

        return false;
    }

    private bool HasUnscopedPowerShellProviderOperand(ShellCommandAnalysis analysis)
    {
        if (Environment.Grammar != ShellGrammar.PowerShell)
            return false;

        return analysis.Commands
            .SelectMany(static occurrence => occurrence.Clause.Args)
            .Any(static arg => LooksLikeNonFileSystemProviderPath(arg.Raw));
    }

    private static bool LooksLikeNonFileSystemProviderPath(string raw)
    {
        var value = raw.Trim();
        if (value.Length >= 2 && value[0] is '\'' or '"' && value[^1] == value[0])
            value = value[1..^1];

        var providerSeparator = value.IndexOf("::", StringComparison.Ordinal);
        if (providerSeparator > 0)
        {
            var provider = value[..providerSeparator];
            return !provider.Equals("FileSystem", StringComparison.OrdinalIgnoreCase)
                   && !provider.EndsWith(
                       "\\FileSystem",
                       StringComparison.OrdinalIgnoreCase);
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 1 || !char.IsAsciiLetter(value[0]))
            return false;

        // URI schemes are data operands, not PowerShell provider drives.
        if (value.Length > colon + 2
            && value[colon + 1] == '/'
            && value[colon + 2] == '/')
        {
            return false;
        }

        for (var i = 1; i < colon; i++)
        {
            if (!char.IsAsciiLetterOrDigit(value[i]) && value[i] is not ('_' or '-'))
                return false;
        }

        return true;
    }

    private static bool IsSideEffectCommand(ShellSyntaxTree.CommandOccurrence occurrence)
        => ShellTokenizer.SingleTokenSideEffectVerbs.Contains(NormalizedVerb(occurrence));

    private static string NormalizedVerb(ShellSyntaxTree.CommandOccurrence occurrence)
    {
        var clause = occurrence.Clause;
        var parsedVerb = clause.Verb.CanonicalVerb
            ?? string.Join(" ", TrimTrailingValueTokens(clause.Verb.Tokens));
        return ShellTokenizer.ApplyVerbShortCircuit(parsedVerb);
    }

    public string FormatForDisplay(ToolName toolName, IDictionary<string, object?>? arguments)
        => AnalyzeInvocation(toolName, arguments).DisplayText;

    private string FormatForDisplay(
        string command,
        ShellCommandAnalysis analysis)
    {
        // Fast path: a command with no embedded line break renders verbatim.
        if (!ContainsLineBreak(command))
            return command;

        // Issue #1402: channel renderers embed DisplayText in single-line
        // code fences, so a multi-line quoted string (a message body, an
        // inline script) dumped verbatim corrupts the approval prompt. On
        // either grammar, rebuild a one-line view from the environment-bound
        // parse tree with multi-line args summarized by size. Heredoc and
        // here-string fallbacks encode line breaks
        // before the trailing replacement so command boundaries stay visible.
        // The trailing replacement catches any other leaked line breaks and
        // collapses CRLF to a single space.
        var display = BuildSanitizedDisplayViaParser(command, analysis);

        return display.ReplaceLineEndings(" ");
    }

    /// <summary>
    /// Rebuilds a one-line display string for a multi-line command from its
    /// parse tree: statement separators render as explicit operators and any
    /// multi-line argument is replaced with a <c>(N lines, M chars)</c>
    /// summary — the operator approving the command needs its shape, not the
    /// full content (issue #1402). Returns the raw command when the parser
    /// cannot decompose it. A heredoc or here string uses a raw view with
    /// visible line-break markers because the compatibility redirect cannot
    /// preserve the v0.3 operation and data facts. A subshell uses the raw
    /// fallback because its grouping
    /// does not survive the flat clause list, so a reconstruction would
    /// misstate which statements a pipe or <c>&amp;&amp;</c> guard applies
    /// to. The raw fallback is ugly but fully disclosed.
    /// </summary>
    private static string BuildSanitizedDisplayViaParser(
        string command,
        ShellCommandAnalysis result)
    {
        if (!result.IsResolved)
            return command;

        if (result.Commands.Any(static occurrence =>
                occurrence.Redirects.Any(IsRawDisplayRedirect)))
        {
            return command.ReplaceLineEndings(" ⏎ ");
        }

        if (result.Commands.Any(static occurrence => occurrence.Clause.IsSubshell))
            return command;

        try
        {
            var sb = new StringBuilder();
            var first = true;

            foreach (var occurrence in result.Commands)
            {
                var clause = occurrence.Clause;
                if (!first)
                    sb.Append(ClauseOperatorText(clause.Operator));
                first = false;

                sb.Append(clause.Verb.Joined);

                foreach (var arg in clause.Args)
                {
                    if (arg.IsCwdAttribution || string.IsNullOrEmpty(arg.Raw))
                        continue;

                    sb.Append(' ');
                    sb.Append(ContainsLineBreak(arg.Raw)
                        ? SummarizeMultilineArg(arg.Raw)
                        : arg.Raw);
                }

                foreach (var redirect in clause.Redirects)
                {
                    if (string.IsNullOrEmpty(redirect.Target))
                        continue;

                    sb.Append(' ');
                    sb.Append(RedirectToken(redirect.Direction));
                    sb.Append(' ');
                    sb.Append(ContainsLineBreak(redirect.Target)
                        ? SummarizeMultilineArg(redirect.Target)
                        : redirect.Target);
                }
            }

            return sb.ToString();
        }
        catch
        {
            // Defensive: display formatting must never take down the
            // approval prompt — the caller flattens the raw command's
            // line breaks instead.
            return command;
        }
    }

    /// <summary>
    /// True when the v0.3 redirect operation carries shell-fed data that the
    /// compatibility clause cannot reconstruct without changing its meaning.
    /// See
    /// <see cref="BuildSanitizedDisplayViaParser"/>.
    /// </summary>
    private static bool IsRawDisplayRedirect(ShellSyntaxTree.RedirectAnalysis redirect)
        => redirect is ShellSyntaxTree.HereDocumentRedirectAnalysis
            or ShellSyntaxTree.HereStringRedirectAnalysis;

    /// <summary>
    /// Size summary shown in place of a multi-line argument. Outer quotes
    /// are excluded from the character count — the operator cares about the
    /// content's size, not the shell syntax around it. Line endings are
    /// normalized first so CRLF and lone CR count the same as LF.
    /// </summary>
    private static string SummarizeMultilineArg(string raw)
    {
        var content = raw.Length >= 2 && (raw[0] == '"' || raw[0] == '\'') && raw[^1] == raw[0]
            ? raw[1..^1]
            : raw;

        var normalized = content.ReplaceLineEndings("\n");
        var lines = normalized.Count(c => c == '\n') + 1;
        return $"({lines} lines, {normalized.Length} chars)";
    }

    private static string ClauseOperatorText(ShellSyntaxTree.CompoundOperator op) => op switch
    {
        ShellSyntaxTree.CompoundOperator.AndIf => " && ",
        ShellSyntaxTree.CompoundOperator.OrIf => " || ",
        ShellSyntaxTree.CompoundOperator.Sequence => "; ",
        ShellSyntaxTree.CompoundOperator.Pipe => " | ",
        // None is not just the leading-clause marker: the parser emits it on
        // the clause following a closed subshell, so it is reachable on
        // non-first clauses. Render it as a plain statement separator.
        ShellSyntaxTree.CompoundOperator.None => "; ",
        _ => throw new ArgumentOutOfRangeException(
            nameof(op), op,
            "Unknown ShellSyntaxTree compound operator — a package upgrade needs a matcher update."),
    };

    private static string? GetCommand(IDictionary<string, object?>? arguments)
        => ToolArgumentHelper.GetString(arguments, "Command");

    private static string? GetWorkingDirectory(IDictionary<string, object?>? arguments)
        => ToolArgumentHelper.GetString(arguments, "WorkingDirectory");

}

/// <summary>
/// Default approval matcher for non-shell tools. Approval is at the tool-name
/// level — either the tool is approved or it isn't. Directory scoping does
/// not apply.
/// </summary>
public sealed class DefaultApprovalMatcher : IToolApprovalMatcher
{
    public static readonly DefaultApprovalMatcher Instance = new();

    public string GetApprovalModeKey(ToolName toolName, IDictionary<string, object?>? arguments)
        => toolName.Value;

    public bool IsFailClosedOnPersonal(ToolName toolName, IDictionary<string, object?>? arguments)
        => false;

    public IReadOnlyList<string> ExtractPatterns(ToolName toolName, IDictionary<string, object?>? arguments)
        => [toolName.Value];

    public IReadOnlyList<string> ExtractCandidateVerbs(ToolName toolName, IDictionary<string, object?>? arguments)
        => [toolName.Value];

    public IReadOnlyList<ApprovalCandidate> ExtractCandidates(ToolName toolName, IDictionary<string, object?>? arguments)
        => [new ApprovalCandidate(toolName.Value, Directory: null)];

    public bool IsApproved(
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        IReadOnlyList<ApprovalEntry> approvedEntries,
        string? cwd)
        => ApprovalPatternMatching.MatchesAny(toolName.Value, approvedEntries);

    public bool IsMessy(ToolName toolName, IDictionary<string, object?>? arguments)
        => false;

    public string FormatForDisplay(ToolName toolName, IDictionary<string, object?>? arguments)
        => toolName.Value;
}

/// <summary>
/// MCP approval matcher. Grant matching remains at the canonical tool-name
/// level, while the display text includes a bounded, redacted preview of the
/// server arguments so an operator can make an informed decision.
/// </summary>
public sealed class McpApprovalMatcher : IToolApprovalMatcher
{
    public static readonly McpApprovalMatcher Instance = new();

    private const int MaxToolNameChars = 256;
    private const int MaxArgumentNameChars = 160;
    private const int MaxArguments = 24;
    private const int MaxDisplayChars = 1_600;
    private const int MaxNestedItems = 12;
    private const int MaxNestedDepth = 3;
    private const int MaxStructurePreviewChars = 360;
    private const int MaxUrlParseChars = 4_096;
    private const int MaxLineCountChars = 100_000;
    private const int StandardStringPreviewChars = 240;
    private const int LocatorStringPreviewChars = 1_000;
    private const int LocatorPreviewHeadChars = 600;
    private const int LocatorPreviewTailChars = 350;
    private const string Redacted = "***REDACTED***";

    private static DefaultApprovalMatcher Default => DefaultApprovalMatcher.Instance;

    public string GetApprovalModeKey(ToolName toolName, IDictionary<string, object?>? arguments)
        => Default.GetApprovalModeKey(toolName, arguments);

    public bool IsFailClosedOnPersonal(ToolName toolName, IDictionary<string, object?>? arguments)
        => Default.IsFailClosedOnPersonal(toolName, arguments);

    public IReadOnlyList<string> ExtractPatterns(ToolName toolName, IDictionary<string, object?>? arguments)
        => Default.ExtractPatterns(toolName, arguments);

    public IReadOnlyList<string> ExtractCandidateVerbs(ToolName toolName, IDictionary<string, object?>? arguments)
        => Default.ExtractCandidateVerbs(toolName, arguments);

    public IReadOnlyList<ApprovalCandidate> ExtractCandidates(
        ToolName toolName,
        IDictionary<string, object?>? arguments)
        => Default.ExtractCandidates(toolName, arguments);

    public bool IsApproved(
        ToolName toolName,
        IDictionary<string, object?>? arguments,
        IReadOnlyList<ApprovalEntry> approvedEntries,
        string? cwd)
        => Default.IsApproved(toolName, arguments, approvedEntries, cwd);

    public bool IsMessy(ToolName toolName, IDictionary<string, object?>? arguments)
        => Default.IsMessy(toolName, arguments);

    public string FormatForDisplay(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        var displayToolName = FormatToolName(toolName.Value);
        if (arguments is null || arguments.Count == 0)
            return displayToolName;

        // MCP schemas may contain arbitrary property names and arbitrarily
        // large collections. Sample before formatting so an approval prompt
        // has a hard work and allocation ceiling even for a hostile server.
        var sampled = arguments
            .Take(MaxArguments)
            .OrderBy(static pair => IsLocationLike(pair.Value) ? 0 : 1)
            .ToList();

        var display = new StringBuilder(Math.Min(MaxDisplayChars, displayToolName.Length + 256));
        display.Append(displayToolName).Append('(');
        var rendered = 0;

        foreach (var pair in sampled)
        {
            var part = $"{FormatArgumentName(pair.Key)}={FormatArgument(pair.Key, pair.Value)}";
            var separatorChars = rendered == 0 ? 0 : 2;
            if (display.Length + separatorChars + part.Length + 1 > MaxDisplayChars)
                break;

            if (separatorChars > 0)
                display.Append(", ");

            display.Append(part);
            rendered++;
        }

        var omitted = arguments.Count - rendered;
        if (omitted > 0)
        {
            var omission = $"… (+{omitted} arguments)";
            var separator = rendered == 0 ? string.Empty : ", ";
            var available = MaxDisplayChars - display.Length - 1;
            if (separator.Length + omission.Length <= available)
                display.Append(separator).Append(omission);
            else if (available > 1)
                display.Append('…');
        }

        display.Append(')');

        return display.ToString();
    }

    private static string FormatArgument(string key, object? value)
    {
        if (SecretOutputRedactor.IsSecretKey(key) || value is SensitiveString)
            return Redacted;

        if (value is byte[] bytes)
            return $"({bytes.Length} bytes)";

        if (value is string text)
            return FormatString(text);

        if (value is IDictionary dictionary)
            return $"({dictionary.Count} properties)";

        if (value is ICollection collection)
            return $"({collection.Count} items)";

        if (value is IEnumerable)
            return "(collection value)";

        JsonElement element;
        try
        {
            element = value is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return $"({value?.GetType().Name ?? "unknown"} value unavailable)";
        }

        return FormatJsonValue(key, element, depth: 0);
    }

    private static string FormatJsonValue(string key, JsonElement value, int depth)
    {
        if (SecretOutputRedactor.IsSecretKey(key))
            return Redacted;

        return value.ValueKind switch
        {
            JsonValueKind.String => FormatString(value.GetString() ?? string.Empty),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            JsonValueKind.Undefined => "(undefined)",
            JsonValueKind.Object => FormatObject(value, depth),
            JsonValueKind.Array => FormatArray(value, depth),
            _ => "(unsupported value)"
        };
    }

    private static string FormatString(string value)
    {
        if (TrySanitizeAbsoluteUri(value, out var sanitizedUri))
            return SerializeStringForDisplay(sanitizedUri);

        if (IsPathLike(value))
            return FormatPath(value);

        return FormatNonLocatorString(value);
    }

    private static string FormatPath(string value)
    {
        if (value.Length <= LocatorStringPreviewChars)
            return SerializeStringForDisplay(SecretOutputRedactor.Redact(value));

        var preview = value[..LocatorPreviewHeadChars]
                      + $"…[{value.Length} chars]…"
                      + value[^LocatorPreviewTailChars..];
        return SerializeStringForDisplay(SecretOutputRedactor.Redact(preview));
    }

    private static string FormatNonLocatorString(string value)
    {
        if (value.Length <= StandardStringPreviewChars)
            return SerializeStringForDisplay(SecretOutputRedactor.Redact(value));

        return $"({value.Length} chars, {CountLinesForDisplay(value)} lines)";
    }

    private static string FormatObject(JsonElement value, int depth)
    {
        if (depth >= MaxNestedDepth)
            return "(nested object)";

        var properties = value.EnumerateObject().Take(MaxNestedItems + 1).ToList();
        if (properties.Count > MaxNestedItems)
            return $"({MaxNestedItems}+ properties)";

        var parts = properties
            .OrderBy(static property => property.Name, StringComparer.Ordinal)
            .Select(property =>
                $"{SerializeStringForDisplay(BoundArgumentName(property.Name))}:{FormatJsonValue(property.Name, property.Value, depth + 1)}");
        return BoundStructurePreview($"{{{string.Join(",", parts)}}}", properties.Count, "properties");
    }

    private static string FormatArray(JsonElement value, int depth)
    {
        if (depth >= MaxNestedDepth)
            return "(nested array)";

        var items = value.EnumerateArray().Take(MaxNestedItems + 1).ToList();
        if (items.Count > MaxNestedItems)
            return $"({MaxNestedItems}+ items)";

        var preview = $"[{string.Join(",", items.Select(item => FormatJsonValue(string.Empty, item, depth + 1)))}]";
        return BoundStructurePreview(preview, items.Count, "items");
    }

    private static string BoundStructurePreview(string preview, int count, string unit)
        => preview.Length <= MaxStructurePreviewChars ? preview : $"({count} {unit})";

    private static string FormatArgumentName(string key)
    {
        if (key.Length is > 0 and <= MaxArgumentNameChars && key.All(IsSafeArgumentNameChar))
            return key;

        // Approval displays are embedded in inline-code markup by the chat
        // renderers. Encode every non-identifier code unit, including
        // backticks, line breaks, ANSI controls, and bidi overrides, so an MCP
        // schema cannot escape the invocation line or spoof prompt chrome.
        var bounded = BoundArgumentName(key);
        var escaped = new StringBuilder(bounded.Length + 2);
        escaped.Append('"');
        foreach (var ch in bounded)
        {
            if (IsSafeArgumentNameChar(ch))
                escaped.Append(ch);
            else
                escaped.Append("\\u").Append(((int)ch).ToString("X4"));
        }

        return escaped.Append('"').ToString();
    }

    private static string BoundArgumentName(string key)
        => key.Length <= MaxArgumentNameChars
            ? key
            : $"{key[..120]}…[{key.Length} chars]";

    private static string FormatToolName(string toolName)
    {
        var bounded = toolName.Length <= MaxToolNameChars
            ? toolName
            : $"{toolName[..200]}…[{toolName.Length} chars]";
        return EscapePresentationControls(bounded);
    }

    private static bool IsSafeArgumentNameChar(char ch)
        => ch is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_' or '-' or '.';

    private static string SerializeStringForDisplay(string value)
    {
        var serialized = JsonSerializer.Serialize(value);
        return EscapePresentationControls(serialized);
    }

    private static string EscapePresentationControls(string value)
    {
        if (!value.Any(IsPresentationControl))
            return value;

        var escaped = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (IsPresentationControl(ch))
                escaped.Append("\\u").Append(((int)ch).ToString("X4"));
            else
                escaped.Append(ch);
        }

        return escaped.ToString();
    }

    private static bool IsPresentationControl(char ch)
        => ch == '`' || char.IsControl(ch) || IsDirectionalOrLineControl(ch);

    private static bool IsDirectionalOrLineControl(char ch)
        => ch is '\u061C' or '\u200E' or '\u200F'
            or >= '\u2028' and <= '\u202E'
            or >= '\u2066' and <= '\u2069'
            or '\uFEFF';

    private static bool TrySanitizeAbsoluteUri(string value, out string sanitized)
    {
        sanitized = string.Empty;
        if (value.Length > MaxUrlParseChars
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        try
        {
            var builder = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty,
                Query = string.IsNullOrEmpty(uri.Query) ? string.Empty : Redacted,
                Fragment = string.IsNullOrEmpty(uri.Fragment) ? string.Empty : Redacted
            };
            sanitized = SecretOutputRedactor.Redact(builder.Uri.AbsoluteUri);
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool IsPathLike(string value)
    {
        if (value.Length == 0 || value.IndexOfAny(['\r', '\n']) >= 0)
            return false;

        return Path.IsPathRooted(value)
               || value.StartsWith("~/", StringComparison.Ordinal)
               || value.StartsWith("./", StringComparison.Ordinal)
               || value.StartsWith("../", StringComparison.Ordinal)
               || value.StartsWith("\\\\", StringComparison.Ordinal)
               || value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':'
                   && value[2] is '\\' or '/';
    }

    private static bool IsLocationLike(object? value)
    {
        var text = value switch
        {
            string stringValue => stringValue,
            JsonElement { ValueKind: JsonValueKind.String } jsonValue => jsonValue.GetString(),
            _ => null
        };

        return text is not null
               && (IsPathLike(text) || TrySanitizeAbsoluteUri(text, out _));
    }

    private static string CountLinesForDisplay(string value)
    {
        var lines = 1;
        var charsToInspect = Math.Min(value.Length, MaxLineCountChars);
        for (var i = 0; i < charsToInspect; i++)
        {
            if (value[i] == '\n')
                lines++;
        }

        return value.Length <= MaxLineCountChars ? lines.ToString() : $"{lines}+";
    }

}
