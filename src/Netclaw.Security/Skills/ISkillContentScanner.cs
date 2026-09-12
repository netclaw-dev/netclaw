// -----------------------------------------------------------------------
// <copyright file="ISkillContentScanner.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
/// Verdict from a skill content scan.
/// </summary>
public enum ScanVerdict
{
    /// <summary>Content passed all checks.</summary>
    Allowed,

    /// <summary>Content triggered a low-severity match; allowed with a logged warning.</summary>
    Warning,

    /// <summary>Content was rejected due to a high-severity match.</summary>
    Rejected
}

/// <summary>
/// Result of a skill content scan.
/// </summary>
/// <param name="Verdict">Whether the content was allowed, warned, or rejected.</param>
/// <param name="Reason">Explanation when content triggers a warning or rejection; null when allowed.</param>
public sealed record SkillScanResult(ScanVerdict Verdict, string? Reason)
{
    /// <summary>Backward-compatible: true when <see cref="Verdict"/> is not <see cref="ScanVerdict.Rejected"/>.</summary>
    public bool IsAllowed => Verdict != ScanVerdict.Rejected;

    public static SkillScanResult Allow() => new(ScanVerdict.Allowed, null);
    public static SkillScanResult Warn(string reason) => new(ScanVerdict.Warning, reason);
    public static SkillScanResult Reject(string reason) => new(ScanVerdict.Rejected, reason);
}
