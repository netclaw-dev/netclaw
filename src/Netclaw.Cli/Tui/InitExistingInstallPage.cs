// -----------------------------------------------------------------------
// <copyright file="InitExistingInstallPage.cs" company="Petabridge, LLC">
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
/// Termina page for the existing-install menu and its in-place start-over flow.
/// Renders a single selection list whose contents follow the ViewModel's phase
/// (menu → reset scope → two confirmations), so the destructive path is explicit and
/// double-confirmed (simplify-netclaw-init).
/// </summary>
public sealed class InitExistingInstallPage : ReactivePage<InitExistingInstallViewModel>
{
    private SelectionListNode<string>? _list;
    private DynamicLayoutNode? _bodyNode;
    private readonly CompositeDisposable _phaseSubs = [];

    protected override void OnBound()
    {
        base.OnBound();

        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);

        // Rebuild the body when the phase changes so the list reflects the new options.
        ViewModel.CurrentPhase
            .Subscribe(_ => _bodyNode?.Invalidate())
            .DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
    {
        return NetclawTuiChrome.BuildPageFrame("Netclaw Setup", BuildInnerLayout());
    }

    private ILayoutNode BuildInnerLayout()
    {
        _bodyNode = new DynamicLayoutNode(BuildBody);
        return Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(_bodyNode)
            .WithChild(Layouts.Empty().Fill())
            .WithChild(BuildStatusBar())
            .WithChild(BuildKeyBindings());
    }

    private ILayoutNode BuildBody()
    {
        // Each rebuild creates a fresh list + subscription; clear the previous one so
        // SelectionConfirmed handlers don't accumulate across phase changes (#792).
        _phaseSubs.Clear();

        var phase = ViewModel.CurrentPhase.Value;
        var header = Layouts.Vertical().WithSpacing(0);

        switch (phase)
        {
            case InitExistingInstallViewModel.Phase.Menu:
                header.WithChild(new TextNode("  Existing Netclaw install detected.").WithForeground(Color.White).Bold());
                header.WithChild(new TextNode("  Your current config is untouched until you confirm an action.").WithForeground(Color.Gray));
                break;
            case InitExistingInstallViewModel.Phase.ResetScope:
                header.WithChild(new TextNode("  Start over from scratch — choose a scope:").WithForeground(Color.White).Bold());
                break;
            default:
                var full = ViewModel.Scope == InitExistingInstallViewModel.ResetScopeKind.Full;
                var n = phase == InitExistingInstallViewModel.Phase.ResetConfirm1 ? 1 : 2;
                header.WithChild(new TextNode($"  ⚠  {(full ? "Full reset" : "Reset setup")} — confirmation {n} of 2")
                    .WithForeground(Color.Yellow).Bold());
                header.WithChild(new TextNode(full
                        ? "  This permanently deletes config, memory, sessions, and secrets. This cannot be undone."
                        : "  This re-runs setup. Memory, sessions, and skills are kept.")
                    .WithForeground(Color.Gray));
                break;
        }

        var rows = ViewModel.CurrentItems
            .Select(item => $"{item.Label,-26} {item.Description}")
            .ToList();

        _list = Layouts.SelectionList(rows)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);
        _list.OnFocused();
        _list.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0)
                    return;
                var index = rows.IndexOf(selected[0]);
                if (index >= 0)
                {
                    ViewModel.SelectedIndex.Value = index;
                    ViewModel.ActivateSelected();
                }
            })
            .DisposeWith(_phaseSubs);

        return Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(header)
            .WithChild(_list);
    }

    private LayoutNode BuildStatusBar()
    {
        return ViewModel.StatusMessage
            .Select(msg => NetclawTuiChrome.BuildStatusLine(msg, Color.Yellow))
            .AsLayout()
            .Height(1);
    }

    private LayoutNode BuildKeyBindings()
    {
        return NetclawTuiChrome.BuildKeyHintLine(" [↑/↓] Navigate  [Enter] Select  [Esc] Back  [Ctrl+Q] Quit");
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

        _list?.HandleInput(keyInfo);
        ViewModel.RequestRedraw();
    }

    public override void Dispose()
    {
        _phaseSubs.Dispose();
        base.Dispose();
    }
}
