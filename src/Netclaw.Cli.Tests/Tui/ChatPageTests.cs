// -----------------------------------------------------------------------
// <copyright file="ChatPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Netclaw.Actors.Protocol;
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
            new TurnCompleted { SessionId = new SessionId("tui/test"), TurnNumber = new TurnNumber(1) });

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
            new TurnCompleted { SessionId = new SessionId("tui/test"), TurnNumber = new TurnNumber(1) });

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
            new TurnCompleted { SessionId = sid, TurnNumber = new TurnNumber(1) });

        var rows = ChatRows(screen);
        Assert.Single(rows, r => r == "Netclaw: Let me check");
        Assert.Single(rows, r => r == "Netclaw: Now the second");
        Assert.Single(rows, r => r == "Netclaw: Done");
        Assert.DoesNotContain(rows, r => r.Contains("✓", StringComparison.Ordinal) && r.Contains("Netclaw:", StringComparison.Ordinal));
        Assert.Equal(3, rows.Count(r => r.Contains("Netclaw:", StringComparison.Ordinal)));
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
                        "test-session")),
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

    private static ToolInteractionRequest BuildApproval(string displayText, string toolName = "shell_execute")
    {
        return new ToolInteractionRequest
        {
            SessionId = new SessionId("test-session"),
            TimestampMs = 0,
            Kind = "approval",
            CallId = new Netclaw.Tools.ToolCallId("test-call"),
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
            IReadOnlyList<SessionOutput>? replay = null)
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
                sp => new ChatPage(sp.GetRequiredService<IAnsiTerminal>()),
                _ =>
                {
                    var effectiveSeed = seed is null
                        ? null
                        : options is null
                            ? seed
                            : seed with { Options = options };
                    capturedVm = new TestChatViewModel(effectiveSeed, startGenerating, replay);
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

        public TestChatViewModel(ToolInteractionRequest? seed, bool startGenerating = false,
            IReadOnlyList<SessionOutput>? replay = null)
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
        }

        protected override Task InitializeSessionAsync()
        {
            foreach (var output in _replay ?? [])
                ProcessOutputForTesting(output);
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
            LifecycleEvents.Add($"submit:{selectedKey}");
            return Task.CompletedTask;
        }
    }
}
