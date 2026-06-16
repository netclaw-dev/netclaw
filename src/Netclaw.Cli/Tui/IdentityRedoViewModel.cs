// -----------------------------------------------------------------------
// <copyright file="IdentityRedoViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui;

/// <summary>
/// "Redo identity setup" flow reached from the existing-install menu. Hosts the
/// init-owned identity step single-step and, on completion, rewrites ONLY the identity
/// files — it deliberately does not call <see cref="WizardOrchestrator.WriteConfig"/>,
/// which would clobber the existing <c>netclaw.json</c> with bootstrap defaults
/// (simplify-netclaw-init: identity stays init-owned and is editable on its own).
/// </summary>
public sealed class IdentityRedoViewModel : ReactiveViewModel
{
    private readonly WizardContext _context;
    private readonly WizardOrchestrator _orchestrator;
    private readonly IdentityStepViewModel _step;
    private readonly NetclawPaths _paths;

    public IdentityRedoViewModel(NetclawPaths paths)
    {
        _paths = paths;
        _step = new IdentityStepViewModel();
        _context = new WizardContext
        {
            Paths = paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = RequestRedraw,
            ExistingConfig = ConfigFileHelper.LoadJsonDictOrNull(paths.NetclawConfigPath),
        };
        _orchestrator = new WizardOrchestrator([_step], _context, singleStepMode: true);
    }

    public WizardContext Context => _context;
    public IdentityStepViewModel Step => _step;
    public IdentityStepView StepView { get; } = new();
    public ReactiveProperty<bool> IsSaved { get; } = new(false);
    public Action? OnStepContentChanged { get; set; }

    public void GoNext()
    {
        if (IsSaved.Value)
        {
            Shutdown();
            return;
        }

        if (_orchestrator.GoNext())
        {
            _context.StatusMessage.Value = "";
            NotifyContentChanged();
            return;
        }

        // Identity collected. Rewrite identity files only; built-in agents are left
        // untouched so a redo never clobbers customized agent definitions.
        _step.WriteIdentityFiles(_paths);
        IsSaved.Value = true;
        _context.StatusMessage.Value = "Identity updated. Run `netclaw chat` to talk to your agent.";
        NotifyContentChanged();
    }

    public void GoBack()
    {
        if (IsSaved.Value)
        {
            Shutdown();
            return;
        }

        if (_orchestrator.GoBack())
        {
            _context.StatusMessage.Value = "";
            NotifyContentChanged();
            return;
        }

        // Esc at the first identity field returns to the existing-install menu.
        Navigate?.Invoke(InitExistingInstallViewModel.MenuRoute);
    }

    public void RequestQuit() => Shutdown();

    private void NotifyContentChanged()
    {
        OnStepContentChanged?.Invoke();
        RequestRedraw();
    }

    public override void Dispose()
    {
        IsSaved.Dispose();
        _orchestrator.Dispose();
        _context.Dispose();
        base.Dispose();
    }
}
