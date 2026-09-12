// -----------------------------------------------------------------------
// <copyright file="FakeTelegramProbe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Telegram;

namespace Netclaw.Cli.Tests.Tui;

public sealed class FakeTelegramProbe : ITelegramProbe
{
    public TelegramProbeResult NextProbeResult { get; set; } = new(true, null, "netclaw_bot");
    public TelegramChatResolutionResult NextResolutionResult { get; set; } = new(true, null, [], []);
    public int ProbeCallCount { get; private set; }
    public int ResolveCallCount { get; private set; }
    public string? LastBotToken { get; private set; }
    public IReadOnlyList<string> LastChatIds { get; private set; } = [];

    public Task<TelegramProbeResult> ProbeAsync(string botToken, CancellationToken ct = default)
    {
        ProbeCallCount++;
        LastBotToken = botToken;
        return Task.FromResult(NextProbeResult);
    }

    public Task<TelegramChatResolutionResult> ResolveChatIdsAsync(string botToken, IReadOnlyList<string> chatIds, CancellationToken ct = default)
    {
        ResolveCallCount++;
        LastBotToken = botToken;
        LastChatIds = chatIds;
        return Task.FromResult(NextResolutionResult);
    }
}
