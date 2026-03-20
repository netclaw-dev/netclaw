using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

public sealed class WebhookNotificationServiceTests : IAsyncDisposable
{
    private readonly List<WebhookNotificationService> _services = [];

    [Fact]
    public async Task DeliversAlert_ToSingleTarget()
    {
        var handler = new SequenceHandler(HttpStatusCode.OK);
        var service = CreateService(
            new NotificationsConfig
            {
                Webhooks = [new WebhookTarget { Url = new SensitiveString("https://example.com/hook") }],
                DeduplicationWindowSeconds = 0
            },
            handler);

        await service.StartAsync(CancellationToken.None);
        service.Emit(CreateAlert());

        await handler.WaitForRequestsAsync(1);

        Assert.Single(handler.Requests);
        Assert.Equal("https://example.com/hook", handler.Requests[0].RequestUri?.ToString());
    }

    [Fact]
    public async Task IncludesCustomHeaders_WhenConfigured()
    {
        var handler = new SequenceHandler(HttpStatusCode.OK);
        var service = CreateService(
            new NotificationsConfig
            {
                Webhooks =
                [
                    new WebhookTarget
                    {
                        Url = new SensitiveString("https://example.com/hook"),
                        Headers = new Dictionary<string, SensitiveString>
                        {
                            ["Authorization"] = new SensitiveString("Bearer test-token"),
                            ["X-Custom"] = new SensitiveString("custom-value")
                        }
                    }
                ],
                DeduplicationWindowSeconds = 0
            },
            handler);

        await service.StartAsync(CancellationToken.None);
        service.Emit(CreateAlert());

        await handler.WaitForRequestsAsync(1);

        var request = handler.Requests[0];
        Assert.Contains("Bearer test-token", request.Headers.GetValues("Authorization"));
        Assert.Contains("custom-value", request.Headers.GetValues("X-Custom"));
    }

    [Fact]
    public async Task DoesNotRetry_On4xxErrors()
    {
        var handler = new SequenceHandler(HttpStatusCode.BadRequest);
        var service = CreateService(
            new NotificationsConfig
            {
                Webhooks = [new WebhookTarget { Url = new SensitiveString("https://example.com/hook") }],
                DeduplicationWindowSeconds = 0,
                MaxRetries = 2
            },
            handler);

        await service.StartAsync(CancellationToken.None);
        service.Emit(CreateAlert());

        await handler.WaitForRequestsAsync(1);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Retries_On429Errors_UpToConfiguredLimit()
    {
        var handler = new SequenceHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        var service = CreateService(
            new NotificationsConfig
            {
                Webhooks = [new WebhookTarget { Url = new SensitiveString("https://example.com/hook") }],
                DeduplicationWindowSeconds = 0,
                MaxRetries = 1
            },
            handler,
            retryDelayFactory: static _ => TimeSpan.Zero);

        await service.StartAsync(CancellationToken.None);
        service.Emit(CreateAlert());

        await handler.WaitForRequestsAsync(2);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Retries_On5xxErrors_UpToConfiguredLimit()
    {
        var handler = new SequenceHandler(HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        var logger = new TestLogger<WebhookNotificationService>();
        var service = CreateService(
            new NotificationsConfig
            {
                Webhooks = [new WebhookTarget { Url = new SensitiveString("https://example.com/hook"), Name = "ops" }],
                DeduplicationWindowSeconds = 0,
                MaxRetries = 2
            },
            handler,
            logger: logger,
            retryDelayFactory: static _ => TimeSpan.Zero);

        await service.StartAsync(CancellationToken.None);
        service.Emit(CreateAlert());

        await handler.WaitForRequestsAsync(3);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains(logger.Messages, static message => message.Contains("Retrying webhook delivery", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, static message => message.Contains("Webhook delivered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContinuesRunning_WhenAllTargetsFail()
    {
        var handler = new SequenceHandler(HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError);
        var service = CreateService(
            new NotificationsConfig
            {
                Webhooks = [new WebhookTarget { Url = new SensitiveString("https://example.com/hook") }],
                DeduplicationWindowSeconds = 0,
                MaxRetries = 0
            },
            handler);

        await service.StartAsync(CancellationToken.None);

        service.Emit(CreateAlert(source: "a"));
        await handler.WaitForRequestsAsync(1);

        service.Emit(CreateAlert(source: "b"));
        await handler.WaitForRequestsAsync(2);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DoesNotRecordDeduplication_WhenDeliveryFails()
    {
        var handler = new SequenceHandler(HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-19T12:00:00Z"));
        var service = CreateService(
            new NotificationsConfig
            {
                Webhooks = [new WebhookTarget { Url = new SensitiveString("https://example.com/hook") }],
                DeduplicationWindowSeconds = 300,
                MaxRetries = 0
            },
            handler,
            timeProvider: timeProvider);

        var alert = CreateAlert(source: "same-source");

        await service.StartAsync(CancellationToken.None);
        service.Emit(alert);
        await handler.WaitForRequestsAsync(1);

        timeProvider.Advance(TimeSpan.FromSeconds(1));

        service.Emit(alert);
        await handler.WaitForRequestsAsync(2);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task RedactsConfiguredHeaderValues_InLogs_WhileKeepingTargetIdentityVisible()
    {
        var handler = new SequenceHandler(HttpStatusCode.InternalServerError);
        var logger = new TestLogger<WebhookNotificationService>();
        var service = CreateService(
            new NotificationsConfig
            {
                Webhooks =
                [
                    new WebhookTarget
                    {
                        Name = "ops-primary",
                        Url = new SensitiveString("https://alerts.example/hooks/netclaw?token=secret"),
                        Headers = new Dictionary<string, SensitiveString>
                        {
                            ["Authorization"] = new SensitiveString("Bearer super-secret"),
                            ["X-Custom"] = new SensitiveString("also-secret")
                        }
                    }
                ],
                DeduplicationWindowSeconds = 0,
                MaxRetries = 0
            },
            handler,
            logger: logger);

        await service.StartAsync(CancellationToken.None);
        service.Emit(CreateAlert());

        await handler.WaitForRequestsAsync(1);

        var joined = string.Join(Environment.NewLine, logger.Messages);
        Assert.Contains("ops-primary (https://alerts.example/<redacted>)", joined, StringComparison.Ordinal);
        Assert.Contains("Authorization=<redacted>", joined, StringComparison.Ordinal);
        Assert.Contains("X-Custom=<redacted>", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("also-secret", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("token=secret", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("hooks/netclaw", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsInvalidConfig_OnConstruction()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CreateService(
            new NotificationsConfig
            {
                Webhooks = [new WebhookTarget { Url = new SensitiveString("http://alerts.internal.example/hooks/netclaw") }]
            },
            new SequenceHandler(HttpStatusCode.OK)));

        Assert.Contains("Notifications.Webhooks[0].Url", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PayloadContainsExpectedFields()
    {
        var handler = new SequenceHandler(HttpStatusCode.OK);
        var service = CreateService(
            new NotificationsConfig
            {
                Webhooks = [new WebhookTarget { Url = new SensitiveString("https://example.com/hook") }],
                DeduplicationWindowSeconds = 0
            },
            handler);

        await service.StartAsync(CancellationToken.None);
        service.Emit(CreateAlert(type: "provider.failover", category: AlertType.ProviderFailover));

        await handler.WaitForRequestsAsync(1);

        var body = handler.RequestBodies[0];
        var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal("provider.failover", root.GetProperty("type").GetString());
        Assert.Equal("warning", root.GetProperty("severity").GetString());
        Assert.Equal("netclaw", root.GetProperty("source").GetString());
        Assert.True(root.TryGetProperty("hostname", out _));
        Assert.True(root.TryGetProperty("alertId", out _));
        Assert.True(root.TryGetProperty("timestamp", out _));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _services)
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }

        _services.Clear();
    }

    private static OperationalAlert CreateAlert(
        string type = "mcp.server.disconnected",
        AlertType category = AlertType.McpServerDisconnected,
        string? source = null)
    {
        return new OperationalAlert
        {
            AlertId = Guid.NewGuid().ToString("N")[..12],
            Type = type,
            Category = category,
            Summary = $"Test alert: {type}",
            Timestamp = TimeProvider.System.GetUtcNow(),
            Severity = "warning",
            Source = source,
            Context = source is not null
                ? new Dictionary<string, string> { ["serverName"] = source }
                : null
        };
    }

    private WebhookNotificationService CreateService(
        NotificationsConfig config,
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null,
        ILogger<WebhookNotificationService>? logger = null,
        Func<int, TimeSpan>? retryDelayFactory = null)
    {
        var factory = new TestHttpClientFactory(handler);
        var service = new WebhookNotificationService(
            config,
            factory,
            timeProvider ?? TimeProvider.System,
            logger ?? new TestLogger<WebhookNotificationService>(),
            retryDelayFactory);

        _services.Add(service);
        return service;
    }

    private sealed class SequenceHandler(params HttpStatusCode[] responses) : HttpMessageHandler
    {
        private readonly HttpStatusCode[] _responses = responses.Length == 0 ? [HttpStatusCode.OK] : responses;
        private readonly object _sync = new();
        private readonly List<(int ExpectedCount, TaskCompletionSource<bool> Waiter)> _waiters = [];
        private int _requestCount;

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> RequestBodies { get; } = [];

        public Task WaitForRequestsAsync(int expectedCount)
        {
            lock (_sync)
            {
                if (_requestCount >= expectedCount)
                    return Task.CompletedTask;

                var waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((expectedCount, waiter));
                return waiter.Task;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : string.Empty;

            lock (Requests)
            {
                Requests.Add(CloneRequest(request));
                RequestBodies.Add(body);
            }

            var requestCount = Interlocked.Increment(ref _requestCount);

            List<TaskCompletionSource<bool>> readyWaiters = [];
            lock (_sync)
            {
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    var (expectedCount, waiter) = _waiters[i];
                    if (requestCount < expectedCount)
                        continue;

                    readyWaiters.Add(waiter);
                    _waiters.RemoveAt(i);
                }
            }

            foreach (var waiter in readyWaiters)
                waiter.TrySetResult(true);

            var statusCode = _responses[Math.Min(requestCount - 1, _responses.Length - 1)];
            return new HttpResponseMessage(statusCode);
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

            return clone;
        }
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (exception is not null)
                message = $"{message} {exception.Message}";

            Messages.Add(message);
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
