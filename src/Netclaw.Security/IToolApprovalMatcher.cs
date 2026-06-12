// -----------------------------------------------------------------------
// <copyright file="IToolApprovalMatcher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Netclaw.Configuration;
using Netclaw.Tools;

namespace Netclaw.Security;

/// <summary>
/// One approval candidate extracted from a tool invocation. The verb is the
/// command head plus subcommand chain (e.g., <c>find</c>, <c>git status</c>).
/// The directory is the first path-like positional argument with the
/// file-parent rule applied — when present it overrides the resolved cwd as
/// the candidate's effective directory in the approval matcher; when null
/// the matcher falls back to the spawned process's cwd.
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
    /// invocation. The directory half is the first path-like positional
    /// argument extracted from each clause (with the file-parent rule
    /// applied), or null when the clause has no path argument. The matcher
    /// SHALL use this directory as the candidate's effective directory,
    /// falling back to <see cref="ToolExecutionContext.Cwd"/> when null.
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
/// Shell-specific approval matcher. Verb-chain extraction stops at the first
/// flag, path, or URL token; <c>&amp;&amp;</c> / <c>||</c> / <c>;</c> split
/// approval units while <c>|</c> stays inside one unit; <c>bash -c</c> /
/// <c>sh -c</c> wrappers recurse into the inner command.
/// </summary>
public sealed class ShellApprovalMatcher : IToolApprovalMatcher
{
    public static readonly ShellApprovalMatcher Instance = new();

    // BashParser is immutable — Parse delegates to a pure static — so one
    // shared instance is safe and avoids a per-evaluation allocation. The DI
    // container registers it as a singleton for the same reason.
    private static readonly ShellSyntaxTree.BashParser Parser = new();

    public string GetApprovalModeKey(ToolName toolName, IDictionary<string, object?>? arguments)
        => toolName.Value;

    public bool IsFailClosedOnPersonal(ToolName toolName, IDictionary<string, object?>? arguments)
        => true;

    public IReadOnlyList<string> ExtractPatterns(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        var command = GetCommand(arguments);
        if (string.IsNullOrWhiteSpace(command))
            return [];

        var workingDirectory = GetWorkingDirectory(arguments);
        var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // POSIX commands route through BashParser so the approval units
        // match the clause decomposition ExtractCandidates already uses — in
        // particular a bare newline separates statements, so a multi-line
        // command yields one unit per statement. Windows keeps the legacy
        // ShellTokenizer path — ShellSyntaxTree is bash-only.
        if (!OperatingSystem.IsWindows())
        {
            foreach (var unit in ExtractApprovalUnitsViaBashParser(command))
            {
                var normalized = ShellTokenizer.NormalizeApprovalUnit(unit, workingDirectory);
                if (!string.IsNullOrEmpty(normalized))
                    patterns.Add(normalized);
            }

            return patterns.ToList();
        }

        TraverseApprovalUnits(command, unit =>
        {
            var normalized = ShellTokenizer.NormalizeApprovalUnit(unit, workingDirectory);
            if (!string.IsNullOrEmpty(normalized))
                patterns.Add(normalized);
        });

        return patterns.ToList();
    }

    public IReadOnlyList<string> ExtractCandidateVerbs(ToolName toolName, IDictionary<string, object?>? arguments)
        => ExtractCandidates(toolName, arguments)
            .Select(c => c.Verb)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<ApprovalCandidate> ExtractCandidates(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        var command = GetCommand(arguments);
        if (string.IsNullOrWhiteSpace(command))
            return [];

        // POSIX commands route through BashParser so we pick up the parser's
        // cd-in-compound cwd attribution. The parser walks `cd X && verb`,
        // `bash -c "cd X && verb"`, and multi-step `cd A && cd B && verb`
        // chains; the candidate's directory inherits the latest cd target
        // when the clause itself has no anchored path arg. Windows keeps
        // the legacy ShellTokenizer path — ShellSyntaxTree is bash-only.
        if (!OperatingSystem.IsWindows())
            return ExtractCandidatesViaBashParser(command);

        var seen = new HashSet<(string, string?)>();
        var candidates = new List<ApprovalCandidate>();
        TraverseApprovalUnits(command, unit =>
        {
            var verb = ShellTokenizer.ExtractVerbChain(unit);
            if (string.IsNullOrEmpty(verb))
                return;

            var directory = ShellTokenizer.ExtractFirstPathArgument(unit);
            var key = (verb.ToLowerInvariant(), directory);
            if (seen.Add(key))
                candidates.Add(new ApprovalCandidate(verb, directory));
        });

        return candidates;
    }

    /// <summary>
    /// Parses <paramref name="command"/>, returning null when the parser
    /// rejects it (unparseable, no clauses) or throws — a parser exception
    /// must never take down the approval flow. Callers treat null as
    /// "cannot decompose" and apply their own fail-safe: an empty
    /// pattern/candidate list (messy semantics — Once/Deny prompt only) or
    /// the flattened raw command for display.
    /// </summary>
    private static ShellSyntaxTree.ParsedCommand? TryParseCommand(string command)
    {
        try
        {
            var result = Parser.Parse(command);
            return result.IsUnparseable || result.Clauses.Count == 0 ? null : result;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<ApprovalCandidate> ExtractCandidatesViaBashParser(string command)
    {
        var result = TryParseCommand(command);
        if (result is null)
            return [];

        // Group consecutive Pipe clauses into a single approval unit so
        // `cat /etc/hosts | wc -l` stays one decision rather than two.
        // AndIf / OrIf / Sequence and the leading None-operator clause each
        // start a fresh group.
        var seen = new HashSet<(string, string?)>();
        var candidates = new List<ApprovalCandidate>();
        ShellSyntaxTree.Clause? groupHead = null;

        foreach (var clause in result.Clauses)
        {
            if (clause.Operator != ShellSyntaxTree.CompoundOperator.Pipe)
                groupHead = clause;

            if (groupHead is null)
                continue;

            if (!ReferenceEquals(clause, groupHead))
                continue;  // pipe-tail clauses fold into the group head

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
            var verb = ShellTokenizer.ApplyVerbShortCircuit(
                string.Join(" ", TrimTrailingValueTokens(clause.Verb.Tokens)));
            if (string.IsNullOrEmpty(verb))
                continue;

            // Side-effect verbs (echo, printf, :, true, false) don't
            // operate on the filesystem, so inheriting the cd target
            // would (a) break ApprovalPatternMatching.IsPureSideEffect's
            // null-directory invariant and (b) attach a misleading scope
            // to a verb that ignores cwd. Their candidates remain
            // directory-less; redirects (echo X > /tmp/log) still
            // surface their target via the explicit-path scan above
            // when BashParser exposes the redirect arg.
            var isSideEffectVerb = ShellTokenizer.SingleTokenSideEffectVerbs.Contains(verb);
            var directory = ResolveClauseDirectory(clause, isSideEffectVerb);
            var key = (verb.ToLowerInvariant(), directory);
            if (seen.Add(key))
                candidates.Add(new ApprovalCandidate(verb, directory));
        }

        return candidates;
    }

    private static string? ResolveClauseDirectory(ShellSyntaxTree.Clause clause, bool isSideEffectVerb)
    {
        // First explicit path arg wins — that's the candidate's own
        // operand, e.g. `dotnet test /home/user/repos/Foo`. Only the
        // anchored-path predicate from the legacy tokenizer counts (/, ~/,
        // ./, ../ and the bare ~/./..), so `feature/freshdesk-cli-skill`
        // and other internal-slash tokens stay as args, not directories.
        // The IsPathToken classification runs on the raw user-facing form
        // so branch names whose Resolved happens to look path-like don't
        // get misclassified; once classified as a path, we persist the
        // parser-resolved absolute path when available so it compares
        // string-equal to cwd-attributed directories produced by other
        // clauses (otherwise `cd ~/x && verb` produces "2 directories" in
        // the approval prompt even though both refer to the same folder).
        foreach (var arg in clause.Args)
        {
            if (arg.IsCwdAttribution)
                continue;

            var raw = arg.Raw;
            if (string.IsNullOrEmpty(raw))
                continue;

            if (raw.StartsWith('-'))
                continue;

            if (ShellTokenizer.IsPathToken(raw))
            {
                var canonical = !string.IsNullOrEmpty(arg.Resolved) ? arg.Resolved : raw;
                return ShellTokenizer.ApplyFileParentRule(canonical);
            }
        }

        // Side-effect verbs ignore cd attribution — see caller's comment
        // on why (null-directory invariant in IsPureSideEffect, and these
        // verbs don't operate on the filesystem anyway).
        if (isSideEffectVerb)
            return null;

        // No explicit path → inherit the cd-attributed cwd from any
        // preceding `cd X` in this compound (or in a wrapping `bash -c
        // "..."` invocation; the parser flattens that).
        var cwdAttribution = clause.Args.FirstOrDefault(a => a.IsCwdAttribution);
        return cwdAttribution?.Resolved;
    }

    /// <summary>
    /// Splits a POSIX command into approval-unit strings via BashParser:
    /// one unit per statement, with consecutive <c>|</c> clauses folded into
    /// the same unit so <c>cat x | wc -l</c> stays a single decision.
    /// Returns an empty list for messy, unparseable, or parser-rejected
    /// commands — mirroring the legacy <see cref="ShellTokenizer.SplitCompoundCommand"/>
    /// empty-result contract so the prompt builder offers only Once/Deny.
    /// </summary>
    private static IReadOnlyList<string> ExtractApprovalUnitsViaBashParser(string command)
    {
        // Messy commands (control-flow keywords, unbalanced brackets) cannot
        // be cleanly decomposed into approval units; mirror the legacy
        // splitter, which returns no units for them.
        if (ShellTokenizer.IsMessyCompoundCommand(command))
            return [];

        var result = TryParseCommand(command);
        if (result is null)
            return [];

        try
        {
            var units = new List<string>();
            var current = new StringBuilder();

            foreach (var clause in result.Clauses)
            {
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
    /// typed. Call-specific value arguments (issue #1331, generalized to
    /// digit-bearing tokens — see <see cref="IsCallSpecificValueToken"/>)
    /// are also excluded since they vary between invocations of the same
    /// verb chain. Once a value token is encountered, the greedy walk
    /// terminates — subsequent args (wrapped subcommands like <c>curl</c>
    /// after <c>timeout 30</c>) are outside the approval intent.
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

        // Messy commands cannot be auto-approved: the matcher cannot extract a
        // candidate verb-chain to evaluate against the persisted store, so
        // every messy invocation must round-trip through the user. The prompt
        // builder offers only Once/Deny in this case (see IsMessy).
        if (ShellTokenizer.IsMessyCompoundCommand(command))
            return false;

        // Fail-closed on a parser miss: BashParser swallows exceptions and
        // unparseable results return an empty candidate list. Treating that
        // as "approved" would silently auto-allow any command our parser
        // regresses on. Force the gate instead.
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
        => ShellTokenizer.IsMessyCompoundCommand(GetCommand(arguments));

    public string FormatForDisplay(ToolName toolName, IDictionary<string, object?>? arguments)
    {
        var command = GetCommand(arguments);
        if (string.IsNullOrWhiteSpace(command))
            return "(empty command)";

        // Fast path: a command with no embedded line break renders verbatim.
        if (!ContainsLineBreak(command))
            return command;

        // Issue #1402: channel renderers embed DisplayText in single-line
        // code fences, so a multi-line quoted string (a message body, an
        // inline script) dumped verbatim corrupts the approval prompt. On
        // POSIX, rebuild a one-line view from the parse tree with multi-line
        // args summarized by size; Windows flattens — ShellSyntaxTree is
        // bash-only. The trailing ReplaceLineEndings catches line breaks the
        // reconstruction can leak and collapses CRLF to a single space.
        var display = OperatingSystem.IsWindows()
            ? command
            : BuildSanitizedDisplayViaParser(command);

        return display.ReplaceLineEndings(" ");
    }

    /// <summary>
    /// Rebuilds a one-line display string for a multi-line command from its
    /// parse tree: statement separators render as explicit operators and any
    /// multi-line argument is replaced with a <c>(N lines, M chars)</c>
    /// summary — the operator approving the command needs its shape, not the
    /// full content (issue #1402). Returns the raw command (for the caller
    /// to flatten) when the parser cannot decompose it, when the command
    /// contains a heredoc — the parser drops heredoc bodies entirely (only
    /// the <c>&lt;&lt;EOF</c> marker survives as a redirect target), so a
    /// tree reconstruction would silently omit executable content the
    /// approver must see — or when it contains a subshell, whose grouping
    /// does not survive the flat clause list, so a reconstruction would
    /// misstate which statements a pipe or <c>&amp;&amp;</c> guard applies
    /// to. The flattened raw command is ugly but fully disclosed.
    /// </summary>
    private static string BuildSanitizedDisplayViaParser(string command)
    {
        var result = TryParseCommand(command);
        if (result is null)
            return command;

        if (result.Clauses.Any(c => c.IsSubshell || c.Redirects.Any(IsHeredocRedirect)))
            return command;

        try
        {
            var sb = new StringBuilder();
            var first = true;

            foreach (var clause in result.Clauses)
            {
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
    /// True when a parsed redirect is a heredoc marker (<c>&lt;&lt;EOF</c>,
    /// <c>&lt;&lt;-EOF</c>). The parser keeps only the marker as the redirect
    /// target and drops the body from the tree, so heredoc-bearing commands
    /// must not be display-reconstructed — see
    /// <see cref="BuildSanitizedDisplayViaParser"/>.
    /// </summary>
    private static bool IsHeredocRedirect(ShellSyntaxTree.Redirect redirect)
        => redirect.Target?.StartsWith("<<", StringComparison.Ordinal) == true;

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

    private static void TraverseApprovalUnits(string command, Action<string> visitUnit)
    {
        // Approval units recurse through shell wrappers but keep the outer
        // splitting rules stable, so `bash -c "grep ... | wc -l" && git push`
        // still becomes two independent approval decisions.
        foreach (var segment in ShellTokenizer.SplitCompoundCommand(command))
        {
            var innerCommands = ShellTokenizer.ExtractInnerCommands(segment);
            if (innerCommands.Count > 0)
            {
                foreach (var inner in innerCommands)
                    TraverseApprovalUnits(inner, visitUnit);

                continue;
            }

            visitUnit(segment);
        }
    }
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
