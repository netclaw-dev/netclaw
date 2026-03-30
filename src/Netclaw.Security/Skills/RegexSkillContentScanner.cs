using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Security.Skills;

/// <summary>
/// Scans skill content for security threats by delegating to <see cref="IPromptInjectionDetector"/>
/// and applying trust-tier-aware policy to map detection results to scan outcomes.
/// This is a heuristic guardrail, not a complete malware scanner.
/// </summary>
public sealed class RegexSkillContentScanner : ISkillContentScanner
{
    private readonly IPromptInjectionDetector _detector;
    private readonly ILogger<RegexSkillContentScanner> _logger;

    public RegexSkillContentScanner(
        IPromptInjectionDetector detector,
        ILogger<RegexSkillContentScanner> logger)
    {
        _detector = detector;
        _logger = logger;
    }

    public async Task<SkillScanResult> ScanAsync(
        string skillName,
        string content,
        SkillTrustTier trustTier = SkillTrustTier.User,
        CancellationToken cancellationToken = default)
    {
        // System and User (operator) tiers are not scanned.
        // System skills are hash-verified from CDN; User skills are operator responsibility.
        if (trustTier <= SkillTrustTier.User)
            return SkillScanResult.Allow();

        PromptInjectionResult detection;
        try
        {
            detection = await _detector.DetectAsync(content, $"skill:{skillName}", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skill content scanning failed for '{SkillName}'", skillName);
            return SkillScanResult.Reject("content scanning failed");
        }

        if (detection.Risk == PromptInjectionRisk.None)
            return SkillScanResult.Allow();

        var verdict = MapToVerdict(detection.Risk, trustTier);
        var reason = string.IsNullOrWhiteSpace(detection.Category)
            ? detection.Message ?? "prompt injection risk detected"
            : $"[{detection.Category}] {detection.Message}";

        if (verdict == ScanVerdict.Rejected)
        {
            _logger.LogWarning(
                "Skill '{SkillName}' ({TrustTier}) rejected: {Reason}",
                skillName, trustTier, reason);
            return SkillScanResult.Reject(reason);
        }

        if (verdict == ScanVerdict.Warning)
        {
            _logger.LogWarning(
                "Skill '{SkillName}' ({TrustTier}) passed with warning: {Reason}",
                skillName, trustTier, reason);
            return SkillScanResult.Warn(reason);
        }

        return SkillScanResult.Allow();
    }

    /// <summary>
    /// Maps a detection risk level to a scan verdict based on trust tier.
    /// </summary>
    /// <remarks>
    /// <list type="table">
    ///   <listheader><term>Risk</term><description>Community → External/Agent</description></listheader>
    ///   <item><term>Low</term><description>Allow → Warn</description></item>
    ///   <item><term>Medium</term><description>Warn → Reject</description></item>
    ///   <item><term>High</term><description>Reject → Reject</description></item>
    /// </list>
    /// </remarks>
    private static ScanVerdict MapToVerdict(PromptInjectionRisk risk, SkillTrustTier tier)
    {
        var isStrictTier = tier >= SkillTrustTier.External; // External and Agent

        return risk switch
        {
            PromptInjectionRisk.Low => isStrictTier ? ScanVerdict.Warning : ScanVerdict.Allowed,
            PromptInjectionRisk.Medium => isStrictTier ? ScanVerdict.Rejected : ScanVerdict.Warning,
            PromptInjectionRisk.High => ScanVerdict.Rejected,
            _ => ScanVerdict.Allowed
        };
    }
}
