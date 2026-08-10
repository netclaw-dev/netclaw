// -----------------------------------------------------------------------
// <copyright file="WebhookNotificationService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Channels.Slack.Webhooks;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Background service that receives operational alerts via <see cref="IOperationalNotificationSink"/>
/// and delivers them as HTTP POST requests to configured webhook targets.
///
/// <para>
/// Design constraints:
/// <list type="bullet">
/// <item><see cref="Emit"/> is synchronous and non-blocking — uses a bounded channel internally.</item>
/// <item>No actor system dependency — plain DI singleton + BackgroundService.</item>
/// <item>Deduplicates alerts within <see cref="NotificationsConfig.DeduplicationWindowSeconds"/>.</item>
/// <item>Retries failed deliveries with exponential backoff.</item>
/// <item>Never crashes the daemon — all delivery failures are logged and swallowed.</item>
/// </list>
/// </para>
/// </summary>
public sealed class WebhookNotificationService : BackgroundService, IOperationalNotificationSink
{
    private const int ChannelCapacity = 256;
    private static readonly string Hostname = Environment.MachineName;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly NotificationsConfig _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ServiceIdentity _identity;
    private readonly ILogger<WebhookNotificationService> _logger;

    private readonly Channel<OperationalAlert> _channel;
    private readonly ConcurrentDictionary<string, long> _lastEmittedAtMs = new(StringComparer.Ordinal);

    public WebhookNotificationService(
        NotificationsConfig config,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        ServiceIdentity identity,
        ILogger<WebhookNotificationService> logger)
    {
        _config = config;
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider;
        _identity = identity;
        _logger = logger;

        _channel = Channel.CreateBounded<OperationalAlert>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public void Emit(OperationalAlert alert)
    {
        _channel.Writer.TryWrite(alert);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Webhook notification service started with {TargetCount} target(s)",
            _config.Webhooks.Count);

        var alertsProcessed = 0;

        await foreach (var alert in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (IsDuplicate(alert))
                {
                    _logger.LogDebug(
                        "Suppressed duplicate alert: {AlertType} (dedup key: {Key})",
                        alert.Type, alert.DeduplicationKey);
                    continue;
                }

                RecordEmission(alert);
                await DeliverToAllTargetsAsync(alert, stoppingToken);

                // Periodically prune expired dedup entries to prevent unbounded growth
                if (++alertsProcessed % 50 == 0)
                    PruneExpiredDedupEntries();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error processing alert {AlertType}", alert.Type);
            }
        }
    }

    private bool IsDuplicate(OperationalAlert alert)
    {
        var key = alert.DeduplicationKey;
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var windowMs = _config.DeduplicationWindowSeconds * 1000L;

        if (_lastEmittedAtMs.TryGetValue(key, out var lastMs) && (nowMs - lastMs) < windowMs)
            return true;

        return false;
    }

    private void RecordEmission(OperationalAlert alert)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        _lastEmittedAtMs[alert.DeduplicationKey] = nowMs;
    }

    private void PruneExpiredDedupEntries()
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var windowMs = _config.DeduplicationWindowSeconds * 1000L;

        foreach (var (key, lastMs) in _lastEmittedAtMs)
        {
            if ((nowMs - lastMs) >= windowMs)
                _lastEmittedAtMs.TryRemove(key, out _);
        }
    }

    private async Task DeliverToAllTargetsAsync(OperationalAlert alert, CancellationToken ct)
    {
        foreach (var target in _config.Webhooks)
        {
            await DeliverToTargetAsync(target, alert, ct);
        }
    }

    private async Task DeliverToTargetAsync(
        WebhookTarget target,
        OperationalAlert alert,
        CancellationToken ct)
    {
        var targetName = target.Name ?? target.Url;

        for (var attempt = 0; attempt <= _config.MaxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    var delay = GetRetryDelay(attempt);
                    _logger.LogDebug(
                        "Retrying webhook delivery to {Target} (attempt {Attempt}/{Max}, delay {Delay}ms)",
                        targetName, attempt + 1, _config.MaxRetries + 1, delay.TotalMilliseconds);
                    await Task.Delay(delay, ct);
                }

                using var client = _httpClientFactory.CreateClient("Notifications");
                client.Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);

                using var request = new HttpRequestMessage(HttpMethod.Post, target.Url);
                request.Content = BuildContent(target, alert);

                if (target.Headers is { Count: > 0 })
                {
                    foreach (var (key, value) in target.Headers)
                        request.Headers.TryAddWithoutValidation(key, value);
                }

                using var response = await client.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug(
                        "Webhook delivered: {AlertType} → {Target} ({StatusCode})",
                        alert.Type, targetName, (int)response.StatusCode);
                    return;
                }

                _logger.LogWarning(
                    "Webhook delivery failed: {AlertType} → {Target} ({StatusCode})",
                    alert.Type, targetName, (int)response.StatusCode);

                // Don't retry on 4xx (client errors) — only retry on 5xx / transient
                if ((int)response.StatusCode < 500)
                    return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Webhook delivery error: {AlertType} → {Target} (attempt {Attempt}/{Max})",
                    alert.Type, targetName, attempt + 1, _config.MaxRetries + 1);

                if (attempt >= _config.MaxRetries)
                    return; // Exhausted retries, give up on this target
            }
        }
    }

    private JsonContent BuildContent(WebhookTarget target, OperationalAlert alert)
    {
        return target.Format switch
        {
            WebhookFormat.Slack => JsonContent.Create(
                SlackWebhookPayloadBuilder.Build(alert, _identity), options: JsonOptions),
            _ => JsonContent.Create(BuildGenericPayload(alert), options: JsonOptions),
        };
    }

    private WebhookPayload BuildGenericPayload(OperationalAlert alert) => new()
    {
        AlertId = alert.AlertId,
        Type = alert.Type,
        Severity = alert.Severity.ToString().ToLowerInvariant(),
        Summary = alert.Summary,
        Timestamp = alert.Timestamp,
        Source = "netclaw",
        Hostname = Hostname,
        Service = new ServicePayload
        {
            Name = _identity.Name,
            Namespace = _identity.Namespace,
            InstanceId = _identity.InstanceId,
            Version = _identity.Version,
        },
        Context = alert.Context,
    };

    private static TimeSpan GetRetryDelay(int attempt)
    {
        // Exponential backoff: 1s, 2s, 4s, ... capped at 30s with ±25% jitter
        var baseDelay = TimeSpan.FromSeconds(1);
        var exponential = TimeSpan.FromTicks(baseDelay.Ticks * (1L << attempt));
        var capped = exponential > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : exponential;
        var jitter = 0.75 + Random.Shared.NextDouble() * 0.5;
        return TimeSpan.FromTicks((long)(capped.Ticks * jitter));
    }

    /// <summary>
    /// Wire-format payload for webhook POST body.
    /// </summary>
    private sealed class WebhookPayload
    {
        public string AlertId { get; init; } = "";
        public string Type { get; init; } = "";
        public string Severity { get; init; } = "";
        public string Summary { get; init; } = "";
        public DateTimeOffset Timestamp { get; init; }
        public string Source { get; init; } = "";
        public string Hostname { get; init; } = "";
        public ServicePayload Service { get; init; } = new();
        public Dictionary<string, string>? Context { get; init; }
    }

    /// <summary>
    /// Wire-format projection of <see cref="ServiceIdentity"/>, nested under
    /// <c>service</c> in the generic payload. Field names follow the
    /// OpenTelemetry <c>service.*</c> resource-attribute convention.
    /// </summary>
    private sealed class ServicePayload
    {
        public string Name { get; init; } = "";
        public string? Namespace { get; init; }
        public string? InstanceId { get; init; }
        public string Version { get; init; } = "";
    }
}
