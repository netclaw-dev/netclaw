// -----------------------------------------------------------------------
// <copyright file="PromptInjectionResult.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security;

/// <summary>
/// Result of prompt injection detection analysis.
/// </summary>
public sealed record PromptInjectionResult(
    PromptInjectionRisk Risk,
    string? Message = null,
    string? Category = null)
{
    public static PromptInjectionResult Safe() =>
        new(PromptInjectionRisk.None);

    public static PromptInjectionResult Detected(PromptInjectionRisk risk, string message, string? category = null) =>
        new(risk, message, category);
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
