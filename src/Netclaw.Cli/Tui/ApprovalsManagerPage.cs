// -----------------------------------------------------------------------
// <copyright file="ApprovalsManagerPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using R3;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Termina page for <c>netclaw approvals</c>. Lists persistent approvals
/// grouped by audience and tool; supports revoke via Delete or R with a
/// confirmation step. Read-and-revoke only — the page never adds grants.
/// </summary>
public sealed class ApprovalsManagerPage : ReactivePage<ApprovalsManagerViewModel>
{
    private SelectionListNode<string>? _approvalList;
    private SelectionListNode<string>? _confirmList;
    private DynamicLayoutNode? _contentNode;
    private readonly CompositeDisposable _stepSubs = [];

    protected override void OnBound()
    {
        base.OnBound();
        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
    {
        return Layouts.Vertical()
            .WithChild(
                new PanelNode()
                    .WithTitle("Approvals Manager")
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
            .WithChild(BuildStatusBar())
            .WithChild(BuildKeyBindings());
    }

    private LayoutNode BuildContent()
    {
        _contentNode = new DynamicLayoutNode(() =>
        {
            _stepSubs.Clear();

            return ViewModel.CurrentState.Value switch
            {
                ApprovalsManagerState.Loading => BuildLoadingView(),
                ApprovalsManagerState.Empty => BuildEmptyView(),
                ApprovalsManagerState.List => BuildListView(),
                ApprovalsManagerState.RevokeConfirm => BuildRevokeConfirmView(),
                _ => Layouts.Empty()
            };
        });

        ViewModel.StateVersion
            .Subscribe(_ => _contentNode.Invalidate())
            .DisposeWith(Subscriptions);

        // Fill the panel: the list view inside relies on a Fill-constrained
        // ancestor to receive the terminal's full height (see BuildListView).
        return _contentNode.Fill();
    }

    private LayoutNode BuildStatusBar()
    {
        return ViewModel.StatusMessage
            .Select(msg => (ILayoutNode)(string.IsNullOrWhiteSpace(msg)
                ? Layouts.Empty()
                : new TextNode($"  {msg}").WithForeground(Color.Green)))
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
                    ApprovalsManagerState.Empty =>
                        " [Ctrl+Q] Quit",
                    ApprovalsManagerState.List =>
                        " [↑/↓] Navigate  [R/Del] Revoke  [Ctrl+Q] Quit",
                    ApprovalsManagerState.RevokeConfirm =>
                        " [Enter] Confirm  [Esc] Cancel  [Ctrl+Q] Quit",
                    _ =>
                        " [Ctrl+Q] Quit"
                };
                return (ILayoutNode)new TextNode(text).WithForeground(Color.BrightBlack);
            })
            .AsLayout()
            .Height(1);
    }

    private ILayoutNode BuildLoadingView()
        => new TextNode("  Loading approvals...").WithForeground(Color.Yellow);

    private ILayoutNode BuildEmptyView()
    {
        return Layouts.Vertical()
            .WithChild(new TextNode("  No persistent approvals.").WithForeground(Color.White))
            .WithChild(new TextNode("").Height(1))
            .WithChild(new TextNode("  Approvals are stored when a session prompts you to ")
                .WithForeground(Color.Gray))
            .WithChild(new TextNode("  \"Approve always\" for a shell command or directory root.")
                .WithForeground(Color.Gray));
    }

    private ILayoutNode BuildListView()
    {
        var rows = ViewModel.DisplayApprovals
            .Select(item => $"{item.AudienceWire,-10} {item.ToolName,-20} {item.DisplayText,-44} {item.AddedText}")
            .ToList();

        _approvalList = Layouts.SelectionList(rows)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _approvalList.OnFocused();

        // Enter on the list also acts as "begin revoke confirm" for the highlighted row.
        _approvalList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0) return;
                var idx = rows.IndexOf(selected[0]);
                if (idx < 0) return;
                ViewModel.SelectedIndex = idx;
                ViewModel.StartRevoke();
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode($"  {"Audience",-10} {"Tool",-20} {"Approval",-44} Added")
                .WithForeground(Color.White).Bold())
            .WithChild(_approvalList.WithFillHeight());
    }

    private ILayoutNode BuildRevokeConfirmView()
    {
        var target = ViewModel.PendingRevoke;
        var summary = target is null
            ? "  Revoke entry?"
            : $"  Revoke '{target.DisplayText}' from {target.AudienceWire} / {target.ToolName}?";

        var items = new List<string> { "Yes, revoke", "No, cancel" };
        _confirmList = Layouts.SelectionList(items)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Red);

        _confirmList.OnFocused();

        _confirmList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0 && selected[0].StartsWith("Yes", StringComparison.Ordinal))
                    ViewModel.ConfirmRevoke();
                else
                    ViewModel.GoBack();
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode(summary).WithForeground(Color.Yellow))
            .WithChild(_confirmList);
    }

    private void HandleKeyPress(KeyPressed key)
    {
        var state = ViewModel.CurrentState.Value;
        var keyInfo = key.KeyInfo;

        if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestQuit();
            return;
        }

        if (keyInfo.Key == ConsoleKey.Escape)
        {
            if (state == ApprovalsManagerState.RevokeConfirm)
                ViewModel.GoBack();
            // List/Empty: no parent to return to; Ctrl+Q quits.
            return;
        }

        if (state == ApprovalsManagerState.List)
        {
            if (keyInfo.Key == ConsoleKey.R || keyInfo.Key == ConsoleKey.Delete)
            {
                ViewModel.StartRevoke();
                return;
            }

            _approvalList?.HandleInput(keyInfo);
            ViewModel.RequestRedraw();
            return;
        }

        if (state == ApprovalsManagerState.RevokeConfirm)
        {
            _confirmList?.HandleInput(keyInfo);
            ViewModel.RequestRedraw();
            return;
        }
    }
}
