// -----------------------------------------------------------------------
// <copyright file="IThreadHistoryFetcher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Channel-agnostic contract for fetching thread history before the first LLM turn.
/// Each channel adapter that supports threaded conversations implements this interface.
/// </summary>
public interface IThreadHistoryFetcher
{
    /// <summary>
    /// Fetches all prior messages in the thread identified by <paramref name="sessionId"/>.
    /// Returns messages in chronological order.
    /// Returns an empty list if the thread has no prior messages or if the fetch fails.
    /// </summary>
    Task<IReadOnlyList<ChannelInput>> FetchThreadHistoryAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);
}
