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
/// the curated <see cref="SafeVerbList"/> AND the candidate's cwd resolves
/// under one of the audience-aware safe-space roots, the policy short-circuits
/// to "approved" without prompting the user.
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
/// read-only by the bundled safe-verbs list and any user-additive override.
/// </summary>
internal sealed class ScopedShellSafeVerbPolicy
{
    private readonly SafeVerbList _safeVerbs;

    public ScopedShellSafeVerbPolicy(SafeVerbList safeVerbs)
    {
        _safeVerbs = safeVerbs;
    }

    /// <summary>
    /// Evaluates a candidate (verb, cwd) pair against the safe-verb policy.
    /// Returns <c>true</c> when the gate should short-circuit to allow with
    /// no user prompt; <c>false</c> when the candidate should fall through
    /// to the existing approval gate.
    /// </summary>
    public bool ShortCircuitsApproval(string candidateVerb, string? cwd, ToolExecutionContext? context)
        => AllShortCircuit([candidateVerb], cwd, context);

    /// <summary>
    /// Returns true when every candidate verb in <paramref name="candidateVerbs"/>
    /// is short-circuited by the safe-verb policy under the supplied
    /// <paramref name="cwd"/>. Used by the gate to bypass the approval prompt
    /// only when the entire compound is read-only-in-safe-space; any single
    /// non-safe candidate falls the whole invocation through to the prompt.
    /// Cwd-and-roots resolution runs once per call rather than per verb,
    /// so an N-verb compound costs one path-normalize + one symlink-segment
    /// scan instead of N.
    /// </summary>
    public bool AllShortCircuit(IReadOnlyList<string> candidateVerbs, string? cwd, ToolExecutionContext? context)
    {
        if (candidateVerbs.Count == 0)
            return false;

        if (string.IsNullOrWhiteSpace(cwd))
            return false;

        foreach (var verb in candidateVerbs)
        {
            if (string.IsNullOrWhiteSpace(verb) || !_safeVerbs.Contains(verb))
                return false;
        }

        var safeRoots = ResolveSafeSpaceRoots(context);
        if (safeRoots.Count == 0)
            return false;

        string fullCwd;
        try
        {
            fullCwd = Path.GetFullPath(cwd);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (var root in safeRoots)
        {
            if (!PathUtility.IsWithinRoot(fullCwd, root))
                continue;

            // A planted symlink under a safe-space root could redirect the
            // cwd into a path outside that root. Refuse the short-circuit if
            // any segment of the cwd path is a reparse point — the user can
            // still grant manually via the interactive prompt, where they
            // will see the literal cwd they are authorizing.
            if (PathUtility.ContainsSymlinkSegment(root, fullCwd))
                continue;

            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves the audience-aware safe-space roots for the current
    /// invocation. Personal and Team get <c>session_dir + project_dir</c>;
    /// Public gets <c>session_dir</c> only.
    /// </summary>
    private static IReadOnlyList<string> ResolveSafeSpaceRoots(ToolExecutionContext? context)
    {
        if (context is null)
            return [];

        var audience = ResolveAudience(context);
        var roots = new List<string>(2);

        if (!string.IsNullOrWhiteSpace(context.SessionDirectory))
            roots.Add(PathUtility.Normalize(context.SessionDirectory));

        // Public audience cannot expand its safe space via project_dir —
        // mirrors the file_read read-roots restriction enforced by
        // ScopedFileAccessPolicy. Even a Public session that has somehow
        // populated WorkingContext.ProjectDirectory does not get to use it
        // as a shell safe-space root.
        if (audience != TrustAudience.Public
            && !string.IsNullOrWhiteSpace(context.ProjectDirectory))
        {
            roots.Add(PathUtility.Normalize(context.ProjectDirectory));
        }

        return roots;
    }

    private static TrustAudience ResolveAudience(ToolExecutionContext context)
        => SecurityPolicyDefaults.ResolveAudienceWithFallback(context.Audience, context.SessionId);
}
