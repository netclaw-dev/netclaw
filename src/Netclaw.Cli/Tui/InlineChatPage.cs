// -----------------------------------------------------------------------
// <copyright file="InlineChatPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Netclaw.Actors.Protocol;
using R3;
using Termina.Clipboard;
using Termina.Components.Streaming;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Primary-buffer chat page. Stable blocks enter terminal scrollback.
/// The live region contains activity, approvals, the composer, and status.
/// </summary>
public sealed class InlineChatPage : ReactivePage<ChatViewModel>
{
    private static readonly TimeSpan DoubleEscapeWindow = TimeSpan.FromMilliseconds(500);
    private const int MaximumReadableWidth = 120;

    private readonly IAnsiTerminal _terminal;
    private readonly IInlineOutput _inlineOutput;
    private readonly IClipboardService _clipboardService;
    private readonly TimeProvider _timeProvider;
    private readonly object _commitLock = new();
    private readonly CompositeDisposable _approvalSubscriptions = [];

    private TextAreaNode _promptInput = null!;
    private DynamicLayoutNode _liveRegion = null!;
    private SelectionListNode<string>? _approvalList;
    private ScrollableContainerNode? _approvalDetail;
    private ScrollableContainerNode? _assistantStream;
    private CopyableTextNode? _inspectorCopyNode;
    private ScrollableContainerNode? _inspectorDetail;
    private string? _approvalCallId;
    private string? _approvalDetailCallId;
    private string? _inspectorBlockKey;
    private string? _inspectorCopyStatus;
    private int _inspectorRenderWidth;
    private int _thinkingFrame;
    private bool _assistantTailPaused;
    private ChatPresentationState _state = ChatPresentationState.Empty;
    private Task _commitTail = Task.CompletedTask;
    private readonly List<ChatPresentationBlock> _deferredInspectorCommits = [];
    private readonly List<QueuedPromptDisplay> _queuedPromptDisplays = [];
    private readonly HashSet<string> _unseenAssistantEvents = new(StringComparer.Ordinal);
    private long? _lastEscapeTimestamp;
    private int _inspectorIndex;
    private bool _inspectorOpen;
    private TerminalCapabilityAvailability _modifiedEnterKeySupport =
        TerminalCapabilityAvailability.Unknown;

    public InlineChatPage(
        IAnsiTerminal terminal,
        IInlineOutput inlineOutput,
        IClipboardService clipboardService,
        TimeProvider timeProvider)
    {
        _terminal = terminal;
        _inlineOutput = inlineOutput;
        _clipboardService = clipboardService;
        _timeProvider = timeProvider;
        FocusPolicy = FocusPolicy.FirstFocusable;
    }

    protected override void OnBound()
    {
        base.OnBound();

        _promptInput = new TextAreaNode()
            .WithPlaceholder("Ask Netclaw...")
            .WithForeground(ChatVisualTheme.Text)
            .WithBackground(ChatVisualTheme.SurfaceStrong)
            .WithMaxHeight(8)
            .WithHistory(100)
            .WithNewlineModifier(ConsoleModifiers.Shift);
        _liveRegion = new DynamicLayoutNode(BuildLiveRegion);
        var thinkingTimer = new System.Timers.Timer(500) { AutoReset = true };
        thinkingTimer.Elapsed += (_, _) => Post(() =>
        {
            if (!ViewModel.IsGenerating.Value)
                return;

            _thinkingFrame = (_thinkingFrame + 1) % 3;
            _liveRegion.Invalidate();
        });
        thinkingTimer.Start();
        thinkingTimer.DisposeWith(Subscriptions);

        _promptInput.Submitted
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Subscribe(SubmitPrompt)
            .DisposeWith(Subscriptions);

        ViewModel.SessionOutput
            .Subscribe(output => Post(() => ApplyOutput(output)))
            .DisposeWith(Subscriptions);

        ViewModel.StatusMessage
            .Subscribe(_ => _liveRegion.Invalidate())
            .DisposeWith(Subscriptions);
        ViewModel.SessionIdDisplay
            .Subscribe(_ => _liveRegion.Invalidate())
            .DisposeWith(Subscriptions);
        ViewModel.IsApprovalDetailVisible
            .Subscribe(_ => _liveRegion.Invalidate())
            .DisposeWith(Subscriptions);
        ViewModel.IsGenerating
            .Subscribe(_ =>
            {
                if (ShowsComposer(_state))
                    Focus.SetFocus(_promptInput);
                _liveRegion.Invalidate();
            })
            .DisposeWith(Subscriptions);
        ViewModel.Input.OfType<IInputEvent, ResizeEvent>()
            .Subscribe(_ => _liveRegion.Invalidate())
            .DisposeWith(Subscriptions);
        ViewModel.Input.OfType<IInputEvent, MouseScrollEvent>()
            .Subscribe(HandleAssistantMouseScroll)
            .DisposeWith(Subscriptions);
        ViewModel.Input.OfType<IInputEvent, TerminalInputCapabilitiesChanged>()
            .Subscribe(capabilities =>
            {
                _modifiedEnterKeySupport = capabilities.Capabilities.ModifiedEnterKeySupport;
                _liveRegion.Invalidate();
            })
            .DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout() => _liveRegion;

    internal int ApprovalDetailScrollOffset => _approvalDetail?.ScrollOffset ?? 0;

    internal bool ApprovalDetailCanScrollDown => _approvalDetail?.CanScrollDown == true;

    internal int AssistantScrollOffset => _assistantStream?.ScrollOffset ?? 0;

    internal bool AssistantCanScrollDown => _assistantStream?.CanScrollDown == true;

    internal int UnseenAssistantEventCount => _unseenAssistantEvents.Count;

    public override bool HandlePageInput(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key == ConsoleKey.Q
            && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestAppShutdown();
            return true;
        }

        if (_inspectorOpen)
            return HandleInspectorInput(keyInfo);

        if (keyInfo.Key == ConsoleKey.Escape)
        {
            HandleEscape();
            return true;
        }

        if (_state.PendingApproval is not null && ViewModel.IsApprovalDetailVisible.Value)
        {
            if (keyInfo.Key == ConsoleKey.PageUp)
            {
                _approvalDetail?.PageUp();
                return true;
            }

            if (keyInfo.Key == ConsoleKey.PageDown)
            {
                _approvalDetail?.PageDown();
                return true;
            }
        }

        if (_assistantStream is not null && _state.PendingApproval is null)
        {
            if (keyInfo.Key == ConsoleKey.PageUp && _assistantStream.CanScrollUp)
            {
                _assistantStream.PageUp();
                _assistantTailPaused = true;
                _liveRegion.Invalidate();
                return true;
            }

            if (keyInfo.Key == ConsoleKey.PageDown && _assistantStream.CanScrollDown)
            {
                _assistantStream.PageDown();
                ResumeAssistantTailIfAtBottom();
                _liveRegion.Invalidate();
                return true;
            }

            if (keyInfo.Key == ConsoleKey.End && IsAssistantTailPaused())
            {
                _assistantStream.ScrollToBottom();
                _assistantTailPaused = false;
                _unseenAssistantEvents.Clear();
                _liveRegion.Invalidate();
                return true;
            }
        }

        if (_state.PendingApproval is not null
            && keyInfo.Key == ConsoleKey.O
            && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.ToggleApprovalDetail();
            return true;
        }

        if (_state.PendingApproval is null
            && keyInfo.Key == ConsoleKey.O
            && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control)
            && _state.Transcript.Count > 0)
        {
            OpenInspector();
            return true;
        }

        return base.HandlePageInput(keyInfo);
    }

    private void SubmitPrompt(string text)
    {
        _promptInput.Clear();
        _lastEscapeTimestamp = null;
        var messageId = ChatViewModel.CreateUserMessageId();
        if (ViewModel.IsGenerating.Value)
        {
            _queuedPromptDisplays.Add(new QueuedPromptDisplay(messageId, text, false));
            _liveRegion.Invalidate();
            _ = ViewModel.SubmitAsync(text, messageId);
            return;
        }

        ApplyReduction(ChatPresentationReducer.RecordUserPrompt(
            _state,
            text,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()));
        _ = ViewModel.SubmitAsync(text, messageId);
    }

    private void HandleAssistantMouseScroll(MouseScrollEvent mouseScroll)
    {
        if (_assistantStream is null || _inspectorOpen || _state.PendingApproval is not null)
            return;

        var scrollable = (IScrollable)_assistantStream;
        if (mouseScroll.Delta > 0)
        {
            var offset = _assistantStream.ScrollOffset;
            scrollable.ScrollUp(3);
            if (_assistantStream.ScrollOffset != offset)
                _assistantTailPaused = true;
        }
        else if (mouseScroll.Delta < 0)
            scrollable.ScrollDown(3);
        else
            return;

        ResumeAssistantTailIfAtBottom();
        _liveRegion.Invalidate();
    }

    private void ApplyMessageLifecycle(SessionOutput output)
    {
        switch (output)
        {
            case UserMessageQueuedOutput queued:
            {
                var index = _queuedPromptDisplays.FindIndex(prompt =>
                    string.Equals(prompt.MessageId, queued.MessageId, StringComparison.Ordinal));
                if (index >= 0)
                    _queuedPromptDisplays[index] = _queuedPromptDisplays[index] with { IsAccepted = true };
                break;
            }
            case UserMessagesPulledOutput pulled:
            {
                var pulledIds = pulled.Messages
                    .Select(message => message.MessageId)
                    .ToHashSet(StringComparer.Ordinal);
                _queuedPromptDisplays.RemoveAll(prompt => pulledIds.Contains(prompt.MessageId));
                break;
            }
        }
    }

    private void ApplyOutput(SessionOutput output)
    {
        TrackUnseenAssistantEvent(output);
        ApplyMessageLifecycle(output);
        ApplyReduction(ChatPresentationReducer.Reduce(_state, output));
    }

    private void TrackUnseenAssistantEvent(SessionOutput output)
    {
        if (output is TurnCompleted)
        {
            _assistantTailPaused = false;
            _unseenAssistantEvents.Clear();
            return;
        }

        if (!IsAssistantTailPaused())
        {
            _unseenAssistantEvents.Clear();
            return;
        }

        if (AssistantEventKey(output) is { } key)
            _unseenAssistantEvents.Add(key);
    }

    private string? AssistantEventKey(SessionOutput output) => output switch
    {
        TextDeltaOutput or TextOutput => $"text:{CurrentReplyPassageIndex()}",
        ThinkingDeltaOutput or ThinkingOutput => "thought",
        ToolCallOutput tool => $"tool:{tool.CallId.Value}",
        ToolActivityOutput activity => $"tool:{activity.CallId.Value}",
        ToolResultOutput result => $"tool:{result.CallId.Value}",
        SubAgentOutput agent => $"agent:{agent.RunId?.Value ?? agent.AgentName.Value}",
        UserMessagesPulledOutput pulled => $"pull:{pulled.BatchId}",
        _ => null
    };

    private int CurrentReplyPassageIndex()
    {
        if (_state.ReplyPassages.Count == 0)
            return 0;

        var passage = _state.ReplyPassages[^1];
        return passage.IsFinal ? passage.Index + 1 : passage.Index;
    }

    private bool IsAssistantTailPaused() => _assistantTailPaused;

    private void ResumeAssistantTailIfAtBottom()
    {
        if (_assistantStream?.CanScrollDown == false)
        {
            _assistantTailPaused = false;
            _unseenAssistantEvents.Clear();
        }
    }

    private void SynchronizeAssistantTailState()
    {
        if (_assistantStream is null)
            return;

        if (_assistantStream is { CanScrollDown: true, IsNearBottom: false })
        {
            _assistantTailPaused = true;
            return;
        }

        ResumeAssistantTailIfAtBottom();
    }

    private void ApplyReduction(ChatReduction reduction)
    {
        var hadComposer = ShowsComposer(_state);
        var hadApproval = _state.PendingApproval is not null;
        var priorApprovalCallId = _state.PendingApproval?.CallId.Value;
        _state = reduction.State;

        foreach (var effect in reduction.Effects)
        {
            switch (effect)
            {
                case ChatPresentationEffect.Commit commit:
                    if (!ShowsInPrimaryTranscript(commit.Block))
                        break;
                    if (_inspectorOpen)
                        _deferredInspectorCommits.Add(commit.Block);
                    else
                        QueueCommit(commit.Block);
                    break;
                case ChatPresentationEffect.SetStatus status:
                    ViewModel.StatusMessage.Value = status.Text;
                    break;
            }
        }

        var hasApproval = _state.PendingApproval is not null;
        var hasComposer = ShowsComposer(_state);
        var approvalHeadChanged = !string.Equals(
            priorApprovalCallId,
            _state.PendingApproval?.CallId.Value,
            StringComparison.Ordinal);
        if (hadApproval != hasApproval || hadComposer != hasComposer || approvalHeadChanged)
        {
            if (!hasApproval)
                ClearApprovalList();
            InvalidateLayout();
            if (hasApproval)
                Focus.SetFocus(EnsureApprovalList());
            else if (hasComposer)
                Focus.SetFocus(_promptInput);
            else
                Focus.ClearFocus();
        }
        else
        {
            _liveRegion.Invalidate();
        }
    }

    private void QueueCommit(ChatPresentationBlock block)
    {
        lock (_commitLock)
        {
            _commitTail = CommitAfterAsync(_commitTail, block);
        }
    }

    private async Task CommitAfterAsync(Task prior, ChatPresentationBlock block)
    {
        try
        {
            await prior.ConfigureAwait(false);
            await _inlineOutput.CommitAsync(
                ChatPresentationRenderer.BuildStableBlock(block, _terminal.Width),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Post(() =>
            {
                ViewModel.StatusMessage.Value = $"Output failed: {ex.Message}";
                _liveRegion.Invalidate();
            });
        }
    }

    private ILayoutNode BuildLiveRegion()
    {
        if (_inspectorOpen)
            return BuildInspector();

        var content = Layouts.Vertical();
        if (_state.Transcript.Count > 0)
            content.WithChild(Layouts.Empty().Height(1));
        var hasLiveReply = _state.ReplyPassages.Count > 0
                           || _state.Tools.Count > 0
                           || _state.SubAgents.Count > 0
                           || _state.AgentPulls.Count > 0
                           || !string.IsNullOrWhiteSpace(_state.ThoughtText);
        content.WithChild(BuildLiveReplyBlock());
        if (hasLiveReply)
            content.WithChild(Layouts.Empty().Height(1));
        content.WithChild(BuildSessionHeader());
        if (_queuedPromptDisplays.Count > 0)
        {
            content
                .WithChild(BuildQueueShelf())
                .WithChild(Layouts.Empty().Height(1));
        }

        if (_state.PendingApproval is not null)
            content.WithChild(BuildDecisionGate(_state.PendingApproval));
        else if (ShowsComposer(_state))
            content.WithChild(BuildComposer());

        content.WithChild(BuildStatusLine());
        return WithViewportMargin(content);
    }

    private ILayoutNode BuildInspector()
    {
        var block = _state.Transcript[_inspectorIndex];
        var showEventList = _terminal.Width >= 92;
        var eventListWidth = showEventList ? Math.Min(36, ReadableWidth() / 3) : 0;
        var detailWidth = Math.Max(1, ReadableWidth() - eventListWidth - (showEventList ? 4 : 2));
        if (_inspectorDetail is null
            || _inspectorBlockKey != block.Key
            || _inspectorRenderWidth != detailWidth)
        {
            _inspectorBlockKey = block.Key;
            _inspectorRenderWidth = detailWidth;
            _inspectorDetail ??= new ScrollableContainerNode()
                .WithAutoScroll(AutoScrollPolicy.None)
                .WithScrollbar(false);
            var semanticText = ChatPresentationRenderer.SemanticCopyText(block.SemanticText);
            var displayText = RemoveDuplicateInspectorLabel(semanticText, block.Label);
            if (block.Kind == ChatBlockKind.Assistant)
            {
                displayText = ChatPresentationRenderer.MarkdownToPlainText(displayText);
                displayText = WordWrapInspectorText(displayText, detailWidth);
            }
            _inspectorCopyNode = new CopyableTextNode(_clipboardService, displayText)
                .WithSemanticContent(semanticText)
                .WithHint(null);
            _inspectorDetail.WithContent(_inspectorCopyNode);
            _inspectorDetail.ScrollToTop();
        }

        var heading = $"{InspectorDetailTitle(block)}  {EventTime(block)}  event {_inspectorIndex + 1} of {_state.Transcript.Count}";
        var detailContent = Layouts.Vertical()
            .WithChild(new TextNode(heading).WithForeground(ChatVisualTheme.Primary).Bold())
            .WithChild(_inspectorDetail.Fill());
        if (_inspectorCopyStatus is not null)
        {
            var color = _inspectorCopyStatus.StartsWith("Copy failed", StringComparison.Ordinal)
                ? ChatVisualTheme.Danger
                : ChatVisualTheme.Success;
            detailContent.WithChild(new TextNode(_inspectorCopyStatus).WithForeground(color));
        }
        detailContent.WithChild(new TextNode(
                "Y copy event  Shift+Y copy turn")
            .WithForeground(ChatVisualTheme.Muted));

        var inspectorHeight = Math.Max(6, _terminal.Height - 3);
        var detailPanel = new PanelNode()
            .WithBorder(BorderStyle.None)
            .WithBackground(ChatVisualTheme.Surface)
            .WithPadding(1)
            .WithContent(detailContent)
            .Height(inspectorHeight);

        ILayoutNode inspectorBody;
        if (showEventList)
        {
            inspectorBody = Layouts.Horizontal()
                .WithChild(BuildInspectorEventList(eventListWidth, inspectorHeight))
                .WithChild(Layouts.Empty().Width(2))
                .WithChild(detailPanel.WidthFill());
        }
        else
        {
            inspectorBody = detailPanel.Width(ReadableWidth());
        }

        var help = _terminal.Width >= 86
            ? "Up/Down event  PgUp/PgDn detail  Ctrl+O or Esc close"
            : "Up/Down event  Pg scroll  Esc close";
        var inspectorHeader = new PanelNode()
            .WithBorder(BorderStyle.None)
            .WithBackground(ChatVisualTheme.HeaderSurface)
            .WithContent(new TextNode(
                    $"INSPECTOR  {_state.SessionTitle ?? "current turn"}  event {_inspectorIndex + 1} of {_state.Transcript.Count}")
                .WithForeground(ChatVisualTheme.Primary)
                .Bold())
            .Height(1);
        var inspector = Layouts.Vertical()
            .WithChild(inspectorHeader)
            .WithChild(inspectorBody)
            .WithChild(new TextNode(help).WithForeground(ChatVisualTheme.Muted))
            .Width(ReadableWidth());
        return WithViewportMargin(inspector);
    }

    private ILayoutNode BuildInspectorEventList(int width, int height)
    {
        var rowCapacity = Math.Max(1, height - 3);
        var start = Math.Clamp(
            _inspectorIndex - (rowCapacity / 2),
            0,
            Math.Max(0, _state.Transcript.Count - rowCapacity));
        var content = Layouts.Vertical()
            .WithChild(new TextNode("TURN EVENTS").WithForeground(ChatVisualTheme.Muted));
        foreach (var (block, index) in _state.Transcript
                     .Skip(start)
                     .Take(rowCapacity)
                     .Select((value, offset) => (value, start + offset)))
        {
            var state = InspectorEventState(block);
            var rowText = ChatPresentationRenderer.OneLine(
                $"{state,-8} {InspectorEventName(block)}",
                Math.Max(1, width - 2));
            var row = new TextNode(rowText)
                .WithForeground(block.IsFailure ? ChatVisualTheme.Danger : ChatVisualTheme.Text)
                .NoWrap();
            content.WithChild(new PanelNode()
                .WithBorder(BorderStyle.None)
                .WithBackground(index == _inspectorIndex
                    ? ChatVisualTheme.SurfaceSelected
                    : ChatVisualTheme.Surface)
                .WithContent(row)
                .Height(1));
        }

        return new PanelNode()
            .WithBorder(BorderStyle.None)
            .WithBackground(ChatVisualTheme.Surface)
            .WithPadding(1)
            .WithContent(content)
            .Width(width)
            .Height(height);
    }

    private static string RemoveDuplicateInspectorLabel(string text, string label)
    {
        var prefix = $"{label}\n";
        return text.StartsWith(prefix, StringComparison.Ordinal)
            ? text[prefix.Length..]
            : text;
    }

    private static string WordWrapInspectorText(string text, int width) => string.Join(
        '\n',
        WordWrapper.WrapLines(text.ReplaceLineEndings("\n").Split('\n'), width));

    private bool HandleInspectorInput(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.Escape:
                CloseInspector();
                return true;
            case ConsoleKey.O when keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control):
                CloseInspector();
                return true;
            case ConsoleKey.UpArrow:
                SelectInspectorEvent(-1);
                return true;
            case ConsoleKey.DownArrow:
                SelectInspectorEvent(1);
                return true;
            case ConsoleKey.PageUp:
                _inspectorDetail?.PageUp();
                return true;
            case ConsoleKey.PageDown:
                _inspectorDetail?.PageDown();
                return true;
            case ConsoleKey.Home:
                _inspectorDetail?.ScrollToTop();
                return true;
            case ConsoleKey.End:
                _inspectorDetail?.ScrollToBottom();
                return true;
            case ConsoleKey.Y:
                CopyInspectorSelection(keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift));
                return true;
            default:
                return true;
        }
    }

    private void OpenInspector()
    {
        _inspectorOpen = true;
        _inspectorIndex = FindDefaultInspectorIndex();
        _inspectorBlockKey = null;
        _inspectorCopyStatus = null;
        _lastEscapeTimestamp = null;
        Focus.ClearFocus();
        InvalidateLayout();
    }

    private void CloseInspector()
    {
        _inspectorOpen = false;
        _inspectorBlockKey = null;
        _inspectorCopyStatus = null;
        InvalidateLayout();
        if (_state.PendingApproval is not null)
            Focus.SetFocus(EnsureApprovalList());
        else if (ShowsComposer(_state))
            Focus.SetFocus(_promptInput);
        else
            Focus.ClearFocus();

        foreach (var block in _deferredInspectorCommits)
            QueueCommit(block);
        _deferredInspectorCommits.Clear();
    }

    private void SelectInspectorEvent(int delta)
    {
        var index = Math.Clamp(_inspectorIndex + delta, 0, _state.Transcript.Count - 1);
        if (index == _inspectorIndex)
            return;

        _inspectorIndex = index;
        _inspectorBlockKey = null;
        _inspectorCopyStatus = null;
        _liveRegion.Invalidate();
    }

    private void CopyInspectorSelection(bool completeTurn)
    {
        if (_inspectorCopyNode is null)
            return;

        var semanticText = completeTurn
            ? ChatPresentationRenderer.BuildSemanticTurn(_state.Transcript, _inspectorIndex)
            : ChatPresentationRenderer.SemanticCopyText(_state.Transcript[_inspectorIndex].SemanticText);
        _inspectorCopyNode.WithSemanticContent(semanticText);
        var success = _inspectorCopyNode.TryCopy();
        _inspectorCopyStatus = success
            ? completeTurn ? "Turn copied" : "Event copied"
            : "Copy failed. The selected event remains available.";
        _liveRegion.Invalidate();
    }

    private ILayoutNode BuildSessionHeader()
    {
        var connectionPart = _state.HasJoined ? "connected" : "connecting";
        var sessionPart = !_state.HasJoined
            ? "connecting"
            : _state.SessionTitle ?? "new session";
        var modelPart = _terminal.Width >= 100 ? $"  {ViewModel.ModelId}" : string.Empty;
        var contextPart = _terminal.Width >= 110 && _state.ContextUsagePercent is { } usage
            ? $"  {Math.Round(usage * 100, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}%"
            : string.Empty;
        var daemonPart = _terminal.Width >= 76
            ? $"  {connectionPart}"
            : string.Empty;
        var headerText = _terminal.Width < 60
            ? $"NETCLAW  {connectionPart}"
            : $"NETCLAW  {sessionPart}{modelPart}{contextPart}{daemonPart}";
        var header = new TextNode(headerText)
            .WithForeground(ChatVisualTheme.Primary)
            .Bold();
        return new PanelNode()
            .WithBorder(BorderStyle.None)
            .WithBackground(ChatVisualTheme.HeaderSurface)
            .WithContent(header)
            .Width(ReadableWidth())
            .Height(1);
    }

    private ILayoutNode BuildQueueShelf()
    {
        if (_queuedPromptDisplays.Count == 0)
            return Layouts.Empty();

        var count = _queuedPromptDisplays.Count;
        var label = count == 1 ? "1 message" : $"{count} messages";
        var content = Layouts.Vertical()
            .WithChild(new TextNode($"QUEUED  {label}")
                .WithForeground(ChatVisualTheme.Muted)
                .Bold());
        var index = 1;
        foreach (var prompt in _queuedPromptDisplays)
        {
            var preview = ChatPresentationRenderer.OneLine(
                prompt.Text,
                Math.Max(20, ReadableWidth() - 18));
            var state = prompt.IsAccepted ? "queued" : "sending";
            content.WithChild(new TextNode($"{index,2}  {state,-7}  {preview}")
                .WithForeground(ChatVisualTheme.Text));
            index++;
        }

        return new PanelNode()
            .WithBorder(BorderStyle.None)
            .WithBackground(ChatVisualTheme.Surface)
            .WithPadding(1)
            .WithContent(content)
            .Width(ReadableWidth())
            .Height(count + 3);
    }

    private ILayoutNode BuildLiveReplyBlock()
    {
        var hasReply = _state.ReplyPassages.Count > 0
                       || _state.Tools.Count > 0
                       || _state.SubAgents.Count > 0
                       || _state.AgentPulls.Count > 0
                       || !string.IsNullOrWhiteSpace(_state.ThoughtText);
        if (!hasReply)
            return Layouts.Empty();

        var reply = Layouts.Vertical()
            .WithChild(new TextNode("NETCLAW  LIVE")
                .WithForeground(ChatVisualTheme.Primary)
                .Bold());
        var lineWidth = Math.Max(20, ReadableWidth() - 4);
        if (!string.IsNullOrWhiteSpace(_state.ThoughtText))
        {
            reply.WithChild(new TextNode(ChatPresentationRenderer.OneLine(
                    $"Reasoning  {_state.ThoughtText}",
                    lineWidth))
                .WithForeground(ChatVisualTheme.Muted));
        }

        var agents = _state.SubAgents.Values
            .OrderBy(value => value.StartedAtMs)
            .ThenBy(value => value.RunId, StringComparer.Ordinal)
            .ToList();
        var renderedAgents = new HashSet<string>(StringComparer.Ordinal);
        var renderedTools = new HashSet<string>(StringComparer.Ordinal);
        var renderedPulls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pull in _state.AgentPulls.Where(pull => pull.AfterPassageIndex < 0))
        {
            reply.WithChild(BuildAgentPull(pull, lineWidth));
            renderedPulls.Add(pull.BatchId);
        }
        foreach (var passage in _state.ReplyPassages.OrderBy(value => value.Index))
        {
            if (!string.IsNullOrWhiteSpace(passage.Text))
            {
                reply.WithChild(new TextNode(ChatPresentationRenderer.MarkdownToPlainText(passage.Text))
                    .WithForeground(ChatVisualTheme.Text));
            }

            var passageTools = passage.ToolCallIds
                .Select(callId => _state.Tools.GetValueOrDefault(callId))
                .Where(tool => tool is not null)
                .Cast<ToolActivityPresentation>()
                .ToList();
            if (passageTools.Count == 0)
                continue;

            reply.WithChild(new TextNode("Work trace")
                .WithForeground(ChatVisualTheme.Muted));
            foreach (var group in passageTools.GroupBy(tool =>
                         tool.BatchSize > 1 && tool.BatchId.Length > 0
                             ? tool.BatchId
                             : tool.CallId))
            {
                var tools = group.ToList();
                if (tools.Any(tool => tool.BatchSize > 1))
                {
                    var settled = tools.Count(tool => tool.CompletedAtMs is not null);
                    reply.WithChild(new TextNode(
                            $"Parallel work  · {settled}/{tools.Max(tool => tool.BatchSize)} complete")
                        .WithForeground(ChatVisualTheme.Muted));
                }

                foreach (var tool in tools)
                {
                    reply.WithChild(BuildToolActivity(tool, agents, renderedAgents, lineWidth));
                    renderedTools.Add(tool.CallId);
                }
            }

            foreach (var pull in _state.AgentPulls.Where(pull =>
                         pull.AfterPassageIndex == passage.Index))
            {
                reply.WithChild(BuildAgentPull(pull, lineWidth));
                renderedPulls.Add(pull.BatchId);
            }
        }

        foreach (var tool in _state.Tools.Values
                     .Where(tool => !renderedTools.Contains(tool.CallId))
                     .OrderBy(tool => tool.StartedAtMs))
        {
            reply.WithChild(BuildToolActivity(tool, agents, renderedAgents, lineWidth));
        }
        foreach (var run in agents.Where(value => !renderedAgents.Contains(value.RunId)))
            reply.WithChild(BuildAgentActivity(run, lineWidth));
        foreach (var pull in _state.AgentPulls.Where(pull => !renderedPulls.Contains(pull.BatchId)))
            reply.WithChild(BuildAgentPull(pull, lineWidth));

        var maximumHeight = Math.Max(5, Math.Min(18, _terminal.Height / 2));
        if (_assistantStream is null)
        {
            _assistantStream = new ScrollableContainerNode()
                .WithScrollbar(false);
            _assistantStream.Invalidated
                .Subscribe(_ => SynchronizeAssistantTailState())
                .DisposeWith(Subscriptions);
        }
        _assistantStream.AutoScroll = _assistantTailPaused
            ? AutoScrollPolicy.None
            : AutoScrollPolicy.AlwaysTail;
        _assistantStream.WithContent(reply);

        return new PanelNode()
            .WithBorder(BorderStyle.None)
            .WithBackground(ChatVisualTheme.Surface)
            .WithPadding(1)
            .WithContent(_assistantStream.HeightAuto(
                min: 1,
                max: Math.Max(1, maximumHeight - 2)))
            .Width(ReadableWidth())
            .HeightAuto(min: 3, max: maximumHeight);
    }

    private static ILayoutNode BuildAgentPull(AgentPullPresentation pull, int lineWidth)
    {
        var countLabel = pull.Messages.Count == 1 ? "1 message" : $"{pull.Messages.Count} messages";
        var rows = Layouts.Vertical()
            .WithChild(new TextNode($"Pulled by agent  · {countLabel}")
                .WithForeground(ChatVisualTheme.Muted));
        foreach (var message in pull.Messages)
        {
            rows.WithChild(new TextNode(ChatPresentationRenderer.OneLine(
                    $"  {message.Content}",
                    lineWidth))
                .WithForeground(ChatVisualTheme.Text));
        }

        return rows;
    }

    private ILayoutNode BuildToolActivity(
        ToolActivityPresentation tool,
        IReadOnlyCollection<SubAgentActivityPresentation> agents,
        ISet<string> renderedAgents,
        int lineWidth)
    {
        var childRuns = agents.Where(value => value.ParentCallId == tool.CallId).ToList();
        var approvalPosition = _state.ApprovalQueuePosition(tool.CallId);
        var isCurrentApproval = approvalPosition == 1;
        var waitsForApproval = approvalPosition > 1;
        var phase = isCurrentApproval
            ? "awaiting decision"
            : waitsForApproval
                ? $"decision {approvalPosition} of {_state.PendingApprovalCount}"
            : childRuns.Count switch
            {
                0 => tool.Phase,
                1 => $"orchestrating {childRuns[0].AgentName}",
                _ => $"orchestrating {childRuns.Count} agents"
            };
        var state = isCurrentApproval
            ? "Decision"
            : waitsForApproval
                ? "Waiting"
                : ActivityState(tool.Phase);
        if (string.Equals(state, "Live", StringComparison.Ordinal))
            state = $"Live{new string('.', _thinkingFrame + 1)}";
        var phaseText = !isCurrentApproval
                        && !waitsForApproval
                        && string.Equals(phase, tool.Phase, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $"  {phase}";
        var summary = string.IsNullOrWhiteSpace(tool.Summary) ? string.Empty : $"  {tool.Summary}";
        var action = ChatPresentationReducer.ToolWorkTitle(tool);
        var rows = Layouts.Vertical()
            .WithChild(new TextNode(ChatPresentationRenderer.OneLine(
                    $"{state,-8} {action}  · {tool.ToolName}{phaseText}{summary}",
                    lineWidth))
                .WithForeground(isCurrentApproval
                    ? ChatVisualTheme.Warning
                    : waitsForApproval
                        ? ChatVisualTheme.Muted
                        : ActivityColor(tool.Phase)));
        foreach (var run in childRuns)
        {
            rows.WithChild(BuildAgentActivity(run, lineWidth));
            renderedAgents.Add(run.RunId);
        }

        return rows;
    }

    private ILayoutNode BuildComposer()
    {
        var content = Layouts.Vertical()
            .WithChild(new TextNode("MESSAGE").WithForeground(ChatVisualTheme.Primary).Bold())
            .WithChild(_promptInput);
        var composer = new PanelNode()
            .WithBorder(BorderStyle.None)
            .WithBackground(ChatVisualTheme.SurfaceStrong)
            .WithPadding(1)
            .WithContent(content)
            .Width(ReadableWidth())
            .HeightAuto(min: 4, max: Math.Max(4, Math.Min(11, _terminal.Height / 3)));
        return Layouts.Horizontal()
            .WithChild(composer)
            .WithChild(Layouts.Empty().Fill());
    }

    private ILayoutNode BuildDecisionGate(ToolInteractionRequest approval)
    {
        var width = Math.Max(20, ReadableWidth() - 4);
        var detailHeight = ApprovalDetailHeight(approval, width);
        var maximumHeight = ViewModel.IsApprovalDetailVisible.Value
            ? detailHeight + approval.Options.Count + 4
            : approval.Options.Count + 5;
        var queuePosition = _state.PendingApprovalCount > 1
            ? $"  1 of {_state.PendingApprovalCount}"
            : string.Empty;
        var header = new PanelNode()
            .WithBorder(BorderStyle.None)
            .WithBackground(ChatVisualTheme.ApprovalHeader)
            .WithContent(new TextNode(
                    $"Approval required{queuePosition}  {ChatPresentationRenderer.ApprovalPath(_state, approval)}")
                .WithForeground(ChatVisualTheme.Warning)
                .Bold())
            .Height(1);
        var gate = Layouts.Vertical().WithChild(header);
        if (ViewModel.IsApprovalDetailVisible.Value)
        {
            gate.WithChild(EnsureApprovalDetail(approval)
                .Height(detailHeight));
        }
        else
        {
            gate.WithChild(new TextNode(ChatPresentationRenderer.OneLine(
                    approval.DisplayText,
                    Math.Max(20, width - 4)))
                .WithForeground(ChatVisualTheme.Text));
        }

        gate
            .WithChild(new TextNode(
                    ViewModel.IsApprovalDetailVisible.Value
                        ? "PgUp/PgDn scroll  Ctrl+O close details  Escape deny"
                        : "Ctrl+O details  Escape deny")
                .WithForeground(ChatVisualTheme.Muted))
            .WithChild(EnsureApprovalList());

        var panel = new PanelNode()
            .WithBorder(BorderStyle.None)
            .WithBackground(ChatVisualTheme.ApprovalSurface)
            .WithPadding(1)
            .WithContent(gate)
            .Width(ReadableWidth())
            .HeightAuto(min: 6, max: Math.Max(6, Math.Min(maximumHeight, _terminal.Height / 2)));
        return Layouts.Horizontal()
            .WithChild(panel)
            .WithChild(Layouts.Empty().Fill());
    }

    private SelectionListNode<string> EnsureApprovalList()
    {
        var approval = _state.PendingApproval
            ?? throw new InvalidOperationException("An approval list requires a pending approval.");
        if (_approvalList is not null && _approvalCallId == approval.CallId.Value)
            return _approvalList;

        ClearApprovalList();
        _approvalCallId = approval.CallId.Value;
        _approvalList = Layouts.SelectionList(approval.Options.Select(ApprovalOptionLabel).ToList())
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Yellow);
        _approvalList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    var option = approval.Options.FirstOrDefault(candidate =>
                        string.Equals(ApprovalOptionLabel(candidate), selected[0], StringComparison.Ordinal));
                    if (option is not null)
                        _ = ViewModel.SubmitInteractionOptionAsync(approval.CallId, option.Label);
                }
            })
            .DisposeWith(_approvalSubscriptions);
        return _approvalList;
    }

    private void ClearApprovalList()
    {
        _approvalSubscriptions.Clear();
        _approvalList = null;
        _approvalCallId = null;
        _approvalDetail = null;
        _approvalDetailCallId = null;
    }

    private static string ApprovalOptionLabel(ToolInteractionOption option) => option.Key.Value switch
    {
        ApprovalOptionKeys.ApproveOnce => $"{option.Label} — only this request",
        ApprovalOptionKeys.ApproveSession => $"{option.Label} — until this chat ends",
        ApprovalOptionKeys.ApproveAlways => $"{option.Label} — this tool in this folder",
        ApprovalOptionKeys.ApproveEverywhere => $"{option.Label} — this tool in any folder",
        ApprovalOptionKeys.Deny => $"{option.Label} — do not run",
        _ => option.Label
    };

    private ScrollableContainerNode EnsureApprovalDetail(ToolInteractionRequest approval)
    {
        if (_approvalDetail is not null && _approvalDetailCallId == approval.CallId.Value)
            return _approvalDetail;

        _approvalDetailCallId = approval.CallId.Value;
        _approvalDetail = new ScrollableContainerNode()
            .WithAutoScroll(AutoScrollPolicy.None)
            .WithScrollbar(false)
            .WithContent(new TextNode(BuildApprovalDetail(approval)).WithForeground(ChatVisualTheme.Text));
        return _approvalDetail;
    }

    private ILayoutNode BuildStatusLine()
    {
        var status = ViewModel.StatusMessage.Value;
        var displayStatus = status switch
        {
            "Generating..." => $"Thinking{new string('.', _thinkingFrame + 1)}",
            "Connecting..." when _terminal.Width < 48 => "Connect",
            _ => status
        };
        if (_state.PendingApproval is not null)
            displayStatus = "Decision needed";
        var tailPaused = IsAssistantTailPaused();
        var keys = StatusKeys(
            _terminal.Width,
            _state.PendingApproval is not null,
            ViewModel.IsApprovalDetailVisible.Value,
            ShowsComposer(_state),
            _modifiedEnterKeySupport,
            tailPaused);
        var statusNode = new TextNode(
                string.Equals(status, "Generating...", StringComparison.Ordinal)
                    ? displayStatus.PadRight(12)
                    : displayStatus)
            .WithForeground(StatusColor(status));
        if (string.Equals(status, "Generating...", StringComparison.Ordinal))
            statusNode.Width(12);
        else
            statusNode.WidthAuto();
        var statusLine = Layouts.Horizontal()
            .WithChild(statusNode);
        if (tailPaused)
        {
            var count = _unseenAssistantEvents.Count;
            var eventLabel = $"{count} new {(count == 1 ? "event" : "events")}";
            statusLine.WithChild(new TextNode($"  {eventLabel.PadRight(13)}")
                .WithForeground(ChatVisualTheme.Primary)
                .Width(15));
        }

        statusLine
            .WithChild(new TextNode($"  {keys}")
                .WithForeground(ChatVisualTheme.Muted)
                .NoWrap()
                .Fill())
            .Width(ReadableWidth())
            .Height(1);
        return Layouts.Horizontal()
            .WithChild(statusLine)
            .WithChild(Layouts.Empty().Fill());
    }

    private int ReadableWidth() => Math.Min(
        Math.Max(1, _terminal.Width - ViewportLeftMargin()),
        MaximumReadableWidth);

    private int ViewportLeftMargin() => _terminal.Width >= 60 ? 2 : 0;

    private ILayoutNode WithViewportMargin(ILayoutNode content)
    {
        var margin = ViewportLeftMargin();
        if (margin == 0)
            return content;

        return Layouts.Horizontal()
            .WithChild(Layouts.Empty().Width(margin))
            .WithChild(content)
            .WithChild(Layouts.Empty().Fill());
    }

    private ILayoutNode BuildAgentActivity(SubAgentActivityPresentation run, int lineWidth)
    {
        var approvalPosition = SubAgentApprovalQueuePosition(run);
        var isCurrentApproval = approvalPosition == 1;
        var waitsForApproval = approvalPosition > 1;
        var summary = string.IsNullOrWhiteSpace(run.Summary) ? string.Empty : $"  {run.Summary}";
        var prefix = run.ParentCallId is null ? "  " : "    ";
        var state = isCurrentApproval
            ? "Decision"
            : waitsForApproval
                ? "Waiting"
                : ActivityState(run.Phase);
        var phase = isCurrentApproval
            ? "awaiting decision"
            : waitsForApproval
                ? $"decision {approvalPosition} of {_state.PendingApprovalCount}"
                : run.Phase;
        var rows = new List<ILayoutNode>
        {
            new TextNode(ChatPresentationRenderer.OneLine(
                    $"{prefix}{state,-8} Agent  {run.AgentName}  {phase}{summary}",
                    lineWidth))
                .WithForeground(isCurrentApproval
                    ? ChatVisualTheme.Warning
                    : waitsForApproval
                        ? ChatVisualTheme.Muted
                        : ActivityColor(run.Phase))
        };
        if (run.ActiveToolName is not null)
        {
            var toolPrefix = run.ParentCallId is null ? "    " : "      ";
            rows.Add(new TextNode(ChatPresentationRenderer.OneLine(
                    $"{toolPrefix}{ActivityState(run.Phase),-8} Tool  {run.ActiveToolName}  {run.Phase}",
                    lineWidth))
                .WithForeground(ActivityColor(run.Phase)));
        }

        return Layouts.Vertical([.. rows]);
    }

    private int SubAgentApprovalQueuePosition(SubAgentActivityPresentation run)
    {
        if (run.ParentCallId is null)
            return 0;

        const string marker = "/subagent-approval/";
        var position = 1;
        foreach (var approval in _state.PendingApprovals)
        {
            var callId = approval.CallId.Value;
            var markerIndex = callId.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex > 0
                && string.Equals(callId[..markerIndex], run.ParentCallId, StringComparison.Ordinal))
            {
                return position;
            }

            position++;
        }

        return 0;
    }

    private string BuildApprovalDetail(ToolInteractionRequest approval)
    {
        var lines = new List<string>
        {
            $"Requester: {ChatPresentationRenderer.ApprovalRequester(_state, approval)}",
            $"Action: Run {approval.ToolName.Value}",
            approval.DisplayText
        };
        if (approval.Patterns.Count > 0)
            lines.Add($"Patterns: {string.Join(", ", approval.Patterns)}");
        if (approval.CandidateVerbs.Count > 0)
            lines.Add($"Verbs: {string.Join(", ", approval.CandidateVerbs)}");
        if (!string.IsNullOrWhiteSpace(approval.Cwd))
            lines.Add($"Directory: {approval.Cwd}");
        if (approval.IsMessy)
            lines.Add("Complex command: persistent approval is unavailable.");
        if (approval.HasAdoptedContext)
        {
            var source = approval.HasThirdPartyAdoptedContext ? "third-party context" : "adopted context";
            lines.Add($"Context: {source}; persisted={approval.PersistedAdoptedContext}.");
        }

        return ChatPresentationRenderer.VisibleControlText(
            string.Join('\n', lines),
            ChatViewModel.MaxExpandedApprovalBodyChars);
    }

    private int ApprovalDetailHeight(ToolInteractionRequest approval, int width)
    {
        var contentWidth = Math.Max(1, width - 2);
        var lineCount = BuildApprovalDetail(approval)
            .Split('\n')
            .Sum(line => Math.Max(1, (line.Length + contentWidth - 1) / contentWidth));
        return Math.Clamp(lineCount, 3, 10);
    }

    private static string StatusKeys(
        int width,
        bool hasApproval,
        bool approvalDetailVisible,
        bool hasComposer,
        TerminalCapabilityAvailability modifiedEnterKeySupport,
        bool tailPaused)
    {
        if (hasApproval)
            return width >= 88
                ? approvalDetailVisible
                    ? "Up/Down select  Enter confirm  Ctrl+O close  Esc deny  Ctrl+Q quit"
                    : "Up/Down select  Enter confirm  Ctrl+O details  Esc deny  Ctrl+Q quit"
                : "Up/Down select  Enter confirm  Esc deny";
        if (!hasComposer)
            return width >= 70
                ? tailPaused
                    ? "End follow  Ctrl+O inspect  Ctrl+Q quit"
                    : "Ctrl+O inspect  Ctrl+Q quit"
                : "Ctrl+Q quit";

        if (tailPaused)
            return width >= 88
                ? "PageDown/End follow  Enter send  Esc x2 clear  Ctrl+Q quit"
                : "End follow  Enter send";

        if (modifiedEnterKeySupport == TerminalCapabilityAvailability.Unavailable)
        {
            if (width >= 110)
                return "Enter send  Esc x2 clear  Ctrl+O inspect  Ctrl+Q quit";

            return width >= 66
                ? "Enter send  Esc x2 clear"
                : "Enter send";
        }

        if (modifiedEnterKeySupport == TerminalCapabilityAvailability.Unknown)
        {
            if (width >= 110)
                return "Enter send  Esc x2 clear  Ctrl+O inspect  Ctrl+Q quit";

            return width >= 66
                ? "Enter send  Esc x2 clear"
                : "Enter send";
        }

        if (width >= 110)
            return "Enter send  Shift+Enter newline  Esc x2 clear  Ctrl+O inspect  Ctrl+Q quit";
        return width >= 66
            ? "Enter send  Shift+Enter line  Esc x2 clear  Ctrl+O inspect"
            : "Enter send  Shift+Enter line";
    }

    private static Color ActivityColor(string phase) => phase.ToLowerInvariant() switch
    {
        "queued" => ChatVisualTheme.Muted,
        "failed" or "error" or "denied" or "rejected" => ChatVisualTheme.Danger,
        "completed" or "complete" => ChatVisualTheme.Muted,
        _ => ChatVisualTheme.Primary
    };

    private static string ActivityState(string phase) => phase.ToLowerInvariant() switch
    {
        "queued" => "Queued",
        "failed" or "error" => "Failed",
        "denied" => "Denied",
        "rejected" => "Rejected",
        "completed" or "complete" => "Done",
        _ => "Live"
    };

    private static bool ShowsInPrimaryTranscript(ChatPresentationBlock block) =>
        block.Kind is not ChatBlockKind.System and not ChatBlockKind.Usage;

    private static string InspectorEventState(ChatPresentationBlock block) => block.Kind switch
    {
        _ when block.IsFailure => "Fail",
        ChatBlockKind.User => "Prompt",
        ChatBlockKind.Assistant => "Reply",
        ChatBlockKind.System => "Title",
        ChatBlockKind.Thought => "Thought",
        ChatBlockKind.Tool => "Tool",
        ChatBlockKind.Parallel => "Batch",
        ChatBlockKind.SubAgent => "Agent",
        ChatBlockKind.Approval => "Approval",
        ChatBlockKind.File => "File",
        ChatBlockKind.Usage => "Usage",
        ChatBlockKind.Compaction => "Context",
        ChatBlockKind.Diagnostic => "Notice",
        _ => "Done"
    };

    private static string InspectorEventName(ChatPresentationBlock block) => block.Kind switch
    {
        ChatBlockKind.User => "User",
        ChatBlockKind.Assistant => "Netclaw",
        ChatBlockKind.System => ChatPresentationRenderer.OneLine(block.Summary, 24),
        ChatBlockKind.Usage => ChatPresentationRenderer.OneLine(block.Summary, 24),
        ChatBlockKind.Tool => ChatPresentationRenderer.OneLine(block.Summary, 24),
        ChatBlockKind.SubAgent => ChatPresentationRenderer.OneLine(block.Summary, 24),
        _ => ChatPresentationRenderer.DisplayLabel(block.Kind)
    };

    private static string InspectorDetailTitle(ChatPresentationBlock block) => block.Kind switch
    {
        ChatBlockKind.User => "User prompt",
        ChatBlockKind.Assistant => "Netclaw reply",
        ChatBlockKind.Tool => "Tool result",
        _ => ChatPresentationRenderer.DisplayLabel(block.Kind)
    };

    private static string EventTime(ChatPresentationBlock block) => block.TimestampMs > 0
        ? DateTimeOffset.FromUnixTimeMilliseconds(block.TimestampMs).ToString("HH:mm")
        : string.Empty;

    private int FindDefaultInspectorIndex()
    {
        for (var index = _state.Transcript.Count - 1; index >= 0; index--)
        {
            if (_state.Transcript[index].Kind is not ChatBlockKind.Usage and not ChatBlockKind.System)
                return index;
        }

        return _state.Transcript.Count - 1;
    }

    private void HandleEscape()
    {
        if (_state.PendingApproval is { } approval)
        {
            _lastEscapeTimestamp = null;
            _ = ViewModel.DenyPendingInteractionAsync(approval.CallId);
            return;
        }

        if (ViewModel.IsGenerating.Value)
        {
            _lastEscapeTimestamp = null;
            ViewModel.StatusMessage.Value = "Cancel generation is not supported yet.";
            _liveRegion.Invalidate();
            return;
        }

        var now = _timeProvider.GetTimestamp();
        if (_lastEscapeTimestamp is { } prior
            && _timeProvider.GetElapsedTime(prior, now) <= DoubleEscapeWindow)
        {
            _promptInput.Clear();
            _lastEscapeTimestamp = null;
            ViewModel.StatusMessage.Value = "Input cleared";
            _liveRegion.Invalidate();
            return;
        }

        _lastEscapeTimestamp = now;
    }

    private static Color StatusColor(string status) => status switch
    {
        "Ready" => ChatVisualTheme.Success,
        "Approval required" => ChatVisualTheme.Warning,
        _ when status.StartsWith("Connected", StringComparison.Ordinal) => ChatVisualTheme.Success,
        _ when status.StartsWith("Reconnected", StringComparison.Ordinal) => ChatVisualTheme.Success,
        _ when status.StartsWith("Generating", StringComparison.Ordinal) => ChatVisualTheme.Warning,
        _ when status.StartsWith("Output failed", StringComparison.Ordinal) => ChatVisualTheme.Danger,
        _ when status.StartsWith("Connection failed", StringComparison.Ordinal) => ChatVisualTheme.Danger,
        _ => ChatVisualTheme.Muted
    };

    private bool ShowsComposer(ChatPresentationState state) =>
        state.PendingApproval is null;

    private sealed record QueuedPromptDisplay(string MessageId, string Text, bool IsAccepted);
}

internal static partial class ChatPresentationRenderer
{
    public static ILayoutNode BuildStableBlock(ChatPresentationBlock block, int width)
    {
        var timestamp = block.TimestampMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(block.TimestampMs).ToString("HH:mm")
            : string.Empty;
        var timePart = string.IsNullOrEmpty(timestamp) ? string.Empty : $"  {timestamp}";
        var body = block.Kind == ChatBlockKind.Assistant
            ? MarkdownToPlainText(block.Summary)
            : block.Summary;

        var leftMargin = width >= 60 ? 2 : 0;
        var readableWidth = Math.Min(width - leftMargin, MaximumReadableWidth);
        var bodyNode = new TextNode(VisibleControlText(body, 16_000))
            .WithForeground(BodyColor(block))
            .Width(readableWidth - (UsesSurface(block) ? 2 : 0));
        var heading = new TextNode($"{DisplayLabel(block.Kind)}{timePart}")
            .WithForeground(LabelColor(block))
            .Bold();
        ILayoutNode content;
        if (UsesSurface(block))
        {
            content = new PanelNode()
                .WithBorder(BorderStyle.None)
                .WithBackground(SurfaceColor(block))
                .WithPadding(1)
                .WithContent(bodyNode)
                .Width(readableWidth);
        }
        else
        {
            content = Layouts.Horizontal()
                .WithChild(bodyNode)
                .WithChild(Layouts.Empty().Fill());
        }

        var stableBlock = Layouts.Vertical()
            .WithChild(heading)
            .WithChild(content)
            .WithChild(Layouts.Empty().Height(1));
        if (leftMargin == 0)
            return stableBlock;

        return Layouts.Horizontal()
            .WithChild(Layouts.Empty().Width(leftMargin))
            .WithChild(stableBlock.Width(readableWidth))
            .WithChild(Layouts.Empty().Fill());
    }

    private const int MaximumReadableWidth = 120;

    public static string OneLine(string? text, int maximumLength)
    {
        var safe = VisibleControlText(text ?? string.Empty, Math.Max(1, maximumLength));
        var oneLine = safe.Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        return oneLine.Length <= maximumLength
            ? oneLine
            : string.Concat(oneLine.AsSpan(0, Math.Max(0, maximumLength - 1)), "…");
    }

    public static string VisibleControlText(string text, int maximumLength)
    {
        var builder = new System.Text.StringBuilder(Math.Min(text.Length, maximumLength));
        foreach (var character in text)
        {
            if (builder.Length >= maximumLength)
                break;

            if (character is '\n' or '\t')
            {
                builder.Append(character);
                continue;
            }

            builder.Append(char.IsControl(character)
                ? $"\\u{(int)character:X4}"
                : character);
        }

        if (text.Length > maximumLength)
            builder.Append('…');
        return builder.ToString();
    }

    public static string SemanticCopyText(string text) => VisibleControlText(text, int.MaxValue);

    public static string MarkdownToPlainText(string markdown)
    {
        var output = new StringBuilder(markdown.Length);
        var inCodeFence = false;
        foreach (var sourceLine in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = sourceLine.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                continue;
            }

            var line = inCodeFence
                ? $"  {sourceLine}"
                : MarkdownLineToPlainText(sourceLine);
            if (output.Length > 0)
                output.Append('\n');
            output.Append(line);
        }

        return output.ToString();
    }

    public static string BuildSemanticTurn(
        IReadOnlyList<ChatPresentationBlock> transcript,
        int selectedIndex)
    {
        if (transcript.Count == 0)
            return string.Empty;

        var boundedIndex = Math.Clamp(selectedIndex, 0, transcript.Count - 1);
        var start = boundedIndex;
        while (start > 0 && transcript[start].Kind != ChatBlockKind.User)
            start--;
        if (transcript[start].Kind != ChatBlockKind.User)
            start = boundedIndex;

        var end = boundedIndex + 1;
        while (end < transcript.Count && transcript[end].Kind != ChatBlockKind.User)
            end++;

        return string.Join(
            "\n\n",
            transcript.Skip(start).Take(end - start)
                .Select(block => SemanticCopyText(block.SemanticText)));
    }

    public static string CompactIdentity(string identity, int maximumLength) =>
        identity.Length <= maximumLength
            ? identity
            : $"…{identity[^Math.Max(1, maximumLength - 1)..]}";

    public static string ApprovalPath(
        ChatPresentationState state,
        ToolInteractionRequest approval)
    {
        var requester = ApprovalRequester(state, approval);
        return $"{requester} requests permission to run {approval.ToolName.Value}";
    }

    public static string ApprovalRequester(
        ChatPresentationState state,
        ToolInteractionRequest approval)
    {
        const string subAgentMarker = "/subagent-approval/";
        var markerIndex = approval.CallId.Value.IndexOf(subAgentMarker, StringComparison.Ordinal);
        if (markerIndex <= 0)
            return "Netclaw";

        var parentCallId = approval.CallId.Value[..markerIndex];
        var requester = state.SubAgents.Values.FirstOrDefault(run =>
            string.Equals(run.ParentCallId, parentCallId, StringComparison.Ordinal));
        return requester is null
            ? "A sub-agent"
            : requester.AgentName;
    }

    private static Color LabelColor(ChatPresentationBlock block) => block.Kind switch
    {
        ChatBlockKind.User => ChatVisualTheme.Human,
        ChatBlockKind.Assistant => ChatVisualTheme.Primary,
        ChatBlockKind.Thought => ChatVisualTheme.Warning,
        ChatBlockKind.Tool when block.IsFailure => ChatVisualTheme.Danger,
        ChatBlockKind.Tool => ChatVisualTheme.Success,
        ChatBlockKind.Parallel => ChatVisualTheme.Primary,
        ChatBlockKind.SubAgent when block.IsFailure => ChatVisualTheme.Danger,
        ChatBlockKind.SubAgent => ChatVisualTheme.Success,
        ChatBlockKind.Approval => ChatVisualTheme.Warning,
        ChatBlockKind.File => ChatVisualTheme.Primary,
        ChatBlockKind.Error => ChatVisualTheme.Danger,
        ChatBlockKind.Usage => ChatVisualTheme.Muted,
        ChatBlockKind.Compaction => ChatVisualTheme.Muted,
        ChatBlockKind.Diagnostic => ChatVisualTheme.Danger,
        _ => ChatVisualTheme.Muted
    };

    private static Color BodyColor(ChatPresentationBlock block) => block.Kind switch
    {
        ChatBlockKind.Error or ChatBlockKind.Diagnostic => ChatVisualTheme.Danger,
        ChatBlockKind.Approval when block.IsFailure => ChatVisualTheme.Danger,
        ChatBlockKind.Usage or ChatBlockKind.Compaction => ChatVisualTheme.Muted,
        _ => ChatVisualTheme.Text
    };

    private static bool UsesSurface(ChatPresentationBlock block) => block.Kind is
        ChatBlockKind.User
        or ChatBlockKind.Tool
        or ChatBlockKind.Parallel
        or ChatBlockKind.SubAgent
        or ChatBlockKind.Approval
        or ChatBlockKind.Error
        or ChatBlockKind.Diagnostic;

    private static Color SurfaceColor(ChatPresentationBlock block) => block.Kind switch
    {
        ChatBlockKind.User => ChatVisualTheme.HumanSurface,
        ChatBlockKind.Approval => ChatVisualTheme.ApprovalSurface,
        ChatBlockKind.Error or ChatBlockKind.Diagnostic => ChatVisualTheme.DangerSurface,
        _ => ChatVisualTheme.Surface
    };

    internal static string DisplayLabel(ChatBlockKind kind) => kind switch
    {
        ChatBlockKind.System => "Session",
        ChatBlockKind.User => "You",
        ChatBlockKind.Assistant => "Netclaw",
        ChatBlockKind.Thought => "Thought",
        ChatBlockKind.Tool => "Tool",
        ChatBlockKind.Parallel => "Parallel tools",
        ChatBlockKind.SubAgent => "Agent",
        ChatBlockKind.Approval => "Approval",
        ChatBlockKind.File => "File",
        ChatBlockKind.Error => "Error",
        ChatBlockKind.Usage => "Usage",
        ChatBlockKind.Compaction => "Context",
        _ => "Diagnostic"
    };

    private static string MarkdownLineToPlainText(string sourceLine)
    {
        var line = HeadingPrefixRegex().Replace(sourceLine, string.Empty);
        line = QuotePrefixRegex().Replace(line, "  ");
        line = BulletPrefixRegex().Replace(line, "$1• ");
        line = MarkdownLinkRegex().Replace(line, "$1 <$2>");
        line = InlineCodeRegex().Replace(line, "$1");
        line = StrongRegex().Replace(line, "$2");
        line = StrikeRegex().Replace(line, "$1");
        return EmphasisRegex().Replace(line, "$2");
    }

    [GeneratedRegex(@"^[ \t]{0,3}#{1,6}[ \t]+")]
    private static partial Regex HeadingPrefixRegex();

    [GeneratedRegex(@"^[ \t]{0,3}>[ \t]?")]
    private static partial Regex QuotePrefixRegex();

    [GeneratedRegex(@"^([ \t]*)[-+*][ \t]+")]
    private static partial Regex BulletPrefixRegex();

    [GeneratedRegex(@"\[([^\]\r\n]+)\]\(([^)\r\n]+)\)")]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"`([^`\r\n]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"(\*\*|__)(?=\S)(.+?\S)\1")]
    private static partial Regex StrongRegex();

    [GeneratedRegex(@"~~(?=\S)(.+?\S)~~")]
    private static partial Regex StrikeRegex();

    [GeneratedRegex(@"(?<!\w)([*_])(?=\S)(.+?\S)\1(?!\w)")]
    private static partial Regex EmphasisRegex();
}

internal static class ChatVisualTheme
{
    public static readonly Color Text = Color.FromHex("CDD6F4");
    public static readonly Color Muted = Color.FromHex("7F849C");
    public static readonly Color Primary = Color.FromHex("89B4FA");
    public static readonly Color Human = Color.FromHex("A6E3A1");
    public static readonly Color Success = Color.FromHex("A6E3A1");
    public static readonly Color Warning = Color.FromHex("F9E2AF");
    public static readonly Color Danger = Color.FromHex("F38BA8");
    public static readonly Color HeaderSurface = Color.FromHex("171A21");
    public static readonly Color Surface = Color.FromHex("171B22");
    public static readonly Color SurfaceStrong = Color.FromHex("202530");
    public static readonly Color SurfaceSelected = Color.FromHex("24354D");
    public static readonly Color HumanSurface = Color.FromHex("19221F");
    public static readonly Color ApprovalHeader = Color.FromHex("4A3D24");
    public static readonly Color ApprovalSurface = Color.FromHex("1D1C1A");
    public static readonly Color DangerSurface = Color.FromHex("281A20");
}
