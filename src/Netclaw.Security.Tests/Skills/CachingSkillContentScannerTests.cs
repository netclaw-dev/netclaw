using Netclaw.Configuration;
using Netclaw.Security.Skills;
using Xunit;

namespace Netclaw.Security.Tests.Skills;

public sealed class CachingSkillContentScannerTests
{
    [Fact]
    public async Task ScanAsync_returns_cached_result_for_identical_content()
    {
        var counter = new CountingScanner();
        var caching = new CachingSkillContentScanner(counter);

        var result1 = await caching.ScanAsync("skill-a", "safe content", SkillTrustTier.Community);
        var result2 = await caching.ScanAsync("skill-b", "safe content", SkillTrustTier.Community);

        Assert.Equal(ScanVerdict.Allowed, result1.Verdict);
        Assert.Equal(ScanVerdict.Allowed, result2.Verdict);
        Assert.Equal(1, counter.CallCount); // Second call was served from cache
    }

    [Fact]
    public async Task ScanAsync_different_trust_tiers_are_cached_separately()
    {
        var counter = new CountingScanner();
        var caching = new CachingSkillContentScanner(counter);

        await caching.ScanAsync("skill", "content", SkillTrustTier.Community);
        await caching.ScanAsync("skill", "content", SkillTrustTier.External);

        Assert.Equal(2, counter.CallCount);
    }

    [Fact]
    public async Task ScanAsync_different_content_is_not_cached()
    {
        var counter = new CountingScanner();
        var caching = new CachingSkillContentScanner(counter);

        await caching.ScanAsync("skill", "content-a", SkillTrustTier.Community);
        await caching.ScanAsync("skill", "content-b", SkillTrustTier.Community);

        Assert.Equal(2, counter.CallCount);
    }

    [Fact]
    public async Task ScanAsync_does_not_cache_rejections()
    {
        var rejectOnce = new RejectOnceScanner();
        var caching = new CachingSkillContentScanner(rejectOnce);

        var result1 = await caching.ScanAsync("skill", "content", SkillTrustTier.Community);
        var result2 = await caching.ScanAsync("skill", "content", SkillTrustTier.Community);

        Assert.Equal(ScanVerdict.Rejected, result1.Verdict);
        Assert.Equal(ScanVerdict.Allowed, result2.Verdict); // Not cached, re-evaluated
    }

    private sealed class CountingScanner : ISkillContentScanner
    {
        public int CallCount { get; private set; }

        public Task<SkillScanResult> ScanAsync(
            string skillName, string content, SkillTrustTier trustTier,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(SkillScanResult.Allow());
        }
    }

    private sealed class RejectOnceScanner : ISkillContentScanner
    {
        private int _callCount;

        public Task<SkillScanResult> ScanAsync(
            string skillName, string content, SkillTrustTier trustTier,
            CancellationToken cancellationToken = default)
        {
            _callCount++;
            return Task.FromResult(_callCount == 1
                ? SkillScanResult.Reject("transient failure")
                : SkillScanResult.Allow());
        }
    }
}
