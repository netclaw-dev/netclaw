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

    private static readonly Counter<long> DiscordEventsReceived =
        Meter.CreateCounter<long>("netclaw.discord.events.received");

    private static readonly Counter<long> DiscordEventsDropped =
        Meter.CreateCounter<long>("netclaw.discord.events.dropped");

    private static readonly Counter<long> DiscordEventsFiltered =
        Meter.CreateCounter<long>("netclaw.discord.events.filtered");

    private static readonly Counter<long> DiscordEventsRouted =
        Meter.CreateCounter<long>("netclaw.discord.events.routed");

    private static readonly Counter<long> DiscordMessagesEnqueued =
        Meter.CreateCounter<long>("netclaw.discord.messages.enqueued");

    private static readonly Counter<long> DiscordRepliesPosted =
        Meter.CreateCounter<long>("netclaw.discord.replies.posted");

    private static readonly Counter<long> DiscordRepliesRejected =
        Meter.CreateCounter<long>("netclaw.discord.replies.rejected");

    private static readonly Counter<long> DiscordRepliesFailed =
        Meter.CreateCounter<long>("netclaw.discord.replies.failed");

    private static readonly Counter<long> DiscordInteractionErrors =
        Meter.CreateCounter<long>("netclaw.discord.interactions.errors");

    private static readonly Counter<long> DiscordApprovalFallbackActivated =
        Meter.CreateCounter<long>("netclaw.discord.approval.fallback_activated");

    private static readonly Histogram<double> DiscordReplyDurationMs =
        Meter.CreateHistogram<double>("netclaw.discord.reply.duration.ms", unit: "ms");

    private static long _slackEventsReceivedTotal;
    private static long _slackEventsDroppedTotal;
    private static long _slackEventsFilteredTotal;
    private static long _slackEventsRoutedTotal;
    private static long _slackMessagesEnqueuedTotal;
    private static long _slackRepliesPostedTotal;
    private static long _slackRepliesRejectedTotal;
    private static long _slackRepliesFailedTotal;
    private static long _discordEventsReceivedTotal;
    private static long _discordEventsDroppedTotal;
    private static long _discordEventsFilteredTotal;
    private static long _discordEventsRoutedTotal;
    private static long _discordMessagesEnqueuedTotal;
    private static long _discordRepliesPostedTotal;
    private static long _discordRepliesRejectedTotal;
    private static long _discordRepliesFailedTotal;
    private static long _discordInteractionErrorsTotal;
    private static long _discordApprovalFallbackActivatedTotal;

    public sealed record Snapshot(
        long SlackEventsReceived,
        long SlackEventsDropped,
        long SlackEventsFiltered,
        long SlackEventsRouted,
        long SlackMessagesEnqueued,
        long SlackRepliesPosted,
        long SlackRepliesRejected,
        long SlackRepliesFailed,
        long DiscordEventsReceived,
        long DiscordEventsDropped,
        long DiscordEventsFiltered,
        long DiscordEventsRouted,
        long DiscordMessagesEnqueued,
        long DiscordRepliesPosted,
        long DiscordRepliesRejected,
        long DiscordRepliesFailed,
        long DiscordInteractionErrors,
        long DiscordApprovalFallbackActivated);

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

    public static void RecordDiscordEventReceived(string kind)
    {
        Interlocked.Increment(ref _discordEventsReceivedTotal);
        DiscordEventsReceived.Add(1, new KeyValuePair<string, object?>("kind", kind));
    }

    public static void RecordDiscordEventDropped(string reason)
    {
        Interlocked.Increment(ref _discordEventsDroppedTotal);
        DiscordEventsDropped.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    public static void RecordDiscordEventFiltered(string reason)
    {
        Interlocked.Increment(ref _discordEventsFilteredTotal);
        DiscordEventsFiltered.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    public static void RecordDiscordEventRouted(string kind)
    {
        Interlocked.Increment(ref _discordEventsRoutedTotal);
        DiscordEventsRouted.Add(1, new KeyValuePair<string, object?>("kind", kind));
    }

    public static void RecordDiscordMessageEnqueued()
    {
        Interlocked.Increment(ref _discordMessagesEnqueuedTotal);
        DiscordMessagesEnqueued.Add(1);
    }

    public static void RecordDiscordReplyPosted(double durationMs)
    {
        Interlocked.Increment(ref _discordRepliesPostedTotal);
        DiscordRepliesPosted.Add(1);
        DiscordReplyDurationMs.Record(durationMs);
    }

    public static void RecordDiscordReplyRejected(string? errorCode)
    {
        Interlocked.Increment(ref _discordRepliesRejectedTotal);
        DiscordRepliesRejected.Add(1, new KeyValuePair<string, object?>("error_code", errorCode ?? "unknown"));
    }

    public static void RecordDiscordReplyFailed(double durationMs)
    {
        Interlocked.Increment(ref _discordRepliesFailedTotal);
        DiscordRepliesFailed.Add(1);
        DiscordReplyDurationMs.Record(durationMs);
    }

    public static void RecordDiscordInteractionError(string reason)
    {
        Interlocked.Increment(ref _discordInteractionErrorsTotal);
        DiscordInteractionErrors.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    public static void RecordDiscordApprovalFallbackActivated(string reason)
    {
        Interlocked.Increment(ref _discordApprovalFallbackActivatedTotal);
        DiscordApprovalFallbackActivated.Add(1, new KeyValuePair<string, object?>("reason", reason));
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
            SlackRepliesFailed: Interlocked.Read(ref _slackRepliesFailedTotal),
            DiscordEventsReceived: Interlocked.Read(ref _discordEventsReceivedTotal),
            DiscordEventsDropped: Interlocked.Read(ref _discordEventsDroppedTotal),
            DiscordEventsFiltered: Interlocked.Read(ref _discordEventsFilteredTotal),
            DiscordEventsRouted: Interlocked.Read(ref _discordEventsRoutedTotal),
            DiscordMessagesEnqueued: Interlocked.Read(ref _discordMessagesEnqueuedTotal),
            DiscordRepliesPosted: Interlocked.Read(ref _discordRepliesPostedTotal),
            DiscordRepliesRejected: Interlocked.Read(ref _discordRepliesRejectedTotal),
            DiscordRepliesFailed: Interlocked.Read(ref _discordRepliesFailedTotal),
            DiscordInteractionErrors: Interlocked.Read(ref _discordInteractionErrorsTotal),
            DiscordApprovalFallbackActivated: Interlocked.Read(ref _discordApprovalFallbackActivatedTotal));

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
        Interlocked.Exchange(ref _discordEventsReceivedTotal, 0);
        Interlocked.Exchange(ref _discordEventsDroppedTotal, 0);
        Interlocked.Exchange(ref _discordEventsFilteredTotal, 0);
        Interlocked.Exchange(ref _discordEventsRoutedTotal, 0);
        Interlocked.Exchange(ref _discordMessagesEnqueuedTotal, 0);
        Interlocked.Exchange(ref _discordRepliesPostedTotal, 0);
        Interlocked.Exchange(ref _discordRepliesRejectedTotal, 0);
        Interlocked.Exchange(ref _discordRepliesFailedTotal, 0);
        Interlocked.Exchange(ref _discordInteractionErrorsTotal, 0);
        Interlocked.Exchange(ref _discordApprovalFallbackActivatedTotal, 0);
    }
}
