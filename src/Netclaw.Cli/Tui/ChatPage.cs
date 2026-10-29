// -----------------------------------------------------------------------
// <copyright file="ChatPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using System.Text.RegularExpressions;
using Netclaw.Actors.Protocol;
using R3;
using Termina.Components.Streaming;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Termina page for the interactive chat UI (<c>netclaw chat</c>).
/// Layout: scrollable chat history (fill) + auto-sizing input panel + status bar.
/// </summary>
public sealed class ChatPage : ReactivePage<ChatViewModel>
{
    private readonly IAnsiTerminal _terminal;

    private StreamingTextNode _chatHistory = null!;
    private TextAreaNode _promptInput = null!;
    private SelectionListNode<string>? _approvalList;
    private DynamicLayoutNode? _inputContentNode;
    private readonly CompositeDisposable _inputSubs = [];

    private readonly TimeProvider _time;

    public ChatPage(IAnsiTerminal terminal, TimeProvider? timeProvider = null)
    {
        _terminal = terminal;
        _time = timeProvider ?? TimeProvider.System;
    }

    private int _nextSegmentId = 1;

    private SegmentId NextSegmentId() => new(_nextSegmentId++);

    // Pending tool lines by call id: a turn can run several tool calls in
    // parallel, and each one owns its own row and start time. Every animated
    // segment makes the history rebuild its whole buffer on each tick, so only
    // the oldest pending line animates (spinner and elapsed time); the others
    // show a static marker until the animated one goes away and the oldest of
    // them takes over. Every call reports its own elapsed time on its result line.
    private readonly record struct PendingTool(SegmentId Segment, long StartTimestamp, string Label, bool Animated);

    private readonly Dictionary<Netclaw.Tools.ToolCallId, PendingTool> _pendingTools = new();

    // Track active streamed assistant text segment
    private SegmentId _assistantSegmentId;
    private readonly StringBuilder _assistantBuffer = new();

    protected override void OnBound()
    {
        base.OnBound();

        _chatHistory = StreamingTextNode.Create().WithScrollbar();
        _promptInput = new TextAreaNode()
            .WithPlaceholder("Type a message...")
            .WithMaxHeight(8)
            .WithHistory(100);

        // Handle prompt submission
        _promptInput.Submitted
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Subscribe(text =>
            {
                // The input node has already cleared itself before raising Submitted, so on refusal put the
                // text back. A single line can be edited; a multi-line paste comes back as one paste block
                // (delete it or append to it) and Enter re-checks it.
                if (ChatViewModel.CheckMessageSize(text) is { } tooLarge)
                {
                    _promptInput.Text = text;
                    _promptInput.MoveCursorToEnd();
                    _chatHistory.AppendLine($"  [error] {tooLarge}", Color.Red);
                    ViewModel.StatusMessage.Value = tooLarge;
                    return;
                }

                _promptInput.Clear();

                // Render user message
                _chatHistory.AppendLine("");
                _chatHistory.AppendLine($"You: {text}", Color.Cyan);

                _ = ViewModel.SubmitAsync(text);
            })
            .DisposeWith(Subscriptions);

        // Subscribe to session output
        ViewModel.SessionOutput
            .Subscribe(HandleOutput)
            .DisposeWith(Subscriptions);

        // Route keyboard input
        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);

        // Route bracketed paste events directly to the text input node
        ViewModel.Input.OfType<IInputEvent, PasteEvent>()
            .Subscribe(paste =>
            {
                if (!ViewModel.HasPendingInteraction)
                    _promptInput.HandlePaste(paste);
            })
            .DisposeWith(Subscriptions);

        // Route mouse wheel scrolling to chat history
        ViewModel.Input.OfType<IInputEvent, MouseScrollEvent>()
            .Subscribe(HandleMouseScroll)
            .DisposeWith(Subscriptions);

        // Re-render on terminal resize. The outer Input panel's HeightAuto(max)
        // constraint is baked into LayoutNode fields at BuildLayout-time and
        // doesn't update on resize. InvalidateLayout() discards the cached tree
        // and rebuilds via BuildLayout() with the updated _terminal.Height, so
        // the panel cap follows the resize. The DynamicLayoutNode inside still
        // re-evaluates body width and the internal expanded-body cap. The
        // UiVersion bump is needed so the status bar CombineLatest triggers the
        // key-hints re-pick for narrow widths.
        ViewModel.Input.OfType<IInputEvent, ResizeEvent>()
            .Subscribe(_ =>
            {
                InvalidateLayout();
                ViewModel.NotifyViewportChanged();
            })
            .DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
    {
        return Layouts.Vertical()
            // Chat history panel (fills available space)
            .WithChild(
                new PanelNode()
                    .WithTitle("Netclaw Chat")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Gray)
                    .WithContent(_chatHistory.Fill())
                    .Fill())
            // Input panel (grows with content). The cap is generous — the
            // actual cap that prevents the chat-history pane from being
            // squeezed comes from the body-cap math inside BuildInputContent,
            // which derives an upper bound from _terminal.Height. We give the
            // panel itself a wide ceiling here so HeightAuto sizes to the
            // content we deliberately build, not the other way around.
            // The TextAreaNode in non-approval mode is independently capped
            // via WithMaxHeight(8).
            .WithChild(
                new PanelNode()
                    .WithTitle("Input")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Cyan)
                    .WithContent(BuildInputContent())
                    .HeightAuto(min: 3, max: Math.Max(8, _terminal.Height - 4)))
            // Status bar
            .WithChild(
                BuildStatusBar());
    }

    private ILayoutNode BuildInputContent()
    {
        _inputContentNode = new DynamicLayoutNode(() =>
        {
            _inputSubs.Clear();

            if (!ViewModel.HasPendingInteraction)
            {
                _promptInput.OnFocused();
                return _promptInput;
            }

            var items = ViewModel.ApprovalOptions.ToList();
            _approvalList = Layouts.SelectionList(items)
                .WithMode(SelectionMode.Single)
                .WithHighlightColors(Color.Black, Color.Yellow);

            _approvalList.SelectionConfirmed
                .Subscribe(selected =>
                {
                    if (selected.Count > 0)
                        _ = ViewModel.SubmitInteractionOptionAsync(selected[0]);
                })
                .DisposeWith(_inputSubs);

            _approvalList.OnFocused();

            // Read live terminal dimensions every render so the layout adapts
            // to resize. Panel content width = terminal width minus the panel
            // borders (2 cols) and a couple of safety cells.
            var panelContentWidth = Math.Max(20, _terminal.Width - 4);

            // How many rows can we afford for the body in expanded mode?
            // Chrome cost per render: summary(1) + hint(1) + N options + borders(2).
            // Cap the panel at half the terminal height so the chat history
            // pane stays visible. Floor of 8 keeps the math sane on tiny
            // terminals (the panel will still render best-effort).
            var optionCount = Math.Max(1, ViewModel.ApprovalOptions.Count);
            var panelMaxRows = Math.Max(8, _terminal.Height / 2);
            var chromeRows = 4 + optionCount;
            var expandedBodyMaxRows = Math.Max(1, panelMaxRows - chromeRows);

            var summary = new TextNode(ViewModel.GetApprovalSummary())
                .WithForeground(Color.Yellow);
            var hint = new TextNode(ViewModel.GetApprovalHint() ?? string.Empty)
                .WithForeground(Color.Gray);

            LayoutNode body = new TextNode(ViewModel.GetApprovalBody(panelContentWidth))
                .WithForeground(Color.White);

            // Collapsed body is forced single-line; expanded body wraps up to
            // the dynamic cap above. The full DisplayText is always available
            // in the chat history pane regardless.
            body = ViewModel.IsApprovalDetailVisible.Value
                ? body.HeightAuto(min: 1, max: expandedBodyMaxRows)
                : body.Height(1);

            return Layouts.Vertical()
                .WithChild(summary)
                .WithChild(body)
                .WithChild(hint)
                .WithChild(_approvalList);
        });

        ViewModel.UiVersion
            .Subscribe(_ => _inputContentNode.Invalidate())
            .DisposeWith(Subscriptions);

        return _inputContentNode;
    }

    private LayoutNode BuildStatusBar()
    {
        return Observable.CombineLatest(
                ViewModel.IsGenerating,
                ViewModel.IsInputEnabled,
                ViewModel.StatusMessage,
                ViewModel.UsageDisplay,
                ViewModel.IsApprovalDetailVisible,
                ViewModel.UiVersion,
                (isGenerating, isInputEnabled, status, usage, isApprovalDetailVisible, _) =>
                {
                    // Read terminal width live so a resize re-shortens the
                    // keys string. UiVersion is included above to make the
                    // CombineLatest re-emit on resize (ChatPage.OnBound bumps
                    // UiVersion when ResizeEvent fires).
                    var width = Math.Max(40, _terminal.Width);

                    var keys = BuildKeyHints(width, isGenerating, isApprovalDetailVisible);

                    var usagePart = usage is not null ? $"  |  {usage}" : "";
                    var modelPart = width >= 80 ? $"  |  {ViewModel.ModelId}" : "";
                    var text = $" {keys}  |  {status}{modelPart}{usagePart}";

                    var barColor = status switch
                    {
                        "Ready" => Color.Green,
                        _ when status.StartsWith("Connecting", StringComparison.Ordinal) => Color.Yellow,
                        _ when status.StartsWith("Reconnecting", StringComparison.Ordinal) => Color.Yellow,
                        _ when status.StartsWith("Connected", StringComparison.Ordinal) => Color.Green,
                        _ when status.StartsWith("Reconnected", StringComparison.Ordinal) => Color.Green,
                        _ when status.StartsWith("Disconnected", StringComparison.Ordinal) => Color.Red,
                        _ when status.StartsWith("Generating", StringComparison.Ordinal) => Color.Yellow,
                        _ when status.StartsWith("Connection failed", StringComparison.Ordinal) => Color.Red,
                        _ => Color.BrightBlack
                    };

                    return (ILayoutNode)new TextNode(text).WithForeground(barColor);
                })
            .AsLayout()
            .Height(1);
    }

    /// <summary>
    /// Builds the status-bar key hints, shortening on narrow terminals so
    /// the bar stays on one line. The most critical action stays visible at
    /// every width: Confirm/Cancel-equivalent for approval, Send for idle.
    /// </summary>
    private string BuildKeyHints(int width, bool isGenerating, bool isApprovalDetailVisible)
    {
        if (ViewModel.HasPendingInteraction)
        {
            var toggle = isApprovalDetailVisible ? "Collapse" : "View full";
            return width >= 100
                ? $"[Up/Down] Select  [Enter] Confirm  [Ctrl+O] {toggle}  [PgUp/PgDn] Scroll  [Ctrl+Q] Quit"
                : width >= 70
                    ? $"[↑↓] Select  [Enter] Confirm  [Ctrl+O] {toggle}  [Ctrl+Q] Quit"
                    : $"[↑↓] [Enter] OK  [^O] {toggle}  [^Q] Quit";
        }

        if (isGenerating)
            return "[Ctrl+Q] Quit";

        return width >= 100
            ? "[Enter] Send  [Ctrl+Enter] Newline  [PgUp/PgDn/Wheel] Scroll  [Ctrl+Q] Quit"
            : width >= 70
                ? "[Enter] Send  [Ctrl+Enter] Newline  [PgUp/PgDn] Scroll  [Ctrl+Q] Quit"
                : "[Enter] Send  [^Enter] NL  [^Q] Quit";
    }

    private void HandleKeyPress(KeyPressed key)
    {
        var keyInfo = key.KeyInfo;

        // Ctrl+Q always quits
        if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestAppShutdown();
            return;
        }

        // Escape is a cancel key everywhere — never quits. With an approval
        // prompt up it denies the pending interaction (#1757); while
        // generating it shows a status message (cancel not supported yet);
        // idle it's a no-op. Ctrl+Q is the only quit affordance.
        if (keyInfo.Key == ConsoleKey.Escape)
        {
            _ = ViewModel.CancelFromInputAsync();
            return;
        }

        // Ctrl+O toggles full-body view of a pending approval prompt.
        // Was originally Ctrl+V, but that's intercepted by the OS as "paste"
        // on Windows terminals (#1334).
        if (ViewModel.HasPendingInteraction
            && keyInfo.Key == ConsoleKey.O
            && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.ToggleApprovalDetail();
            return;
        }

        // PageUp/PageDown: scroll chat history
        if (_chatHistory.HandleInput(keyInfo, viewportHeight: 20, viewportWidth: 80))
            return;

        // Everything else goes to the text area
        if (ViewModel.HasPendingInteraction)
        {
            _approvalList?.HandleInput(keyInfo);
            return;
        }

        _promptInput.HandleInput(keyInfo);
    }

    private void HandleMouseScroll(MouseScrollEvent evt)
    {
        // +1 = wheel up, -1 = wheel down
        if (evt.Delta > 0)
            _chatHistory.ScrollUp(3, 80);
        else if (evt.Delta < 0)
            _chatHistory.ScrollDown(3);
    }

    private void HandleOutput(SessionOutput output)
    {
        switch (output)
        {
            case SessionJoined msg:
                var sessionState = msg.TurnCount > 0 ? "Resumed session." : "New session.";
                _chatHistory.AppendLine(
                    $"System: Session started. {(msg.Title is not null ? $"Title: {msg.Title}" : sessionState)}",
                    Color.BrightBlack);

                // Replay recent conversation history so the user has context
                if (msg.RecentMessages is { Count: > 0 })
                {
                    _chatHistory.AppendLine("");
                    _chatHistory.AppendLine("--- Previous conversation ---", Color.BrightBlack);
                    foreach (var historic in msg.RecentMessages)
                    {
                        var label = historic.Role == "user" ? "You" : "Netclaw";
                        _chatHistory.AppendLine($"{label}: {historic.Content}", Color.BrightBlack);
                    }
                    _chatHistory.AppendLine("--- End of history ---", Color.BrightBlack);
                }

                _chatHistory.AppendLine("");
                _chatHistory.ScrollToBottom();
                break;

            case TextOutput msg:
                // Remove pending tool lines if present
                RemovePendingToolLines();

                // When streaming deltas were already rendered, TextOutput is the
                // final full snapshot for compatibility. Finalize without duplicating.
                if (_assistantSegmentId.Value != 0)
                {
                    FinalizeAssistantSegmentIfNeeded();
                    _chatHistory.ScrollToBottom();
                    break;
                }

                _chatHistory.AppendLine($"Netclaw: {msg.Text}", Color.White);
                _chatHistory.AppendLine("");
                _chatHistory.ScrollToBottom();
                break;

            case TextDeltaOutput msg:
                RemovePendingToolLines();
                EnsureAssistantSegment();
                _assistantBuffer.Append(msg.Delta);
                _chatHistory.Replace(_assistantSegmentId,
                    new StaticTextSegment($"Netclaw: {_assistantBuffer}", Color.White),
                    keepTracked: true);
                _chatHistory.ScrollToBottom();
                break;

            case ThinkingOutput:
                // Hidden — reasoning output is too verbose for the chat view.
                // TODO: collapsible thinking sections when Termina supports it.
                break;

            case ToolCallOutput msg:
                // Streamed text that never got a closing TextOutput (whitespace-only
                // preamble, or a tool call straight after deltas) would otherwise
                // stay open and be joined by the tool line.
                FinalizeAssistantSegmentIfNeeded();
                RemovePendingToolLine(msg.CallId);
                var toolSegmentId = NextSegmentId();
                var pendingTool = new PendingTool(toolSegmentId, _time.GetTimestamp(),
                    $" {msg.ToolName}({TruncateArgs(msg.ArgumentsJson)})", Animated: _pendingTools.Count == 0);
                _pendingTools[msg.CallId] = pendingTool;
                _chatHistory.AppendTracked(toolSegmentId, PendingLine(pendingTool));
                break;

            case ToolResultOutput msg:
                // Replace spinner+timer with checkmark and final elapsed time
                if (_pendingTools.Remove(msg.CallId, out var pending))
                {
                    _chatHistory.Replace(pending.Segment,
                        new StaticTextSegment(
                            $"  \u2713 {msg.ToolName} \u2192 {Truncate(msg.Result, 80)} ({FormatElapsed(_time.GetElapsedTime(pending.StartTimestamp))})\n",
                            Color.Green),
                        keepTracked: false);
                    PromoteOldestPendingToolLine(pending);
                }

                break;

            case ErrorOutput msg:
                RemovePendingToolLines();
                _chatHistory.AppendLine($"  [error] {msg.Message}", Color.Red);
                break;

            case ToolInteractionRequest msg:
                RemovePendingToolLine(msg.CallId);
                _chatHistory.AppendLine(
                    msg.ToolName.IsMcp
                        ? "System: MCP tool approval required"
                        : $"System: Approval required for {msg.ToolName}",
                    Color.Yellow);
                if (msg.ToolName.IsMcp)
                    _chatHistory.AppendLine($"  Tool: {msg.ToolName}", Color.BrightBlack);
                _chatHistory.AppendLine(
                    msg.ToolName.IsMcp ? $"  Invocation: {msg.DisplayText}" : $"  {msg.DisplayText}",
                    Color.White);
                if (!string.IsNullOrEmpty(msg.Notes))
                {
                    foreach (var note in msg.Notes.Split('\n'))
                        _chatHistory.AppendLine($"  {note}", Color.BrightBlack);
                }

                if (!msg.ToolName.IsMcp && msg.Patterns.Count > 0)
                    _chatHistory.AppendLine($"  Patterns: {string.Join(", ", msg.Patterns)}", Color.BrightBlack);
                _chatHistory.AppendLine($"  Options: {string.Join(", ", msg.Options.Select(o => o.Label))}", Color.Yellow);
                _chatHistory.ScrollToBottom();
                break;

            case TurnCompleted:
                RemovePendingToolLines();
                FinalizeAssistantSegmentIfNeeded();
                _chatHistory.ScrollToBottom();
                break;

            case FileOutput msg:
                _chatHistory.AppendLine(
                    $"  [file] {msg.FileName} \u2192 {msg.FilePath}",
                    Color.Cyan);
                _chatHistory.ScrollToBottom();
                break;

            case CompactionOutput msg:
                _chatHistory.AppendLine(
                    $"  [compaction] {msg.MessagesBefore} \u2192 {msg.MessagesAfter} messages (keep={msg.KeepCountUsed}, {msg.PreCompactionInputTokens}/{msg.ContextWindowTokens} tokens)",
                    Color.Yellow);
                break;
        }

        ViewModel.RequestRedraw();
    }

    private ITextSegment PendingLine(PendingTool tool)
    {
        var label = new StaticTextSegment(tool.Label, Color.Yellow);
        var endOfLine = new StaticTextSegment("\n", TextStyle.Default);
        return tool.Animated
            ? new CompositeTextSegment(
                new SpinnerSegment(Termina.Components.Streaming.SpinnerStyle.Dots, Color.Yellow, intervalMs: 80),
                label,
                new ElapsedTimeSegment(Color.BrightBlack, timeProvider: _time, startTimestamp: tool.StartTimestamp),
                endOfLine)
            : new CompositeTextSegment(new StaticTextSegment("\u280f", Color.Yellow), label, endOfLine);
    }

    // The animated line is gone: the oldest line left starts animating, counting from its own start.
    private void PromoteOldestPendingToolLine(PendingTool gone)
    {
        if (!gone.Animated || _pendingTools.Count == 0)
            return;

        var oldest = _pendingTools.MinBy(p => p.Value.Segment.Value);
        var promoted = oldest.Value with { Animated = true };
        _pendingTools[oldest.Key] = promoted;
        _chatHistory.Replace(promoted.Segment, PendingLine(promoted), keepTracked: true);
    }

    private void RemovePendingToolLine(Netclaw.Tools.ToolCallId callId)
    {
        if (!_pendingTools.Remove(callId, out var pending))
            return;

        _chatHistory.Remove(pending.Segment);
        PromoteOldestPendingToolLine(pending);
    }

    private void RemovePendingToolLines()
    {
        foreach (var pending in _pendingTools.Values)
            _chatHistory.Remove(pending.Segment);
        _pendingTools.Clear();
    }

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    private static string TruncateArgs(string? json) =>
        json is null or "" ? "" : Truncate(json, 60);

    // Tool output (a web_fetch summary is a dozen lines) must stay on the one
    // row its tool line owns, so whitespace runs and newlines collapse. Only a
    // bounded prefix is scanned: the preview is far shorter than the result.
    private const int PreviewScanChars = 512;

    // The preview is one plain row: control sequences are stripped (a cut escape
    // would swallow the text after it) and the cut falls on a text element, by
    // display width, so a wide or surrogate-pair character is never split.
    private static string Truncate(string text, int maxLength)
    {
        var clipped = text.Length > PreviewScanChars;
        if (clipped)
        {
            // Clamp on a small window: parsing a whole multi-megabyte result costs far more than the preview.
            var window = text[..Math.Min(text.Length, PreviewScanChars + 64)];
            text = text[..DisplayWidth.ClampToTextElementBoundary(window, PreviewScanChars)];
        }

        // Whitespace first: sanitizing drops \f, \v and U+0085 without a space.
        text = DisplayWidth.SanitizeTerminalText(WhitespaceRun.Replace(text, " "));
        text = WhitespaceRun.Replace(text, " ").Trim();
        return !clipped && DisplayWidth.GetColumnCount(text) <= maxLength
            ? text
            : DisplayWidth.TruncateToColumns(text, maxLength - 3) + "...";
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalSeconds < 60
            ? $"{elapsed.TotalSeconds:F1}s"
            : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";

    private void EnsureAssistantSegment()
    {
        if (_assistantSegmentId.Value != 0)
            return;

        _assistantBuffer.Clear();
        _assistantSegmentId = NextSegmentId();
        _chatHistory.AppendTracked(_assistantSegmentId,
            new StaticTextSegment("Netclaw: ", Color.White));
    }

    private void FinalizeAssistantSegmentIfNeeded()
    {
        if (_assistantSegmentId.Value == 0)
            return;

        _chatHistory.Replace(_assistantSegmentId,
            new StaticTextSegment($"Netclaw: {_assistantBuffer}", Color.White),
            keepTracked: false);
        _assistantSegmentId = default;
        _assistantBuffer.Clear();
        _chatHistory.AppendLine("");
    }

}
