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

namespace Netclaw.Cli.Tui;

/// <summary>
/// Termina page for the interactive chat UI (<c>netclaw chat</c>).
/// Layout: scrollable chat history (fill) + auto-sizing input panel + status bar.
/// </summary>
public sealed class ChatPage : ReactivePage<ChatViewModel>
{
    private StreamingTextNode _chatHistory = null!;
    private TextAreaNode _promptInput = null!;
    private SelectionListNode<string>? _approvalList;
    private DynamicLayoutNode? _inputContentNode;
    private readonly CompositeDisposable _inputSubs = new();

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
            // Input panel (grows with content, 3–10 rows)
            .WithChild(
                new PanelNode()
                    .WithTitle("Input")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Cyan)
                    .WithContent(BuildInputContent())
                    .HeightAuto(min: 3, max: 10))
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

            return Layouts.Vertical()
                .WithChild(new TextNode(ViewModel.GetApprovalPrompt()).WithForeground(Color.Yellow))
                .WithChild(new TextNode(ViewModel.GetApprovalHint() ?? string.Empty).WithForeground(Color.Gray))
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
                (isGenerating, isInputEnabled, status, usage) =>
                {
                    var keys = ViewModel.HasPendingInteraction
                        ? "[Up/Down] Select  [Enter] Confirm  [PgUp/PgDn/Wheel] Scroll  [Ctrl+Q] Quit"
                        : isGenerating
                        ? "[Ctrl+Q] Quit"
                        : "[Enter] Send  [Ctrl+Enter] Newline  [PgUp/PgDn/Wheel] Scroll  [Ctrl+Q] Quit";

                    var usagePart = usage is not null ? $"  |  {usage}" : "";
                    var text = $" {keys}  |  {status}  |  {ViewModel.ModelId}{usagePart}";

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

    private void HandleKeyPress(KeyPressed key)
    {
        var keyInfo = key.KeyInfo;

        // Ctrl+Q always quits
        if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestAppShutdown();
            return;
        }

        // Escape: cancel or quit
        if (keyInfo.Key == ConsoleKey.Escape)
        {
            if (ViewModel.IsGenerating.Value)
            {
                // TODO: cancel generation when supported
            }
            else
            {
                ViewModel.RequestAppShutdown();
            }

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
                // Compute context % from ViewModel's SessionConfig (known-good)
                // rather than msg.ContextWindowTokens which may be default(0)
                var ctxWindow = ViewModel.ContextWindowTokens;
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
                _chatHistory.AppendLine($"System: Approval required for {msg.ToolName}", Color.Yellow);
                _chatHistory.AppendLine($"  {msg.DisplayText}", Color.White);
                if (msg.Patterns.Count > 0)
                    _chatHistory.AppendLine($"  Patterns: {string.Join(", ", msg.Patterns)}", Color.BrightBlack);
                _chatHistory.AppendLine("  Choose Approve once, Approve for this chat, Approve always, or Deny below.", Color.Yellow);
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
