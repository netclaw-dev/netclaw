// -----------------------------------------------------------------------
// <copyright file="FakeDiscordProbe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Discord;

namespace Netclaw.Cli.Tests.Tui;

public sealed class FakeDiscordProbe : IDiscordProbe
{
    public DiscordProbeResult NextProbeResult { get; set; } = new(
        true, null, "TestBot");

    public int ProbeCallCount { get; private set; }

    public string? LastBotToken { get; private set; }

    public DiscordChannelResolutionResult NextResolutionResult { get; set; } = new(true, null, [], []);

    public int ResolveCallCount { get; private set; }

    public IReadOnlyList<string>? LastResolvedIds { get; private set; }

    public TimeSpan? DelayBeforeResult { get; set; }

    public async Task<DiscordProbeResult> ProbeAsync(string botToken, CancellationToken ct = default)
    {
        ProbeCallCount++;
        LastBotToken = botToken;
        if (DelayBeforeResult.HasValue)
            await Task.Delay(DelayBeforeResult.Value, ct);
        return NextProbeResult;
    }

    public async Task<DiscordChannelResolutionResult> ResolveChannelIdsAsync(
        string botToken, IReadOnlyList<string> channelIds, CancellationToken ct = default)
    {
        ResolveCallCount++;
        LastResolvedIds = channelIds;
        if (DelayBeforeResult.HasValue)
            await Task.Delay(DelayBeforeResult.Value, ct);
        return NextResolutionResult;
    }
}
