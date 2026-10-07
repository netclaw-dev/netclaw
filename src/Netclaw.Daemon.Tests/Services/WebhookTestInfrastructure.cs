// -----------------------------------------------------------------------
// <copyright file="WebhookTestInfrastructure.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;

namespace Netclaw.Daemon.Tests.Services;

/// <summary>
/// Shared test infrastructure for webhook notification tests.
/// </summary>
internal static class WebhookTestInfrastructure
{
    public static async Task WaitForDeliveryAsync(
        RecordingHandler handler,
        int expectedCount,
        int timeoutMs = 5000)
    {
        // One budget per delivery — this is a hang guard, not a wall-clock limit
        // for the whole batch. Deliveries are sequential (DeliverToAllTargetsAsync),
        // so a single shared CTS let a slow delivery #1 consume the budget of the
        // rest on loaded CI machines. A genuinely stuck delivery still fails in
        // timeoutMs. A bare timeout bump would keep the coupling between
        // deliveries; per-delivery removes it.
        for (var i = 0; i < expectedCount; i++)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            await handler.DeliverySemaphore.WaitAsync(cts.Token);
        }
    }
}

internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly Exception? _exception;
    private readonly Task? _holdFirstRequestUntil;
    private readonly SemaphoreSlim _deliverySemaphore = new(0);
    private int _requestCount;

    /// <param name="statusCode">The status code of each response.</param>
    /// <param name="holdFirstRequestUntil">
    /// When set, the first request signals <see cref="DeliverySemaphore"/> and then waits for this
    /// task before it returns. A test uses it to keep a delivery in flight while it stops the service.
    /// </param>
    public RecordingHandler(HttpStatusCode statusCode, Task? holdFirstRequestUntil = null)
    {
        _statusCode = statusCode;
        _holdFirstRequestUntil = holdFirstRequestUntil;
    }

    /// <param name="exception">The exception that each request throws.</param>
    /// <param name="holdFirstRequestUntil">
    /// When set, the first request signals <see cref="DeliverySemaphore"/> and then waits for this
    /// task before it throws.
    /// </param>
    public RecordingHandler(Exception exception, Task? holdFirstRequestUntil = null)
    {
        _exception = exception;
        _holdFirstRequestUntil = holdFirstRequestUntil;
    }

    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> RequestBodies { get; } = [];
    public SemaphoreSlim DeliverySemaphore => _deliverySemaphore;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is not null
            ? await request.Content.ReadAsStringAsync(cancellationToken)
            : "";

        lock (Requests)
        {
            Requests.Add(request);
            RequestBodies.Add(body);
        }

        _deliverySemaphore.Release();

        // A real transport observes the request token, so a held request ends when the
        // caller cancels it.
        if (_holdFirstRequestUntil is not null && Interlocked.Increment(ref _requestCount) == 1)
            await _holdFirstRequestUntil.WaitAsync(cancellationToken);

        if (_exception is not null)
            throw _exception;

        return new HttpResponseMessage(_statusCode);
    }
}

internal sealed class TestHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public TestHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}
