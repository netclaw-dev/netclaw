using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Tests.Channels.TestHelpers;

internal sealed class RecordingThreadHistoryFetcher : IThreadHistoryFetcher
{
    private int _fetchCount;
    private IReadOnlyList<ChannelInput> _history = Array.Empty<ChannelInput>();
    private Exception? _throwOnFetch;

    public int FetchCount => Volatile.Read(ref _fetchCount);

    public void ResetFetchCount() => Interlocked.Exchange(ref _fetchCount, 0);

    public void SetHistory(IReadOnlyList<ChannelInput> history) => _history = history;

    public void SetThrowOnFetch(Exception ex) => _throwOnFetch = ex;

    public Task<IReadOnlyList<ChannelInput>> FetchThreadHistoryAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _fetchCount);
        if (_throwOnFetch is not null)
            throw _throwOnFetch;
        return Task.FromResult(_history);
    }
}
