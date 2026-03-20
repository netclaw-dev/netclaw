using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<WebhookNotificationService> _logger;
    private readonly Func<int, TimeSpan> _retryDelayFactory;

    private readonly Channel<OperationalAlert> _channel;
    private readonly ConcurrentDictionary<string, long> _lastEmittedAtMs = new(StringComparer.Ordinal);
    private long _droppedAlertCount;

    public WebhookNotificationService(
        NotificationsConfig config,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        ILogger<WebhookNotificationService> logger,
        Func<int, TimeSpan>? retryDelayFactory = null)
    {
        var validation = NotificationConfigValidator.Validate(config);
        if (!validation.IsValid)
        {
            var details = string.Join(" ", validation.Issues.Select(static issue =>
                $"{issue.FieldPath}: {issue.Message}"));
            throw new InvalidOperationException($"Webhook notification service requires valid notification config. {details}");
        }

        _config = config;
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _retryDelayFactory = retryDelayFactory ?? GetRetryDelay;

        _channel = Channel.CreateBounded<OperationalAlert>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public void Emit(OperationalAlert alert)
    {
        if (_channel.Writer.TryWrite(alert))
            return;

        var droppedCount = Interlocked.Increment(ref _droppedAlertCount);
        _logger.LogWarning(
            "Dropping operational alert {AlertType} because the notification queue is full (dropped count: {DroppedCount})",
            alert.Type,
            droppedCount);
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

                var delivered = await DeliverToAllTargetsAsync(alert, stoppingToken);
                if (delivered)
                    RecordEmission(alert);

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

    private async Task<bool> DeliverToAllTargetsAsync(OperationalAlert alert, CancellationToken ct)
    {
        var payload = BuildPayload(alert);
        var deliveredAny = false;

        foreach (var target in _config.Webhooks)
        {
            deliveredAny |= await DeliverToTargetAsync(target, payload, alert, ct);
        }

        return deliveredAny;
    }

    private async Task<bool> DeliverToTargetAsync(
        WebhookTarget target,
        WebhookPayload payload,
        OperationalAlert alert,
        CancellationToken ct)
    {
        var targetName = GetTargetIdentity(target, _config.Webhooks.IndexOf(target));
        var redactedHeaders = NotificationConfigValidator.FormatRedactedHeaders(target.Headers);
        var targetUrl = target.Url?.Value ?? string.Empty;

        for (var attempt = 0; attempt <= _config.MaxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    var delay = _retryDelayFactory(attempt);
                    _logger.LogDebug(
                        "Retrying webhook delivery to {Target} with headers {Headers} (attempt {Attempt}/{Max}, delay {Delay}ms)",
                        targetName, redactedHeaders, attempt + 1, _config.MaxRetries + 1, delay.TotalMilliseconds);
                    await Task.Delay(delay, ct);
                }

                using var client = _httpClientFactory.CreateClient("Notifications");
                client.Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds);

                using var request = new HttpRequestMessage(HttpMethod.Post, targetUrl);
                request.Content = JsonContent.Create(payload, options: JsonOptions);

                if (target.Headers is { Count: > 0 })
                {
                    foreach (var (key, value) in target.Headers)
                        request.Headers.TryAddWithoutValidation(key, value.Value);
                }

                using var response = await client.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug(
                        "Webhook delivered: {AlertType} → {Target} with headers {Headers} ({StatusCode})",
                        alert.Type, targetName, redactedHeaders, (int)response.StatusCode);
                    return true;
                }

                _logger.LogWarning(
                    "Webhook delivery failed: {AlertType} → {Target} with headers {Headers} ({StatusCode})",
                    alert.Type, targetName, redactedHeaders, (int)response.StatusCode);

                if (!ShouldRetry(response.StatusCode, attempt))
                    return false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Webhook delivery error: {AlertType} → {Target} with headers {Headers} (attempt {Attempt}/{Max})",
                    alert.Type, targetName, redactedHeaders, attempt + 1, _config.MaxRetries + 1);

                if (attempt >= _config.MaxRetries)
                    return false;
            }
        }

        return false;
    }

    internal static string GetTargetIdentity(WebhookTarget target, int index)
    {
        return NotificationConfigValidator.FormatTargetIdentity(target, index);
    }

    private static WebhookPayload BuildPayload(OperationalAlert alert) => new()
    {
        AlertId = alert.AlertId,
        Type = alert.Type,
        Severity = alert.Severity,
        Summary = alert.Summary,
        Timestamp = alert.Timestamp,
        Source = "netclaw",
        Hostname = Hostname,
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

    private bool ShouldRetry(HttpStatusCode statusCode, int attempt)
    {
        if (attempt >= _config.MaxRetries)
            return false;

        if ((int)statusCode >= 500)
            return true;

        return statusCode == HttpStatusCode.TooManyRequests;
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
        public Dictionary<string, string>? Context { get; init; }
    }
}
