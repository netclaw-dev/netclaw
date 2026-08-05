// -----------------------------------------------------------------------
// <copyright file="ChatPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
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

    public ChatPage(IAnsiTerminal terminal)
    {
        _terminal = terminal;
    }

    private int _nextSegmentId = 1;

    private SegmentId NextSegmentId() => new(_nextSegmentId++);

    // Track the current "thinking" spinner segment so we can replace it
    private SegmentId _thinkingSegmentId;

    // Track active tool timer so we can read final elapsed on completion
    private ElapsedTimeSegment? _toolTimer;

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
                ViewModel.UiVersion.Value++;
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
            // A pending approval prompt always takes precedence. IsGenerating
            // is cleared when a ToolInteractionRequest arrives, but the UI
            // thread can observe a stale value, so the prompt check must come
            // first — otherwise Escape gets swallowed by the generation-cancel
            // TODO branch and the user has to press it again.
            if (ViewModel.HasPendingInteraction)
            {
                // Fire-and-forget is safe: SubmitInteractionSelectionAsync
                // catches daemon exceptions internally and re-presents the
                // prompt (or shows a reconnect status) on failure.
                _ = ViewModel.DenyPendingInteractionAsync();
            }
            else if (ViewModel.IsGenerating.Value)
            {
                // TODO: cancel generation when supported (#1757 follow-up).
                // For now, tell the user instead of silently eating the key.
                ViewModel.StatusMessage.Value = "Cancel generation is not supported yet.";
                ViewModel.RequestRedraw();
            }
            // else: idle — no-op. The status bar advertises [Ctrl+Q] Quit.

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
                // Remove thinking spinner if present
                RemoveThinkingSpinner();

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
                RemoveThinkingSpinner();
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
                RemoveThinkingSpinner();
                var toolSegmentId = NextSegmentId();
                _thinkingSegmentId = toolSegmentId;
                _toolTimer = new ElapsedTimeSegment(Color.BrightBlack);
                _chatHistory.AppendTracked(toolSegmentId,
                    new CompositeTextSegment(
                        new SpinnerSegment(Termina.Components.Streaming.SpinnerStyle.Dots, Color.Yellow, intervalMs: 80),
                        new StaticTextSegment($" {msg.ToolName}({TruncateArgs(msg.ArgumentsJson)})",
                            Color.Yellow),
                        _toolTimer));
                break;

            case ToolResultOutput msg:
                // Replace spinner+timer with checkmark and final elapsed time
                if (_thinkingSegmentId.Value != 0)
                {
                    // Read elapsed from the timer segment before it gets disposed by Replace
                    var elapsed = _toolTimer is not null
                        ? $" ({FormatElapsed(_toolTimer.Elapsed)})"
                        : "";
                    _toolTimer = null;

                    _chatHistory.Replace(_thinkingSegmentId,
                        new StaticTextSegment(
                            $"  \u2713 {msg.ToolName} \u2192 {Truncate(msg.Result, 80)}{elapsed}",
                            Color.Green),
                        keepTracked: false);
                    _thinkingSegmentId = default;
                }

                break;

            case UsageOutput msg:
                // Prefer the daemon-reported context window (authoritative, auto-detected
                // from the provider); fall back to the DI-injected value when absent.
                var ctxWindow = msg.ContextWindowTokens > 0
                    ? msg.ContextWindowTokens
                    : ViewModel.ContextWindowTokens;
                var usagePercent = msg.InputTokens.HasValue && ctxWindow > 0
                    ? (double)msg.InputTokens.Value / ctxWindow
                    : (double?)null;
                var ctxPart = usagePercent.HasValue
                    ? $" ({usagePercent.Value:P0} ctx)"
                    : "";
                ViewModel.UsageDisplay.Value = $"in={msg.InputTokens ?? 0} out={msg.OutputTokens ?? 0}{ctxPart}";
                break;

            case ErrorOutput msg:
                RemoveThinkingSpinner();
                _chatHistory.AppendLine($"  [error] {msg.Message}", Color.Red);
                break;

            case ToolInteractionRequest msg:
                RemoveThinkingSpinner();
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
                if (!msg.ToolName.IsMcp && msg.Patterns.Count > 0)
                    _chatHistory.AppendLine($"  Patterns: {string.Join(", ", msg.Patterns)}", Color.BrightBlack);
                _chatHistory.AppendLine($"  Options: {string.Join(", ", msg.Options.Select(o => o.Label))}", Color.Yellow);
                _chatHistory.ScrollToBottom();
                break;

            case TurnCompleted:
                RemoveThinkingSpinner();
                FinalizeAssistantSegmentIfNeeded();
                ViewModel.StatusMessage.Value = "Ready";
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

    private void RemoveThinkingSpinner()
    {
        if (_thinkingSegmentId.Value != 0)
        {
            _chatHistory.Remove(_thinkingSegmentId);
            _thinkingSegmentId = default;
        }
    }

    private static string TruncateArgs(string? json) =>
        json is null or "" ? "" : Truncate(json, 60);

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength - 3), "...");

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
