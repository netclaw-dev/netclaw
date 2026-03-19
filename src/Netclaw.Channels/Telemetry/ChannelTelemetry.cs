using System.Diagnostics.Metrics;
using System.Threading;

namespace Netclaw.Channels.Telemetry;

public static class ChannelTelemetry
{
    public const string MeterName = "Netclaw.Channels";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> SlackEventsReceived =
        Meter.CreateCounter<long>("netclaw.slack.events.received");

    private static readonly Counter<long> SlackEventsDropped =
        Meter.CreateCounter<long>("netclaw.slack.events.dropped");

    private static readonly Counter<long> SlackEventsRouted =
        Meter.CreateCounter<long>("netclaw.slack.events.routed");

    private static readonly Counter<long> SlackMessagesEnqueued =
        Meter.CreateCounter<long>("netclaw.slack.messages.enqueued");

    private static readonly Counter<long> SlackRepliesPosted =
        Meter.CreateCounter<long>("netclaw.slack.replies.posted");

    private static readonly Counter<long> SlackRepliesFailed =
        Meter.CreateCounter<long>("netclaw.slack.replies.failed");

    private static readonly Counter<long> SlackRepliesPlainTextFallback =
        Meter.CreateCounter<long>("netclaw.slack.replies.plain_text_fallback");

    private static readonly Histogram<double> SlackReplyDurationMs =
        Meter.CreateHistogram<double>("netclaw.slack.reply.duration.ms", unit: "ms");

    private static long _slackEventsReceivedTotal;
    private static long _slackEventsDroppedTotal;
    private static long _slackEventsRoutedTotal;
    private static long _slackMessagesEnqueuedTotal;
    private static long _slackRepliesPostedTotal;
    private static long _slackRepliesFailedTotal;
    private static long _slackRepliesPlainTextFallbackTotal;

    public sealed record Snapshot(
        long SlackEventsReceived,
        long SlackEventsDropped,
        long SlackEventsRouted,
        long SlackMessagesEnqueued,
        long SlackRepliesPosted,
        long SlackRepliesFailed,
        long SlackRepliesPlainTextFallback);

    public static void RecordSlackEventReceived(string kind)
    {
        Interlocked.Increment(ref _slackEventsReceivedTotal);
        SlackEventsReceived.Add(1, new KeyValuePair<string, object?>("kind", kind));
    }

    public static void RecordSlackEventDropped(string reason)
    {
        Interlocked.Increment(ref _slackEventsDroppedTotal);
        SlackEventsDropped.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    public static void RecordSlackEventRouted(string kind)
    {
        Interlocked.Increment(ref _slackEventsRoutedTotal);
        SlackEventsRouted.Add(1, new KeyValuePair<string, object?>("kind", kind));
    }

    public static void RecordSlackMessageEnqueued()
    {
        Interlocked.Increment(ref _slackMessagesEnqueuedTotal);
        SlackMessagesEnqueued.Add(1);
    }

    public static void RecordSlackReplyPosted(double durationMs)
    {
        Interlocked.Increment(ref _slackRepliesPostedTotal);
        SlackRepliesPosted.Add(1);
        SlackReplyDurationMs.Record(durationMs);
    }

    public static void RecordSlackReplyFailed(double durationMs)
    {
        Interlocked.Increment(ref _slackRepliesFailedTotal);
        SlackRepliesFailed.Add(1);
        SlackReplyDurationMs.Record(durationMs);
    }

    public static void RecordSlackReplyPlainTextFallback()
    {
        Interlocked.Increment(ref _slackRepliesPlainTextFallbackTotal);
        SlackRepliesPlainTextFallback.Add(1);
    }

    public static Snapshot GetSnapshot()
        => new(
            SlackEventsReceived: Interlocked.Read(ref _slackEventsReceivedTotal),
            SlackEventsDropped: Interlocked.Read(ref _slackEventsDroppedTotal),
            SlackEventsRouted: Interlocked.Read(ref _slackEventsRoutedTotal),
            SlackMessagesEnqueued: Interlocked.Read(ref _slackMessagesEnqueuedTotal),
            SlackRepliesPosted: Interlocked.Read(ref _slackRepliesPostedTotal),
            SlackRepliesFailed: Interlocked.Read(ref _slackRepliesFailedTotal),
            SlackRepliesPlainTextFallback: Interlocked.Read(ref _slackRepliesPlainTextFallbackTotal));

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _slackEventsReceivedTotal, 0);
        Interlocked.Exchange(ref _slackEventsDroppedTotal, 0);
        Interlocked.Exchange(ref _slackEventsRoutedTotal, 0);
        Interlocked.Exchange(ref _slackMessagesEnqueuedTotal, 0);
        Interlocked.Exchange(ref _slackRepliesPostedTotal, 0);
        Interlocked.Exchange(ref _slackRepliesFailedTotal, 0);
        Interlocked.Exchange(ref _slackRepliesPlainTextFallbackTotal, 0);
    }
}
