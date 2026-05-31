// -----------------------------------------------------------------------
// <copyright file="FakeMattermostProbe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Mattermost;

namespace Netclaw.Cli.Tests.Tui;

public sealed class FakeMattermostProbe : IMattermostProbe
{
    public MattermostProbeResult NextProbeResult { get; set; } = new(
        true, null, "testbot");

    public int ProbeCallCount { get; private set; }

    public string? LastServerUrl { get; private set; }

    public string? LastBotToken { get; private set; }

    public MattermostChannelResolutionResult NextResolutionResult { get; set; } = new(true, null, [], []);

    public int ResolveCallCount { get; private set; }

    public IReadOnlyList<string>? LastResolvedIds { get; private set; }

    public TimeSpan? DelayBeforeResult { get; set; }

    public async Task<MattermostProbeResult> ProbeAsync(string serverUrl, string botToken, CancellationToken ct = default)
    {
        ProbeCallCount++;
        LastServerUrl = serverUrl;
        LastBotToken = botToken;
        if (DelayBeforeResult.HasValue)
            await Task.Delay(DelayBeforeResult.Value, ct);
        return NextProbeResult;
    }

    public async Task<MattermostChannelResolutionResult> ResolveChannelIdsAsync(
        string serverUrl, string botToken, IReadOnlyList<string> channelIds, CancellationToken ct = default)
    {
        ResolveCallCount++;
        LastServerUrl = serverUrl;
        LastBotToken = botToken;
        LastResolvedIds = channelIds;
        if (DelayBeforeResult.HasValue)
            await Task.Delay(DelayBeforeResult.Value, ct);
        return NextResolutionResult;
    }
}
