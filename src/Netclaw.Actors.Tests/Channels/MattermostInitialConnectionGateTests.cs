// -----------------------------------------------------------------------
// <copyright file="MattermostInitialConnectionGateTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Mattermost.Events;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Channels.Mattermost.Transport;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class MattermostInitialConnectionGateTests
{
    [Fact]
    public async Task StartWaitsForConnectedAndRemovesHandler()
    {
        var timeProvider = new FakeTimeProvider();
        var subscription = new ConnectionSubscription();
        var startCount = 0;

        var start = MattermostInitialConnectionGate.StartAndWaitAsync(
            cancellationToken =>
            {
                Assert.False(cancellationToken.IsCancellationRequested);
                Assert.NotNull(subscription.Handler);
                startCount++;
                return Task.CompletedTask;
            },
            subscription.Subscribe,
            subscription.Unsubscribe,
            timeProvider,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, startCount);
        Assert.False(start.IsCompleted);

        subscription.RaiseConnected(timeProvider);
        await start;

        Assert.Equal(1, subscription.UnsubscribeCount);
        Assert.Equal(subscription.Handler, subscription.RemovedHandler);
    }

    [Fact]
    public async Task TimeoutUsesTimeProviderAndRemovesHandler()
    {
        var timeProvider = new FakeTimeProvider();
        var subscription = new ConnectionSubscription();
        var start = MattermostInitialConnectionGate.StartAndWaitAsync(
            _ => Task.CompletedTask,
            subscription.Subscribe,
            subscription.Unsubscribe,
            timeProvider,
            TestContext.Current.CancellationToken);

        timeProvider.Advance(MattermostInitialConnectionGate.Timeout);

        await Assert.ThrowsAsync<TimeoutException>(() => start);
        Assert.Equal(1, subscription.UnsubscribeCount);
        Assert.Equal(subscription.Handler, subscription.RemovedHandler);
    }

    [Fact]
    public async Task CancellationRemovesHandler()
    {
        var timeProvider = new FakeTimeProvider();
        var subscription = new ConnectionSubscription();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var start = MattermostInitialConnectionGate.StartAndWaitAsync(
            _ => Task.CompletedTask,
            subscription.Subscribe,
            subscription.Unsubscribe,
            timeProvider,
            cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(1, subscription.UnsubscribeCount);
        Assert.Equal(subscription.Handler, subscription.RemovedHandler);
    }

    [Fact]
    public async Task StartFailureRemovesHandler()
    {
        var timeProvider = new FakeTimeProvider();
        var subscription = new ConnectionSubscription();
        var start = MattermostInitialConnectionGate.StartAndWaitAsync(
            _ => throw new InvalidOperationException("Start failed."),
            subscription.Subscribe,
            subscription.Unsubscribe,
            timeProvider,
            TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => start);

        Assert.Equal("Start failed.", exception.Message);
        Assert.Equal(1, subscription.UnsubscribeCount);
        Assert.Equal(subscription.Handler, subscription.RemovedHandler);
    }

    private sealed class ConnectionSubscription
    {
        public EventHandler<ConnectionEventArgs>? Handler { get; private set; }

        public EventHandler<ConnectionEventArgs>? RemovedHandler { get; private set; }

        public int UnsubscribeCount { get; private set; }

        public void Subscribe(EventHandler<ConnectionEventArgs> handler) => Handler = handler;

        public void Unsubscribe(EventHandler<ConnectionEventArgs> handler)
        {
            RemovedHandler = handler;
            UnsubscribeCount++;
        }

        public void RaiseConnected(TimeProvider timeProvider)
        {
            Assert.NotNull(Handler);
            Handler(
                this,
                new ConnectionEventArgs(
                    new Uri("wss://mattermost.test/api/v4/websocket"),
                    timeProvider.GetUtcNow().UtcDateTime));
        }
    }
}
