// -----------------------------------------------------------------------
// <copyright file="ExposureModeConfigViewModel.cs" company="Petabridge, LLC">
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

namespace Netclaw.Cli.Tui.Config;

public sealed class ExposureModeConfigViewModel : ReactiveViewModel
{
    private readonly WizardContext _context;
    private readonly WizardOrchestrator _orchestrator;
    private readonly ExposureModeStepViewModel _step;

    public ExposureModeConfigViewModel(NetclawPaths paths)
    {
        _step = new ExposureModeStepViewModel(includeWebhookToggle: false);
        _context = new WizardContext
        {
            Paths = paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = RequestRedraw,
            ExistingConfig = LoadExistingConfig(paths)
        };
        _orchestrator = new WizardOrchestrator([_step], _context, singleStepMode: true);
    }

    internal Action<string>? RouteRequested { get; set; }
    public WizardContext Context => _context;
    public WizardOrchestrator Orchestrator => _orchestrator;
    public ExposureModeStepViewModel Step => _step;
    public ExposureModeStepView StepView { get; } = new();
    public ReactiveProperty<bool> IsSaved { get; } = new(false);
    public Action? OnStepContentChanged { get; set; }

    public void GoNext()
    {
        if (IsSaved.Value)
        {
            BackToSecurityAccess();
            return;
        }

        if (_orchestrator.GoNext())
        {
            _context.StatusMessage.Value = "";
            NotifyContentChanged();
            return;
        }

        if (_step.GetStructuralValidationError() is { } validationError)
        {
            _context.StatusMessage.Value = validationError;
            NotifyContentChanged();
            return;
        }

        if (_step.GetBootstrapPairingValidationError(_context.Paths) is { } pairingError)
        {
            _context.StatusMessage.Value = pairingError;
            NotifyContentChanged();
            return;
        }

        _orchestrator.WriteConfig();
        IsSaved.Value = true;
        _context.StatusMessage.Value = "Exposure mode saved.";
        NotifyContentChanged();
    }

    public void GoBack()
    {
        if (IsSaved.Value)
        {
            IsSaved.Value = false;
            _step.ReturnToModeSelection();
            _context.StatusMessage.Value = "";
            NotifyContentChanged();
            return;
        }

        if (_orchestrator.GoBack())
        {
            _context.StatusMessage.Value = "";
            NotifyContentChanged();
            return;
        }

        BackToSecurityAccess();
    }

    public void RequestQuit() => Shutdown();

    private void BackToSecurityAccess()
    {
        RouteRequested?.Invoke("/security");
        Navigate?.Invoke("/security");
    }

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

    private static Dictionary<string, object>? LoadExistingConfig(NetclawPaths paths)
    {
        if (!File.Exists(paths.NetclawConfigPath))
            return null;

        var config = ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);
        return config.Count == 0 ? null : config;
    }
}
