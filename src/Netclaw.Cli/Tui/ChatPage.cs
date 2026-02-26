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
/// Layout: scrollable chat history (fill) + fixed input panel (3 rows) + status bar.
/// </summary>
public sealed class ChatPage : ReactivePage<ChatViewModel>
{
    private StreamingTextNode _chatHistory = null!;
    private TextInputNode _promptInput = null!;

    private int _nextSegmentId = 1;

    private SegmentId NextSegmentId() => new(_nextSegmentId++);

    // Track the current "thinking" spinner segment so we can replace it
    private SegmentId _thinkingSegmentId;

    // Track active tool timer so we can read final elapsed on completion
    private ElapsedTimeSegment? _toolTimer;

    // Track active streamed assistant text segment
    private SegmentId _assistantSegmentId;
    private readonly StringBuilder _assistantBuffer = new();

    // Prompt history navigation
    private readonly List<string> _promptHistory = [];
    private int _historyIndex = -1;
    private string _historyDraft = string.Empty;

    protected override void OnBound()
    {
        base.OnBound();

        _chatHistory = StreamingTextNode.Create().WithScrollbar();
        _promptInput = new TextInputNode()
            .WithPlaceholder("Type a message...");

        // Handle prompt submission — Chunk coalesces rapid-fire submissions
        // (e.g., pasting multi-line text where each CRLF triggers Submitted)
        // into a single message. Normal typing produces one item per buffer window.
        _promptInput.Submitted
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Chunk(TimeSpan.FromMilliseconds(100))
            .Where(batch => batch.Length > 0)
            .Subscribe(batch =>
            {
                _promptInput.Clear();

                var combined = string.Join("\n", batch);

                RememberPrompt(combined);

                // Render user message
                _chatHistory.AppendLine("");
                _chatHistory.AppendLine($"You: {combined}", Color.Cyan);

                _ = ViewModel.SubmitAsync(combined);
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
            .Subscribe(paste => _promptInput.HandlePaste(paste))
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
            // Input panel (fixed 3 rows)
            .WithChild(
                new PanelNode()
                    .WithTitle("Input")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Cyan)
                    .WithContent(_promptInput)
                    .Height(3))
            // Status bar
            .WithChild(
                BuildStatusBar());
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
                    var keys = isGenerating
                        ? "[Ctrl+Q] Quit"
                        : "[Enter] Send  [Ctrl+Shift+V] Paste  [PgUp/PgDn/Wheel] Scroll  [Ctrl+Q] Quit";

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
                        _ when status.StartsWith("Generating") => Color.Yellow,
                        _ when status.StartsWith("Connection failed") => Color.Red,
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

        // Everything else goes to the text input
        if (keyInfo.Key == ConsoleKey.UpArrow)
        {
            NavigateHistoryUp();
            return;
        }

        if (keyInfo.Key == ConsoleKey.DownArrow)
        {
            NavigateHistoryDown();
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
                _chatHistory.AppendLine("");
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

            case TurnCompleted:
                RemoveThinkingSpinner();
                FinalizeAssistantSegmentIfNeeded();
                ViewModel.StatusMessage.Value = "Ready";
                _chatHistory.ScrollToBottom();
                break;

            case CompactionOutput msg:
                _chatHistory.AppendLine(
                    $"  [compaction] {msg.MessagesBefore} \u2192 {msg.MessagesAfter} messages",
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

    private void RememberPrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        if (_promptHistory.Count == 0 || !string.Equals(_promptHistory[^1], prompt, StringComparison.Ordinal))
            _promptHistory.Add(prompt);

        if (_promptHistory.Count > 100)
            _promptHistory.RemoveAt(0);

        _historyIndex = -1;
        _historyDraft = string.Empty;
    }

    private void NavigateHistoryUp()
    {
        if (_promptHistory.Count == 0)
            return;

        if (_historyIndex < 0)
        {
            _historyDraft = _promptInput.Text;
            _historyIndex = _promptHistory.Count - 1;
        }
        else if (_historyIndex > 0)
        {
            _historyIndex--;
        }

        _promptInput.Text = _promptHistory[_historyIndex];
    }

    private void NavigateHistoryDown()
    {
        if (_historyIndex < 0)
            return;

        if (_historyIndex < _promptHistory.Count - 1)
        {
            _historyIndex++;
            _promptInput.Text = _promptHistory[_historyIndex];
            return;
        }

        _historyIndex = -1;
        _promptInput.Text = _historyDraft;
    }
}
