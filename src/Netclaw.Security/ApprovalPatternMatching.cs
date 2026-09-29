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

    /// <summary>
    /// Returns true when <paramref name="approvedEntries"/> contains an entry
    /// whose verb equals <paramref name="candidateVerb"/> AND whose directory
    /// is either <c>null</c> (the global wildcard) or an ancestor of the
    /// candidate's effective directory with no symlink segments along the
    /// path between the two.
    ///
    /// The candidate's effective directory is
    /// <paramref name="candidateDirectory"/> when non-null (the path argument
    /// extracted from the command), otherwise <paramref name="cwd"/>. Relative
    /// effective directories (<c>./build</c>, <c>../shared</c>) are resolved
    /// against <paramref name="cwd"/> before the under-check.
    ///
    /// The symlink-segment guard prevents a planted symlink under an approved
    /// directory from being used to redirect the candidate to a path outside
    /// that directory: the filesystem authority walks each component from the
    /// approved root toward the effective directory and refuses the match if
    /// any segment is a reparse point.
    /// </summary>
    public static bool MatchesShellApproval(
        string candidateVerb,
        string? candidateDirectory,
        string? cwd,
        IEnumerable<ApprovalEntry> approvedEntries)
        => MatchesApprovalScope(
            candidateDirectory,
            cwd,
            approvedEntries.Where(entry =>
                entry.Repository is null
                && entry.AssignmentDigest is null
                && ToolApprovalEntryComparer.Equals(entry.Verb, candidateVerb)));

    private static bool MatchesApprovalScope(
        string? candidateDirectory,
        string? cwd,
        IEnumerable<ApprovalEntry> approvedEntries,
        ApprovalShell? shell = null)
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

        if (entry.Shell is { } entryShell && candidate.Shell != entryShell)
        {
            return false;
        }

        if (entry.Match == ApprovalMatchKind.LegacyExact)
        {
            return ToolApprovalEntryComparer.Equals(
                entry.Verb,
                candidate.Verb,
                entry.Shell!.Value);
        }

        if (entry.Match != ApprovalMatchKind.TokenPrefix ||
            candidate.Shell is null ||
            candidate.VerbTokens is null ||
            entry.VerbTokens is null ||
            candidate.VerbTokens.Any(static token =>
                token.Length == 0 || token.Any(char.IsWhiteSpace)) ||
            entry.VerbTokens.Count > candidate.VerbTokens.Count)
        {
            return false;
        }

        for (var index = 0; index < entry.VerbTokens.Count; index++)
        {
            if (!ToolApprovalEntryComparer.Equals(
                    entry.VerbTokens[index],
                    candidate.VerbTokens[index],
                    entry.Shell!.Value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Backwards-compatible overload for callers that pass cwd
    /// only. Equivalent to passing <c>null</c> for the candidate directory.
    /// </summary>
    public static bool MatchesShellApproval(
        string candidateVerb,
        string? cwd,
        IEnumerable<ApprovalEntry> approvedEntries)
        => MatchesShellApproval(candidateVerb, candidateDirectory: null, cwd, approvedEntries);

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
    /// (<see cref="ShellTokenizer.SingleTokenSideEffectVerbs"/>) is shared
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

        return ShellTokenizer.SingleTokenSideEffectVerbs.Contains(candidate.Verb);
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
