// -----------------------------------------------------------------------
// <copyright file="HeadlessUnseenCloseTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Cli.Daemon;
using Netclaw.Tests.Utilities;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests.Cli;

/// <summary>
/// Review fix N2 of PR 2 of <c>child-approval-events-and-parent-pause</c>. A new subscriber also gets the restart closes
/// of the session at its join. <c>netclaw chat -p</c> prints a close only for a prompt that its run showed, so a reader
/// never sees a withdrawal of a prompt that the run did not print.
/// </summary>
[Collection(HeadlessProcessStateCollection.Name)]
public sealed class HeadlessUnseenCloseTests : IAsyncDisposable
{
    private const string Session = "fake/session";
    private const string RunId = "0f3c9a7d5e2b41c8a6d4e1f2b3c4d5e6";

    private readonly DisposableTempDir _directory = new();
    private readonly FakeDaemonHubTransport _transport = new();
    private readonly TaskCompletionSource _promptSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DaemonClient _client;

    public HeadlessUnseenCloseTests()
    {
        _client = new("http://127.0.0.1:1", _transport, reconnectDelays: [TimeSpan.Zero]);
        _transport.VoidInvokeHook = (method, _, _) =>
        {
            if (method == "SendMessage")
                _promptSent.TrySetResult();
            return Task.CompletedTask;
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        _directory.Dispose();
    }

    // Planted bug: the channel prints every close that the session sends, also the restart close of a prompt that the run
    // never showed.
    [Fact]
    public async Task The_channel_prints_no_close_for_a_prompt_that_it_never_showed()
    {
        var run = await HeadlessRunHarness.RunAsync(
            _client, _directory, json: false, TestContext.Current.CancellationToken,
            drive: async _ =>
            {
                await _promptSent.Task;
                Push(SessionOutputDtoMapper.ToDto(new ToolInteractionClosed(
                    new ToolCallId($"{RunId}/subagent-approval/1"),
                    new ToolInteractionCloseReason.Withdrawn(WithdrawReason.DaemonRestarted))
                {
                    SessionId = new SessionId(Session)
                }));
                Push(SessionOutputDtoMapper.ToDto(new TextOutput("Done.") { SessionId = new SessionId(Session) }));
                Push(SessionOutputDtoMapper.ToDto(new TurnCompleted
                {
                    SessionId = new SessionId(Session),
                    TurnNumber = new TurnNumber(1),
                    Outcome = TurnOutcome.Completed,
                    OpenChildRunIds = []
                }));
            });

        var printed = run.Stdout + run.Stderr;
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("Done.", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("withdrawn", printed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[approval:closed]", printed, StringComparison.Ordinal);
    }

    private void Push(SessionOutputDto output) => _transport.PushOutput(output);
}
