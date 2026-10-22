// -----------------------------------------------------------------------
// <copyright file="BinaryUpdateCheckTimeoutTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration.Feeds;
using Netclaw.Daemon.Services;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

/// <summary>
/// A network timeout in the daemon update check must not stop the host. HttpClient reports its
/// own timeout as TaskCanceledException, which derives from OperationCanceledException.
/// </summary>
public sealed class BinaryUpdateCheckTimeoutTests
{
    private sealed class ThrowingHandler(Func<Exception> createException) : HttpMessageHandler
    {
        private int _calls;

        public TaskCompletionSource FourthCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // One check sends two requests (manifest and signature).
            if (Interlocked.Increment(ref _calls) == 4)
                FourthCall.TrySetResult();
            return Task.FromException<HttpResponseMessage>(createException());
        }
    }

    private sealed class RecordingLogger : ILogger<BinaryUpdateCheckService>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                    return [.. _entries];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class TimerSignalingTimeProvider : FakeTimeProvider
    {
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            TimerCreated.TrySetResult();
            return timer;
        }
    }

    private static BinaryUpdateCheckService Create(
        HttpMessageHandler handler, bool selfUpdateDisabled, RecordingLogger? logger = null)
        => new(new HttpClient(handler), logger ?? new RecordingLogger(), "0.1.0", selfUpdateDisabled);

    private static Exception HttpClientTimeout()
        => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.",
            new TimeoutException());

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // Docker sets Daemon:DisableSelfUpdate=true; the service still runs.
    public async Task CheckAndNotify_HttpClientTimeout_LogsWarningAndDoesNotEscape(bool selfUpdateDisabled)
    {
        var logger = new RecordingLogger();
        var sut = Create(new ThrowingHandler(HttpClientTimeout), selfUpdateDisabled, logger);

        // The caller token is not cancelled, so this is a network timeout and not a host shutdown.
        await sut.CheckAndNotifyAsync(CancellationToken.None);

        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("update check failed", entry.Message, StringComparison.Ordinal);
        Assert.Contains(FeedConstants.BinaryManifestUrl, entry.Message, StringComparison.Ordinal);
        Assert.Contains("10s", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAndNotify_LinkedTimeoutToken_LogsWarningAndDoesNotEscape()
    {
        // FetchVerifiedManifestAsync cancels its own linked token after BinaryFeedHttpTimeout.
        // That token is not the caller's token, so the throw looks like the one below.
        using var internalTimeout = new CancellationTokenSource();
        await internalTimeout.CancelAsync();
        var logger = new RecordingLogger();
        var sut = Create(
            new ThrowingHandler(() => new OperationCanceledException(internalTimeout.Token)),
            selfUpdateDisabled: true, logger);

        await sut.CheckAndNotifyAsync(CancellationToken.None);

        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning
            && e.Message.Contains("update check failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostedService_Timeout_KeepsRunningAtStartAndAtRecheck()
    {
        var time = new TimerSignalingTimeProvider();
        var handler = new ThrowingHandler(HttpClientTimeout);
        var sut = new BinaryUpdateCheckService(
            new HttpClient(handler), new RecordingLogger(), "0.1.0", selfUpdateDisabled: true,
            notificationSink: null, timeProvider: time);

        await sut.StartAsync(CancellationToken.None);

        // ExecuteAsync creates its PeriodicTimer only after the first check returns. Either the timer
        // appears (the check survived) or ExecuteTask ends first (it faulted, and a host with
        // StopHost then stops the daemon).
        var afterStart = await Task.WhenAny(sut.ExecuteTask!, time.TimerCreated.Task);
        Assert.Same(time.TimerCreated.Task, afterStart);

        time.Advance(BinaryUpdateCheckService.RecheckInterval);
        var afterRecheck = await Task.WhenAny(sut.ExecuteTask!, handler.FourthCall.Task);
        Assert.Same(handler.FourthCall.Task, afterRecheck);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CheckAndNotify_HostShutdown_StillPropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var sut = Create(new ThrowingHandler(() => new OperationCanceledException(cts.Token)), selfUpdateDisabled: false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.CheckAndNotifyAsync(cts.Token));
    }

    [Fact]
    public async Task FetchVerifiedManifest_HostShutdown_StillPropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var client = new HttpClient(new ThrowingHandler(() => new OperationCanceledException(cts.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => UpdateCheckService.FetchVerifiedManifestAsync(client, cts.Token));
    }
}
