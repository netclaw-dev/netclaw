// -----------------------------------------------------------------------
// <copyright file="ApprovalPatternMatching.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security.Authorization.Filesystem;

namespace Netclaw.Security;

/// <summary>
/// Approval match helpers that consume typed
/// <see cref="ApprovalEntry"/> store. Shell approvals use
/// <see cref="MatchesShellApproval"/> which evaluates the candidate's verb
/// chain together with its cwd against each entry's <c>(verb, directory)</c>
/// pair. Other tools use <see cref="MatchesAny"/> for verb-only matching.
/// </summary>
public static class ApprovalPatternMatching
{
    // Verb equality routes through ToolApprovalEntryComparer.Equals so the
    // operator CLI and the daemon gate stay in lock-step on case rules. See
    // ToolApprovalEntryComparer for the rationale (POSIX is case-sensitive
    // for $PATH lookups; Windows is not).

    private static bool MatchesApprovalScope(
        string? candidateDirectory,
        string? cwd,
        IEnumerable<ApprovalEntry> approvedEntries,
        ApprovalShell? shell)
    {
        foreach (var entry in approvedEntries)
        {
            if (EvaluateApprovalScope(candidateDirectory, cwd, entry, shell) == ShellApprovalScopeResult.Match)
                return true;
        }

        return false;
    }

    private static ShellApprovalScopeResult EvaluateApprovalScope(
        string? candidateDirectory,
        string? cwd,
        ApprovalEntry entry,
        ApprovalShell? shell)
    {
        if (entry.Repository is not null)
        {
            return RepositoryIdentity.TryResolve(candidateDirectory, cwd, out var repository)
                   && ToolApprovalEntryComparer.Equals(repository!.CommonDirectory, entry.Repository)
                ? ShellApprovalScopeResult.Match
                : ShellApprovalScopeResult.OutsideDirectory;
        }

        if (entry.Directory is null)
            return ShellApprovalScopeResult.Match;

        // A PowerShell scope uses Windows path rules on every host. Other shells
        // use the host path API, with home expansion for a relative candidate.
        var candidateCreated = shell == ApprovalShell.PowerShell
            ? TryCreateWindowsScopePath(candidateDirectory, cwd, out var candidate)
            : TryCreateHostScopePath(candidateDirectory, cwd, out candidate);
        if (candidateCreated is null)
            return ShellApprovalScopeResult.MissingDirectory;

        var rootCreated = shell == ApprovalShell.PowerShell
            ? CanonicalPath.TryCreate(entry.Directory, relativeBase: null, ShellPathStyle.Windows, out var root)
            : CanonicalPath.TryCreateHost(entry.Directory, relativeBase: null, out root);
        if (candidateCreated == false || !rootCreated)
            return ShellApprovalScopeResult.OutsideDirectory;

        // A folder grant refuses links only below its root (R3). The operator
        // approved the root and its ancestors, which can include an OS alias.
        return FileSystemAuthority.EvaluateMembership(
                candidate,
                [new PathBoundary.Folder(root, LinkRule.BelowRoot)]) switch
            {
                PathDecision.Allowed => ShellApprovalScopeResult.Match,
                PathDecision.CrossesLink => ShellApprovalScopeResult.Symlink,
                _ => ShellApprovalScopeResult.OutsideDirectory,
            };
    }

    /// <summary>
    /// Creates the host path of a candidate. The candidate falls back to cwd. A
    /// relative candidate expands home tokens and resolves against cwd. Returns
    /// null when no directory exists to evaluate.
    /// </summary>
    private static bool? TryCreateHostScopePath(string? candidateDirectory, string? cwd, out CanonicalPath path)
    {
        path = default;
        var directory = string.IsNullOrEmpty(candidateDirectory) ? cwd : candidateDirectory;
        if (string.IsNullOrEmpty(directory))
            return null;

        return string.IsNullOrEmpty(candidateDirectory) || Path.IsPathRooted(candidateDirectory)
            ? CanonicalPath.TryCreateHost(directory, relativeBase: null, out path)
            : CanonicalPath.TryCreateHost(PathUtility.ExpandHome(candidateDirectory), cwd, out path);
    }

    /// <summary>
    /// Creates the Windows path of a PowerShell candidate. The candidate falls back
    /// to cwd, and a relative candidate resolves against cwd. Returns null when no
    /// valid directory exists to evaluate.
    /// </summary>
    private static bool? TryCreateWindowsScopePath(string? candidateDirectory, string? cwd, out CanonicalPath path)
    {
        var created = string.IsNullOrEmpty(candidateDirectory)
            ? CanonicalPath.TryCreate(cwd, relativeBase: null, ShellPathStyle.Windows, out path)
            : CanonicalPath.TryCreate(candidateDirectory, cwd, ShellPathStyle.Windows, out path);
        return created ? true : null;
    }

    /// <summary>
    /// Matches one structured shell candidate against version-3 phrase forms.
    /// A folder grant matches when its directory contains the candidate's
    /// effective directory (the path operand, else <paramref name="cwd"/>) and
    /// no link lies below the grant root. A grant with no directory matches any
    /// directory. A repository grant matches the registered worktrees of its
    /// repository.
    /// </summary>
    public static bool MatchesShellApproval(
        ApprovalCandidate candidate,
        string? cwd,
        IEnumerable<ApprovalEntry> approvedEntries)
    {
        var phraseMatches = approvedEntries.Where(entry => PhraseMatches(candidate, entry));
        return MatchesApprovalScope(
            candidate.Directory,
            cwd,
            phraseMatches,
            candidate.Shell);
    }

    internal static ShellApprovalEvaluation EvaluateShellApproval(
        ApprovalCandidate candidate,
        string? cwd,
        IEnumerable<ApprovalEntry> approvedEntries,
        int maximumNearMisses)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumNearMisses);
        List<ShellApprovalNearMiss>? nearMisses = null;

        foreach (var entry in approvedEntries)
        {
            if (PhraseMatches(candidate, entry))
            {
                var scopeResult = EvaluateApprovalScope(
                    candidate.Directory,
                    cwd,
                    entry,
                    candidate.Shell);
                if (scopeResult == ShellApprovalScopeResult.Match)
                    return new ShellApprovalEvaluation(entry, []);

                if ((nearMisses?.Count ?? 0) < maximumNearMisses)
                {
                    (nearMisses ??= []).Add(new ShellApprovalNearMiss(
                        entry,
                        ToNearMissReason(scopeResult)));
                }

                continue;
            }

            if ((nearMisses?.Count ?? 0) >= maximumNearMisses
                || !TryGetPhraseNearMissReason(candidate, entry, out var reason))
            {
                continue;
            }

            (nearMisses ??= []).Add(new ShellApprovalNearMiss(entry, reason));
        }

        return new ShellApprovalEvaluation(
            MatchedEntry: null,
            nearMisses ?? (IReadOnlyList<ShellApprovalNearMiss>)[]);
    }

    private static bool TryGetPhraseNearMissReason(
        ApprovalCandidate candidate,
        ApprovalEntry entry,
        out ShellApprovalNearMissReason reason)
    {
        reason = default;
        if (candidate.VerbTokens is not { Count: > 0 }
            || entry.VerbTokens is not { Count: > 0 }
            || entry.Shell is null)
        {
            return false;
        }

        var sameExecutable = string.Equals(
            candidate.VerbTokens[0],
            entry.VerbTokens[0],
            StringComparison.OrdinalIgnoreCase);
        if (!sameExecutable)
            return false;

        if (candidate.Shell != entry.Shell)
        {
            reason = ShellApprovalNearMissReason.ShellMismatch;
            return true;
        }

        if (candidate.AssignmentDigest != entry.AssignmentDigest)
        {
            reason = ShellApprovalNearMissReason.AssignmentMismatch;
            return true;
        }

        reason = ShellApprovalNearMissReason.TokenMismatch;
        return true;
    }

    private static ShellApprovalNearMissReason ToNearMissReason(ShellApprovalScopeResult result)
        => result switch
        {
            ShellApprovalScopeResult.OutsideDirectory => ShellApprovalNearMissReason.OutsideDirectory,
            ShellApprovalScopeResult.Symlink => ShellApprovalNearMissReason.Symlink,
            ShellApprovalScopeResult.MissingDirectory => ShellApprovalNearMissReason.MissingDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, "The scope result is not a near miss."),
        };

    private static bool PhraseMatches(ApprovalCandidate candidate, ApprovalEntry entry)
    {
        if (candidate.AssignmentDigest != entry.AssignmentDigest)
        {
            return false;
        }

        if (entry.Match is null)
        {
            return ToolApprovalEntryComparer.Equals(entry.Verb, candidate.Verb);
        }

        if (entry.Shell is not { } entryShell
            || candidate.Shell != entryShell
            || candidate.VerbTokens is not { Count: > 0 }
            || candidate.VerbTokens.Any(static token =>
                token.Length == 0 || token.Any(char.IsWhiteSpace)))
        {
            return false;
        }

        return entry.Match switch
        {
            // The legacy phrase is the space-joined command words, and it must
            // equal all of them: "git push origin" does not cover "git push origin main".
            // It also must equal the display verb, as before, so the command
            // words never widen a legacy phrase past the older matcher.
            ApprovalMatchKind.LegacyExact =>
                ToolApprovalEntryComparer.Equals(entry.Verb, candidate.Verb, entryShell)
                && MatchesChain(entry.Verb.Split(' ', StringSplitOptions.RemoveEmptyEntries), candidate.VerbTokens, entryShell),
            ApprovalMatchKind.TokenPrefix when entry.VerbTokens is { } grantTokens =>
                MatchesChain(grantTokens, candidate.VerbTokens, entryShell),
            _ => false,
        };
    }

    private static bool MatchesChain(
        IReadOnlyList<string> grantTokens,
        IReadOnlyList<string> candidateTokens,
        ApprovalShell shell)
        => VerbChainEquals(grantTokens, candidateTokens, shell)
           || IsSingleTokenProgramGrant(grantTokens, candidateTokens, shell);

    /// <summary>
    /// True when a bare-program grant names a program that policy data gives a
    /// one-token verb chain (<c>echo</c>, <c>which</c>, <c>jq</c>). The command
    /// words keep a plain operand (<c>echo hi</c>), but policy treats that word
    /// as an argument, as <see cref="ShellVerbPolicyData.ApplyVerbShortCircuit"/> does.
    /// </summary>
    private static bool IsSingleTokenProgramGrant(
        IReadOnlyList<string> grantTokens,
        IReadOnlyList<string> candidateTokens,
        ApprovalShell shell)
        => grantTokens.Count == 1
           && ShellVerbPolicyData.HasSingleTokenVerbChain(grantTokens[0])
           && ToolApprovalEntryComparer.Equals(grantTokens[0], candidateTokens[0], shell);

    /// <summary>
    /// True when a grant's tokens equal the candidate's command words.
    /// </summary>
    /// <remarks>
    /// SECURITY: a grant covers exactly its command words, and the arguments are
    /// free. A grant never covers other words: a <c>gh</c> grant covers
    /// <c>gh --help</c>, not <c>gh auth logout</c>. The stored match kind keeps
    /// its historical name <see cref="ApprovalMatchKind.TokenPrefix"/> so that
    /// the version-3 store format does not change.
    /// </remarks>
    internal static bool VerbChainEquals(
        IReadOnlyList<string> grantTokens,
        IReadOnlyList<string> candidateTokens,
        ApprovalShell shell)
    {
        // Focused mutation gate: run-exact-verb-chain-mutations.sh. Removal of
        // this check restores prefix matching ("gh" would cover "gh auth logout").
        var grantLength = grantTokens.Count;
        var candidateLength = candidateTokens.Count;
        if (grantLength != candidateLength)
            return false;

        for (var index = 0; index < grantLength; index++)
        {
            if (!ToolApprovalEntryComparer.Equals(
                    grantTokens[index],
                    candidateTokens[index],
                    shell))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="approvedEntries"/> contains an entry
    /// whose verb equals <paramref name="candidate"/>. Used by non-shell
    /// matchers where the directory half of an entry is not meaningful — the
    /// candidate is the tool name and a verb match alone authorizes.
    /// </summary>
    public static bool MatchesAny(string candidate, IEnumerable<ApprovalEntry> approvedEntries)
    {
        foreach (var approved in approvedEntries)
        {
            if (approved.Repository is null
                && ToolApprovalEntryComparer.Equals(approved.Verb, candidate))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true when this candidate is a pure side-effect clause that
    /// should not be persisted on Always-here/Always-anywhere clicks. The
    /// rule is verb-in-skip-list AND no effective directory. The shell
    /// candidate extractor emits a separate directory candidate for each
    /// redirect target. Thus, <c>echo X &gt; /tmp/log</c> is not exempt.
    /// </summary>
    /// <remarks>
    /// The side-effect verb set
    /// (<see cref="ShellVerbPolicyData.SingleTokenSideEffectVerbs"/>) is shared
    /// with the verb-chain short-circuit so both paths agree on which
    /// verbs collapse to depth 1 and which ones skip persistence.
    /// Conservative on purpose. <c>eval</c>, <c>command</c>, <c>exec</c>,
    /// and other reflective builtins are NOT in the set because they
    /// execute their arguments. Adding entries there is a
    /// security-relevant change reviewed alongside the safe-verb list.
    /// </remarks>
    public static bool IsPureSideEffect(ApprovalCandidate candidate)
    {
        if (candidate.Directory is not null || candidate.AssignmentDigest is not null)
            return false;

        return ShellVerbPolicyData.SingleTokenSideEffectVerbs.Contains(candidate.Verb);
    }
}

internal enum ShellApprovalScopeResult
{
    Match = 0,
    OutsideDirectory = 1,
    Symlink = 2,
    MissingDirectory = 3,
}

internal enum ShellApprovalNearMissReason
{
    OutsideDirectory = 0,
    Symlink = 1,
    MissingDirectory = 2,
    TokenMismatch = 3,
    ShellMismatch = 4,
    AssignmentMismatch = 5,
}

internal sealed record ShellApprovalNearMiss(
    ApprovalEntry Grant,
    ShellApprovalNearMissReason Reason);

internal sealed record ShellApprovalEvaluation(
    ApprovalEntry? MatchedEntry,
    IReadOnlyList<ShellApprovalNearMiss> NearMisses);
