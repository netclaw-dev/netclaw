// -----------------------------------------------------------------------
// <copyright file="ConfigurablePromptInjectionDetector.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

public sealed class ConfigurablePromptInjectionDetector : IPromptInjectionDetector
{
    private readonly Func<string, string, CancellationToken, Task<PromptInjectionResult>> _behavior;

    public ConfigurablePromptInjectionDetector(PromptInjectionResult result)
    {
        _behavior = (_, _, _) => Task.FromResult(result);
    }

    public ConfigurablePromptInjectionDetector(Exception exception)
    {
        _behavior = (_, _, _) => throw exception;
    }

    public ConfigurablePromptInjectionDetector(
        Func<string, string, CancellationToken, Task<PromptInjectionResult>> behavior)
    {
        _behavior = behavior;
    }

    public Task<PromptInjectionResult> DetectAsync(
        string text,
        string sourceContext,
        CancellationToken cancellationToken = default)
        => _behavior(text, sourceContext, cancellationToken);
}
