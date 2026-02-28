namespace Netclaw.Security;

/// <summary>
/// Result of prompt injection detection analysis.
/// </summary>
public sealed record PromptInjectionResult(
    PromptInjectionRisk Risk,
    string? Message = null)
{
    public static PromptInjectionResult Safe() =>
        new(PromptInjectionRisk.None);

    public static PromptInjectionResult Detected(PromptInjectionRisk risk, string message) =>
        new(risk, message);
}

/// <summary>
/// Risk level for prompt injection detection.
/// </summary>
public enum PromptInjectionRisk
{
    None,
    Low,
    Medium,
    High
}
