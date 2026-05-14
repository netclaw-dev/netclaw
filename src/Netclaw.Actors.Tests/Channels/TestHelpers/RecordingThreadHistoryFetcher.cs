// -----------------------------------------------------------------------
// <copyright file="RecordingThreadHistoryFetcher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

internal sealed class RecordingThreadHistoryFetcher : IThreadHistoryFetcher
{
    private int _fetchCount;
    private IReadOnlyList<ChannelInput> _history = Array.Empty<ChannelInput>();
    private Exception? _throwOnFetch;
    private TaskCompletionSource? _gate;
    private readonly TaskCompletionSource _fetchCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int FetchCount => Volatile.Read(ref _fetchCount);

    /// <summary>Resolves once <see cref="FetchThreadHistoryAsync"/> is first called.</summary>
    public Task FetchCalledTask => _fetchCalled.Task;

    public void ResetFetchCount() => Interlocked.Exchange(ref _fetchCount, 0);

    public void SetHistory(IReadOnlyList<ChannelInput> history) => _history = history;

    public void SetThrowOnFetch(Exception ex) => _throwOnFetch = ex;

    /// <summary>
    /// Installs a TCS gate that blocks <see cref="FetchThreadHistoryAsync"/>
    /// until <see cref="ReleaseGate"/> is called. Used by stash tests to hold
    /// the actor in the Hydrating behavior while inbound messages arrive.
    /// </summary>
    public void InstallGate() =>
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseGate() => _gate?.TrySetResult();

    public async Task<IReadOnlyList<ChannelInput>> FetchThreadHistoryAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _fetchCount);
        _fetchCalled.TrySetResult();
        if (_gate is { } gate)
            await gate.Task.WaitAsync(cancellationToken);
        if (_throwOnFetch is not null)
            throw _throwOnFetch;
        return _history;
    }
}
