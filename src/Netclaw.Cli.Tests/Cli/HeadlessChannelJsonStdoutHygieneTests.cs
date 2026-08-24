// -----------------------------------------------------------------------
// <copyright file="HeadlessChannelJsonStdoutHygieneTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Proves that <c>chat -p --json</c> keeps stdout pure JSON when the daemon
/// sends an output type this CLI build does not recognize (for example a
/// newer daemon streaming "tool_activity" to an older client). The client
/// mapper turns the unrecognized type into a diagnostic <c>ErrorOutput</c>
/// (<see cref="SessionOutputDtoMapper.FromDto"/>); this suite proves the
/// headless channel keeps that diagnostic off stdout, still surfaces it
/// (stderr + logger), and still emits a parseable JSON envelope.
/// </summary>
[Collection("Update verification")]
public sealed class HeadlessChannelJsonStdoutHygieneTests : IDisposable
{
    private static readonly TimeSpan[] ImmediateDelays = [TimeSpan.Zero];

    private readonly DisposableTempDir _dir = new();
    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalError = Console.Error;

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        _dir.Dispose();
    }

    [Fact]
    public async Task Unrecognized_output_type_stays_off_stdout_and_envelope_stays_parseable()
    {
        var transport = new FakeDaemonHubTransport();

        // Model a daemon that streams an output type this CLI build does
        // not know about (e.g. "tool_activity") ahead of the real reply.
        transport.VoidInvokeHook = (method, args, _) =>
        {
            if (method == "SendMessage")
            {
                var sessionId = (string)args[0]!;
                transport.PushOutput(new SessionOutputDto
                {
                    Type = "tool_activity",
                    SessionId = sessionId,
                    TimestampMs = 1
                });
                transport.PushOutput(new SessionOutputDto
                {
                    Type = "text",
                    SessionId = sessionId,
                    TimestampMs = 2,
                    Text = "hello"
                });
                transport.PushOutput(new SessionOutputDto
                {
                    Type = "turn_completed",
                    SessionId = sessionId,
                    TimestampMs = 3,
                    TurnNumber = new TurnNumber(1)
                });
            }

            return Task.CompletedTask;
        };

        await using var daemonClient = new DaemonClient(
            "http://localhost",
            transport,
            reconnectDelays: ImmediateDelays,
            rpcTimeout: TimeSpan.FromSeconds(5));

        var paths = new NetclawPaths(_dir.Path);
        var lifetime = new RecordingHostLifetime();
        var logger = new RecordingLogger<HeadlessChannel>();
        var options = new HeadlessOptions("hi") { JsonOutput = true };

        var channel = new HeadlessChannel(
            daemonClient, paths, lifetime, new FakeTimeProvider(), options, logger);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);

        await channel.StartAsync(TestContext.Current.CancellationToken);
        await lifetime.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var stdoutText = stdout.ToString();
        var stderrText = stderr.ToString();

        // Stdout carries exactly the JSON envelope — nothing else.
        var envelope = JsonSerializer.Deserialize<JsonElement>(stdoutText.Trim());
        Assert.Equal("hello", envelope.GetProperty("response").GetString());
        Assert.DoesNotContain("Unknown output type", stdoutText, StringComparison.Ordinal);
        Assert.DoesNotContain("[diagnostic]", stdoutText, StringComparison.Ordinal);
        Assert.DoesNotContain("[error]", stdoutText, StringComparison.Ordinal);

        // The diagnostic is not silently dropped — stderr and the logger both see it.
        Assert.Contains("Unknown output type from daemon: tool_activity", stderrText, StringComparison.Ordinal);
        Assert.Contains(logger.Messages, m => m.Contains("tool_activity", StringComparison.Ordinal));
        Assert.Contains(logger.Levels, l => l == LogLevel.Warning);
    }

    private sealed class RecordingHostLifetime : IHostApplicationLifetime
    {
        public TaskCompletionSource StopRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping { get; } = CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => StopRequested.TrySetResult();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
            Messages.Add(formatter(state, exception));
        }
    }
}
