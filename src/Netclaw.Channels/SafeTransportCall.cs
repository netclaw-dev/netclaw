// -----------------------------------------------------------------------
// <copyright file="SafeTransportCall.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Channels.Telemetry;

namespace Netclaw.Channels;

public abstract class TransportResult
{
    private TransportResult() { }
    public sealed class Success : TransportResult { }
    public sealed class Failure(DeliveryFailureKind kind, string message) : TransportResult
    {
        public DeliveryFailureKind Kind { get; } = kind;
        public string Message { get; } = message;
    }
}

public sealed class SafeTransportCall
{
    private readonly ChannelType _channelType;
    private readonly TimeProvider _timeProvider;

    /// <param name="channelType">Telemetry category for the channel.</param>
    /// <param name="timeProvider">Clock for the call duration.</param>
    public SafeTransportCall(
        ChannelType channelType,
        TimeProvider timeProvider)
    {
        _channelType = channelType;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Runs one transport call and returns its result.
    /// </summary>
    /// <param name="callAsync">The transport call.</param>
    /// <param name="logFailure">
    /// Writes the channel's own failure log line. Each channel keeps its
    /// wording and its level, so this callback owns the log call.
    /// </param>
    public async Task<TransportResult> InvokeAsync(Func<Task> callAsync, Action<Exception> logFailure)
    {
        var startedAt = _timeProvider.GetTimestamp();
        try
        {
            await callAsync();
            ChannelTelemetry.For(_channelType).RecordReplyPosted(ElapsedMs(startedAt));
            return new TransportResult.Success();
        }
        catch (Exception ex)
        {
            var duration = ElapsedMs(startedAt);
            logFailure(ex);
            ChannelTelemetry.For(_channelType).RecordReplyFailed(duration);
            return new TransportResult.Failure(DeliveryFailureKind.TransportFailure, ex.Message);
        }
    }

    /// <summary>
    /// Splits <paramref name="text"/> into transport-sized chunks and posts
    /// them in order and stops at the first failed chunk.
    /// </summary>
    /// <param name="text">The text to post.</param>
    /// <param name="maxChunkLength">The transport's message length ceiling.</param>
    /// <param name="postChunkAsync">Posts one chunk.</param>
    /// <param name="logFailure">Writes the channel's own failure log line.</param>
    public async Task<TransportResult> PostChunkedAsync(
        string text,
        int maxChunkLength,
        Func<string, Task> postChunkAsync,
        Action<Exception> logFailure)
    {
        foreach (var chunk in MessageChunker.Chunk(text, maxChunkLength))
        {
            var result = await InvokeAsync(() => postChunkAsync(chunk), logFailure);
            if (result is TransportResult.Failure)
                return result;
        }

        return new TransportResult.Success();
    }

    private double ElapsedMs(long startedAt)
        => _timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;
}
