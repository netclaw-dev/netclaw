// -----------------------------------------------------------------------
// <copyright file="HeadlessStopRuleMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tests.Cli;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.MutationTests;

/// <summary>
/// The stop rule of <c>netclaw chat -p</c>: a run ends on a failed turn, or on the first completed turn that
/// reports no open child run. Any other turn keeps the run waiting. A wrong rule gives one of two failures.
/// The run exits early and the child result is lost, or it waits for a turn that never comes.
/// <para>
/// Each test runs the real channel against a fake transport and reads the exit code and the stdout of the
/// run. A run that must stop but does not hits the fault ceiling, so the test fails. No test waits on a delay.
/// </para>
/// </summary>
[Collection(HeadlessConsoleCollection.Name)]
public sealed class HeadlessStopRuleMutationTests
{
    private const string Session = "fake/session";

    // A fault ceiling. A run that never stops fails here. No test orchestrates with this wait.
    private static readonly TimeSpan StopCeiling = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task An_open_run_keeps_the_wait_until_a_turn_reports_none()
    {
        var run = await RunAsync(
            Text("Started."), Turn(1, TurnOutcome.Completed, "run-1"),
            Text("Result."), Turn(2, TurnOutcome.Completed));

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("[subagent:waiting] 1 open: run-1", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("Result.", run.Stdout, StringComparison.Ordinal);
        Assert.Equal(1, run.StopCount);
    }

    [Fact]
    public async Task Two_open_runs_keep_the_wait_through_a_turn_that_reports_one()
    {
        var run = await RunAsync(
            Text("Started."), Turn(1, TurnOutcome.Completed, "run-a", "run-b"),
            Text("First."), Turn(2, TurnOutcome.Completed, "run-b"),
            Text("Second."), Turn(3, TurnOutcome.Completed));

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("[subagent:waiting] 2 open: run-a,run-b", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("[subagent:waiting] 1 open: run-b", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("Second.", run.Stdout, StringComparison.Ordinal);
        Assert.Equal(1, run.StopCount);
    }

    [Fact]
    public async Task A_completed_turn_with_no_open_run_ends_the_run_at_once()
    {
        var run = await RunAsync(Text("Done."), Turn(1, TurnOutcome.Completed));

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("[subagent:waiting]", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_turn_from_an_old_daemon_without_the_field_ends_the_run()
    {
        var run = await RunAsync(Text("Done."), Turn(1, TurnOutcome.Completed, open: null));

        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("[subagent:waiting]", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_turn_ends_the_run_even_when_a_run_is_open()
    {
        var run = await RunAsync(Turn(1, TurnOutcome.Failed, "run-1"));

        Assert.Equal(1, run.ExitCode);
        Assert.DoesNotContain("[subagent:waiting]", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_skipped_turn_with_an_open_run_does_not_end_the_run()
    {
        var run = await RunAsync(
            Turn(1, TurnOutcome.Skipped, "run-1"),
            Text("Result."), Turn(2, TurnOutcome.Completed));

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("[subagent:waiting] 1 open: run-1", run.Stdout, StringComparison.Ordinal);
        Assert.Contains("Result.", run.Stdout, StringComparison.Ordinal);
    }

    private static async Task<Run> RunAsync(params SessionOutputDto[] outputs)
    {
        var directory = Path.Combine(Path.GetTempPath(), "netclaw-headless-stop-rule-" + Guid.NewGuid().ToString("N"));
        var transport = new FakeDaemonHubTransport();
        transport.VoidInvokeHook = (method, _, _) =>
        {
            if (method == "SendMessage")
            {
                // The client pump delivers the outputs in order, so the channel sees every turn.
                foreach (var output in outputs)
                    transport.PushOutput(output);
            }

            return Task.CompletedTask;
        };
        await using var client = new DaemonClient("http://127.0.0.1:1", transport, reconnectDelays: [TimeSpan.Zero]);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new CountingLifetime(stopped);
        var channel = new HeadlessChannel(
            client,
            new NetclawPaths(directory),
            lifetime,
            TimeProvider.System,
            new HeadlessOptions("Do the task."),
            NullLogger<HeadlessChannel>.Instance);

        var stdout = new Capture();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        Environment.ExitCode = 0;
        Console.SetOut(stdout);
        Console.SetError(new Capture());
        try
        {
            await channel.StartAsync(CancellationToken.None);
            await stopped.Task.WaitAsync(StopCeiling);
            await channel.StopAsync(CancellationToken.None);
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            return new Run(stdout.Snapshot(), Environment.ExitCode, lifetime.StopCount);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.ExitCode = 0;
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static SessionOutputDto Text(string text)
        => SessionOutputDtoMapper.ToDto(new TextOutput(text) { SessionId = new SessionId(Session) });

    // The field is set on the DTO by hand. A null list models the earlier daemon, which sends no field.
    private static SessionOutputDto Turn(int number, TurnOutcome outcome, params string[]? open)
        => SessionOutputDtoMapper.ToDto(new TurnCompleted
        {
            SessionId = new SessionId(Session),
            TurnNumber = new TurnNumber(number),
            Outcome = outcome
        }) with
        {
            OpenChildRunIds = open is null ? null : [.. open]
        };

    private sealed record Run(string Stdout, int ExitCode, int StopCount);

    private sealed class CountingLifetime(TaskCompletionSource stopped) : IHostApplicationLifetime
    {
        private int _stopCount;

        public int StopCount => Volatile.Read(ref _stopCount);

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            Interlocked.Increment(ref _stopCount);
            stopped.TrySetResult();
        }
    }

    // Every write and the snapshot take one lock, so the test never reads a write in progress.
    private sealed class Capture : TextWriter
    {
        private readonly object _gate = new();
        private readonly StringBuilder _buffer = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_gate)
                _buffer.Append(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_gate)
                _buffer.Append(buffer, index, count);
        }

        public override void Write(string? value)
        {
            lock (_gate)
                _buffer.Append(value);
        }

        public string Snapshot()
        {
            lock (_gate)
                return _buffer.ToString();
        }
    }
}

/// <summary>Serializes the tests that redirect the console and set the process exit code.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeadlessConsoleCollection
{
    public const string Name = "Headless console";
}
