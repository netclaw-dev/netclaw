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
/// Termina page for the bootstrap personality interview.
/// Simplified chat interface: scrollable message history + text input.
/// Connected to the daemon via <see cref="BootstrapViewModel"/>.
/// </summary>
public sealed class BootstrapPage : ReactivePage<BootstrapViewModel>
{
    private StreamingTextNode _chatHistory = null!;
    private TextInputNode _promptInput = null!;

    // Track active streamed assistant text segment
    private int _nextSegmentId = 1;
    private SegmentId _assistantSegmentId;
    private readonly StringBuilder _assistantBuffer = new();

    private SegmentId NextSegmentId() => new(_nextSegmentId++);

    protected override void OnBound()
    {
        base.OnBound();

        _chatHistory = StreamingTextNode.Create().WithScrollbar();
        _promptInput = new TextInputNode()
            .WithPlaceholder("Type your reply...");

        _promptInput.Submitted
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Subscribe(text =>
            {
                _promptInput.Clear();
                _chatHistory.AppendLine("");
                _chatHistory.AppendLine($"You: {text}", Color.Cyan);
                _ = ViewModel.SubmitAsync(text);
            })
            .DisposeWith(Subscriptions);

        ViewModel.SessionOutput
            .Subscribe(HandleOutput)
            .DisposeWith(Subscriptions);

        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);

        ViewModel.Input.OfType<IInputEvent, PasteEvent>()
            .Subscribe(paste => _promptInput.HandlePaste(paste))
            .DisposeWith(Subscriptions);

        ViewModel.Input.OfType<IInputEvent, MouseScrollEvent>()
            .Subscribe(evt =>
            {
                if (evt.Delta > 0) _chatHistory.ScrollUp(3, 80);
                else if (evt.Delta < 0) _chatHistory.ScrollDown(3);
            })
            .DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
    {
        return Layouts.Vertical()
            .WithChild(
                new PanelNode()
                    .WithTitle("Netclaw Personality Setup")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Magenta)
                    .WithContent(BuildChatContent())
                    .Fill())
            .WithChild(
                new PanelNode()
                    .WithTitle("Reply")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Cyan)
                    .WithContent(_promptInput)
                    .Height(3))
            .WithChild(BuildStatusBar());
    }

    private ILayoutNode BuildChatContent()
    {
        return new DynamicLayoutNode(() =>
        {
            if (ViewModel.ErrorMessage.Value is not null)
            {
                return Layouts.Vertical()
                    .WithChild(new TextNode($"  Error: {ViewModel.ErrorMessage.Value}").WithForeground(Color.Red))
                    .WithChild(new TextNode(""))
                    .WithChild(new TextNode("  Press Ctrl+Q to exit and run setup manually later.").WithForeground(Color.BrightBlack));
            }

            if (ViewModel.IsConnecting.Value)
            {
                return Layouts.Vertical()
                    .WithChild(new TextNode($"  {ViewModel.StatusMessage.Value}").WithForeground(Color.Yellow));
            }

            return _chatHistory.Fill();
        });
    }

    private LayoutNode BuildStatusBar()
    {
        return Observable.CombineLatest(
                ViewModel.IsGenerating,
                ViewModel.IsComplete,
                ViewModel.StatusMessage,
                (isGenerating, isComplete, status) =>
                {
                    if (isComplete)
                        return (ILayoutNode)new TextNode(
                            " [Enter] Finish  [Ctrl+Q] Quit").WithForeground(Color.Green);

                    var keys = isGenerating
                        ? "[Ctrl+Q] Quit"
                        : "[Enter] Send  [PgUp/PgDn] Scroll  [Ctrl+Q] Quit";

                    return (ILayoutNode)new TextNode(
                        $" {keys}  |  {status}").WithForeground(Color.BrightBlack);
                })
            .AsLayout()
            .Height(1);
    }

    private void HandleKeyPress(KeyPressed key)
    {
        var keyInfo = key.KeyInfo;

        if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestAppShutdown();
            return;
        }

        if (keyInfo.Key == ConsoleKey.Escape)
        {
            ViewModel.RequestAppShutdown();
            return;
        }

        if (keyInfo.Key == ConsoleKey.Enter && ViewModel.IsComplete.Value)
        {
            ViewModel.RequestAppShutdown();
            return;
        }

        if (_chatHistory.HandleInput(keyInfo, viewportHeight: 20, viewportWidth: 80))
            return;

        _promptInput.HandleInput(keyInfo);
    }

    private void HandleOutput(SessionOutput output)
    {
        switch (output)
        {
            case TextOutput msg:
                if (_assistantSegmentId.Value != 0)
                {
                    FinalizeAssistantSegment();
                    _chatHistory.ScrollToBottom();
                    break;
                }
                _chatHistory.AppendLine($"Netclaw: {msg.Text}", Color.White);
                _chatHistory.AppendLine("");
                _chatHistory.ScrollToBottom();
                break;

            case TextDeltaOutput msg:
                EnsureAssistantSegment();
                _assistantBuffer.Append(msg.Delta);
                _chatHistory.Replace(_assistantSegmentId,
                    new StaticTextSegment($"Netclaw: {_assistantBuffer}", Color.White),
                    keepTracked: true);
                _chatHistory.ScrollToBottom();
                break;

            case ToolCallOutput msg:
                _chatHistory.AppendLine($"  [using {msg.ToolName}...]", Color.BrightBlack);
                break;

            case ErrorOutput msg:
                _chatHistory.AppendLine($"  [error] {msg.Message}", Color.Red);
                break;

            case TurnCompleted:
                FinalizeAssistantSegment();
                _chatHistory.ScrollToBottom();
                break;
        }

        ViewModel.RequestRedraw();
    }

    private void EnsureAssistantSegment()
    {
        if (_assistantSegmentId.Value != 0)
            return;

        _assistantBuffer.Clear();
        _assistantSegmentId = NextSegmentId();
        _chatHistory.AppendTracked(_assistantSegmentId,
            new StaticTextSegment("Netclaw: ", Color.White));
    }

    private void FinalizeAssistantSegment()
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
