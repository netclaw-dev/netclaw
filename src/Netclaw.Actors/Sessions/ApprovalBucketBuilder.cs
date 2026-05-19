// -----------------------------------------------------------------------
// <copyright file="ApprovalBucketBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;

namespace Netclaw.Actors.Sessions;

internal static class ApprovalBucketBuilder
{
    /// <summary>
    /// Groups approval candidates into the per-directory buckets that become
    /// <c>RecordApprovalAsync</c> calls.
    /// </summary>
    /// <remarks>
    /// Session-scope entries use <c>candidate.Directory</c> directly without
    /// falling back to <paramref name="cwd"/>. The session approval dictionary
    /// matches verb-only, so threading cwd through here creates buckets that the
    /// session-scratch guard can drop for standalone verbs such as curl or git status.
    ///
    /// Persistent scope still falls back to <paramref name="cwd"/> and applies
    /// the session-scratch guard so folder-scoped grants pointing at the session
    /// scratch directory are not saved as dead-on-arrival approvals.
    /// </remarks>
    public static Dictionary<string, List<string>> Build(
        IReadOnlyList<ApprovalCandidate> candidates,
        bool persistent,
        bool globalWildcard,
        string? cwd,
        string? sessionDirectory)
    {
        var grouping = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (ApprovalPatternMatching.IsPureSideEffect(candidate))
                continue;

            string? effectiveDirectory;
            if (globalWildcard)
            {
                effectiveDirectory = null;
            }
            else if (!persistent)
            {
                effectiveDirectory = candidate.Directory;
            }
            else
            {
                effectiveDirectory = candidate.Directory ?? cwd;

                if (effectiveDirectory is not null
                    && sessionDirectory is not null
                    && PathUtility.AreEquivalentPaths(effectiveDirectory, sessionDirectory))
                {
                    continue;
                }
            }

            var key = effectiveDirectory ?? string.Empty;
            if (!grouping.TryGetValue(key, out var verbs))
            {
                verbs = [];
                grouping[key] = verbs;
            }

            if (!verbs.Contains(candidate.Verb, StringComparer.OrdinalIgnoreCase))
                verbs.Add(candidate.Verb);
        }

        return grouping;
    }
}
