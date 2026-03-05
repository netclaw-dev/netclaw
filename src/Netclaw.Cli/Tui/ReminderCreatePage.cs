using R3;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui;

public sealed class ReminderCreatePage : ReactivePage<ReminderCreateViewModel>
{
    private DynamicLayoutNode? _contentNode;

    private TextInputNode _titleInput = null!;
    private SelectionListNode<string> _scheduleTypeList = null!;
    private TextInputNode _scheduleInput = null!;
    private TextAreaNode _instructionsInput = null!;
    private TextAreaNode _notifyInput = null!;
    private SelectionListNode<string> _confirmList = null!;
    private SelectionListNode<string> _doneList = null!;

    protected override void OnBound()
    {
        base.OnBound();

        _titleInput = new TextInputNode().WithPlaceholder("daily-standup");
        _titleInput.Submitted
            .Subscribe(ViewModel.SetTitle)
            .DisposeWith(Subscriptions);

        _scheduleTypeList = Layouts.SelectionList(new List<string> { "once", "interval", "cron" })
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);
        _scheduleTypeList.SelectionConfirmed
            .Subscribe(items =>
            {
                if (items.Count > 0)
                    ViewModel.SetScheduleType(items[0]);
            })
            .DisposeWith(Subscriptions);

        _scheduleInput = new TextInputNode().WithPlaceholder("30m / every 6h / 0 */6 * * *");
        _scheduleInput.Submitted
            .Subscribe(ViewModel.SetSchedule)
            .DisposeWith(Subscriptions);

        _instructionsInput = new TextAreaNode()
            .WithPlaceholder("What Netclaw should do when this reminder fires...")
            .WithMaxHeight(10)
            .WithHistory(20);
        _instructionsInput.Submitted
            .Subscribe(ViewModel.SetInstructions)
            .DisposeWith(Subscriptions);

        _notifyInput = new TextAreaNode()
            .WithPlaceholder("How Netclaw should notify you about results...")
            .WithMaxHeight(8)
            .WithHistory(20);
        _notifyInput.Submitted
            .Subscribe(ViewModel.SetNotifyInstructions)
            .DisposeWith(Subscriptions);

        _confirmList = Layouts.SelectionList(new List<string> { "Create reminder", "Back" })
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Green);
        _confirmList.SelectionConfirmed
            .Subscribe(items =>
            {
                if (items.Count == 0)
                    return;

                if (items[0] == "Create reminder")
                    _ = ViewModel.SubmitAsync();
                else
                    ViewModel.GoBack();
            })
            .DisposeWith(Subscriptions);

        _doneList = Layouts.SelectionList(new List<string> { "Create another", "Quit" })
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);
        _doneList.SelectionConfirmed
            .Subscribe(items =>
            {
                if (items.Count == 0)
                    return;

                if (items[0] == "Create another")
                    ViewModel.Reset();
                else
                    ViewModel.RequestQuit();
            })
            .DisposeWith(Subscriptions);

        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);

        ViewModel.Input.OfType<IInputEvent, PasteEvent>()
            .Subscribe(HandlePaste)
            .DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
    {
        return Layouts.Vertical()
            .WithChild(
                new PanelNode()
                    .WithTitle("Reminder Builder")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Cyan)
                    .WithContent(BuildInnerLayout())
                    .Fill());
    }

    private ILayoutNode BuildInnerLayout()
    {
        return Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(BuildContent())
            .WithChild(Layouts.Empty().Fill())
            .WithChild(BuildStatusBar())
            .WithChild(BuildKeyBindings());
    }

    private LayoutNode BuildContent()
    {
        _contentNode = new DynamicLayoutNode(() =>
            ViewModel.CurrentState.Value switch
            {
                ReminderCreateState.Title => BuildTitle(),
                ReminderCreateState.ScheduleType => BuildScheduleType(),
                ReminderCreateState.Schedule => BuildSchedule(),
                ReminderCreateState.Instructions => BuildInstructions(),
                ReminderCreateState.NotifyInstructions => BuildNotifyInstructions(),
                ReminderCreateState.Confirm => BuildConfirm(),
                ReminderCreateState.Done => BuildDone(),
                _ => Layouts.Empty()
            });

        ViewModel.StateVersion
            .Subscribe(_ => _contentNode.Invalidate())
            .DisposeWith(Subscriptions);

        return _contentNode;
    }

    private LayoutNode BuildStatusBar()
    {
        return ViewModel.StatusMessage
            .Select(msg => (ILayoutNode)new TextNode($"  {msg}").WithForeground(Color.Green))
            .AsLayout()
            .Height(1);
    }

    private LayoutNode BuildKeyBindings()
    {
        return ViewModel.CurrentState
            .Select(state =>
            {
                var text = state switch
                {
                    ReminderCreateState.Title => " [Enter] Next  [Esc] Quit  [Ctrl+Q] Quit",
                    ReminderCreateState.ScheduleType => " [Up/Down] Select  [Enter] Next  [Esc] Back  [Ctrl+Q] Quit",
                    ReminderCreateState.Schedule => " [Enter] Next  [Esc] Back  [Ctrl+Q] Quit",
                    ReminderCreateState.Instructions => " [Ctrl+Enter] Newline  [Enter] Next  [Esc] Back  [Ctrl+Q] Quit",
                    ReminderCreateState.NotifyInstructions => " [Ctrl+Enter] Newline  [Enter] Next  [Esc] Back  [Ctrl+Q] Quit",
                    ReminderCreateState.Confirm => " [Up/Down] Select  [Enter] Confirm  [Esc] Back  [Ctrl+Q] Quit",
                    ReminderCreateState.Done => " [Up/Down] Select  [Enter] Continue  [Esc] Quit  [Ctrl+Q] Quit",
                    _ => " [Ctrl+Q] Quit"
                };
                return (ILayoutNode)new TextNode(text).WithForeground(Color.BrightBlack);
            })
            .AsLayout()
            .Height(1);
    }

    private ILayoutNode BuildTitle()
    {
        _titleInput.OnFocused();
        return Layouts.Vertical()
            .WithChild(new TextNode("  Step 1 of 6: Reminder title").WithForeground(Color.White).Bold())
            .WithChild(new TextNode("  Choose a short identifier for this reminder.").WithForeground(Color.Gray))
            .WithChild(new PanelNode()
                .WithTitle("Title")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_titleInput)
                .Height(3));
    }

    private ILayoutNode BuildScheduleType()
    {
        _scheduleTypeList.OnFocused();
        return Layouts.Vertical()
            .WithChild(new TextNode("  Step 2 of 6: Schedule type").WithForeground(Color.White).Bold())
            .WithChild(new TextNode("  once: one-time, interval: repeating duration, cron: calendar expression.")
                .WithForeground(Color.Gray))
            .WithChild(_scheduleTypeList);
    }

    private ILayoutNode BuildSchedule()
    {
        _scheduleInput.OnFocused();
        var hint = ViewModel.ScheduleType switch
        {
            "once" => "Example: 30m or 2026-03-05T18:00:00Z",
            "interval" => "Example: 15m, 2h, every 1d",
            "cron" => "Example: 0 */6 * * *",
            _ => "Example: 30m"
        };

        return Layouts.Vertical()
            .WithChild(new TextNode("  Step 3 of 6: Schedule value").WithForeground(Color.White).Bold())
            .WithChild(new TextNode($"  {hint}").WithForeground(Color.Gray))
            .WithChild(new PanelNode()
                .WithTitle("Schedule")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_scheduleInput)
                .Height(3));
    }

    private ILayoutNode BuildInstructions()
    {
        _instructionsInput.OnFocused();
        return Layouts.Vertical()
            .WithChild(new TextNode("  Step 4 of 6: Instructions").WithForeground(Color.White).Bold())
            .WithChild(new TextNode("  Describe exactly what Netclaw should do.").WithForeground(Color.Gray))
            .WithChild(new PanelNode()
                .WithTitle("Instructions")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_instructionsInput)
                .HeightAuto(min: 5, max: 12));
    }

    private ILayoutNode BuildNotifyInstructions()
    {
        _notifyInput.OnFocused();
        return Layouts.Vertical()
            .WithChild(new TextNode("  Step 5 of 6: Notify instructions").WithForeground(Color.White).Bold())
            .WithChild(new TextNode("  Explain how and when Netclaw should notify you.").WithForeground(Color.Gray))
            .WithChild(new PanelNode()
                .WithTitle("Notify Instructions")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_notifyInput)
                .HeightAuto(min: 4, max: 10));
    }

    private ILayoutNode BuildConfirm()
    {
        _confirmList.OnFocused();

        return Layouts.Vertical()
            .WithChild(new TextNode("  Step 6 of 6: Review").WithForeground(Color.White).Bold())
            .WithChild(new TextNode($"  Title: {ViewModel.Title}").WithForeground(Color.White))
            .WithChild(new TextNode($"  Type: {ViewModel.ScheduleType}").WithForeground(Color.White))
            .WithChild(new TextNode($"  Schedule: {ViewModel.Schedule}").WithForeground(Color.White))
            .WithChild(new TextNode("  Instructions:").WithForeground(Color.Gray))
            .WithChild(new TextNode($"    {TrimForPreview(ViewModel.Instructions)}").WithForeground(Color.White))
            .WithChild(new TextNode("  Notify Instructions:").WithForeground(Color.Gray))
            .WithChild(new TextNode($"    {TrimForPreview(ViewModel.NotifyInstructions)}").WithForeground(Color.White))
            .WithChild(new TextNode("").Height(1))
            .WithChild(_confirmList);
    }

    private ILayoutNode BuildDone()
    {
        _doneList.OnFocused();
        return Layouts.Vertical()
            .WithChild(new TextNode("  Reminder created.").WithForeground(Color.Green).Bold())
            .WithChild(new TextNode("  What do you want to do next?").WithForeground(Color.Gray))
            .WithChild(_doneList);
    }

    private void HandleKeyPress(KeyPressed key)
    {
        var keyInfo = key.KeyInfo;

        if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestQuit();
            return;
        }

        if (keyInfo.Key == ConsoleKey.Escape)
        {
            ViewModel.GoBack();
            return;
        }

        if (ViewModel.IsSubmitting.Value)
            return;

        switch (ViewModel.CurrentState.Value)
        {
            case ReminderCreateState.Title:
                _titleInput.HandleInput(keyInfo);
                break;
            case ReminderCreateState.ScheduleType:
                _scheduleTypeList.HandleInput(keyInfo);
                break;
            case ReminderCreateState.Schedule:
                _scheduleInput.HandleInput(keyInfo);
                break;
            case ReminderCreateState.Instructions:
                _instructionsInput.HandleInput(keyInfo);
                break;
            case ReminderCreateState.NotifyInstructions:
                _notifyInput.HandleInput(keyInfo);
                break;
            case ReminderCreateState.Confirm:
                _confirmList.HandleInput(keyInfo);
                break;
            case ReminderCreateState.Done:
                _doneList.HandleInput(keyInfo);
                break;
        }

        ViewModel.RequestRedraw();
    }

    private void HandlePaste(PasteEvent paste)
    {
        if (ViewModel.CurrentState.Value == ReminderCreateState.Instructions)
            _instructionsInput.HandlePaste(paste);
        else if (ViewModel.CurrentState.Value == ReminderCreateState.NotifyInstructions)
            _notifyInput.HandlePaste(paste);
    }

    private static string TrimForPreview(string text)
    {
        var singleLine = text.Replace('\n', ' ').Trim();
        if (singleLine.Length <= 100)
            return singleLine;
        return string.Concat(singleLine.AsSpan(0, 97), "...");
    }
}
