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
/// (menu → reset scope → two confirmations → progress), so the destructive path is
/// explicit and double-confirmed (simplify-netclaw-init).
/// </summary>
public sealed class InitExistingInstallPage : ReactivePage<InitExistingInstallViewModel>
{
    private SelectionListNode<string>? _list;
    private DynamicLayoutNode? _bodyNode;
    private DynamicLayoutNode? _keyBindingsNode;
    private readonly CompositeDisposable _phaseSubs = [];

    protected override void OnBound()
    {
        base.OnBound();

        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);

        // Rebuild dynamic chrome when the phase changes so options and key hints stay in sync.
        ViewModel.CurrentPhase
            .Subscribe(_ =>
            {
                _bodyNode?.Invalidate();
                _keyBindingsNode?.Invalidate();
            })
            .DisposeWith(Subscriptions);

        ViewModel.CurrentProgressStep
            .Subscribe(_ => _bodyNode?.Invalidate())
            .DisposeWith(Subscriptions);

        ViewModel.ProgressMessage
            .Subscribe(_ => _bodyNode?.Invalidate())
            .DisposeWith(Subscriptions);

        ViewModel.CanQuitProgress
            .Subscribe(_ => _keyBindingsNode?.Invalidate())
            .DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
    {
        return NetclawTuiChrome.BuildPageFrame("Netclaw Setup", BuildInnerLayout());
    }

    private ILayoutNode BuildInnerLayout()
    {
        _bodyNode = new DynamicLayoutNode(BuildBody);
        _keyBindingsNode = new DynamicLayoutNode(BuildKeyBindings);
        return Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(_bodyNode)
            .WithChild(Layouts.Empty().Fill())
            .WithChild(BuildStatusBar())
            .WithChild(_keyBindingsNode);
    }

    private ILayoutNode BuildBody()
    {
        // Each rebuild creates a fresh list + subscription; clear the previous one so
        // SelectionConfirmed handlers don't accumulate across phase changes (#792).
        _phaseSubs.Clear();

        var phase = ViewModel.CurrentPhase.Value;

        // ── Progress screen ──
        if (phase == InitExistingInstallViewModel.Phase.Progress)
        {
            _list = null;
            return BuildProgressScreen();
        }

        // ── Menu / confirmation screens ──
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

    /// <summary>
    /// Builds the reset progress screen. Each step renders as a line:
    ///   ✓ Completed steps (green checkmark)
    ///   🔄 Current step (spinning dots, bound to CurrentProgressStep)
    ///   Pending steps show their label without any indicator.
    /// </summary>
    private ILayoutNode BuildProgressScreen()
    {
        var progressStep = ViewModel.CurrentProgressStep.Value;

        var stepLabels = new[]
        {
            "Stopping daemon…",
            ViewModel.Scope == InitExistingInstallViewModel.ResetScopeKind.Full
                ? "Deleting data…"
                : "Deleting setup files…",
            "Purge complete",
        };

        var lines = Layouts.Vertical().WithSpacing(1);

        // Header
        lines.WithChild(new TextNode("  Resetting…").WithForeground(Color.White).Bold());

        // Progress steps
        for (var i = 0; i < stepLabels.Length; i++)
        {
            ILayoutNode line;
            if (i < progressStep)
            {
                // Already completed — green checkmark
                line = new TextNode($"  ✓ {stepLabels[i].Replace("…", "")}").WithForeground(Color.Green);
            }
            else if (i == progressStep)
            {
                // In progress — spinning dots
                var color = i == 0 ? Color.Yellow : Color.Cyan;
                line = SpinnerViews.Labeled(stepLabels[i].Replace("…", ""), color);
            }
            else
            {
                // Pending — dimmed label
                line = new TextNode($"  • {stepLabels[i].Replace("…", "")}").WithForeground(Color.Gray);
            }

            lines.WithChild(line);
        }

        // Error message if something went wrong
        if (ViewModel.ProgressMessage.Value.StartsWith("Reset failed:", StringComparison.Ordinal))
            lines.WithChild(new TextNode($"  {ViewModel.ProgressMessage.Value}").WithForeground(Color.Red).Bold());

        return Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(new TextNode("  ").WithForeground(Color.Black)) // top padding
            .WithChild(lines)
            .WithChild(new TextNode("  ").WithForeground(Color.Black)); // bottom padding
    }

    private LayoutNode BuildStatusBar()
    {
        return ViewModel.StatusMessage
            .Select(msg => NetclawTuiChrome.BuildStatusLine(msg, Color.Yellow))
            .AsLayout()
            .Height(1);
    }

    private ILayoutNode BuildKeyBindings()
    {
        var phase = ViewModel.CurrentPhase.Value;
        if (phase == InitExistingInstallViewModel.Phase.Progress)
            return NetclawTuiChrome.BuildKeyHintLine(ViewModel.CanQuitProgress.Value
                ? " [Ctrl+Q] Quit"
                : " Reset in progress — deletion cannot be interrupted");

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

        if (ViewModel.CurrentPhase.Value == InitExistingInstallViewModel.Phase.Progress)
            return;

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
