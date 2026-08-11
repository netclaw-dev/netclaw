// -----------------------------------------------------------------------
// <copyright file="ScopedShellSafeVerbPolicy.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Tools;

/// <summary>
/// Layer 1.5 of the shell approval pipeline (between the hard-deny list and
/// the interactive approval gate): when both the candidate verb chain is on
/// the supplied <see cref="SafeVerbList"/> AND each effective directory is
/// under an audience-aware safe-space root, the policy grants access without
/// a prompt.
///
/// Mirrors <see cref="ScopedFileAccessPolicy"/> for the audience model and
/// the symlink-segment guard. Personal and Team audiences get
/// <c>session_dir + project_dir</c> as their safe-space roots; Public gets
/// <c>session_dir</c> only — Public sessions cannot expand their safe space
/// via <c>set_working_directory</c>, mirroring the read-roots restriction
/// <see cref="ScopedFileAccessPolicy"/> enforces for file_read.
///
/// The policy never relaxes the hard-deny list (layer 1) — that runs first
/// in <see cref="ToolAccessPolicy"/>. It only relaxes the interactive
/// approval gate (layer 2) for verbs that have been explicitly classified as
/// read-only by the supplied safe-verbs list.
/// </summary>
internal sealed class ScopedShellSafeVerbPolicy
{
    private const string GitLsTreeVerb = "git ls-tree";
    private readonly SafeVerbList _safeVerbs;

    public ScopedShellSafeVerbPolicy(SafeVerbList safeVerbs)
    {
        _safeVerbs = safeVerbs;
    }

    /// <summary>
    /// Evaluates a candidate verb and cwd against the safe-verb policy.
    /// Returns <c>true</c> when the gate should short-circuit to allow with
    /// no user prompt; <c>false</c> when the candidate should fall through
    /// to the existing approval gate.
    /// </summary>
    public bool ShortCircuitsApproval(string candidateVerb, string? cwd, ToolInvocationContext context)
        => AllShortCircuit([new ApprovalCandidate(candidateVerb, Directory: null)], cwd, context);

    /// <summary>
    /// Removes the variable tree operand from a read-only <c>git ls-tree</c>
    /// candidate. Other Git commands keep exact parser output because a
    /// trailing token can name a mutating subcommand.
    /// </summary>
    public ApprovalCandidate NormalizeCandidate(ApprovalCandidate candidate)
    {
        if (_safeVerbs.IsOperandBearingMatch(candidate.Verb, GitLsTreeVerb))
        {
            return candidate with { Verb = GitLsTreeVerb };
        }

        return candidate;
    }

    /// <summary>
    /// Returns true when each candidate has a safe verb and a safe effective
    /// directory. The candidate directory takes precedence over the cwd.
    /// </summary>
    public bool AllShortCircuit(
        IReadOnlyList<ApprovalCandidate> candidates,
        string? cwd,
        ToolInvocationContext context)
    {
        if (candidates.Count == 0)
            return false;

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Verb) || !_safeVerbs.Contains(candidate.Verb))
                return false;
        }

        var safeRoots = ResolveSafeSpaceRoots(context);
        if (safeRoots.Count == 0)
            return false;

        foreach (var candidate in candidates)
        {
            var effectiveDirectory = candidate.Directory ?? cwd;
            if (string.IsNullOrWhiteSpace(effectiveDirectory))
                return false;

            string fullDirectory;
            try
            {
                fullDirectory = Path.GetFullPath(effectiveDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }

            var isSafe = safeRoots.Any(root =>
                PathUtility.IsWithinRoot(fullDirectory, root)
                && !PathUtility.ContainsSymlinkSegment(root, fullDirectory));

            if (!isSafe)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves the audience-aware safe-space roots for the current
    /// invocation. Personal and Team get <c>session_dir + project_dir</c>;
    /// Public gets <c>session_dir</c> only.
    /// </summary>
    private static IReadOnlyList<string> ResolveSafeSpaceRoots(ToolInvocationContext context)
    {
        var roots = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(context.SessionDirectory))
            roots.Add(PathUtility.Normalize(context.SessionDirectory));

        // Public audience cannot expand its safe space via project_dir —
        // mirrors the file_read read-roots restriction enforced by
        // ScopedFileAccessPolicy. Even a Public session that has somehow
        // populated WorkingContext.ProjectDirectory does not get to use it
        // as a shell safe-space root.
        if (context.Audience != TrustAudience.Public
            && !string.IsNullOrWhiteSpace(context.ProjectDirectory))
        {
            roots.Add(PathUtility.Normalize(context.ProjectDirectory));
        }

        return roots;
    }
}
