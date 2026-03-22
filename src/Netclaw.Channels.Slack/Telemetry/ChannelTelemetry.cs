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

    private static readonly Counter<long> SlackEventsFiltered =
        Meter.CreateCounter<long>("netclaw.slack.events.filtered");

    private static readonly Counter<long> SlackEventsRouted =
        Meter.CreateCounter<long>("netclaw.slack.events.routed");

    private static readonly Counter<long> SlackMessagesEnqueued =
        Meter.CreateCounter<long>("netclaw.slack.messages.enqueued");

    private static readonly Counter<long> SlackRepliesPosted =
        Meter.CreateCounter<long>("netclaw.slack.replies.posted");

    private static readonly Counter<long> SlackRepliesRejected =
        Meter.CreateCounter<long>("netclaw.slack.replies.rejected");

    private static readonly Counter<long> SlackRepliesFailed =
        Meter.CreateCounter<long>("netclaw.slack.replies.failed");

    private static readonly Histogram<double> SlackReplyDurationMs =
        Meter.CreateHistogram<double>("netclaw.slack.reply.duration.ms", unit: "ms");

    private static long _slackEventsReceivedTotal;
    private static long _slackEventsDroppedTotal;
    private static long _slackEventsFilteredTotal;
    private static long _slackEventsRoutedTotal;
    private static long _slackMessagesEnqueuedTotal;
    private static long _slackRepliesPostedTotal;
    private static long _slackRepliesRejectedTotal;
    private static long _slackRepliesFailedTotal;

    public sealed record Snapshot(
        long SlackEventsReceived,
        long SlackEventsDropped,
        long SlackEventsFiltered,
        long SlackEventsRouted,
        long SlackMessagesEnqueued,
        long SlackRepliesPosted,
        long SlackRepliesRejected,
        long SlackRepliesFailed);

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

    public static void RecordSlackEventFiltered(string reason)
    {
        Interlocked.Increment(ref _slackEventsFilteredTotal);
        SlackEventsFiltered.Add(1, new KeyValuePair<string, object?>("reason", reason));
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

    public static void RecordSlackReplyRejected(string? errorCode)
    {
        Interlocked.Increment(ref _slackRepliesRejectedTotal);
        SlackRepliesRejected.Add(1, new KeyValuePair<string, object?>("error_code", errorCode ?? "unknown"));
    }

    public static void RecordSlackReplyFailed(double durationMs)
    {
        Interlocked.Increment(ref _slackRepliesFailedTotal);
        SlackRepliesFailed.Add(1);
        SlackReplyDurationMs.Record(durationMs);
    }

    public static Snapshot GetSnapshot()
        => new(
            SlackEventsReceived: Interlocked.Read(ref _slackEventsReceivedTotal),
            SlackEventsDropped: Interlocked.Read(ref _slackEventsDroppedTotal),
            SlackEventsFiltered: Interlocked.Read(ref _slackEventsFilteredTotal),
            SlackEventsRouted: Interlocked.Read(ref _slackEventsRoutedTotal),
            SlackMessagesEnqueued: Interlocked.Read(ref _slackMessagesEnqueuedTotal),
            SlackRepliesPosted: Interlocked.Read(ref _slackRepliesPostedTotal),
            SlackRepliesRejected: Interlocked.Read(ref _slackRepliesRejectedTotal),
            SlackRepliesFailed: Interlocked.Read(ref _slackRepliesFailedTotal));

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _slackEventsReceivedTotal, 0);
        Interlocked.Exchange(ref _slackEventsDroppedTotal, 0);
        Interlocked.Exchange(ref _slackEventsFilteredTotal, 0);
        Interlocked.Exchange(ref _slackEventsRoutedTotal, 0);
        Interlocked.Exchange(ref _slackMessagesEnqueuedTotal, 0);
        Interlocked.Exchange(ref _slackRepliesPostedTotal, 0);
        Interlocked.Exchange(ref _slackRepliesRejectedTotal, 0);
        Interlocked.Exchange(ref _slackRepliesFailedTotal, 0);
    }
}
