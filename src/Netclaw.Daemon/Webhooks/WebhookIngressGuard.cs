// -----------------------------------------------------------------------
// <copyright file="WebhookIngressGuard.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Netclaw.Daemon.Webhooks;

public sealed class WebhookIngressGuard
{
    internal static readonly TimeSpan RateLimitWindow = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan DeliveryDedupWindow = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, RouteIngressState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WebhookIngressGuard> _logger;

    public WebhookIngressGuard(TimeProvider timeProvider, ILogger<WebhookIngressGuard> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public WebhookIngressDecision CheckAndRecord(string routeName, string? deliveryId, int rateLimitPerMinute)
    {
        var now = _timeProvider.GetUtcNow();
        var state = _states.GetOrAdd(routeName, _ => new RouteIngressState());

        lock (state.Sync)
        {
            while (state.AcceptedAt.Count > 0 && now - state.AcceptedAt.Peek() >= RateLimitWindow)
                state.AcceptedAt.Dequeue();

            foreach (var stale in state.DeliveryIds
                         .Where(kvp => now - kvp.Value >= DeliveryDedupWindow)
                         .Select(kvp => kvp.Key)
                         .ToList())
            {
                state.DeliveryIds.Remove(stale);
            }

            if (!string.IsNullOrWhiteSpace(deliveryId) && state.DeliveryIds.ContainsKey(deliveryId))
            {
                _logger.LogDebug("Suppressed duplicate webhook delivery route={Route} deliveryId={DeliveryId}", routeName, deliveryId);
                return WebhookIngressDecision.Duplicate;
            }

            if (state.AcceptedAt.Count >= rateLimitPerMinute)
            {
                var retryAfter = (int)Math.Ceiling((RateLimitWindow - (now - state.AcceptedAt.Peek())).TotalSeconds);
                return new WebhookIngressDecision(WebhookIngressDecisionKind.RateLimited, retryAfter);
            }

            state.AcceptedAt.Enqueue(now);
            if (!string.IsNullOrWhiteSpace(deliveryId))
                state.DeliveryIds[deliveryId] = now;

            return WebhookIngressDecision.Accepted;
        }
    }

    private sealed class RouteIngressState
    {
        public object Sync { get; } = new();
        public Queue<DateTimeOffset> AcceptedAt { get; } = new();
        public Dictionary<string, DateTimeOffset> DeliveryIds { get; } = new(StringComparer.Ordinal);
    }
}

public enum WebhookIngressDecisionKind
{
    Accepted,
    Duplicate,
    RateLimited
}

public sealed record WebhookIngressDecision(WebhookIngressDecisionKind Kind, int? RetryAfterSeconds = null)
{
    public static readonly WebhookIngressDecision Accepted = new(WebhookIngressDecisionKind.Accepted);
    public static readonly WebhookIngressDecision Duplicate = new(WebhookIngressDecisionKind.Duplicate);
}
