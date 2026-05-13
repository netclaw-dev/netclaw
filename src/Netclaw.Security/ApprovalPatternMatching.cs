// -----------------------------------------------------------------------
// <copyright file="ApprovalPatternMatching.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Security;

/// <summary>
/// Approval matching helpers that consume the v2 typed
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
    /// that directory: <see cref="PathUtility.ContainsSymlinkSegment"/> walks
    /// each component from the approved root toward the effective directory
    /// and refuses the match if any segment is a reparse point.
    /// </summary>
    public static bool MatchesShellApproval(
        string candidateVerb,
        string? candidateDirectory,
        string? cwd,
        IEnumerable<ApprovalEntry> approvedEntries)
    {
        var effectiveDirectory = ResolveEffectiveDirectory(candidateDirectory, cwd);

        // Lazily computed once per call so the candidate's Path.GetFullPath
        // canonicalization isn't repeated for every folder-scoped entry
        // whose verb happens to match. Wrapped in try/catch below because
        // GetFullPath can throw on malformed input.
        string? normalizedCandidate = null;

        foreach (var entry in approvedEntries)
        {
            if (!ToolApprovalEntryComparer.Equals(entry.Verb, candidateVerb))
                continue;

            // Global wildcard: matches any candidate by definition.
            if (entry.Directory is null)
                return true;

            // Folder-scoped entry requires a concrete effective directory.
            if (string.IsNullOrEmpty(effectiveDirectory))
                continue;

            try
            {
                normalizedCandidate ??= PathUtility.Normalize(effectiveDirectory);

                if (!PathUtility.IsNormalizedWithinRoot(normalizedCandidate, entry.Directory))
                    continue;

                if (PathUtility.ContainsSymlinkSegment(entry.Directory, effectiveDirectory))
                    continue;

                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                continue;
            }
        }

        return false;
    }

    /// <summary>
    /// Backwards-compatible overload retained for v2.0 callers that pass cwd
    /// only. Equivalent to passing <c>null</c> for the candidate directory.
    /// </summary>
    public static bool MatchesShellApproval(
        string candidateVerb,
        string? cwd,
        IEnumerable<ApprovalEntry> approvedEntries)
        => MatchesShellApproval(candidateVerb, candidateDirectory: null, cwd, approvedEntries);

    /// <summary>
    /// Resolves a candidate's path argument to an absolute path. When the
    /// argument is null, falls back to cwd. When the argument is relative
    /// (<c>./build</c>, <c>../shared</c>, or bare <c>~</c> without expansion),
    /// it is resolved against cwd. Tilde-rooted paths are passed through
    /// unchanged — the storage layer treats <c>~</c> consistently with the
    /// daemon's home expansion via <see cref="PathUtility.ExpandAndNormalize"/>
    /// at match time.
    /// </summary>
    private static string? ResolveEffectiveDirectory(string? candidateDirectory, string? cwd)
    {
        if (string.IsNullOrEmpty(candidateDirectory))
            return cwd;

        if (Path.IsPathRooted(candidateDirectory))
            return candidateDirectory;

        // Tilde-rooted paths look "rooted" to the user but aren't to .NET.
        // Expand against the user's home alongside any cwd-relative segments
        // so we end up with a canonicalized absolute path.
        var expanded = PathUtility.ExpandAndNormalize(candidateDirectory, cwd);
        return expanded ?? candidateDirectory;
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
            if (ToolApprovalEntryComparer.Equals(approved.Verb, candidate))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true when this candidate is a pure side-effect clause that
    /// should not be persisted on Always-here/Always-anywhere clicks. The
    /// rule is verb-in-skip-list AND no path argument. Redirect detection
    /// (e.g. <c>echo X &gt; /tmp/log</c>) is implicit: a redirect target
    /// shows up as the candidate's directory via
    /// <see cref="ShellTokenizer.ExtractFirstPathArgument"/>, so a candidate
    /// with a non-null Directory is never considered pure side effect.
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
        if (candidate.Directory is not null)
            return false;

        return ShellTokenizer.SingleTokenSideEffectVerbs.Contains(candidate.Verb);
    }
}
