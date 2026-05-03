// -----------------------------------------------------------------------
// <copyright file="ApprovalPatternMatching.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security;

/// <summary>
/// Shared verb-chain prefix matcher used for tool approval grants. An approved
/// pattern matches a candidate exactly or as a verb-chain prefix on a space
/// boundary — so "git" approves "git push" but never "github-cli".
/// </summary>
public static class ApprovalPatternMatching
{
    public static bool MatchesAny(string candidate, IEnumerable<string> approvedPatterns)
    {
        foreach (var approved in approvedPatterns)
        {
            if (string.Equals(candidate, approved, StringComparison.OrdinalIgnoreCase))
                return true;

            if (candidate.Length <= approved.Length || candidate[approved.Length] != ' ')
                continue;

            if (!candidate.StartsWith(approved, StringComparison.OrdinalIgnoreCase))
                continue;

            // Multi-token patterns always prefix-match on space boundary.
            if (approved.Contains(' ', StringComparison.Ordinal))
                return true;

            // Single-token path-aware verbs (cat, grep, bash, etc.) wildcard-match
            // so "cat" covers "cat /etc/hosts". Non-path-aware single tokens (gh, echo)
            // require exact match only.
            if (ShellTokenizer.PathAwareVerbs.Contains(approved))
                return true;
        }

        return false;
    }
}
