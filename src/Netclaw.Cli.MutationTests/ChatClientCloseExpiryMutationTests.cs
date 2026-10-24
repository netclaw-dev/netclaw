// -----------------------------------------------------------------------
// <copyright file="ChatClientCloseExpiryMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Configuration;
using Akka.TestKit;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tests.Cli;
using Xunit;

namespace Netclaw.Cli.MutationTests;

public sealed class ChatClientCloseExpiryMutationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_bind_reply_dispatches_text_only_before_expiry(bool expired)
    {
        var clock = new ManualClock();
        using var system = ActorSystem.Create($"close-expiry-mutation-{Guid.NewGuid():N}", ConfigurationFactory.ParseString("""
            akka.actor.provider = local
            akka.loglevel = ERROR
            akka.scheduler.implementation = "Akka.TestKit.TestScheduler, Akka.TestKit"
            """));
        try
        {
            Assert.IsType<TestScheduler>(system.Scheduler);
            // The elapsed clock advances after Close. The actor scheduler stays stationary.
            var result = await ChatClientCloseExpiryScenario.RunAsync(system, clock,
                () => clock.Advance(TimeSpan.FromSeconds(expired ? 2 : 1.5)), CancellationToken.None);
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
            await system.Terminate().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }
}
