// -----------------------------------------------------------------------
// <copyright file="EmptyThreadHistoryFetcher.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Test-only <see cref="IThreadHistoryFetcher"/> that returns no prior messages.
/// Use in Slack gateway tests that don't exercise thread hydration — passing
/// this is an explicit "this thread has no history" signal, not a silent no-op.
/// </summary>
internal sealed class EmptyThreadHistoryFetcher : IThreadHistoryFetcher
{
    public static readonly EmptyThreadHistoryFetcher Instance = new();

    public Task<IReadOnlyList<ChannelInput>> FetchThreadHistoryAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ChannelInput>>(Array.Empty<ChannelInput>());
}
