// -----------------------------------------------------------------------
// <copyright file="ExposureModeConfigPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Workflow;
using Netclaw.Configuration;
using R3;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Config;

public sealed class ExposureModeConfigPage : ReactivePage<ExposureModeConfigViewModel>
{
    private DynamicLayoutNode? _contentNode;
    private DynamicLayoutNode? _helpTextNode;
    private readonly CompositeDisposable _stepSubs = [];

    protected override void OnBound()
    {
        base.OnBound();

        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);

        ViewModel.Input.OfType<IInputEvent, PasteEvent>()
            .Subscribe(HandlePaste)
            .DisposeWith(Subscriptions);

        ViewModel.OnStepContentChanged = () =>
        {
            _stepSubs.Clear();
            _contentNode?.Invalidate();
            _helpTextNode?.Invalidate();
        };
    }

    public override ILayoutNode BuildLayout()
        => NetclawTuiChrome.BuildPageFrame("Exposure Mode", BuildInnerLayout());

    private ILayoutNode BuildInnerLayout()
        => Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(BuildContent())
            .WithChild(BuildHelpText())
            .WithChild(Layouts.Empty().Fill())
            .WithChild(BuildStatusBar())
            .WithChild(BuildKeyBindings());

    private LayoutNode BuildContent()
    {
        _contentNode = new DynamicLayoutNode(() =>
        {
            if (ViewModel.IsSaved.Value)
            {
                var modeLabel = FormatModeLabel(ViewModel.Step.SelectedMode);
                return WorkflowViewComponents.BuildSavedScreen(
                    $"{modeLabel} exposure mode saved.",
                    "Press Esc to review exposure modes or Enter to return to Security & Access.");
            }

            ViewModel.StepView.ClearFocusState();
            return ViewModel.StepView.BuildContent(ViewModel.Step, CreateCallbacks());
        });

        return _contentNode;
    }

    private LayoutNode BuildHelpText()
    {
        _helpTextNode = new DynamicLayoutNode(() =>
        {
            if (ViewModel.IsSaved.Value)
                return (ILayoutNode)new TextNode("  Saved state is local to this editor; Esc returns to the mode list first.").WithForeground(Color.Gray);

            return (ILayoutNode)new TextNode(ViewModel.Step.GetHelpText()).WithForeground(Color.Gray);
        });

        return _helpTextNode.Height(2);
    }

    private LayoutNode BuildStatusBar()
        => ViewModel.Context.StatusMessage
            .Select(msg => (ILayoutNode)(string.IsNullOrWhiteSpace(msg)
                ? Layouts.Empty()
                : new TextNode($"  {msg}").WithForeground(Color.Green)))
            .AsLayout()
            .Height(1);

    private LayoutNode BuildKeyBindings()
        => ViewModel.IsSaved
            .Select(saved => (ILayoutNode)new TextNode(saved
                    ? " [Enter] Security & Access  [Esc] Review modes  [Ctrl+Q] Quit"
                    : " [↑/↓] Navigate  [Enter] Next/Save  [Esc] Back  [Ctrl+Q] Quit")
                .WithForeground(Color.BrightBlack))
            .AsLayout()
            .Height(1);

    public override bool HandlePageInput(ConsoleKeyInfo keyInfo)
    {
        if (base.HandlePageInput(keyInfo))
            return true;

        if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestQuit();
            return true;
        }

        return false;
    }

    private void HandleKeyPress(KeyPressed key)
    {
        var keyInfo = key.KeyInfo;
        if (keyInfo.Key == ConsoleKey.Escape)
        {
            ViewModel.GoBack();
            return;
        }

        if (ViewModel.IsSaved.Value && keyInfo.Key == ConsoleKey.Enter)
        {
            ViewModel.GoNext();
            return;
        }

        ViewModel.StepView.HandleKeyPress(key);
        ViewModel.RequestRedraw();
    }

    private void HandlePaste(PasteEvent paste)
    {
        ViewModel.StepView.HandlePaste(paste);
        ViewModel.RequestRedraw();
    }

    private StepViewCallbacks CreateCallbacks()
        => new()
        {
            Subscriptions = _stepSubs,
            InvalidateContent = () => _contentNode?.Invalidate(),
            InvalidateHelp = () => _helpTextNode?.Invalidate(),
            AdvanceStep = ViewModel.GoNext,
            RequestRedraw = ViewModel.RequestRedraw,
        };

    private static string FormatModeLabel(ExposureMode mode)
        => mode switch
        {
            ExposureMode.Local => "Local",
            ExposureMode.ReverseProxy => "Reverse Proxy",
            ExposureMode.TailscaleServe => "Tailscale Serve",
            ExposureMode.TailscaleFunnel => "Tailscale Funnel",
            ExposureMode.CloudflareTunnel => "Cloudflare Tunnel",
            _ => mode.ToString()
        };

    public override void Dispose()
    {
        _stepSubs.Dispose();
        base.Dispose();
    }
}
