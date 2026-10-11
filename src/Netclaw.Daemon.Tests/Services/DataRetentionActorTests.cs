// -----------------------------------------------------------------------
// <copyright file="DataRetentionActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Akka.Actor;
using Akka.Configuration;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Akka.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Services;

/// <summary>
/// The retention actor runs on Akka's virtual scheduler, so "twelve hours later" is one call to
/// <c>Advance</c>. Each test advances the clock, then sends the actor a message and waits for the
/// reply: the mailbox is in order, so every timer tick that was due has been handled by then.
/// </summary>
public sealed class DataRetentionActorTests(ITestOutputHelper output) : TestKit(output: output)
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Start = new(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly ConcurrentQueue<(string Job, DateTimeOffset Now, int Days)> _calls = new();
    private readonly SemaphoreSlim _called = new(0);
    private bool _throwingJobFails = true;

    private TestScheduler VirtualScheduler => (TestScheduler)Sys.Scheduler;

    protected override void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<ILogger<DataRetentionActor>>(NullLogger<DataRetentionActor>.Instance);

        // The first job throws, so a run that stopped at the failure would never reach the second.
        services.AddSingleton(new RetentionJob("failing", 5, now =>
        {
            Record("failing", now, 5);
            return _throwingJobFails ? throw new InvalidOperationException("the prune failed") : (0, 0);
        }));
        services.AddSingleton(new RetentionJob("counting", 14, now =>
        {
            Record("counting", now, 14);
            return (2, 0);
        }));
        services.AddSingleton(new RetentionJob("disabled", 0, now =>
        {
            Record("disabled", now, 0);
            return (0, 0);
        }));
    }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        builder.AddHocon(
            ConfigurationFactory.ParseString("akka.scheduler.implementation = \"Akka.TestKit.TestScheduler, Akka.TestKit\""),
            HoconAddMode.Prepend);
        builder.WithDataRetentionActor();
    }

    private void Record(string job, DateTimeOffset now, int days)
    {
        _calls.Enqueue((job, now, days));
        _called.Release();
    }

    private IActorRef Actor => ActorRegistry.Get<DataRetentionActorKey>();

    // Everything sent to the actor before this is handled when the reply arrives.
    private async Task SettleAsync()
        => await Actor.Ask<ActorIdentity>(new Identify(1), WaitTimeout, TestContext.Current.CancellationToken);

    private async Task WaitForCallsAsync(int count)
    {
        for (var i = 0; i < count; i++)
            Assert.True(await _called.WaitAsync(WaitTimeout, TestContext.Current.CancellationToken), $"only {i} of {count} prune calls arrived");
    }

    // The actor arms its timer in PreStart. A reply to Identify proves PreStart ran, so the
    // virtual clock never moves before the timer exists.
    private async Task AdvanceAsync(TimeSpan by)
    {
        await SettleAsync();
        VirtualScheduler.Advance(by);
    }

    private string[] Jobs() => _calls.Select(call => call.Job).ToArray();

    [Fact]
    public async Task Prunes_once_shortly_after_start_and_not_again_before_the_interval()
    {
        await SettleAsync();
        Assert.Empty(_calls);

        await AdvanceAsync(DataRetentionActor.InitialDelay);
        await WaitForCallsAsync(2);
        await SettleAsync();
        Assert.Equal(["failing", "counting"], Jobs());

        VirtualScheduler.Advance(DataRetentionActor.Interval - TimeSpan.FromSeconds(1));
        await SettleAsync();
        Assert.Equal(2, _calls.Count);
    }

    [Fact]
    public async Task Prunes_again_after_twelve_hours_with_the_current_time_and_each_jobs_own_days()
    {
        await AdvanceAsync(DataRetentionActor.InitialDelay);
        await WaitForCallsAsync(2);

        _time.Advance(DataRetentionActor.Interval);
        VirtualScheduler.Advance(DataRetentionActor.Interval);
        await WaitForCallsAsync(2);
        await SettleAsync();

        var calls = _calls.ToArray();
        Assert.Equal(["failing", "counting", "failing", "counting"], calls.Select(call => call.Job).ToArray());
        Assert.Equal(Start, calls[1].Now);
        Assert.Equal(Start + DataRetentionActor.Interval, calls[3].Now);
        Assert.Equal([5, 14, 5, 14], calls.Select(call => call.Days).ToArray());
    }

    [Fact]
    public async Task A_job_that_throws_does_not_stop_the_other_jobs_or_the_next_run()
    {
        await AdvanceAsync(DataRetentionActor.InitialDelay);
        await WaitForCallsAsync(2);

        // The actor survived the exception: it still answers and the next tick runs both jobs again.
        await SettleAsync();
        _throwingJobFails = false;
        VirtualScheduler.Advance(DataRetentionActor.Interval);
        await WaitForCallsAsync(2);
        await SettleAsync();

        Assert.Equal(["failing", "counting", "failing", "counting"], Jobs());
    }

    [Fact]
    public async Task A_job_with_zero_days_is_never_called()
    {
        await AdvanceAsync(DataRetentionActor.InitialDelay);
        await WaitForCallsAsync(2);
        await SettleAsync();

        Assert.DoesNotContain("disabled", Jobs());
    }
}

public sealed class RetentionJobRegistrationTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void The_daemon_registers_the_log_job_with_the_configured_days_and_the_logs_directory()
    {
        var paths = new NetclawPaths(_dir.Path);
        Directory.CreateDirectory(paths.LogsDirectory);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Retention:Logs:Days"] = "7" })
            .Build();

        var services = new ServiceCollection();
        var warnings = services.AddRetentionJobs(configuration, paths);
        using var provider = services.BuildServiceProvider();

        Assert.Empty(warnings);
        var job = Assert.Single(provider.GetServices<RetentionJob>());
        Assert.Equal("daemon and crash log", job.Name);
        Assert.Equal(7, job.Days);

        foreach (var day in new[] { "2026-01-01", "2026-05-17", "2026-05-18", "2026-05-19", "2026-05-20" })
            File.WriteAllText(Path.Combine(paths.LogsDirectory, $"daemon-{day}.log"), "x");

        Assert.Equal((1, 0), job.Prune(DateTimeOffset.Parse("2026-05-20T12:00:00Z")));
        Assert.False(File.Exists(Path.Combine(paths.LogsDirectory, "daemon-2026-01-01.log")));
    }

    [Fact]
    public void A_bad_value_registers_the_job_with_the_default_and_returns_a_warning()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Retention:Logs:Days"] = "abc" })
            .Build();

        var services = new ServiceCollection();
        var warnings = services.AddRetentionJobs(configuration, new NetclawPaths(_dir.Path));
        using var provider = services.BuildServiceProvider();

        Assert.Equal(14, provider.GetRequiredService<RetentionJob>().Days);
        Assert.Contains("Retention:Logs:Days", Assert.Single(warnings), StringComparison.Ordinal);
    }
}
