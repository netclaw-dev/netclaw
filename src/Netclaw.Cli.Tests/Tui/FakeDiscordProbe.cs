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

    // When null (default), ResolveChannelIdsAsync echoes every input as a resolved channel (mimics
    // "all valid ids/names resolve"). Set it to stage a specific resolved/unresolved outcome.
    public DiscordChannelResolutionResult? NextResolutionResult { get; set; }

    public int ResolveCallCount { get; private set; }

    public IReadOnlyList<string>? LastResolvedIds { get; private set; }

    public TimeSpan? DelayBeforeResult { get; set; }

    /// <summary>
    /// Optional gate. When set, <see cref="ResolveChannelIdsAsync"/> blocks (observing the
    /// cancellation token) until the gate is completed — used to stage an in-flight channel-name
    /// prefetch for race/cancellation tests. Null (default) returns immediately.
    /// </summary>
    public TaskCompletionSource? ResolveGate { get; set; }

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
        LastBotToken = botToken;
        LastResolvedIds = channelIds;
        if (ResolveGate is not null)
            await ResolveGate.Task.WaitAsync(ct);
        if (DelayBeforeResult.HasValue)
            await Task.Delay(DelayBeforeResult.Value, ct);
        return NextResolutionResult ?? new DiscordChannelResolutionResult(
            true,
            null,
            [.. channelIds.Select(id => new ResolvedDiscordChannel(id, id, "Test Guild"))],
            []);
    }
}
