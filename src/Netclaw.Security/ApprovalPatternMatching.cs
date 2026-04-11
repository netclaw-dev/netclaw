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

            if (candidate.StartsWith(approved, StringComparison.OrdinalIgnoreCase)
                && candidate.Length > approved.Length
                && candidate[approved.Length] == ' ')
            {
                return true;
            }
        }

        return false;
    }
}
