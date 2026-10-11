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
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// <c>netclaw chat -p</c> reports a failed turn through its exit code, so a script can tell a
/// model that answered from one that could not be reached.
/// </summary>
public sealed class HeadlessChannelExitCodeTests : IAsyncDisposable
{
    private readonly DaemonClient _client = new("http://127.0.0.1:1", new FakeDaemonHubTransport());

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
