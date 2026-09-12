// -----------------------------------------------------------------------
// <copyright file="SafeTransportCall.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telemetry;

namespace Netclaw.Channels;

/// <summary>
/// Wraps one outbound transport call in the delivery bookkeeping every channel
/// binding actor applies to it: measure the call, record reply telemetry for
/// the channel, and on failure log, record the failure, and tell the session
/// that delivery did not happen.
/// </summary>
/// <remarks>
/// The delivery-failure notifier is required and its exceptions propagate. A
/// dead feedback pipe means the session never learns the turn failed, so the
/// error must reach the actor and let supervision restart it — see PR #2004.
/// This type therefore never wraps the notifier in a catch of its own.
/// </remarks>
public sealed class SafeTransportCall
{
    private readonly ChannelType _channelType;
    private readonly TimeProvider _timeProvider;
    private readonly Func<DeliveryFailureKind, string, Task> _notifyDeliveryFailedAsync;

    /// <param name="channelType">Telemetry category for the channel.</param>
    /// <param name="timeProvider">Clock for the call duration.</param>
    /// <param name="notifyDeliveryFailedAsync">Tells the session that delivery failed.</param>
    public SafeTransportCall(
        ChannelType channelType,
        TimeProvider timeProvider,
        Func<DeliveryFailureKind, string, Task> notifyDeliveryFailedAsync)
    {
        _channelType = channelType;
        _timeProvider = timeProvider;
        _notifyDeliveryFailedAsync = notifyDeliveryFailedAsync;
    }

    /// <summary>
    /// Runs one transport call. Returns <c>true</c> on success; on failure
    /// returns <c>false</c> after the session has been told.
    /// </summary>
    /// <param name="callAsync">The transport call.</param>
    /// <param name="logFailure">
    /// Writes the channel's own failure log line. Each channel keeps its
    /// wording and its level, so this callback owns the log call.
    /// </param>
    public async Task<bool> InvokeAsync(Func<Task> callAsync, Action<Exception> logFailure)
    {
        var startedAt = _timeProvider.GetTimestamp();
        try
        {
            await callAsync();
            ChannelTelemetry.For(_channelType).RecordReplyPosted(ElapsedMs(startedAt));
            return true;
        }
        catch (Exception ex)
        {
            var duration = ElapsedMs(startedAt);
            logFailure(ex);
            ChannelTelemetry.For(_channelType).RecordReplyFailed(duration);
            await _notifyDeliveryFailedAsync(DeliveryFailureKind.TransportFailure, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Splits <paramref name="text"/> into transport-sized chunks and posts
    /// them in order. Returns <c>true</c> only when every chunk posted; stops at
    /// the first failed chunk.
    /// </summary>
    /// <param name="text">The text to post.</param>
    /// <param name="maxChunkLength">The transport's message length ceiling.</param>
    /// <param name="postChunkAsync">Posts one chunk.</param>
    /// <param name="logFailure">Writes the channel's own failure log line.</param>
    public async Task<bool> PostChunkedAsync(
        string text,
        int maxChunkLength,
        Func<string, Task> postChunkAsync,
        Action<Exception> logFailure)
    {
        foreach (var chunk in MessageChunker.Chunk(text, maxChunkLength))
        {
            if (!await InvokeAsync(() => postChunkAsync(chunk), logFailure))
                return false;
        }

        return true;
    }

    private double ElapsedMs(long startedAt)
        => _timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
}
