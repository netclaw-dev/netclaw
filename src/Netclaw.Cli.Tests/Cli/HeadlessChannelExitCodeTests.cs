// -----------------------------------------------------------------------
// <copyright file="HeadlessChannelExitCodeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// <c>netclaw chat -p</c> reports a failed turn through its exit code, so a script can tell a
/// model that answered from one that could not be reached.
/// </summary>
public sealed class HeadlessChannelExitCodeTests : IAsyncDisposable
{
    private readonly FakeDaemonHubTransport _transport = new();
    private readonly DaemonClient _client;

    public HeadlessChannelExitCodeTests() => _client = new("http://127.0.0.1:1", _transport);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    [Fact]
    public void Failed_turn_after_an_error_exits_non_zero()
    {
        var channel = CreateChannel();

        channel.HandleOutput(new ErrorOutput
        {
            SessionId = new SessionId("headless/test"),
            Message = "LLM provider transport error: Connection refused (127.0.0.1:9).",
            Category = ErrorCategory.ProviderFailure
        }, null);
        channel.HandleOutput(Completed(TurnOutcome.Failed), null);

        Assert.Equal(1, channel.ExitCode);
    }

    [Theory]
    [InlineData(TurnOutcome.Completed)]
    [InlineData(TurnOutcome.Skipped)]
    public void Turn_that_did_not_fail_exits_zero(TurnOutcome outcome)
    {
        var channel = CreateChannel();

        channel.HandleOutput(Completed(outcome), null);

        Assert.Equal(0, channel.ExitCode);
    }

    [Fact]
    public async Task Over_limit_prompt_fails_before_connecting_or_creating_a_session()
    {
        using var directory = new DisposableTempDir();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new HeadlessChannel(
            _client,
            new NetclawPaths(directory.Path),
            new SignalingLifetime(stopped),
            TimeProvider.System,
            new HeadlessOptions(new string('a', 600_000)),
            NullLogger<HeadlessChannel>.Instance);
        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            await channel.StartAsync(TestContext.Current.CancellationToken);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Equal(1, Environment.ExitCode);
            Assert.Contains("Message is too large to send", stderr.ToString());
            Assert.Equal(0, _transport.StartAttempts);
            Assert.Equal(0, _transport.EnsureSessionCalls);
        }
        finally
        {
            Console.SetError(original);
            Environment.ExitCode = 0;
        }
    }

    private sealed class SignalingLifetime(TaskCompletionSource stopped) : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stopped.TrySetResult();
    }

    private static TurnCompleted Completed(TurnOutcome outcome) => new()
    {
        SessionId = new SessionId("headless/test"),
        TurnNumber = new TurnNumber(1),
        Outcome = outcome
    };

    private HeadlessChannel CreateChannel() => new(
        _client,
        new NetclawPaths(Path.Combine(Path.GetTempPath(), "netclaw-headless-exit-code-unused")),
        new NoopLifetime(),
        TimeProvider.System,
        new HeadlessOptions("hello"),
        NullLogger<HeadlessChannel>.Instance);

    private sealed class NoopLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
