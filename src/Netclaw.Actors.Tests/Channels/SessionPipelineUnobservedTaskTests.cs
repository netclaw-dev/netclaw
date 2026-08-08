// -----------------------------------------------------------------------
// <copyright file="SessionPipelineUnobservedTaskTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka;
using Akka.Hosting.TestKit;
using Akka.Streams;
using Akka.Streams.Dsl;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

/// <summary>
/// Documents and exercises the patterns that prevent the abrupt-teardown
/// unobserved-task crashes (daemon-unobserved logs with
/// <c>AbruptTerminationException</c> / <c>StreamDetachedException</c>).
/// Akka.Streams stages create internal <see cref="Task{Done}"/> instances
/// that can fault on teardown. Production code observes these tasks before it
/// discards their materialized values. These tests cover two valid patterns:
/// <list type="bullet">
///   <item><c>Keep.Both</c> + await both materialized tasks.</item>
///   <item><c>MapMaterializedValue</c> with a <c>ContinueWith</c> that
///   reads <see cref="Task.Exception"/> on fault.</item>
/// </list>
/// </summary>
public sealed class SessionPipelineUnobservedTaskTests : TestKit
{
    public SessionPipelineUnobservedTaskTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(Akka.Hosting.AkkaConfigurationBuilder builder, IServiceProvider provider) { }

    /// <summary>
    /// Verifies that wrapping <c>Sink.ForEach</c> with <c>MapMaterializedValue</c>
    /// + <c>ContinueWith(OnlyOnFaulted)</c> reliably runs the observation
    /// callback when the upstream is aborted. This is the pattern used in
    /// <c>ChannelPipeline.CreateAsync</c> and <c>SessionPipelineHandle</c>.
    /// </summary>
    [Fact]
    public async Task MapMaterializedValue_continuation_observes_sink_task_on_fault()
    {
        var observed = new TaskCompletionSource<Exception?>();
        var killSwitch = KillSwitches.Shared("test-mapmat");

        var sink = Sink.ForEach<int>(_ => { }).MapMaterializedValue<NotUsed>(task =>
        {
            _ = task.ContinueWith(
                t => observed.TrySetResult(t.Exception?.GetBaseException()),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return NotUsed.Instance;
        });

        var runnable = Source.Repeat(1)
            .Via(killSwitch.Flow<int>())
            .ToMaterialized(sink, Keep.Right);

        runnable.Run(Sys.Materializer());

        var simulatedAbrupt = new InvalidOperationException("simulated abrupt teardown");
        killSwitch.Abort(simulatedAbrupt);

        var observedException = await observed.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.NotNull(observedException);
        Assert.Same(simulatedAbrupt, observedException);
    }

    /// <summary>
    /// Verifies that <c>Keep.Both</c> + await-both observes the Sink.ForEach
    /// task after a stream fault when a caller retains both materialized tasks.
    /// </summary>
    [Fact]
    public async Task KeepBoth_awaits_observe_both_watch_and_sink_tasks_on_fault()
    {
        var killSwitch = KillSwitches.Shared("test-keepboth");

        var (watchTask, sinkTask) = Source.Repeat(1)
            .Via(killSwitch.Flow<int>())
            .WatchTermination((_, done) => done)
            .ToMaterialized(Sink.ForEach<int>(_ => { }), Keep.Both)
            .Run(Sys.Materializer());

        var simulatedAbrupt = new InvalidOperationException("simulated abrupt teardown");
        killSwitch.Abort(simulatedAbrupt);

        Exception? watchFailure = null;
        try
        {
            await watchTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        catch (Exception ex) { watchFailure = ex; }

        Exception? sinkFailure = null;
        try
        {
            await sinkTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        catch (Exception ex) { sinkFailure = ex; }

        Assert.NotNull(watchFailure);
        Assert.NotNull(sinkFailure);
        Assert.Same(simulatedAbrupt, watchFailure!.GetBaseException());
        Assert.Same(simulatedAbrupt, sinkFailure!.GetBaseException());
    }
}
