// -----------------------------------------------------------------------
// <copyright file="ChatPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Tools;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Headless TUI tests for <see cref="ChatPage"/> using Termina's
/// <see cref="VirtualTerminal"/> and <see cref="VirtualInputSource"/>.
/// These exercise the Input-panel layout for pending approval interactions
/// (issue #1132): the bug was that a long <c>shell_execute</c> body wrapped
/// over many lines and pushed the selection list and key hints past the
/// 10-row Input panel cap, leaving the user unable to see <c>[Enter] Confirm</c>.
/// </summary>
public sealed class ChatPageTests
{
    // A representative long body that reproduces the original report: a `cd`
    // with many path arguments from kevin/code/compiler plus several macOS
    // temp paths. Well over 400 chars, so the pre-fix code wrapped it onto
    // 5+ rows and pushed the selection list off-screen.
    private const string LongShellBody =
        "cd /Users/kevin/code/compiler/Diagnostics.pn compiler/Symbol.pn compiler/Binder " +
        "/private/var/folders/pj/ncqg4f1s58l87j6n_9xvnz9m0000gn/T/tmp.aa " +
        "/private/var/folders/pj/ncqg4f1s58l87j6n_9xvnz9m0000gn/T/tmp.bb " +
        "/private/var/folders/pj/ncqg4f1s58l87j6n_9xvnz9m0000gn/T/tmp.cc " +
        "/private/var/folders/pj/ncqg4f1s58l87j6n_9xvnz9m0000gn/T/tmp.dd " +
        "/private/var/folders/pj/ncqg4f1s58l87j6n_9xvnz9m0000gn/T/tmp.ee " +
        "SENTINEL_TAIL_MARKER";

    [Fact]
    public async Task LongApprovalBody_KeepsControlsVisible()
    {
        var (terminal, app, _) = CreateHeadlessApp(BuildApproval(LongShellBody), out var input);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var screen = terminal.ToString();

        // The selection-list options must be on-screen INSIDE the Input panel
        // (the bottom slice), not just somewhere in the chat history pane
        // above. Pre-fix bug: the wrapped body filled the panel and the list
        // rendered past the bottom border, leaving the user unable to pick.
        var inputAndStatus = BottomRows(screen, terminalWidth: 120, rowCount: 14);
        AssertOptionVisible(inputAndStatus, "Once", terminal);
        AssertOptionVisible(inputAndStatus, "This chat", terminal);
        AssertOptionVisible(inputAndStatus, "Deny", terminal);

        // The status-bar Enter hint MUST stay visible — that's the key the
        // user needs to confirm their choice, and was the specific control
        // hidden in the original #1132 screenshot.
        Assert.True(inputAndStatus.Contains("[Enter] Confirm", StringComparison.Ordinal),
            $"Expected '[Enter] Confirm' hint in status bar. Screen:\n{terminal}");
    }

    [Fact]
    public async Task CollapsedView_ShowsEllipsisAndCtrlOHint()
    {
        var (terminal, app, _) = CreateHeadlessApp(BuildApproval(LongShellBody), out var input);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var screen = terminal.ToString();

        // Collapsed body must be truncated with an ellipsis marker. The body
        // contains "SENTINEL_TAIL_MARKER" at the very end; in collapsed mode
        // it MUST NOT be visible in the Input panel because it falls past the
        // truncation point. (It IS expected to be visible up in the chat
        // history pane, which always logs the full DisplayText on arrival —
        // that's the security audit trail, not the user-action surface.)
        Assert.True(screen.Contains('…'),
            $"Expected ellipsis '…' in collapsed body. Screen:\n{terminal}");

        var inputAndStatus = BottomRows(screen, terminalWidth: 120, rowCount: 14);
        Assert.True(!inputAndStatus.Contains("SENTINEL_TAIL_MARKER", StringComparison.Ordinal),
            $"Expected SENTINEL_TAIL_MARKER to be truncated out of the Input panel. " +
            $"Bottom rows:\n{inputAndStatus}\nFull screen:\n{terminal}");

        // The user needs to know how to see the full body. Both the inline
        // hint in the Input panel AND the status-bar hint advertise Ctrl+O.
        Assert.True(screen.Contains("Ctrl+O", StringComparison.Ordinal),
            $"Expected 'Ctrl+O' affordance to be visible. Screen:\n{terminal}");
    }

    /// <summary>
    /// Returns the last <paramref name="rowCount"/> rows of a
    /// <see cref="VirtualTerminal.ToString"/> dump. Used to scope assertions
    /// to the Input panel + status bar (the bottom slice), distinct from the
    /// always-full chat history (the top slice).
    /// </summary>
    private static string BottomRows(string screen, int terminalWidth, int rowCount)
    {
        var lines = screen.Split('\n');
        if (lines.Length <= rowCount)
            return screen;
        return string.Join('\n', lines.AsEnumerable().TakeLast(rowCount));
    }

    private static void AssertOptionVisible(string inputAndStatus, string optionLabel, VirtualTerminal terminal)
    {
        Assert.True(inputAndStatus.Contains(optionLabel, StringComparison.Ordinal),
            $"Expected approval option '{optionLabel}' to render inside the Input panel " +
            $"(bottom slice of the terminal). Bottom rows:\n{inputAndStatus}\nFull screen:\n{terminal}");
    }

    [Fact]
    public async Task CtrlO_TogglesFullBodyAndKeepsControlsVisible()
    {
        var (terminal, app, vm) = CreateHeadlessApp(BuildApproval(LongShellBody), out var input);

        // Ctrl+O to expand, then quit. The toggle happens before the next
        // render, so by the time the app shuts down the terminal holds the
        // expanded frame.
        input.EnqueueKey(ConsoleKey.O, false, false, true);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var screen = terminal.ToString();

        // Controls remain visible even in expanded mode — that's the whole
        // point: Ctrl+O must not regress the original bug. Scope assertions
        // to the bottom of the screen so we're checking the Input panel,
        // not the chat history pane that always echoes the full body.
        var inputAndStatus = BottomRows(screen, terminalWidth: 120, rowCount: 14);
        AssertOptionVisible(inputAndStatus, "Once", terminal);
        AssertOptionVisible(inputAndStatus, "This chat", terminal);
        AssertOptionVisible(inputAndStatus, "Deny", terminal);
        Assert.True(inputAndStatus.Contains("[Enter] Confirm", StringComparison.Ordinal),
            $"Expected '[Enter] Confirm' still visible after expand. Screen:\n{terminal}");

        // The status-bar hint should now read "Collapse" instead of "View full".
        Assert.True(screen.Contains("Collapse", StringComparison.Ordinal),
            $"Expected status hint to flip to 'Collapse' after expand. Screen:\n{terminal}");

        Assert.True(vm.IsApprovalDetailVisible.Value);
    }

    [Fact]
    public async Task CtrlV_DoesNotToggleDetail()
    {
        // Ctrl+V was the original keybinding but is intercepted as "paste" on
        // Windows terminals (#1334). After remapping to Ctrl+O, Ctrl+V must
        // NOT expand the approval detail.
        var (terminal, app, vm) = CreateHeadlessApp(BuildApproval(LongShellBody), out var input);

        input.EnqueueKey(ConsoleKey.V, false, false, true);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.False(vm.IsApprovalDetailVisible.Value);
    }

    [Fact]
    public async Task Escape_WithPendingInteraction_DeniesInsteadOfQuitting()
    {
        // Regression for #1757: pressing Escape during an approval prompt used
        // to call RequestAppShutdown() and tear down the whole session. It must
        // now act as a deny on the pending interaction — the session stays
        // alive and the deny key is what gets submitted to the daemon.
        // The ordered event list proves the sequence: Escape submits deny,
        // and only the explicit Ctrl+Q afterwards shuts the app down.
        var (terminal, app, vm) = CreateHeadlessApp(BuildApproval(LongShellBody), out var input);

        input.EnqueueKey(ConsoleKey.Escape); // deny the approval
        input.EnqueueKey(ConsoleKey.Q, false, false, true); // then quit cleanly

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(new[] { "deny:called", "submit:deny", "shutdown" }, vm.LifecycleEvents);
    }

    [Fact]
    public async Task Escape_WithPendingInteractionAndStaleGenerating_Denies()
    {
        // The reorder fix: a pending approval prompt must win over the
        // generation-cancel branch even when IsGenerating still reads true
        // (stale flag race — the daemon output handler clears it on arrival,
        // but the UI thread can observe the pre-clear value). Pre-fix, Escape
        // took the IsGenerating arm, swallowed the key, and only quit after a
        // second Escape; the prompt was never denied.
        var (terminal, app, vm) = CreateHeadlessApp(
            BuildApproval(LongShellBody), out var input, startGenerating: true);

        input.EnqueueKey(ConsoleKey.Escape); // deny the approval
        input.EnqueueKey(ConsoleKey.Q, false, false, true); // then quit cleanly

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(new[] { "deny:called", "submit:deny", "shutdown" }, vm.LifecycleEvents);
    }

    [Fact]
    public async Task Escape_WhileGenerating_ShowsStatusInsteadOfQuitting()
    {
        // Escape during generation can't cancel the turn yet (separate TODO),
        // but it must NOT quit the app silently and must NOT eat the key
        // without feedback — the user gets a status message instead.
        var (terminal, app, vm) = CreateHeadlessApp(seed: null, out var input, startGenerating: true);

        input.EnqueueKey(ConsoleKey.Escape); // no-op + status message
        input.EnqueueKey(ConsoleKey.Q, false, false, true); // quit cleanly

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(new[] { "shutdown" }, vm.LifecycleEvents);
        Assert.Contains("cancel", vm.StatusMessage.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Escape_WithNoPendingInteraction_IsNoOp_CtrlQQuits()
    {
        // Escape is a pure cancel key everywhere — idle it's a no-op, never
        // a quit. Ctrl+Q is the only quit affordance (#1757). Enqueue Escape
        // first, then Ctrl+Q; the lifecycle log proves Escape didn't shut
        // down and only the explicit Ctrl+Q did.
        var (terminal, app, vm) = CreateHeadlessApp(seed: null, out var input);

        input.EnqueueKey(ConsoleKey.Escape); // idle: no-op
        input.EnqueueKey(ConsoleKey.Q, false, false, true); // quit

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(new[] { "shutdown" }, vm.LifecycleEvents);
    }

    [Fact]
    public async Task DenyPendingInteraction_NoDenyOption_DoesNotSubmit()
    {
        // If an approval interaction somehow lacks a deny option, denying must
        // be a no-op rather than submitting an unrelated option — and Escape
        // must still not quit the session.
        var noDenyOptions = new[]
        {
            new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
            new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel)
        };
        var (terminal, app, vm) = CreateHeadlessApp(
            BuildApproval(LongShellBody), out var input, options: noDenyOptions);

        input.EnqueueKey(ConsoleKey.Escape); // attempt deny
        input.EnqueueKey(ConsoleKey.Q, false, false, true); // then quit cleanly

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Null(vm.LastSubmittedInteractionKey);
        // The "deny:called" marker proves Escape routed to the deny path even
        // though the interaction carried no deny option — the no-op branch
        // ran, and nothing bogus was submitted. Without the marker this test
        // would pass vacuously even if the page never called deny at all.
        Assert.Equal(new[] { "deny:called", "shutdown" }, vm.LifecycleEvents);
    }

    [Fact]
    public void NewInteraction_ResetsCollapsedState()
    {
        // This case operates directly on the ViewModel — the goal is to
        // verify that consecutive ToolInteractionRequest arrivals do not
        // preserve a previous expanded state, so each new approval starts
        // collapsed with controls visible by default.
        var vm = new TestChatViewModel(seed: null);
        vm.SeedPendingInteractionForTesting(BuildApproval("first body"));
        vm.ToggleApprovalDetail();
        Assert.True(vm.IsApprovalDetailVisible.Value);

        vm.SeedPendingInteractionForTesting(BuildApproval("second body"));
        Assert.False(vm.IsApprovalDetailVisible.Value);
    }

    // Shell grant-scope notes travel in Notes, beside the command and never inside it (#2746).
    // Planted bug: the approval body drops the notes, or folds them into the command text.
    [Fact]
    public void ApprovalBody_ShowsNotesAfterTheUnchangedCommand()
    {
        var vm = new TestChatViewModel(seed: null);
        vm.SeedPendingInteractionForTesting(BuildApproval("git push origin main") with
        {
            Notes = "Approve repository covers git commands in this repo."
        });
        vm.ToggleApprovalDetail();

        var body = vm.GetApprovalBody(maxLineWidth: 120);

        Assert.Equal("git push origin main", body.Split('\n')[0]);
        Assert.Contains("\n\nApprove repository covers git commands in this repo.", body, StringComparison.Ordinal);
    }

    // The chat history prints the notes on their own lines under the command.
    // Planted bug: the history line for an approval prints the command only.
    [Fact]
    public async Task ApprovalHistory_PrintsNotesOnTheirOwnLinesAfterTheCommand()
    {
        var (terminal, app, _) = CreateHeadlessApp(seed: null, out var input, replay:
        [
            BuildApproval("git push origin main") with { Notes = "NOTE_LINE_ONE\nNOTE_LINE_TWO" }
        ]);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var frame = terminal.ToString();
        var lines = frame.Split('\n');
        Assert.Contains(lines, line => line.Contains("  git push origin main", StringComparison.Ordinal)
                                       && !line.Contains("NOTE_LINE", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("  NOTE_LINE_ONE", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("  NOTE_LINE_TWO", StringComparison.Ordinal));
    }

    [Fact]
    public void ErrorOutput_EndsGenerationAndShowsRetryReadyState()
    {
        var vm = new TestChatViewModel(seed: null);
        vm.SeedPendingInteractionForTesting(BuildApproval("pending approval"));
        vm.IsGenerating.Value = true;
        vm.IsInputEnabled.Value = false;
        vm.StatusMessage.Value = "Generating...";

        vm.ProcessOutputForTesting(new ErrorOutput
        {
            SessionId = new SessionId("tui/test"),
            Message = "Provider rejected tools."
        });

        Assert.False(vm.IsGenerating.Value);
        Assert.True(vm.IsInputEnabled.Value);
        Assert.False(vm.HasPendingInteraction);
        Assert.Empty(vm.ApprovalOptions);
        Assert.Equal("Last request failed. Ready to retry.", vm.StatusMessage.Value);
    }

    // PR 2 of child-approval-events-and-parent-pause (design D6, task 4.5): the session closes a withdrawn prompt with
    // ToolInteractionClosed. The TUI finds the prompt by its call ID and removes it, wherever it is in the queue, and
    // keeps the other prompts.
    // Planted bug: the TUI ignores the closed output (today), so the withdrawn prompt stays at the head of the queue and
    // the next key answers a prompt that the session closed.
    [Fact]
    public void ClosedOutput_RemovesItsPromptAndKeepsTheOthers()
    {
        var vm = new TestChatViewModel(seed: null);
        vm.SeedPendingInteractionForTesting(BuildApproval("first body", callId: "run-1/subagent-approval/1"));
        vm.SeedPendingInteractionForTesting(BuildApproval("second body", callId: "run-1/subagent-approval/2"));
        vm.SeedPendingInteractionForTesting(BuildApproval("third body", callId: "run-2/subagent-approval/1"));

        vm.ProcessOutputForTesting(Closed("run-1/subagent-approval/2"));
        vm.ProcessOutputForTesting(Closed("run-1/subagent-approval/1"));

        Assert.Equal("run-2/subagent-approval/1", vm.CurrentInteraction?.CallId.Value);
        Assert.Equal(new[] { "Once", "This chat", "Deny" }, vm.ApprovalOptions);

        vm.ProcessOutputForTesting(Closed("run-2/subagent-approval/1"));

        Assert.False(vm.HasPendingInteraction);
        Assert.Empty(vm.ApprovalOptions);
    }

    // Headless typed-key proof: two child prompts arrive, the session closes the first, and the operator presses
    // Escape. The key denies the open prompt and never the closed one, and the Input panel shows the open prompt.
    // Planted bug: the TUI ignores the closed output (today), so Escape answers the withdrawn prompt.
    [Fact]
    public async Task ClosedOutput_TypedKeyAnswersTheOpenPromptAndNotTheClosedOne()
    {
        var (terminal, app, vm) = CreateHeadlessApp(seed: null, out var input, replay:
        [
            BuildApproval("echo SENTINEL_CLOSED_PROMPT", callId: "run-1/subagent-approval/1"),
            BuildApproval("echo SENTINEL_OPEN_PROMPT", callId: "run-1/subagent-approval/2"),
            Closed("run-1/subagent-approval/1")
        ]);

        input.EnqueueKey(ConsoleKey.Escape); // deny the open prompt
        input.EnqueueKey(ConsoleKey.Q, false, false, true); // then quit cleanly

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(new[] { "deny:called", "submit:deny", "shutdown" }, vm.LifecycleEvents);
        Assert.Equal("run-1/subagent-approval/2", vm.LastSubmittedInteractionCallId);
        var inputAndStatus = BottomRows(terminal.ToString(), terminalWidth: 120, rowCount: 14);
        Assert.DoesNotContain("SENTINEL_CLOSED_PROMPT", inputAndStatus, StringComparison.Ordinal);
    }

    // Review fix S4 of PR 2 (TUI part). A person answered the child prompt on another surface, so the session closes
    // it with the reason "answered". The TUI removes the prompt and its options, and a typed key does not answer the
    // closed prompt. The call ran, so no text on the screen says "withdrawn" or "Nothing ran". The TUI shows no close
    // text for either reason, so this spec does not look for "has an answer".
    // Planted bug: the TUI removes only withdrawn prompts, so Escape denies a prompt that has an answer; or it prints a
    // withdrawal text for every close.
    [Fact]
    public async Task AnsweredClose_RemovesThePromptAndShowsNoWithdrawalText()
    {
        var (terminal, app, vm) = CreateHeadlessApp(seed: null, out var input, replay:
        [
            ChildApproval("run-1/subagent-approval/1") with { DisplayText = "echo SENTINEL_ANSWERED_PROMPT" },
            new ToolInteractionClosed(new Netclaw.Tools.ToolCallId("run-1/subagent-approval/1"),
                new Netclaw.Actors.Sessions.ToolInteractionCloseReason.Answered())
            {
                SessionId = new SessionId("test-session")
            }
        ]);

        input.EnqueueKey(ConsoleKey.Escape); // no prompt is open, so Escape answers nothing
        input.EnqueueKey(ConsoleKey.Q, false, false, true); // then quit cleanly

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.False(vm.HasPendingInteraction);
        Assert.Empty(vm.ApprovalOptions);
        Assert.Null(vm.LastSubmittedInteractionCallId);
        Assert.Equal(new[] { "shutdown" }, vm.LifecycleEvents);
        var screen = terminal.ToString();
        Assert.DoesNotContain("SENTINEL_ANSWERED_PROMPT", BottomRows(screen, terminalWidth: 120, rowCount: 14),
            StringComparison.Ordinal);
        Assert.DoesNotContain("withdrawn", screen, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Nothing ran", screen, StringComparison.OrdinalIgnoreCase);
    }

    private static ToolInteractionClosed Closed(string callId)
        => new(new Netclaw.Tools.ToolCallId(callId),
            new Netclaw.Actors.Sessions.ToolInteractionCloseReason.Withdrawn(Netclaw.Actors.Sessions.WithdrawReason.CancelledByParent))
        {
            SessionId = new SessionId("test-session")
        };

    // child-approval-events-and-parent-pause, PR 1, task 1.10 (TUI part). A child consent prompt has the lifetime
    // "until closed". The parent turn can end or fail while the child waits. The TUI keeps the child prompt and drops
    // only the parent prompt, whose lifetime is the turn (#2523).
    // Planted bug: the view model clears every pending prompt at TurnCompleted and at ErrorOutput, as it does today.
    [Fact]
    public void ChildPrompt_SurvivesTurnCompletedAndErrorOutput_ParentPromptDoesNot()
    {
        var vm = new TestChatViewModel(seed: null);
        vm.ProcessOutputForTesting(ChildApproval("r-7f2/subagent-approval/1"));
        vm.ProcessOutputForTesting(BuildApproval("git push origin main") with
        {
            CallId = new Netclaw.Tools.ToolCallId("parent-call"),
            Lifetime = ToolInteractionLifetime.UntilTurnEnd
        });

        vm.ProcessOutputForTesting(new TurnCompleted
        {
            SessionId = new SessionId("tui/test"), TurnNumber = new TurnNumber(1), OpenChildRunIds = []
        });

        Assert.True(vm.HasPendingInteraction);
        Assert.Equal("r-7f2/subagent-approval/1", vm.CurrentInteraction!.CallId.Value);
        Assert.NotEmpty(vm.ApprovalOptions);

        vm.ProcessOutputForTesting(new ErrorOutput { SessionId = new SessionId("tui/test"), Message = "Provider failed." });

        Assert.True(vm.HasPendingInteraction);
        Assert.Equal("r-7f2/subagent-approval/1", vm.CurrentInteraction!.CallId.Value);
        Assert.NotEmpty(vm.ApprovalOptions);
    }

    // Task 1.11, headless typed-key coverage for #2523. The parent turn completes and then a later parent turn fails
    // while a child prompt waits. The operator presses Enter on the prompt. The TUI submits "approve once" for the
    // child call ID, which is the answer that resumes the child.
    // Planted bug: the TUI drops the child prompt at the turn end, so Enter has no prompt to answer.
    [Fact]
    public async Task ChildPrompt_AfterTurnCompletedAndErrorOutput_EnterAnswersTheChild()
    {
        var (_, app, vm) = CreateHeadlessApp(null, out var input, replay:
        [
            ChildApproval("r-7f2/subagent-approval/1"),
            new TurnCompleted { SessionId = new SessionId("tui/test"), TurnNumber = new TurnNumber(1), OpenChildRunIds = [] },
            new ErrorOutput { SessionId = new SessionId("tui/test"), Message = "Provider failed." }
        ]);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(new[] { $"submit:{ApprovalOptionKeys.ApproveOnce}", "shutdown" }, vm.LifecycleEvents);
        Assert.Equal("r-7f2/subagent-approval/1", vm.LastSubmittedInteractionCallId);
    }

    private static ToolInteractionRequest ChildApproval(string callId)
        => BuildApproval("gh pr view 12") with
        {
            CallId = new Netclaw.Tools.ToolCallId(callId),
            Lifetime = ToolInteractionLifetime.UntilClosed
        };

    // What web_fetch hands back for a large page: a multi-line summary whose
    // preview carries converted markdown and, for raw fetches, HTML.
    private const string WebFetchSummary =
        "Fetched: http://example.test/weather\n" +
        "Title: Seattle Weather Forecast\n" +
        "Saved to: /data/web/example_test_weather.md (40,194 chars, 612 lines)\n" +
        "\n" +
        "Preview (first lines):\n" +
        "# Seattle, WA\n" +
        "Today: **71°F** and sunny\n" +
        "<!DOCTYPE html><html><body><table><tr><td>raw markup</td></tr></table></body></html>\n" +
        "... (597 more lines — use file_read or grep to search)";

    [Fact]
    public async Task MultiLineToolResult_RendersOnOneRowAndNextOutputStartsOnNewRow()
    {
        var screen = await RenderSessionOutputsAsync(
            ToolCall("web_fetch"),
            ToolResult("web_fetch", WebFetchSummary),
            new TextDeltaOutput("Done fetching") { SessionId = new SessionId("tui/test") },
            new TurnCompleted { SessionId = new SessionId("tui/test"), TurnNumber = new TurnNumber(1), OpenChildRunIds = [] });

        var rows = ChatRows(screen);
        var toolRow = Assert.Single(rows, r => r.Contains("✓ web_fetch", StringComparison.Ordinal));
        var replyRow = Assert.Single(rows, r => r.Contains("Netclaw: Done fetching", StringComparison.Ordinal));

        // The tool line used to carry the raw newlines of the result and was
        // never terminated, so the reply was appended to the end of it.
        Assert.NotEqual(toolRow, replyRow);
        Assert.Contains("Fetched: http://example.test/weather Title: Seattle Weather Forecast", toolRow, StringComparison.Ordinal);
        Assert.DoesNotContain(rows, r => r.StartsWith("Title:", StringComparison.Ordinal)
            || r.StartsWith("Preview (first lines):", StringComparison.Ordinal)
            || r.Contains("<table>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConsecutiveToolCalls_RenderOnSeparateRows()
    {
        var screen = await RenderSessionOutputsAsync(
            ToolCall("web_fetch", "call-1"),
            ToolResult("web_fetch", "Fetched: http://example.test/a", "call-1"),
            ToolCall("web_search", "call-2"));

        var rows = ChatRows(screen);
        var fetchRow = Assert.Single(rows, r => r.Contains("web_fetch", StringComparison.Ordinal));
        Assert.DoesNotContain("web_search", fetchRow, StringComparison.Ordinal);
        Assert.Single(rows, r => r.Contains("web_search", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToolCallPreviews_CollapseAllWhitespaceAndTrimBeforeTruncating()
    {
        // 100 leading spaces: truncating before collapsing would leave only
        // whitespace inside the 80-character budget.
        var result = new string(' ', 100) + "Fetched:\t\tok\r\n\r\n  done\n\n";
        var screen = await RenderSessionOutputsAsync(
            ToolCall("web_fetch", "call-1", "{\n  \"url\":\t\"http://example.test/a\",\n  \"mode\": \"raw\"\n}"),
            new SessionJoined { SessionId = new SessionId("tui/test") },
            ToolResult("web_fetch", result, "call-1"));

        var rows = ChatRows(screen);
        // Arguments stay on the tool row; nothing after them starts a row of its own.
        Assert.DoesNotContain(rows, r => r.StartsWith("\"url\"", StringComparison.Ordinal)
            || r.StartsWith("\"mode\"", StringComparison.Ordinal) || r.StartsWith('}'));
        // Single space between tokens, trimmed at both ends: "→ Fetched: ok done (0.0s)".
        Assert.Matches(@"✓ web_fetch → Fetched: ok done \(\d", Assert.Single(rows, r => r.Contains("✓ web_fetch", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task PendingToolCall_EndsItsRowBeforeLaterOutput()
    {
        var screen = await RenderSessionOutputsAsync(
            ToolCall("web_fetch", "call-1", "{\n  \"url\": \"http://example.test/a\"\n}"),
            new SessionJoined { SessionId = new SessionId("tui/test"), Title = "after the call" });

        var rows = ChatRows(screen);
        var callRow = Assert.Single(rows, r => r.Contains("web_fetch(", StringComparison.Ordinal));
        Assert.Contains("{ \"url\": \"http://example.test/a\" }", callRow, StringComparison.Ordinal);
        Assert.DoesNotContain("System:", callRow, StringComparison.Ordinal);
        Assert.Single(rows, r => r.StartsWith("System: Session started. Title: after the call", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhitespaceOnlyTextBeforeToolCall_DoesNotJoinTheToolLine()
    {
        var screen = await RenderSessionOutputsAsync(
            new TextDeltaOutput("\n\n") { SessionId = new SessionId("tui/test") },
            ToolCall("web_fetch"),
            ToolResult("web_fetch", "Fetched: ok"),
            new TextDeltaOutput("All done") { SessionId = new SessionId("tui/test") },
            new TurnCompleted { SessionId = new SessionId("tui/test"), TurnNumber = new TurnNumber(1), OpenChildRunIds = [] });

        var rows = ChatRows(screen);
        var toolRow = Assert.Single(rows, r => r.Contains("✓ web_fetch", StringComparison.Ordinal));
        Assert.DoesNotContain("Netclaw:", toolRow, StringComparison.Ordinal);
        Assert.Single(rows, r => r.StartsWith("Netclaw: All done", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TextToolText_RendersEachOnceOnItsOwnRow()
    {
        var sid = new SessionId("tui/test");
        var screen = await RenderSessionOutputsAsync(
            new TextDeltaOutput("Let me check") { SessionId = sid },
            new TextOutput("Let me check") { SessionId = sid },
            ToolCall("web_fetch", "call-1"),
            ToolResult("web_fetch", "Fetched: ok", "call-1"),
            // Streamed deltas with no closing TextOutput before the second tool call.
            new TextDeltaOutput("Now the second") { SessionId = sid },
            ToolCall("web_search", "call-2"),
            ToolResult("web_search", "Found: ok", "call-2"),
            new TextDeltaOutput("Done") { SessionId = sid },
            new TextOutput("Done") { SessionId = sid },
            new TurnCompleted { SessionId = sid, TurnNumber = new TurnNumber(1), OpenChildRunIds = [] });

        var rows = ChatRows(screen);
        Assert.Single(rows, r => r == "Netclaw: Let me check");
        Assert.Single(rows, r => r == "Netclaw: Now the second");
        Assert.Single(rows, r => r == "Netclaw: Done");
        Assert.DoesNotContain(rows, r => r.Contains("✓", StringComparison.Ordinal) && r.Contains("Netclaw:", StringComparison.Ordinal));
        Assert.Equal(3, rows.Count(r => r.Contains("Netclaw:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ParallelToolCalls_EachShowsItsOwnPendingLine()
    {
        var screen = await RenderSessionOutputsAsync(
            ToolCall("shell_execute", "call-1", "{\"command\":\"sleep 4; echo one\"}"),
            ToolCall("shell_execute", "call-2", "{\"command\":\"echo two\"}"));

        var rows = ChatRows(screen);
        var first = Assert.Single(rows, r => r.Contains("echo one", StringComparison.Ordinal));
        var second = Assert.Single(rows, r => r.Contains("echo two", StringComparison.Ordinal));
        Assert.Contains("shell_execute(", first, StringComparison.Ordinal);
        Assert.Contains("shell_execute(", second, StringComparison.Ordinal);
        Assert.True(Array.IndexOf(rows, first) < Array.IndexOf(rows, second));
    }

    [Fact]
    public async Task ParallelToolCalls_FirstResultReplacesOnlyItsOwnLine()
    {
        var screen = await RenderSessionOutputsAsync(
            ToolCall("shell_execute", "call-1", "{\"command\":\"echo one\"}"),
            ToolCall("shell_execute", "call-2", "{\"command\":\"echo two\"}"),
            ToolResult("shell_execute", "Exit code: 0 two", "call-2"));

        var rows = ChatRows(screen);
        Assert.Single(rows, r => r.Contains("echo one", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Contains("echo two", StringComparison.Ordinal));
        Assert.Single(rows, r => r.Contains("✓ shell_execute → Exit code: 0 two", StringComparison.Ordinal));
        Assert.True(
            Array.FindIndex(rows, r => r.Contains("echo one", StringComparison.Ordinal))
            < Array.FindIndex(rows, r => r.Contains("✓", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("call-1", "call-2", "call-3")]
    [InlineData("call-3", "call-2", "call-1")]
    [InlineData("call-2", "call-3", "call-1")]
    public async Task ParallelToolCalls_ResultsKeepCallOrderWhateverTheCompletionOrder(string done1, string done2, string done3)
    {
        var screen = await RenderSessionOutputsAsync(
            ToolCall("tool_a", "call-1", "{}"),
            ToolCall("tool_b", "call-2", "{}"),
            ToolCall("tool_c", "call-3", "{}"),
            ToolResult(ToolFor(done1), $"result of {done1}", done1),
            ToolResult(ToolFor(done2), $"result of {done2}", done2),
            ToolResult(ToolFor(done3), $"result of {done3}", done3));

        var rows = ChatRows(screen).Where(r => r.Contains("tool_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, rows.Length);
        Assert.StartsWith("  ✓ tool_a → result of call-1 (", rows[0], StringComparison.Ordinal);
        Assert.StartsWith("  ✓ tool_b → result of call-2 (", rows[1], StringComparison.Ordinal);
        Assert.StartsWith("  ✓ tool_c → result of call-3 (", rows[2], StringComparison.Ordinal);

        static string ToolFor(string callId) => callId switch
        {
            "call-1" => "tool_a",
            "call-2" => "tool_b",
            _ => "tool_c"
        };
    }

    [Fact]
    public async Task ParallelToolCalls_ErroringCallDoesNotDisturbItsSibling()
    {
        var failed = ToolResult("shell_execute", "Error: command not found", "call-1") with { FailureCode = "preflight_rejected" };
        var screen = await RenderSessionOutputsAsync(
            ToolCall("shell_execute", "call-1", "{\"command\":\"nope\"}"),
            ToolCall("shell_execute", "call-2", "{\"command\":\"echo two\"}"),
            failed,
            ToolResult("shell_execute", "Exit code: 0 two", "call-2"));

        var rows = ChatRows(screen);
        Assert.Single(rows, r => r.Contains("✓ shell_execute → Error: command not found", StringComparison.Ordinal));
        Assert.Single(rows, r => r.Contains("✓ shell_execute → Exit code: 0 two", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Contains("shell_execute(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParallelToolCalls_ApprovalPromptForOneCallLeavesTheOtherLineAlone()
    {
        var approval = BuildApproval("rm -rf build") with { CallId = new Netclaw.Tools.ToolCallId("call-2") };
        var screen = await RenderSessionOutputsAsync(
            ToolCall("file_read", "call-1", "{\"path\":\"a.txt\"}"),
            ToolCall("shell_execute", "call-2", "{\"command\":\"rm -rf build\"}"),
            approval,
            ToolResult("file_read", "contents of a", "call-1"));

        var rows = ChatRows(screen);
        Assert.Single(rows, r => r.Contains("✓ file_read → contents of a", StringComparison.Ordinal));
        // The approval prompt replaces the pending line of the call it gates.
        Assert.DoesNotContain(rows, r => r.Contains("shell_execute(", StringComparison.Ordinal));
        Assert.Single(rows, r => r.Contains("System: Approval required for shell_execute", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParallelToolCalls_ReplayedAcrossARejoinRendersEveryResult()
    {
        var screen = await RenderSessionOutputsAsync(
            ToolCall("tool_a", "call-1", "{}"),
            ToolCall("tool_b", "call-2", "{}"),
            new SessionJoined { SessionId = new SessionId("tui/test"), Title = "rejoined" },
            ToolResult("tool_b", "result of two", "call-2"),
            ToolResult("tool_a", "result of one", "call-1"),
            new TurnCompleted { SessionId = new SessionId("tui/test"), TurnNumber = new TurnNumber(1), OpenChildRunIds = [] });

        var rows = ChatRows(screen);
        var a = Array.FindIndex(rows, r => r.Contains("✓ tool_a → result of one", StringComparison.Ordinal));
        var b = Array.FindIndex(rows, r => r.Contains("✓ tool_b → result of two", StringComparison.Ordinal));
        Assert.True(a >= 0 && b > a, "both results render, in call order");
    }

    [Fact]
    public async Task ParallelToolCalls_NothingStaysPendingOnceTheLastCallFinishes()
    {
        var screen = await RenderSessionOutputsAsync(
            ToolCall("tool_a", "call-1", "{}"),
            ToolCall("tool_b", "call-2", "{}"),
            ToolResult("tool_a", "one", "call-1"),
            ToolResult("tool_b", "two", "call-2"));

        var rows = ChatRows(screen);
        Assert.Equal(2, rows.Count(r => r.Contains('✓', StringComparison.Ordinal)));
        Assert.DoesNotContain(rows, r => r.Contains("tool_a(", StringComparison.Ordinal)
            || r.Contains("tool_b(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParallelToolCalls_OnlyTheFirstPendingLineAnimatesASpinner()
    {
        var screen = await RenderSessionOutputsAsync(
            ToolCall("tool_a", "call-1", "{}"),
            ToolCall("tool_b", "call-2", "{}"),
            ToolCall("tool_c", "call-3", "{}"));

        var rows = ChatRows(screen);
        // Every animated segment rebuilds the whole history buffer on each tick.
        Assert.Matches("^[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏] tool_a\\(", Assert.Single(rows, r => r.Contains("tool_a(", StringComparison.Ordinal)));
        Assert.StartsWith("⠏ tool_b(", Assert.Single(rows, r => r.Contains("tool_b(", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.StartsWith("⠏ tool_c(", Assert.Single(rows, r => r.Contains("tool_c(", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParallelToolCalls_RepeatedCallIdReplacesItsEarlierPendingLine()
    {
        var screen = await RenderSessionOutputsAsync(
            ToolCall("tool_a", "dup", "{}"),
            ToolCall("tool_b", "dup", "{}"),
            ToolResult("tool_b", "result B", "dup"));

        var rows = ChatRows(screen);
        Assert.DoesNotContain(rows, r => r.Contains("tool_a", StringComparison.Ordinal));
        Assert.Single(rows, r => r.Contains("✓ tool_b → result B", StringComparison.Ordinal));
    }

    public static TheoryData<string> OutputsThatClearPendingToolLines() => new() { "turn", "error", "delta", "text" };

    [Theory]
    [MemberData(nameof(OutputsThatClearPendingToolLines))]
    public async Task ParallelToolCalls_AreClearedByTheOutputThatEndsThem(string kind)
    {
        var sid = new SessionId("tui/test");
        SessionOutput ender = kind switch
        {
            "turn" => new TurnCompleted { SessionId = sid, TurnNumber = new TurnNumber(1), OpenChildRunIds = [] },
            "error" => new ErrorOutput { SessionId = sid, Message = "boom" },
            "delta" => new TextDeltaOutput("moving on") { SessionId = sid },
            _ => new TextOutput("moving on") { SessionId = sid }
        };

        var screen = await RenderSessionOutputsAsync(
            ToolCall("tool_a", "call-1", "{}"),
            ToolCall("tool_b", "call-2", "{}"),
            ender);

        var rows = ChatRows(screen);
        Assert.DoesNotContain(rows, r => r.Contains("tool_a", StringComparison.Ordinal)
            || r.Contains("tool_b", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToolPreview_NeverSplitsASurrogatePair()
    {
        // 76 narrow characters leave one column of the 77-column budget; the
        // emoji needs two, so it must be dropped whole rather than cut in half.
        var result = new string('x', 76) + "\U0001F600 and more text after it";
        var screen = await RenderSessionOutputsAsync(
            ToolCall("web_fetch"),
            ToolResult("web_fetch", result));

        var row = Assert.Single(ChatRows(screen), r => r.Contains("✓ web_fetch", StringComparison.Ordinal));
        Assert.Contains("→ " + new string('x', 76) + "... (", row, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', row);
    }

    [Fact]
    public async Task ToolPreview_CutsWideCharactersByDisplayWidth()
    {
        // Each ideograph is two columns: 38 of them fill 76 of the 77 columns.
        var screen = await RenderSessionOutputsAsync(
            ToolCall("web_fetch"),
            ToolResult("web_fetch", new string('\u4E2D', 50)));

        var row = Assert.Single(ChatRows(screen), r => r.Contains("✓ web_fetch", StringComparison.Ordinal));
        Assert.Contains("→ " + new string('\u4E2D', 38) + "...", row, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('\u4E2D', 39), row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolPreview_StripsEscapeSequencesBeforeCutting()
    {
        // The old 77-character cut landed inside the second escape sequence and
        // left "ESC [" dangling, which swallowed the " (0.0s" after it.
        var result = "\u001b[31m" + new string('a', 70) + "\u001b[1;32m" + new string('b', 30) + "\u001b[0m";
        var screen = await RenderSessionOutputsAsync(
            ToolCall("web_fetch"),
            ToolResult("web_fetch", result));

        var row = Assert.Single(ChatRows(screen), r => r.Contains("✓ web_fetch", StringComparison.Ordinal));
        Assert.DoesNotContain('\u001b', row);
        Assert.Matches(@"→ a{70}b{7}\.\.\. \(\d+\.\ds\)$", row);
    }

    [Fact]
    public async Task ToolPreview_PrefixCutDoesNotSplitASurrogatePair()
    {
        // 509 spaces + "ab" put the 512-character scan limit between the halves of the emoji.
        var result = new string(' ', 509) + "ab\U0001F600 and more text after it";
        var screen = await RenderSessionOutputsAsync(
            ToolCall("web_fetch"),
            ToolResult("web_fetch", result));

        var row = Assert.Single(ChatRows(screen), r => r.Contains("✓ web_fetch", StringComparison.Ordinal));
        Assert.Contains("→ ab... (", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolPreview_PrefixCutDoesNotSplitAGraphemeCluster()
    {
        // The family emoji (man, woman, girl joined by ZWJ) straddles the 512-character scan limit.
        var family = "\U0001F468\u200D\U0001F469\u200D\U0001F467";
        var result = string.Concat(Enumerable.Repeat("\u001b[0m", 126)) + "x" + family + " tail text";
        var screen = await RenderSessionOutputsAsync(
            ToolCall("web_fetch"),
            ToolResult("web_fetch", result));

        var row = Assert.Single(ChatRows(screen), r => r.Contains("✓ web_fetch", StringComparison.Ordinal));
        Assert.Contains("→ x... (", row, StringComparison.Ordinal);
        Assert.DoesNotContain("\U0001F468", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolPreview_TreatsFormFeedVerticalTabAndNextLineAsSpaces()
    {
        var screen = await RenderSessionOutputsAsync(
            ToolCall("web_fetch"),
            ToolResult("web_fetch", "abc\fdef\vghi\u0085jkl"));

        var row = Assert.Single(ChatRows(screen), r => r.Contains("✓ web_fetch", StringComparison.Ordinal));
        Assert.Contains("→ abc def ghi jkl (", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParallelToolCalls_ResultLineReportsTheCallsOwnElapsedTime()
    {
        // The fake clock advances one second before each replayed output but the first.
        var screen = await RenderSessionOutputsAsync(new FakeTimeProvider(), TimeSpan.FromSeconds(1),
            ToolCall("tool_a", "call-1", "{}"),
            ToolCall("tool_b", "call-2", "{}"),
            ToolCall("tool_c", "call-3", "{}"),
            ToolResult("tool_c", "c", "call-3"),
            ToolResult("tool_a", "a", "call-1"),
            ToolResult("tool_b", "b", "call-2"));

        var rows = ChatRows(screen);
        Assert.Contains(rows, r => r.StartsWith("  ✓ tool_a → a (4.0s)", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.StartsWith("  ✓ tool_b → b (4.0s)", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.StartsWith("  ✓ tool_c → c (1.0s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ParallelToolCalls_NextPendingLineAnimatesWhenTheAnimatedOneFinishes()
    {
        var screen = await RenderSessionOutputsAsync(new FakeTimeProvider(), TimeSpan.FromSeconds(1),
            ToolCall("tool_a", "call-1", "{}"),
            ToolCall("tool_b", "call-2", "{}"),
            ToolCall("tool_c", "call-3", "{}"),
            ToolResult("tool_a", "a", "call-1"));

        var rows = ChatRows(screen);
        // tool_b started at 1s and takes over the spinner at 3s, when tool_a finishes: it counts from its own start.
        Assert.Matches("^[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏] tool_b\\(\\{\\}\\) 2s", Assert.Single(rows, r => r.Contains("tool_b(", StringComparison.Ordinal)));
        Assert.Equal("⠏ tool_c({})", Assert.Single(rows, r => r.Contains("tool_c(", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ParallelToolCalls_ApprovalPromptForTheAnimatedCallHandsTheSpinnerOn()
    {
        var approval = BuildApproval("rm -rf build") with { CallId = new Netclaw.Tools.ToolCallId("call-1") };
        var screen = await RenderSessionOutputsAsync(new FakeTimeProvider(), TimeSpan.FromSeconds(1),
            ToolCall("shell_execute", "call-1", "{\"command\":\"rm -rf build\"}"),
            ToolCall("tool_b", "call-2", "{}"),
            approval);

        var row = Assert.Single(ChatRows(screen), r => r.Contains("tool_b(", StringComparison.Ordinal));
        Assert.Matches("^[⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏] tool_b\\(\\{\\}\\) 1s", row);
    }

    [Fact]
    public async Task ToolPreview_ScansOnlyABoundedWindowOfAHugeResult()
    {
        var huge = new string('x', 5_000_000);
        var screen = await RenderSessionOutputsAsync(
            ToolCall("web_fetch"),
            ToolResult("web_fetch", huge));

        var row = Assert.Single(ChatRows(screen), r => r.Contains("✓ web_fetch", StringComparison.Ordinal));
        Assert.Contains("→ " + new string('x', 77) + "... (", row, StringComparison.Ordinal);
    }

    private static ToolCallOutput ToolCall(
        string toolName, string callId = "call-1", string argumentsJson = "{\"url\":\"http://example.test/weather\"}") => new()
    {
        SessionId = new SessionId("tui/test"),
        CallId = new Netclaw.Tools.ToolCallId(callId),
        ToolName = new Netclaw.Tools.ToolName(toolName),
        ArgumentsJson = argumentsJson
    };

    private static ToolResultOutput ToolResult(string toolName, string result, string callId = "call-1") => new()
    {
        SessionId = new SessionId("tui/test"),
        CallId = new Netclaw.Tools.ToolCallId(callId),
        ToolName = new Netclaw.Tools.ToolName(toolName),
        Result = result
    };

    private static async Task<string> RenderSessionOutputsAsync(FakeTimeProvider clock, TimeSpan stepPerOutput, params SessionOutput[] outputs)
    {
        var (terminal, app, _) = CreateHeadlessApp(null, out var input, replay: outputs, clock: clock, stepPerOutput: stepPerOutput);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);
        return terminal.ToString();
    }

    /// <summary>
    /// Runs the chat page headlessly with <paramref name="outputs"/> replayed
    /// as if the daemon had sent them, then quits and returns the final frame.
    /// </summary>
    private static async Task<string> RenderSessionOutputsAsync(params SessionOutput[] outputs)
    {
        var (terminal, app, _) = CreateHeadlessApp(null, out var input, replay: outputs);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);
        return terminal.ToString();
    }

    /// <summary>The text inside the chat history border, one entry per terminal row.</summary>
    private static string[] ChatRows(string screen) =>
        screen.Split('\n')
            .Where(l => l.StartsWith('│'))
            .Select(l => l.Trim('│').TrimEnd())
            .ToArray();

    [Fact]
    public async Task NarrowTerminal_PreservesCtrlOHint()
    {
        // 60-col terminal — narrower than the previous hard-coded 76-col
        // body budget. The pre-scaling code would wrap the body+hint to a
        // second line and body.Height(1) would clip the Ctrl+O suffix.
        // With width-aware sizing the hint must still be visible.
        var (terminal, app, _) = CreateHeadlessApp(
            BuildApproval(LongShellBody), out var input, width: 60, height: 30);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var screen = terminal.ToString();
        var bottom = BottomRows(screen, terminalWidth: 60, rowCount: 14);

        Assert.True(bottom.Contains("Ctrl+O", StringComparison.Ordinal)
                || bottom.Contains("^O", StringComparison.Ordinal),
            $"Expected Ctrl+O affordance to be visible on a 60-col terminal. " +
            $"Bottom rows:\n{bottom}\nFull screen:\n{terminal}");

        // Confirm options are still visible — the original #1132 invariant
        // must hold at narrow widths too.
        Assert.True(bottom.Contains("Once", StringComparison.Ordinal),
            $"Expected 'Once' option visible on narrow terminal. Screen:\n{terminal}");
        Assert.True(bottom.Contains("Deny", StringComparison.Ordinal),
            $"Expected 'Deny' option visible on narrow terminal. Screen:\n{terminal}");
    }

    [Fact]
    public async Task FiveOptionApproval_AllOptionsVisibleWhenExpanded()
    {
        // Production ToolAccessPolicy emits up to 5 options for shell_execute
        // (ApproveOnce, ApproveSession, ApproveAlways, ApproveEverywhere, Deny).
        // The previous 14-row hardcoded panel cap could clip the 5th option
        // when expanded body + chrome consumed the whole cap.
        var fiveOptions = new[]
        {
            new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
            new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
            new ToolInteractionOption(ApprovalOptionKeys.ApproveAlwaysKey, ApprovalOptionKeys.ApproveAlwaysLabel),
            new ToolInteractionOption(ApprovalOptionKeys.ApproveEverywhereKey, ApprovalOptionKeys.ApproveEverywhereLabel),
            new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
        };

        var (terminal, app, _) = CreateHeadlessApp(
            BuildApproval(LongShellBody), out var input,
            width: 120, height: 40, options: fiveOptions);

        // Expand first, then quit, so the assertion sees the harder layout.
        input.EnqueueKey(ConsoleKey.O, false, false, true);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var bottom = BottomRows(terminal.ToString(), terminalWidth: 120, rowCount: 20);

        AssertOptionVisible(bottom, ApprovalOptionKeys.ApproveOnceLabel, terminal);
        AssertOptionVisible(bottom, ApprovalOptionKeys.ApproveSessionLabel, terminal);
        AssertOptionVisible(bottom, ApprovalOptionKeys.ApproveAlwaysLabel, terminal);
        AssertOptionVisible(bottom, ApprovalOptionKeys.ApproveEverywhereLabel, terminal);
        AssertOptionVisible(bottom, ApprovalOptionKeys.DenyLabel, terminal);
    }

    [Fact]
    public async Task SmallTerminal_PanelDoesNotEatChatHistory()
    {
        // 16-row terminal: the panel max must scale down so chat history
        // (which uses .Fill()) still has at least a few visible rows.
        var (terminal, app, _) = CreateHeadlessApp(
            BuildApproval(LongShellBody), out var input, width: 100, height: 16);
        input.EnqueueKey(ConsoleKey.O, false, false, true); // expand to stress
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var screen = terminal.ToString();
        var lines = screen.Split('\n');

        // The chat history panel border must appear (it's the first line of
        // the rendered frame). If the input panel ate everything, we'd see
        // the Input panel border on row 0 and no chat panel at all.
        Assert.True(lines.Length > 4 && lines[0].Contains("Netclaw Chat", StringComparison.Ordinal),
            $"Expected chat history panel header on row 0 on a 16-row terminal. Screen:\n{terminal}");

        // Also confirm the selection list still renders inside the panel.
        var bottom = BottomRows(screen, terminalWidth: 100, rowCount: 12);
        Assert.True(bottom.Contains("Once", StringComparison.Ordinal),
            $"Expected 'Once' option visible on small terminal. Screen:\n{terminal}");
        Assert.True(bottom.Contains("Deny", StringComparison.Ordinal),
            $"Expected 'Deny' option visible on small terminal. Screen:\n{terminal}");
    }

    [Fact]
    public async Task Resize_RerendersStatusBarAndBody()
    {
        // Initial 120-col terminal renders the full-width key hints. After
        // resizing to 50 cols and triggering re-render, the shortened keys
        // string must replace the wide one.
        var (terminal, app, _) = CreateHeadlessApp(
            BuildApproval(LongShellBody), out var input, width: 120, height: 30);

        // Resize the underlying VirtualTerminal first so subsequent renders
        // read the new width; then push a ResizeEvent so the page bumps
        // UiVersion and the layout re-evaluates.
        terminal.Resize(50, 30);
        input.EnqueueResize(50, 30);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var bottom = BottomRows(terminal.ToString(), terminalWidth: 50, rowCount: 12);

        // The narrow status bar should use the short Ctrl-prefix form
        // (^O) or omit the longer scroll/quit hints.
        Assert.True(bottom.Contains("^O", StringComparison.Ordinal)
                || bottom.Contains("[Ctrl+O]", StringComparison.Ordinal),
            $"Expected resize to re-render with the narrow status bar. Bottom rows:\n{bottom}");
    }

    [Fact]
    public async Task LongApprovalBody_RenderedFrameSnapshot()
    {
        // Captures both the collapsed and expanded rendered frames for the
        // long-body case and writes them as ASCII snapshots under the test
        // project's __snapshots__ directory. Doubles as the visual artifact
        // for issue #1132: paste the .txt into a GH comment as a fenced
        // code block to show what the fixed UI looks like.
        var collapsed = await CaptureFrameAsync(expand: false);
        var expanded = await CaptureFrameAsync(expand: true);

        WriteSnapshot("chat-approval-collapsed.txt", collapsed);
        WriteSnapshot("chat-approval-expanded.txt", expanded);
    }

    [Fact]
    public async Task LargeMcpApproval_RenderedFrameSnapshot()
    {
        var content = string.Join('\n', Enumerable.Repeat("large memorizer payload that must not render", 2_000));
        var function = AIFunctionFactory.Create(
            (string contents, string source_path, string destination_directory, string access_token) => "uploaded",
            "upload",
            "Upload a file to Dropbox");
        var tool = new McpToolAdapter(function, "Dropbox", "upload");
        var config = new ToolConfig { ShellMode = ShellExecutionMode.HostAllowed };
        config.AudienceProfiles.Personal.AllowedMcpServers.Add("Dropbox");
        config.AudienceProfiles.Personal.ApprovalPolicy = new ToolApprovalConfig
        {
            McpServerDefaults = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["Dropbox"] = ToolApprovalMode.Approval
            }
        };
        var policy = new ToolAccessPolicy(new NetclawPaths(),
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false),
            new ShellCommandPolicy(),
            new ToolPathPolicy([]));
        var executionContext = new ToolExecutionContext(
            new ToolRunScope
            {
                Session = new ToolSessionScope.Bound(
                    "test-session",
                    SessionStoragePaths.CreateLegacy(
                        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "netclaw-chat-page-session")),
                        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "netclaw-chat-page-logs")),
                        "test-session", ManagedTemporaryLayout.InsideSessionStorage.Instance)),
                Audience = TrustAudience.Personal,
                InlineOutputBudget = InlineOutputBudget.Default,
                // A chat can answer the prompt. An unattended run would deny the call (D2).
                InteractiveApproval = TestToolExecutionContext.InteractiveApproval(true)
            },
            ToolExecutionTimeout.Default);
        var registry = new ToolRegistry();
        registry.Register(tool);
        var executor = new DispatchingToolExecutor(registry, policy);
        var consent = await Assert.ThrowsAsync<ToolApprovalRequiredException>(() => executor.ExecuteAsync(
            new FunctionCallContent("call-upload", tool.Name, new Dictionary<string, object?>
            {
                ["contents"] = content,
                ["source_path"] = "/home/operator/reports/2026/Q3/quarterly-results-final.pdf",
                ["destination_directory"] = "/Finance/Board Pack/2026/Q3",
                ["access_token"] = "must-never-render",
                ["_rationale"] = "Upload the requested board report"
            }),
            executionContext,
            TestContext.Current.CancellationToken));
        var approvalContext = consent.ApprovalContext;
        var approval = BuildApproval(approvalContext.DisplayText, approvalContext.ToolName) with
        {
            Patterns = approvalContext.Patterns,
            CandidateVerbs = approvalContext.CandidateVerbs,
            Options = approvalContext.Options
                .Select(option => new ToolInteractionOption(option.Key, option.Label))
                .ToArray()
        };

        var collapsed = await CaptureFrameAsync(approval, expand: false);
        var expanded = await CaptureFrameAsync(approval, expand: true);

        WriteSnapshot("chat-mcp-approval-large-collapsed.txt", collapsed);
        WriteSnapshot("chat-mcp-approval-large-expanded.txt", expanded);

        Assert.Contains("/Finance/Board Pack/2026/Q3", expanded);
        Assert.Contains("quarterly-results-final.pdf", expanded);
        Assert.Contains(content.Length.ToString(), expanded);
        Assert.Contains("chars, 2000 lines", expanded);
        Assert.Contains("MCP tool approval required", expanded);
        Assert.Contains("Invocation:", expanded);
        Assert.Contains(ApprovalOptionKeys.ApproveMcpToolLabel, expanded);
        Assert.DoesNotContain("Patterns:", expanded);
        Assert.DoesNotContain(ApprovalOptionKeys.ApproveEverywhereLabel, expanded);
        Assert.DoesNotContain("large memorizer payload", expanded);
        Assert.DoesNotContain("must-never-render", collapsed);
        Assert.DoesNotContain("must-never-render", expanded);
        Assert.DoesNotContain("_rationale", expanded);
        Assert.DoesNotContain("Upload the requested board report", expanded);
    }

    private async Task<string> CaptureFrameAsync(bool expand)
        => await CaptureFrameAsync(BuildApproval(LongShellBody), expand);

    private async Task<string> CaptureFrameAsync(ToolInteractionRequest approval, bool expand)
    {
        var (terminal, app, _) = CreateHeadlessApp(approval, out var input);
        if (expand)
            input.EnqueueKey(ConsoleKey.O, false, false, true);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        return terminal.ToString();
    }

    private static void WriteSnapshot(string filename, string content)
    {
        // Walk up from the test bin/ to the project's Tui directory.
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        DirectoryInfo? projectDir = null;
        while (current is not null)
        {
            if (current.GetFiles("Netclaw.Cli.Tests.csproj").Length > 0)
            {
                projectDir = current;
                break;
            }
            current = current.Parent;
        }

        // Fall back to bin/ if the source tree isn't reachable (e.g. when
        // running off a published test bundle).
        var targetDir = projectDir is not null
            ? Path.Combine(projectDir.FullName, "Tui", "__snapshots__")
            : Path.Combine(AppContext.BaseDirectory, "__snapshots__");
        Directory.CreateDirectory(targetDir);

        File.WriteAllText(Path.Combine(targetDir, filename), content);
    }

    private static ToolInteractionRequest BuildApproval(
        string displayText, string toolName = "shell_execute", string callId = "test-call")
    {
        return new ToolInteractionRequest
        {
            SessionId = new SessionId("test-session"),
            TimestampMs = 0,
            Kind = "approval",
            CallId = new Netclaw.Tools.ToolCallId(callId),
            ToolName = new Netclaw.Tools.ToolName(toolName),
            DisplayText = displayText,
            Patterns = ["cd"],
            CandidateVerbs = ["cd"],
            Options =
            [
                new ToolInteractionOption(ApprovalOptionKeys.ApproveOnceKey, ApprovalOptionKeys.ApproveOnceLabel),
                new ToolInteractionOption(ApprovalOptionKeys.ApproveSessionKey, ApprovalOptionKeys.ApproveSessionLabel),
                new ToolInteractionOption(ApprovalOptionKeys.DenyKey, ApprovalOptionKeys.DenyLabel)
            ]
        };
    }

    private static (VirtualTerminal Terminal, TerminaApplication App, TestChatViewModel Vm)
        CreateHeadlessApp(ToolInteractionRequest? seed, out VirtualInputSource input,
            int width = 120, int height = 40,
            IReadOnlyList<ToolInteractionOption>? options = null,
            bool startGenerating = false,
            IReadOnlyList<SessionOutput>? replay = null,
            FakeTimeProvider? clock = null,
            TimeSpan stepPerOutput = default)
    {
        var terminal = new VirtualTerminal(width, height);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        TestChatViewModel? capturedVm = null;

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/chat", builder =>
        {
            builder.RegisterRoute<ChatPage, ChatViewModel>(
                "/chat",
                sp => new ChatPage(sp.GetRequiredService<IAnsiTerminal>(), clock),
                _ =>
                {
                    var effectiveSeed = seed is null
                        ? null
                        : options is null
                            ? seed
                            : seed with { Options = options };
                    capturedVm = new TestChatViewModel(effectiveSeed, startGenerating, replay, clock, stepPerOutput);
                    return capturedVm;
                });
        });

        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();

        return (terminal, app, capturedVm!);
    }

    /// <summary>
    /// ChatViewModel subclass that bypasses daemon initialization for headless
    /// tests. The real <see cref="ChatViewModel.InitializeSessionAsync"/> opens
    /// a SignalR connection and subscribes to live daemon output; we override
    /// it to a no-op and stage a pre-baked <see cref="ToolInteractionRequest"/>
    /// via <c>SeedPendingInteractionForTesting</c> instead.
    /// </summary>
    private sealed class TestChatViewModel : ChatViewModel
    {
        private readonly ToolInteractionRequest? _seed;
        private readonly bool _startGenerating;
        private readonly IReadOnlyList<SessionOutput>? _replay;

        /// <summary>
        /// Set when the page routes Escape to app shutdown (the pre-#1757
        /// buggy path). Tests assert this stays false while an approval prompt
        /// is pending.
        /// </summary>
        public bool ShutdownRequested => LifecycleEvents.Contains("shutdown");

        /// <summary>
        /// Ordered record of ViewModel lifecycle events, in the order they
        /// happened. Lets tests prove that Escape submitted a deny BEFORE the
        /// explicit Ctrl+Q quit — the flag alone can't distinguish which key
        /// requested shutdown.
        /// </summary>
        public List<string> LifecycleEvents { get; } = new();

        /// <summary>
        /// Captures the option key submitted to the daemon. Headless tests
        /// cannot reach a live daemon, so the submission seam records the key
        /// the ViewModel resolved and simulates a successful response.
        /// </summary>
        public string? LastSubmittedInteractionKey { get; private set; }

        /// <summary>The call ID of the prompt that the submitted key answered.</summary>
        public string? LastSubmittedInteractionCallId { get; private set; }

        private readonly FakeTimeProvider? _clock;
        private readonly TimeSpan _stepPerOutput;

        public TestChatViewModel(ToolInteractionRequest? seed, bool startGenerating = false,
            IReadOnlyList<SessionOutput>? replay = null, FakeTimeProvider? clock = null, TimeSpan stepPerOutput = default)
            : base(
                // 127.0.0.1:1 is never dialed: InitializeSessionAsync is
                // overridden to no-op, so the underlying HubConnection stays
                // dormant. The DaemonClient constructor only validates that
                // the endpoint string is non-empty.
                new DaemonClient("http://127.0.0.1:1"),
                TimeProvider.System,
                new ModelCapabilities { ModelId = "test-model" },
                new ChatNavigationState(),
                new NetclawPaths())
        {
            _seed = seed;
            _startGenerating = startGenerating;
            _replay = replay;
            _clock = clock;
            _stepPerOutput = stepPerOutput;
        }

        protected override Task InitializeSessionAsync()
        {
            // The clock moves before each output but the first, so after the last output it stays put:
            // a later tick or redraw (a slow machine can run one) reads the same time the test expects.
            var first = true;
            foreach (var output in _replay ?? [])
            {
                if (!first)
                    _clock?.Advance(_stepPerOutput);
                first = false;
                ProcessOutputForTesting(output);
            }
            return Task.CompletedTask;
        }

        public override void OnActivated()
        {
            base.OnActivated();
            if (_seed is not null)
                SeedPendingInteractionForTesting(_seed);
            // Simulate the stale-flag race: IsGenerating can still read true
            // right after an interaction lands (the daemon output handler
            // clears it, but the UI thread can observe the pre-clear value).
            if (_startGenerating)
                IsGenerating.Value = true;
        }

        public override void RequestAppShutdown()
        {
            LifecycleEvents.Add("shutdown");
            // This fixture covers presentation. ChatAdmissionTests covers the real close protocol.
            Shutdown();
        }

        internal override Task DenyPendingInteractionAsync()
        {
            LifecycleEvents.Add("deny:called");
            return base.DenyPendingInteractionAsync();
        }

        protected override Task SubmitInteractionSelectionAsync(string selectedKey)
        {
            LastSubmittedInteractionKey = selectedKey;
            LastSubmittedInteractionCallId = CurrentInteraction?.CallId.Value;
            LifecycleEvents.Add($"submit:{selectedKey}");
            return Task.CompletedTask;
        }
    }
}
