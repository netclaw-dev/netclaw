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
        var policy = new ToolAccessPolicy(
            config,
            new EffectivePolicyDefaults(
                DeploymentPosture.Personal,
                TrustAudience.Personal,
                ShellExecutionMode.HostAllowed,
                UsedStrictFallback: false));
        var executionContext = new ToolExecutionContext(
            new ToolRunScope
            {
                Session = new ToolSessionScope.Bound("test-session", null),
                Audience = TrustAudience.Personal,
                InlineOutputBudget = InlineOutputBudget.Default,
                InteractiveApproval = new InteractiveApprovalCapability.Unavailable()
            },
            ToolExecutionTimeout.Default);
        var decision = policy.AuthorizeInvocation(
            tool,
            executionContext,
            new Dictionary<string, object?>
            {
                ["contents"] = content,
                ["source_path"] = "/home/operator/reports/2026/Q3/quarterly-results-final.pdf",
                ["destination_directory"] = "/Finance/Board Pack/2026/Q3",
                ["access_token"] = "must-never-render",
                ["_rationale"] = "Upload the requested board report"
            });
        var approvalContext = Assert.IsType<ToolApprovalContext>(decision.ApprovalContext);
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
            bool startGenerating = false)
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
                    capturedVm = new TestChatViewModel(effectiveSeed, startGenerating);
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

        public TestChatViewModel(ToolInteractionRequest? seed, bool startGenerating = false)
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
        }

        protected override Task InitializeSessionAsync() => Task.CompletedTask;

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
            base.RequestAppShutdown();
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
