// -----------------------------------------------------------------------
// <copyright file="NullPromptInjectionDetector.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Security;

/// <summary>
/// No-op prompt injection detector that reports all text as safe.
/// Used as the default until a real detector is implemented.
/// </summary>
public sealed class NullPromptInjectionDetector : IPromptInjectionDetector
{
    public Task<PromptInjectionResult> DetectAsync(
        string text,
        string sourceContext,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(PromptInjectionResult.Safe());
    }
}
