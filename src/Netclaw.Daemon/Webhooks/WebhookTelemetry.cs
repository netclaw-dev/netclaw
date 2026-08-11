// -----------------------------------------------------------------------
// <copyright file="WebhookTelemetry.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics.Metrics;

namespace Netclaw.Daemon.Webhooks;

/// <summary>
/// Process-wide counters for inbound webhook ingress outcomes. Every accepted
/// delivery, rejection, filter, or duplicate suppression increments a durable
/// counter here so <c>netclaw stats</c> (and any OpenTelemetry exporter) can
/// surface webhook traffic without reading the daemon's HTTP logs.
///
/// Rejections MUST NOT cross over to <c>IOperationalNotificationSink</c>;
/// those trigger outbound operator notifications. This telemetry surface is
/// strictly in-process.
/// </summary>
public static class WebhookTelemetry
{
    public const string MeterName = "Netclaw.Webhooks";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Accepted =
        Meter.CreateCounter<long>("netclaw.webhooks.accepted");

    private static readonly Counter<long> RouteNotFound =
        Meter.CreateCounter<long>("netclaw.webhooks.rejected.route_not_found");

    private static readonly Counter<long> VerificationFailed =
        Meter.CreateCounter<long>("netclaw.webhooks.rejected.verification_failed");

    private static readonly Counter<long> BodyTooLarge =
        Meter.CreateCounter<long>("netclaw.webhooks.rejected.body_too_large");

    private static readonly Counter<long> InvalidJson =
        Meter.CreateCounter<long>("netclaw.webhooks.rejected.invalid_json");

    private static readonly Counter<long> RateLimited =
        Meter.CreateCounter<long>("netclaw.webhooks.rejected.rate_limited");

    private static readonly Counter<long> EventFiltered =
        Meter.CreateCounter<long>("netclaw.webhooks.filtered.event");

    private static readonly Counter<long> DuplicateDelivery =
        Meter.CreateCounter<long>("netclaw.webhooks.filtered.duplicate");

    private static long _acceptedTotal;
    private static long _routeNotFoundTotal;
    private static long _verificationFailedTotal;
    private static long _bodyTooLargeTotal;
    private static long _invalidJsonTotal;
    private static long _rateLimitedTotal;
    private static long _eventFilteredTotal;
    private static long _duplicateDeliveryTotal;

    public sealed record Snapshot(
        long Accepted,
        long RouteNotFound,
        long VerificationFailed,
        long BodyTooLarge,
        long InvalidJson,
        long RateLimited,
        long EventFiltered,
        long DuplicateDelivery);

    public static void RecordAccepted(string route)
    {
        Interlocked.Increment(ref _acceptedTotal);
        Accepted.Add(1, new KeyValuePair<string, object?>("route", route));
    }

    public static void RecordRouteNotFound(string route)
    {
        Interlocked.Increment(ref _routeNotFoundTotal);
        RouteNotFound.Add(1, new KeyValuePair<string, object?>("route", route));
    }

    public static void RecordVerificationFailed(string route)
    {
        Interlocked.Increment(ref _verificationFailedTotal);
        VerificationFailed.Add(1, new KeyValuePair<string, object?>("route", route));
    }

    public static void RecordBodyTooLarge(string route)
    {
        Interlocked.Increment(ref _bodyTooLargeTotal);
        BodyTooLarge.Add(1, new KeyValuePair<string, object?>("route", route));
    }

    public static void RecordInvalidJson(string route)
    {
        Interlocked.Increment(ref _invalidJsonTotal);
        InvalidJson.Add(1, new KeyValuePair<string, object?>("route", route));
    }

    public static void RecordRateLimited(string route)
    {
        Interlocked.Increment(ref _rateLimitedTotal);
        RateLimited.Add(1, new KeyValuePair<string, object?>("route", route));
    }

    public static void RecordEventFiltered(string route)
    {
        Interlocked.Increment(ref _eventFilteredTotal);
        EventFiltered.Add(1, new KeyValuePair<string, object?>("route", route));
    }

    public static void RecordDuplicateDelivery(string route)
    {
        Interlocked.Increment(ref _duplicateDeliveryTotal);
        DuplicateDelivery.Add(1, new KeyValuePair<string, object?>("route", route));
    }

    public static Snapshot GetSnapshot()
        => new(
            Accepted: Interlocked.Read(ref _acceptedTotal),
            RouteNotFound: Interlocked.Read(ref _routeNotFoundTotal),
            VerificationFailed: Interlocked.Read(ref _verificationFailedTotal),
            BodyTooLarge: Interlocked.Read(ref _bodyTooLargeTotal),
            InvalidJson: Interlocked.Read(ref _invalidJsonTotal),
            RateLimited: Interlocked.Read(ref _rateLimitedTotal),
            EventFiltered: Interlocked.Read(ref _eventFilteredTotal),
            DuplicateDelivery: Interlocked.Read(ref _duplicateDeliveryTotal));

    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _acceptedTotal, 0);
        Interlocked.Exchange(ref _routeNotFoundTotal, 0);
        Interlocked.Exchange(ref _verificationFailedTotal, 0);
        Interlocked.Exchange(ref _bodyTooLargeTotal, 0);
        Interlocked.Exchange(ref _invalidJsonTotal, 0);
        Interlocked.Exchange(ref _rateLimitedTotal, 0);
        Interlocked.Exchange(ref _eventFilteredTotal, 0);
        Interlocked.Exchange(ref _duplicateDeliveryTotal, 0);
    }
}
