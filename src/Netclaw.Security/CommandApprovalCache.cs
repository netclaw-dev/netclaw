using System.Collections.Concurrent;
using Netclaw.Configuration;

namespace Netclaw.Security;

/// <summary>
/// Thread-safe approval cache combining session-scoped (in-memory) and
/// persistent (file-backed) approvals. Each audience has independent approval lists.
/// </summary>
public sealed class CommandApprovalCache
{
    private readonly ToolApprovalStore? _persistentStore;

    // audience -> tool -> set of approved patterns
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentBag<string>>> _sessionApprovals = new();

    public CommandApprovalCache(ToolApprovalStore? persistentStore = null)
    {
        _persistentStore = persistentStore;
    }

    /// <summary>
    /// Checks if a pattern is approved for the given audience and tool,
    /// checking session-scoped cache first, then persistent store.
    /// </summary>
    public bool IsApproved(TrustAudience audience, string toolName, string pattern)
    {
        // Check session-scoped approvals first
        if (IsSessionApproved(audience, toolName, pattern))
            return true;

        // Check persistent store
        if (_persistentStore is null)
            return false;

        var persistedPatterns = _persistentStore.GetApprovedPatterns(audience, toolName);
        return MatchesAnyPattern(pattern, persistedPatterns);
    }

    /// <summary>
    /// Adds a session-scoped approval (lost when the session ends).
    /// </summary>
    public void ApproveForSession(TrustAudience audience, string toolName, string pattern)
    {
        var audienceKey = audience.ToWireValue();
        var toolMap = _sessionApprovals.GetOrAdd(audienceKey, _ => new ConcurrentDictionary<string, ConcurrentBag<string>>(StringComparer.Ordinal));
        var patterns = toolMap.GetOrAdd(toolName, _ => []);

        // Avoid duplicates (ConcurrentBag doesn't deduplicate)
        if (!ContainsPattern(patterns, pattern))
            patterns.Add(pattern);
    }

    /// <summary>
    /// Adds a persistent approval (written to disk immediately, survives restart).
    /// Also caches in session for immediate use.
    /// </summary>
    public void ApprovePersistent(TrustAudience audience, string toolName, string pattern)
    {
        ApproveForSession(audience, toolName, pattern);
        _persistentStore?.AddApproval(audience, toolName, pattern);
    }

    private bool IsSessionApproved(TrustAudience audience, string toolName, string pattern)
    {
        var audienceKey = audience.ToWireValue();
        if (!_sessionApprovals.TryGetValue(audienceKey, out var toolMap))
            return false;

        if (!toolMap.TryGetValue(toolName, out var patterns))
            return false;

        return MatchesAnyPattern(pattern, patterns);
    }

    /// <summary>
    /// Checks if the given pattern matches any approved pattern using
    /// prefix matching (an approved "git push" matches "git push origin main").
    /// </summary>
    private static bool MatchesAnyPattern(string candidatePattern, IEnumerable<string> approvedPatterns)
    {
        foreach (var approved in approvedPatterns)
        {
            if (string.Equals(candidatePattern, approved, StringComparison.OrdinalIgnoreCase))
                return true;

            // Prefix match: approved "git" matches candidate "git push"
            if (candidatePattern.StartsWith(approved, StringComparison.OrdinalIgnoreCase)
                && candidatePattern.Length > approved.Length
                && candidatePattern[approved.Length] == ' ')
                return true;
        }

        return false;
    }

    private static bool ContainsPattern(ConcurrentBag<string> bag, string pattern)
    {
        foreach (var existing in bag)
        {
            if (string.Equals(existing, pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
