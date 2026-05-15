// -----------------------------------------------------------------------
// <copyright file="SafePromptInjectionDetector.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Security;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

/// <summary>
/// A test double for <see cref="IPromptInjectionDetector"/> that always returns a safe
/// (no-injection) result. Use this in test fixtures that construct
/// <see cref="Netclaw.Channels.Slack.SlackGatewayDependencies"/> or
/// <see cref="Netclaw.Channels.Discord.DiscordGatewayDependencies"/> when the test does
/// not exercise prompt-injection detection behavior.
/// </summary>
internal sealed class SafePromptInjectionDetector : IPromptInjectionDetector
{
    public static readonly SafePromptInjectionDetector Instance = new();

    public Task<PromptInjectionResult> DetectAsync(
        string text,
        string sourceContext,
        CancellationToken cancellationToken = default)
        => Task.FromResult(PromptInjectionResult.Safe());
}
