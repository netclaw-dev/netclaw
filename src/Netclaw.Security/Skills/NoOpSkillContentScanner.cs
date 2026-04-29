// -----------------------------------------------------------------------
// <copyright file="NoOpSkillContentScanner.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(SkillScanResult.Allow());
    }
}
