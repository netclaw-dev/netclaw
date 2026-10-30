// -----------------------------------------------------------------------
// <copyright file="HeadlessSessionLogTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// <c>netclaw chat -p</c> writes its own session log next to <c>daemon.log</c>. Tool arguments,
/// tool results, and model text reach it, so it must mask secrets. The eval scripts read its
/// multi-line records verbatim, so a record keeps its text as it is apart from redaction.
/// </summary>
[Collection(HeadlessProcessStateCollection.Name)]
public sealed class HeadlessSessionLogTests : IAsyncDisposable
{
    private static readonly SessionId Session = new("headless/test");
    private readonly FakeDaemonHubTransport _transport = new();
    private readonly DaemonClient _client;
    private readonly DisposableTempDir _dir = new();

    public HeadlessSessionLogTests() => _client = new("http://127.0.0.1:1", _transport);

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        _dir.Dispose();
    }

    [Fact]
    public void Records_are_redacted_and_a_split_secret_is_masked()
    {
        var channel = new HeadlessChannel(
            _client,
            new NetclawPaths(_dir.Path),
            new NoopLifetime(),
            TimeProvider.System,
            new HeadlessOptions("hello") { JsonOutput = true },
            NullLogger<HeadlessChannel>.Instance);
        var logPath = Path.Combine(_dir.Path, "session.log");

        using (var log = new StreamWriter(logPath))
        {
            // One token in two deltas: each part alone matches no pattern.
            channel.HandleOutput(new TextDeltaOutput("token ghp_ABCDEFGHIJ") { SessionId = Session }, log);
            channel.HandleOutput(new TextDeltaOutput("KLMNOPQRSTUVWXYZ0123456789 used") { SessionId = Session }, log);
            channel.HandleOutput(new ToolCallOutput
            {
                SessionId = Session,
                CallId = new ToolCallId("call-1"),
                ToolName = new ToolName("shell_execute"),
                ArgumentsJson = """{"command":"login","password":"hunter2"}""",
            }, log);
            channel.HandleOutput(new ToolResultOutput
            {
                SessionId = Session,
                CallId = new ToolCallId("call-1"),
                ToolName = new ToolName("shell_execute"),
                Result = "Exit code: 0\n[2026-01-01T00:00:00.0000000+00:00] TURN_COMPLETED: turn=9",
            }, log);
        }

        var lines = File.ReadAllLines(logPath);
        var text = string.Join('\n', lines);
        Assert.DoesNotContain("ghp_ABCDEFGHIJ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("KLMNOPQRSTUVWXYZ0123456789", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.Contains(lines, line => line.Contains("] ASSISTANT_DELTA: token ", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("] TOOL_CALL: shell_execute call_id=call-1 args=", StringComparison.Ordinal));
        // The eval scripts compare tool results verbatim.
        Assert.Contains("[2026-01-01T00:00:00.0000000+00:00] TURN_COMPLETED: turn=9", lines);
    }

    private sealed class NoopLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
