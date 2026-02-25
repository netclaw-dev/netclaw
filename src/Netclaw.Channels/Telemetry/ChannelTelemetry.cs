using System.Diagnostics.Metrics;

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

    private static readonly Histogram<double> SlackReplyDurationMs =
        Meter.CreateHistogram<double>("netclaw.slack.reply.duration.ms", unit: "ms");

    public static void RecordSlackEventReceived(string kind)
        => SlackEventsReceived.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public static void RecordSlackEventDropped(string reason)
        => SlackEventsDropped.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public static void RecordSlackEventRouted(string kind)
        => SlackEventsRouted.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public static void RecordSlackMessageEnqueued()
        => SlackMessagesEnqueued.Add(1);

    public static void RecordSlackReplyPosted(double durationMs)
    {
        SlackRepliesPosted.Add(1);
        SlackReplyDurationMs.Record(durationMs);
    }

    public static void RecordSlackReplyFailed(double durationMs)
    {
        SlackRepliesFailed.Add(1);
        SlackReplyDurationMs.Record(durationMs);
    }
}
