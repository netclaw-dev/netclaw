// -----------------------------------------------------------------------
// <copyright file="ChannelTelemetry.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading;
using Netclaw.Actors.Channels;
using Netclaw.Configuration;

namespace Netclaw.Channels.Telemetry;

/// <summary>
/// Per-channel metrics instance. Each channel type gets its own counters
/// via <see cref="ChannelTelemetry.For"/>. Standard counters are shared
/// across all channel types; channel-specific counters use <see cref="RecordExtra"/>.
/// </summary>
public sealed class ChannelMetrics
{
    private static readonly Meter Meter = new(ChannelTelemetry.MeterName);

    private readonly Counter<long> _eventsReceived;
    private readonly Counter<long> _eventsDropped;
    private readonly Counter<long> _eventsFiltered;
    private readonly Counter<long> _eventsRouted;
    private readonly Counter<long> _messagesEnqueued;
    private readonly Counter<long> _repliesPosted;
    private readonly Counter<long> _repliesRejected;
    private readonly Counter<long> _repliesFailed;
    private readonly Histogram<double> _replyDurationMs;

    private long _eventsReceivedTotal;
    private long _eventsDroppedTotal;
    private long _eventsFilteredTotal;
    private long _eventsRoutedTotal;
    private long _messagesEnqueuedTotal;
    private long _repliesPostedTotal;
    private long _repliesRejectedTotal;
    private long _repliesFailedTotal;

    private readonly ConcurrentDictionary<string, long> _extras = new(StringComparer.Ordinal);

    internal ChannelMetrics(ChannelType channelType)
    {
        ChannelType = channelType;
        DisplayName = channelType.ToString();

        var wireValue = channelType.ToWireValue();
        var prefix = $"netclaw.channel.{wireValue}";
        _eventsReceived = Meter.CreateCounter<long>($"{prefix}.events.received");
        _eventsDropped = Meter.CreateCounter<long>($"{prefix}.events.dropped");
        _eventsFiltered = Meter.CreateCounter<long>($"{prefix}.events.filtered");
        _eventsRouted = Meter.CreateCounter<long>($"{prefix}.events.routed");
        _messagesEnqueued = Meter.CreateCounter<long>($"{prefix}.messages.enqueued");
        _repliesPosted = Meter.CreateCounter<long>($"{prefix}.replies.posted");
        _repliesRejected = Meter.CreateCounter<long>($"{prefix}.replies.rejected");
        _repliesFailed = Meter.CreateCounter<long>($"{prefix}.replies.failed");
        _replyDurationMs = Meter.CreateHistogram<double>($"{prefix}.reply.duration.ms", unit: "ms");
    }

    public ChannelType ChannelType { get; }

    public string DisplayName { get; }

    public void RecordEventReceived(string kind)
    {
        Interlocked.Increment(ref _eventsReceivedTotal);
        _eventsReceived.Add(1, new KeyValuePair<string, object?>("kind", kind));
    }

    public void RecordEventDropped(string reason)
    {
        Interlocked.Increment(ref _eventsDroppedTotal);
        _eventsDropped.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    public void RecordEventFiltered(string reason)
    {
        Interlocked.Increment(ref _eventsFilteredTotal);
        _eventsFiltered.Add(1, new KeyValuePair<string, object?>("reason", reason));
    }

    public void RecordEventRouted(string kind)
    {
        Interlocked.Increment(ref _eventsRoutedTotal);
        _eventsRouted.Add(1, new KeyValuePair<string, object?>("kind", kind));
    }

    public void RecordMessageEnqueued()
    {
        Interlocked.Increment(ref _messagesEnqueuedTotal);
        _messagesEnqueued.Add(1);
    }

    public void RecordReplyPosted(double durationMs)
    {
        Interlocked.Increment(ref _repliesPostedTotal);
        _repliesPosted.Add(1);
        _replyDurationMs.Record(durationMs);
    }

    public void RecordReplyRejected(string? errorCode)
    {
        Interlocked.Increment(ref _repliesRejectedTotal);
        _repliesRejected.Add(1, new KeyValuePair<string, object?>("error_code", errorCode ?? "unknown"));
    }

    public void RecordReplyFailed(double durationMs)
    {
        Interlocked.Increment(ref _repliesFailedTotal);
        _repliesFailed.Add(1);
        _replyDurationMs.Record(durationMs);
    }

    /// <summary>
    /// Record a channel-specific counter that doesn't exist on the standard base.
    /// When <paramref name="tag"/> is provided, the key becomes "metricName:tag".
    /// </summary>
    public void RecordExtra(string metricName, string? tag = null)
    {
        var key = tag is null ? metricName : $"{metricName}:{tag}";
        _extras.AddOrUpdate(key, 1, (_, existing) => existing + 1);
    }

    private static readonly IReadOnlyDictionary<string, long> EmptyExtras =
        new Dictionary<string, long>();

    public ChannelMetricsSnapshot GetSnapshot()
        => new(
            ChannelType: ChannelType,
            DisplayName: DisplayName,
            EventsReceived: Interlocked.Read(ref _eventsReceivedTotal),
            EventsDropped: Interlocked.Read(ref _eventsDroppedTotal),
            EventsFiltered: Interlocked.Read(ref _eventsFilteredTotal),
            EventsRouted: Interlocked.Read(ref _eventsRoutedTotal),
            MessagesEnqueued: Interlocked.Read(ref _messagesEnqueuedTotal),
            RepliesPosted: Interlocked.Read(ref _repliesPostedTotal),
            RepliesRejected: Interlocked.Read(ref _repliesRejectedTotal),
            RepliesFailed: Interlocked.Read(ref _repliesFailedTotal),
            Extras: _extras.IsEmpty
                ? EmptyExtras
                : new Dictionary<string, long>(_extras));

    internal void Reset()
    {
        Interlocked.Exchange(ref _eventsReceivedTotal, 0);
        Interlocked.Exchange(ref _eventsDroppedTotal, 0);
        Interlocked.Exchange(ref _eventsFilteredTotal, 0);
        Interlocked.Exchange(ref _eventsRoutedTotal, 0);
        Interlocked.Exchange(ref _messagesEnqueuedTotal, 0);
        Interlocked.Exchange(ref _repliesPostedTotal, 0);
        Interlocked.Exchange(ref _repliesRejectedTotal, 0);
        Interlocked.Exchange(ref _repliesFailedTotal, 0);
        _extras.Clear();
    }
}

public sealed record ChannelMetricsSnapshot(
    ChannelType ChannelType,
    string DisplayName,
    long EventsReceived,
    long EventsDropped,
    long EventsFiltered,
    long EventsRouted,
    long MessagesEnqueued,
    long RepliesPosted,
    long RepliesRejected,
    long RepliesFailed,
    IReadOnlyDictionary<string, long> Extras)
{
    public DaemonStats.ChannelActivity ToWireActivity() => new()
    {
        ChannelType = ChannelType.ToWireValue(),
        DisplayName = DisplayName,
        EventsReceived = EventsReceived,
        EventsRouted = EventsRouted,
        EventsDropped = EventsDropped,
        RepliesPosted = RepliesPosted,
        RepliesRejected = RepliesRejected,
        RepliesFailed = RepliesFailed,
        Extras = Extras.Count > 0 ? new Dictionary<string, long>(Extras) : null
    };
}

/// <summary>
/// Static registry of per-channel metrics instances. Call <see cref="For"/>
/// to get a channel's <see cref="ChannelMetrics"/> — instances are created
/// on first access and cached for the process lifetime.
/// </summary>
public static class ChannelTelemetry
{
    public const string MeterName = "Netclaw.Channels";

    private static readonly ConcurrentDictionary<ChannelType, ChannelMetrics> Registry = new();

    /// <summary>
    /// Get the metrics instance for a channel type. Creates on first call.
    /// </summary>
    public static ChannelMetrics For(ChannelType channelType)
        => Registry.GetOrAdd(channelType, static ct => new ChannelMetrics(ct));

    /// <summary>
    /// Returns snapshots for all channel types that have been accessed.
    /// </summary>
    public static IReadOnlyList<ChannelMetricsSnapshot> GetAllSnapshots()
        => Registry.Values.Select(m => m.GetSnapshot()).ToList();

    internal static void ResetForTests()
    {
        foreach (var metrics in Registry.Values)
            metrics.Reset();
    }
}
