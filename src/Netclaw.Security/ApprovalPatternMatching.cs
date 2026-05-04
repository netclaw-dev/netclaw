// -----------------------------------------------------------------------
// <copyright file="ApprovalPatternMatching.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security;

/// <summary>
/// Shared verb-chain prefix matcher used for tool approval grants. An approved
/// pattern matches a candidate exactly or as a verb-chain prefix on a space
/// boundary — so "git push" approves "git push origin main" but never
/// "github-cli".
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

            // Multi-token patterns prefix-match on a space boundary. Single-token
            // patterns remain exact-only so grants do not silently widen from
            // "cat" to every path-bearing cat invocation.
            if (approved.Contains(' ', StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
