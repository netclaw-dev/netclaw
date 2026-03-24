using Netclaw.Configuration;

namespace Netclaw.Security.Skills;

/// <summary>
/// No-op skill content scanner that allows all content through.
/// Used as a test double and development fallback.
/// </summary>
public sealed class NoOpSkillContentScanner : ISkillContentScanner
{
    public Task<SkillScanResult> ScanAsync(
        string skillName,
        string content,
        SkillTrustTier trustTier = SkillTrustTier.User,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(SkillScanResult.Allow());
    }
}
