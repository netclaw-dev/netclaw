using Microsoft.Extensions.Logging;

namespace Netclaw.Security.Skills;

/// <summary>
/// Scans skill content for security threats by delegating to <see cref="IPromptInjectionDetector"/>
/// and applying a uniform policy to map detection results to scan outcomes.
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
        CancellationToken cancellationToken = default)
    {
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

        var reason = string.IsNullOrWhiteSpace(detection.Category)
            ? detection.Message ?? "prompt injection risk detected"
            : $"[{detection.Category}] {detection.Message}";

        var verdict = MapToVerdict(detection.Risk);

        if (verdict == ScanVerdict.Rejected)
        {
            _logger.LogWarning(
                "Skill '{SkillName}' rejected: {Reason}",
                skillName, reason);
            return SkillScanResult.Reject(reason);
        }

        if (verdict == ScanVerdict.Warning)
        {
            _logger.LogWarning(
                "Skill '{SkillName}' passed with warning: {Reason}",
                skillName, reason);
            return SkillScanResult.Warn(reason);
        }

        return SkillScanResult.Allow();
    }

    /// <summary>
    /// Uniform policy: High → Reject, Medium → Warn, Low → Allow.
    /// </summary>
    private static ScanVerdict MapToVerdict(PromptInjectionRisk risk) => risk switch
    {
        PromptInjectionRisk.Low => ScanVerdict.Allowed,
        PromptInjectionRisk.Medium => ScanVerdict.Warning,
        PromptInjectionRisk.High => ScanVerdict.Rejected,
        _ => ScanVerdict.Allowed
    };
}
