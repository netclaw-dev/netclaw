// -----------------------------------------------------------------------
// <copyright file="IPromptInjectionDetector.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security;

/// <summary>
/// Detects prompt injection attempts in text content.
/// Stub interface for future webhook scenarios (e.g., public GitHub repos).
/// </summary>
public interface IPromptInjectionDetector
{
    Task<PromptInjectionResult> DetectAsync(
        string text,
        string sourceContext,
        CancellationToken cancellationToken = default);
}
