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
        // Degrade to "no existing config" on a malformed/unreadable netclaw.json rather than throwing
        // from the constructor (which would make the Exposure page permanently inaccessible).
        var existingConfig = ConfigFileHelper.TryLoadJsonDictOrNull(paths.NetclawConfigPath, out var loadError);
        _context = new WizardContext
        {
            Paths = paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = RequestRedraw,
            ExistingConfig = existingConfig
        };
        if (loadError is not null)
            _context.StatusMessage.Value = loadError;
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

        try
        {
            _orchestrator.WriteConfig();

            // Keep the configuring client authenticated after switching to a non-local mode. WriteConfig
            // already auto-pairs a fully fresh install (the wizard bootstrap path); this also covers
            // leftover/partial pairing state so `netclaw config` never locks the operator out of chat.
            _step.EnsureCurrentClientPaired(_context.Paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A disk-full / permission-denied / atomic-rename failure here must surface to the operator,
            // not escalate as an unhandled exception that tears down the Termina event loop. Leave
            // IsSaved false so the UI never claims a save that did not fully complete.
            _context.StatusMessage.Value = $"Failed to save exposure mode: {ex.Message}";
            NotifyContentChanged();
            return;
        }

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
}
