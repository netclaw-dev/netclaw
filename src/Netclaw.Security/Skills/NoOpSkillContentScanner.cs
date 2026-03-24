namespace Netclaw.Security.Skills;

/// <summary>
/// No-op skill content scanner that allows all content through.
/// Used as the default until real scanning is implemented alongside
/// the webhook prompt injection detection infrastructure.
/// </summary>
public sealed class NoOpSkillContentScanner : ISkillContentScanner
{
    public Task<SkillScanResult> ScanAsync(
        string skillName,
        string content,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new SkillScanResult(true, null));
    }
}
