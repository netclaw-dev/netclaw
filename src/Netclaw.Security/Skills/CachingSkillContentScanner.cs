using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Netclaw.Configuration;

namespace Netclaw.Security.Skills;

/// <summary>
/// Decorator that caches <see cref="ISkillContentScanner"/> results by content hash
/// and trust tier. Avoids redundant regex scanning when the same skill content is
/// loaded multiple times within a session (e.g., repeated <c>skill_load</c> calls).
/// </summary>
public sealed class CachingSkillContentScanner : ISkillContentScanner
{
    private readonly ISkillContentScanner _inner;
    private readonly ConcurrentDictionary<CacheKey, SkillScanResult> _cache = new();
    private const int MaxCacheEntries = 256;

    public CachingSkillContentScanner(ISkillContentScanner inner)
    {
        _inner = inner;
    }

    public async Task<SkillScanResult> ScanAsync(
        string skillName,
        string content,
        SkillTrustTier trustTier = SkillTrustTier.User,
        CancellationToken cancellationToken = default)
    {
        var key = new CacheKey(ComputeContentHash(content), trustTier);

        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var result = await _inner.ScanAsync(skillName, content, trustTier, cancellationToken);

        // Only cache Allow/Warning verdicts. Rejections from transient errors
        // (e.g., scanner failure) should be retried.
        if (result.Verdict != ScanVerdict.Rejected && _cache.Count < MaxCacheEntries)
            _cache.TryAdd(key, result);

        return result;
    }

    private static string ComputeContentHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    private readonly record struct CacheKey(string ContentHash, SkillTrustTier TrustTier);
}
