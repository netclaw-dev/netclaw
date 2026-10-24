// -----------------------------------------------------------------------
// <copyright file="DaemonClientStartupCloseTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Configuration;
using Akka.TestKit;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Cli.Daemon;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

public sealed class DaemonClientStartupCloseTests
{
    private static readonly TimeSpan FailureCeiling = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QuitBudget = TimeSpan.FromSeconds(2);
    private static readonly string[] SubmittedText = ["initial text", "first text", "second text"];

    [Theory]
    [InlineData(2, false, false, false)] // Startup stays held through expiry.
    [InlineData(2, false, true, false)] // Disposal must preserve the close receipt.
    [InlineData(1, false, false, false)] // Handover retains only the unused budget.
    [InlineData(1, true, false, false)] // UTC rollback must not extend that budget.
    [InlineData(1, false, false, true)] // Admission before expiry remains possible.
    [InlineData(1, true, false, true)]
    public async Task Quit_retains_one_elapsed_budget_across_startup_and_disposal(
        int startupSeconds, bool moveUtcBackward, bool disposeDuringStartup, bool admitBeforeExpiry)
    {
        using var releaseStart = new ManualResetEventSlim(false);
        using var cancelled = new CancellationTokenSource();
        var startEntered = Gate();
        var bindEntered = Gate();
        var releaseBind = Gate();
        var systemCreated = new TaskCompletionSource<ActorSystem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var elapsed = new FakeTimeProvider();
        var clock = new AdjustableUtcClock(elapsed);
        var token = TestContext.Current.CancellationToken;
        var transport = new FakeDaemonHubTransport
        {
            EnsureSessionGate = _ => { bindEntered.TrySetResult(); return releaseBind.Task; }
        };
        var client = new DaemonClient("http://localhost", transport, (name, config) =>
        {
            startEntered.TrySetResult();
            releaseStart.Wait(token);
            var system = ActorSystem.Create(name, ManualSchedulerConfig().WithFallback(config));
            systemCreated.TrySetResult(system);
            return system;
        }, clock, [TimeSpan.Zero], TimeSpan.FromSeconds(60));

        // Open.Initial makes late startup capable of text dispatch without another API call.
        var requests = new[]
        {
            client.OpenChatAsync(null, SubmittedText[0]),
            client.SendAsync(SubmittedText[1], token),
            client.SendAsync(SubmittedText[2], token)
        };
        var outcomes = requests.Select(ChatClientCloseExpiryScenario.ObserveAsync).ToArray();
        var cancelledOutcome = ChatClientCloseExpiryScenario.ObserveAsync(client.SendAsync("cancelled text", cancelled.Token));
        Task? disposal = null;

        try
        {
            await startEntered.Task.WaitAsync(FailureCeiling, token);
            Assert.All(requests, request => Assert.False(request.IsCompleted));
            Assert.Equal(0, transport.StartAttempts);
            cancelled.Cancel();
            Assert.IsAssignableFrom<OperationCanceledException>(await cancelledOutcome.WaitAsync(FailureCeiling, token));

            var close = client.CloseAsync();
            var startedAt = clock.GetTimestamp();
            Assert.Same(close, client.CloseAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendAsync("after quit", token));
            if (disposeDuringStartup) disposal = client.DisposeAsync().AsTask();

            elapsed.Advance(TimeSpan.FromSeconds(startupSeconds));
            if (moveUtcBackward) clock.ShiftUtc(TimeSpan.FromSeconds(-30));

            ActorSystem? system = null;
            if (startupSeconds < 2)
            {
                releaseStart.Set();
                system = await systemCreated.Task.WaitAsync(FailureCeiling, token);
                // The bind acknowledgement proves that the actor owns the remaining close budget.
                await bindEntered.Task.WaitAsync(FailureCeiling, token);
                Assert.False(close.IsCompleted);
                Assert.DoesNotContain(transport.Invocations, call => call.Method == "SendMessage");
            }

            if (admitBeforeExpiry)
            {
                releaseBind.TrySetResult();
                foreach (var outcome in outcomes)
                    Assert.Null(await outcome.WaitAsync(FailureCeiling, token));
            }
            else if (system is not null)
            {
                var remaining = QuitBudget - clock.GetElapsedTime(startedAt);
                elapsed.Advance(remaining);
                ((TestScheduler)system.Scheduler).Advance(remaining);
            }

            var receipt = await close.WaitAsync(FailureCeiling, token);
            Assert.True(clock.GetElapsedTime(startedAt) <= QuitBudget);
            AssertReceipt(receipt, admitBeforeExpiry);
            Assert.Same(receipt, client.CloseReceipt);

            if (startupSeconds == 2)
            {
                Assert.False(releaseStart.IsSet);
                Assert.False(systemCreated.Task.IsCompleted);
                Assert.Equal(0, transport.StartAttempts);
            }

            releaseStart.Set();
            releaseBind.TrySetResult();
            foreach (var outcome in outcomes)
            {
                var error = await outcome.WaitAsync(FailureCeiling, token);
                if (!admitBeforeExpiry) Assert.NotNull(error);
            }
            disposal ??= client.DisposeAsync().AsTask();
            await AssertCleanupAsync(disposal, systemCreated.Task, transport, admitBeforeExpiry, token);
        }
        finally
        {
            // Release both barriers before cleanup, also when a regression assertion fails.
            releaseStart.Set();
            releaseBind.TrySetResult();
            disposal ??= client.DisposeAsync().AsTask();
            await disposal.WaitAsync(FailureCeiling, token);
            await Task.WhenAll(outcomes).WaitAsync(FailureCeiling, token);
            await cancelledOutcome.WaitAsync(FailureCeiling, token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_bind_reply_dispatches_text_only_before_expiry(bool expired)
    {
        var token = TestContext.Current.CancellationToken;
        var elapsed = new FakeTimeProvider();
        using var system = ActorSystem.Create($"close-expiry-{Guid.NewGuid():N}", ManualSchedulerConfig());
        try
        {
            // Keep the actor scheduler stationary until the held bind result reaches the expiry guard.
            var result = await ChatClientCloseExpiryScenario.RunAsync(system, elapsed,
                () => elapsed.Advance(expired ? QuitBudget : TimeSpan.FromSeconds(1.5)), token);
            Assert.Equal(expired ? Array.Empty<string>() : new[] { ChatClientCloseExpiryScenario.Text }, result.SentText);
            if (expired)
            {
                Assert.IsAssignableFrom<OperationCanceledException>(result.SendError);
                var input = Assert.Single(result.Receipt.Inputs);
                Assert.Equal(ChatClientCloseExpiryScenario.Text, input.Text);
                Assert.Equal(InputDeliveryStatus.Unsent, input.Status);
            }
            else
            {
                Assert.Null(result.SendError);
                Assert.Empty(result.Receipt.Inputs);
            }
        }
        finally
        {
            await system.Terminate().WaitAsync(FailureCeiling, token);
        }
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Akka.Configuration.Config ManualSchedulerConfig() => ConfigurationFactory.ParseString("""
        akka.actor.provider = local
        akka.loglevel = ERROR
        akka.scheduler.implementation = "Akka.TestKit.TestScheduler, Akka.TestKit"
        """);

    private static void AssertReceipt(ChatCloseReceipt receipt, bool admitted)
    {
        Assert.Equal(admitted ? Array.Empty<string>() : SubmittedText.Order().ToArray(),
            receipt.Inputs.Select(input => input.Text).Order().ToArray());
        Assert.All(receipt.Inputs, input => Assert.Equal(InputDeliveryStatus.Unsent, input.Status));
    }

    private static async Task AssertCleanupAsync(Task disposal, Task<ActorSystem> systemCreated,
        FakeDaemonHubTransport transport, bool admitted, CancellationToken token)
    {
        await disposal.WaitAsync(FailureCeiling, token);
        var system = await systemCreated.WaitAsync(FailureCeiling, token);
        await system.WhenTerminated.WaitAsync(FailureCeiling, token);
        // Termination ends the interval in which late startup can dispatch expired text.
        Assert.Equal(admitted ? SubmittedText.Order().ToArray() : Array.Empty<string>(),
            transport.Invocations.Where(call => call.Method == "SendMessage")
                .Select(call => (string)call.Args[1]!).Order().ToArray());
        Assert.False(transport.IsConnected);
    }

    private sealed class AdjustableUtcClock(FakeTimeProvider elapsed) : TimeProvider
    {
        private long _utcOffsetTicks;
        public override DateTimeOffset GetUtcNow() => elapsed.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _utcOffsetTicks));
        public override long GetTimestamp() => elapsed.GetTimestamp();
        public override long TimestampFrequency => elapsed.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => elapsed.CreateTimer(callback, state, dueTime, period);
        internal void ShiftUtc(TimeSpan change) => Interlocked.Add(ref _utcOffsetTicks, change.Ticks);
    }
}
