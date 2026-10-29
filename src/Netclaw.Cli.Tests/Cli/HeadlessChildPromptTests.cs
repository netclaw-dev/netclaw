// -----------------------------------------------------------------------
// <copyright file="HeadlessChildPromptTests.cs" company="Petabridge, LLC">
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
/// PR 2 of the change <c>child-approval-events-and-parent-pause</c> (design D6, edge case "Headless resume while a
/// child waits", task 4.5). <c>netclaw chat -p</c> cannot answer a prompt, but the person who reads its output must see
/// why the run waits and when the wait ends (#2541). The channel prints the prompt and its close from
/// <see cref="ToolInteractionRequest"/> and <see cref="ToolInteractionClosed"/> only. No line names the child run or the
/// agent. The child call ID holds the run ID, so the channel must not print the call ID.
/// </summary>
[Collection(HeadlessProcessStateCollection.Name)]
public sealed class HeadlessChildPromptTests : IAsyncDisposable
{
    private const string Session = "fake/session";
    private const string RunId = "0f3c9a7d5e2b41c8a6d4e1f2b3c4d5e6";
    private const string Command = "gh pr view 2558";
    private const string CancelledText =
        "This approval request was withdrawn because the task that asked for it was cancelled. Nothing ran.";

    private readonly DisposableTempDir _directory = new();
    private readonly FakeDaemonHubTransport _transport = new();
    private readonly TaskCompletionSource _promptSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DaemonClient _client;

    public HeadlessChildPromptTests()
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

    // Planted bug: the channel prints nothing for a prompt and its close (today), so the run looks stalled; or it
    // prints the call ID, which names the run.
    [Fact]
    public async Task The_channel_prints_a_child_prompt_and_its_close_with_no_child_identity()
    {
        var callId = new ToolCallId($"{RunId}/subagent-approval/1");

        var run = await HeadlessRunHarness.RunAsync(
            _client, _directory, json: false, TestContext.Current.CancellationToken,
            drive: async _ =>
            {
                await _promptSent.Task;
                Push(SessionOutputDtoMapper.ToDto(new ToolInteractionRequest
                {
                    SessionId = new SessionId(Session),
                    Kind = "approval",
                    CallId = callId,
                    ToolName = new ToolName("shell_execute"),
                    DisplayText = Command,
                    RequesterSenderId = new SenderId("alice"),
                    Options =
                    [
                        new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                        new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
                    ]
                }));
                Push(SessionOutputDtoMapper.ToDto(
                    new ToolInteractionClosed(callId, new ToolInteractionCloseReason.Withdrawn(WithdrawReason.CancelledByParent)) { SessionId = new SessionId(Session) }));
                Push(SessionOutputDtoMapper.ToDto(new TextOutput("Done.") { SessionId = new SessionId(Session) }));
                Push(SessionOutputDtoMapper.ToDto(new TurnCompleted
                {
                    SessionId = new SessionId(Session),
                    TurnNumber = new TurnNumber(1),
                    Outcome = TurnOutcome.Completed,
                    OpenChildRunIds = []
                }));
            });

        // The design does not fix the stream of the two lines, so the spec reads both streams.
        var printed = run.Stdout + run.Stderr;
        Assert.Equal(0, run.ExitCode);
        Assert.Contains(Command, printed, StringComparison.Ordinal);
        Assert.Contains(CancelledText, printed, StringComparison.Ordinal);
        Assert.DoesNotContain(RunId, printed, StringComparison.Ordinal);
        Assert.DoesNotContain("[error]", printed, StringComparison.Ordinal);
    }

    // Planted bug: the channel prints the command but drops the notes, so the reader never sees the grant scope.
    [Fact]
    public async Task The_channel_prints_the_approval_notes_on_a_separate_note_line()
    {
        const string Note = "Approve repository covers git commands in this repo.";
        var callId = new ToolCallId("call-notes");

        var run = await HeadlessRunHarness.RunAsync(
            _client, _directory, json: false, TestContext.Current.CancellationToken,
            drive: async _ =>
            {
                await _promptSent.Task;
                Push(SessionOutputDtoMapper.ToDto(new ToolInteractionRequest
                {
                    SessionId = new SessionId(Session),
                    Kind = "approval",
                    CallId = callId,
                    ToolName = new ToolName("shell_execute"),
                    DisplayText = Command,
                    Notes = Note,
                    RequesterSenderId = new SenderId("alice"),
                    Options =
                    [
                        new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                        new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
                    ]
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

        var lines = run.Stdout.Split('\n', StringSplitOptions.TrimEntries);
        Assert.Contains($"[approval:waiting] shell_execute: {Command}", lines);
        Assert.Contains($"[approval:note] {Note}", lines);
    }

    private void Push(SessionOutputDto output) => _transport.PushOutput(output);
}
