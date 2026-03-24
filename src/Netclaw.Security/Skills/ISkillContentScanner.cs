namespace Netclaw.Security.Skills;

/// <summary>
/// Scans skill content for security threats before it is written to disk.
/// Called during <c>skill_manage</c> create and edit operations.
/// </summary>
public interface ISkillContentScanner
{
    Task<SkillScanResult> ScanAsync(
        string skillName,
        string content,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of a skill content scan.
/// </summary>
/// <param name="IsAllowed">Whether the content passed the scan.</param>
/// <param name="Reason">Explanation when content is rejected; null when allowed.</param>
public sealed record SkillScanResult(bool IsAllowed, string? Reason);
