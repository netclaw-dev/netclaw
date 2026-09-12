// -----------------------------------------------------------------------
// <copyright file="FakeSlackProbe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Slack;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Test double for <see cref="ISlackProbe"/> that returns canned results
/// without making real HTTP calls.
/// </summary>
public sealed class FakeSlackProbe : ISlackProbe
{
    /// <summary>
    /// The result to return from the next <see cref="ProbeAsync"/> call.
    /// Defaults to a successful probe.
    /// </summary>
    public SlackProbeResult NextResult { get; set; } = new(
        true, null, "Test Team", new SlackUserId("U12345"));

    /// <summary>
    /// Number of times <see cref="ProbeAsync"/> has been called.
    /// </summary>
    public int ProbeCallCount { get; private set; }

    /// <summary>
    /// The bot token from the last call.
    /// </summary>
    public string? LastBotToken { get; private set; }

    /// <summary>
    /// The result to return from <see cref="ResolveChannelNamesAsync"/>.
    /// Defaults to a successful empty resolution.
    /// </summary>
    public SlackChannelResolutionResult NextResolutionResult { get; set; } = new(true, null, [], []);

    /// <summary>
    /// Number of times <see cref="ResolveChannelNamesAsync"/> has been called.
    /// </summary>
    public int ResolveCallCount { get; private set; }

    /// <summary>
    /// The channel names from the last <see cref="ResolveChannelNamesAsync"/> call.
    /// </summary>
    public IReadOnlyList<string>? LastResolvedNames { get; private set; }

    /// <summary>
    /// Optional delay before returning results. Used to test timeout behavior.
    /// </summary>
    public TimeSpan? DelayBeforeResult { get; set; }

    public Exception? ResolutionException { get; set; }

    /// <summary>
    /// When set, <see cref="ResolveChannelNamesAsync"/> answers per-request: each requested name found
    /// in this map resolves to its id, the rest come back unresolved. Lets a test exercise multi-channel
    /// (CSV) input where each reference resolves distinctly. <see cref="NextResolutionResult"/> is used
    /// when this is null.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ResolveByName { get; set; }

    /// <summary>
    /// Optional gate for deterministic concurrency tests: when set, <see cref="ResolveChannelNamesAsync"/>
    /// signals <see cref="ResolveEntered"/> (the probe is now in flight) and then awaits this task before
    /// returning — letting a test hold the background refresh open while it races loop-thread edits, then
    /// release it to publish. No <c>Thread.Sleep</c>/<c>Task.Delay</c> in the test: it is a finite handshake.
    /// </summary>
    public Task? ReleaseResolve { get; set; }

    private readonly TaskCompletionSource _resolveEntered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes when <see cref="ResolveChannelNamesAsync"/> has started and parked on <see cref="ReleaseResolve"/>.
    /// </summary>
    public Task ResolveEntered => _resolveEntered.Task;

    public async Task<SlackProbeResult> ProbeAsync(string botToken, CancellationToken ct = default)
    {
        ProbeCallCount++;
        LastBotToken = botToken;
        if (DelayBeforeResult.HasValue)
            await Task.Delay(DelayBeforeResult.Value, ct);
        return NextResult;
    }

    public async Task<SlackChannelResolutionResult> ResolveChannelNamesAsync(
        string botToken, IReadOnlyList<string> channelNames, CancellationToken ct = default)
    {
        ResolveCallCount++;
        LastBotToken = botToken;
        LastResolvedNames = channelNames;
        if (ResolutionException is not null)
            throw ResolutionException;

        if (ReleaseResolve is not null)
        {
            _resolveEntered.TrySetResult();
            await ReleaseResolve.WaitAsync(ct);
        }

        if (DelayBeforeResult.HasValue)
            await Task.Delay(DelayBeforeResult.Value, ct);

        if (ResolveByName is null)
            return NextResolutionResult;

        var resolved = new List<ResolvedSlackChannel>();
        var unresolved = new List<string>();
        foreach (var name in channelNames)
        {
            if (ResolveByName.TryGetValue(name, out var id))
                resolved.Add(new ResolvedSlackChannel(name, id));
            else
                unresolved.Add(name);
        }

        return new SlackChannelResolutionResult(unresolved.Count == 0, null, resolved, unresolved);
    }
}
