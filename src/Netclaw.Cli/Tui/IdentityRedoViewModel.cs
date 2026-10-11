// -----------------------------------------------------------------------
// <copyright file="IdentityRedoViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui.Sections;
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
/// files and the <c>Identity</c> section of <c>netclaw.json</c> — it deliberately does not call
/// <see cref="WizardOrchestrator.WriteConfig"/>, which would clobber the rest of the file with
/// bootstrap defaults (simplify-netclaw-init: identity stays init-owned and is editable on its own).
/// After a successful save the operator can start the guided identity chat, which hands
/// the same onboarding trigger as the full wizard to <see cref="ChatNavigationState"/>.
/// </summary>
public sealed class IdentityRedoViewModel : ReactiveViewModel
{
    private readonly WizardContext _context;
    private readonly WizardOrchestrator _orchestrator;
    private readonly IdentityStepViewModel _step;
    private readonly NetclawPaths _paths;
    private readonly ChatNavigationState _chatNavigationState;
    private readonly HealthCheckStepViewModel _daemonReadiness;
    private readonly CancellationTokenSource _lifetime = new();
    private (bool WasRunning, int? GenerationBefore) _daemonBeforeSave;
    internal Task OperationCompletion { get; private set; } = Task.CompletedTask;

    public IdentityRedoViewModel(NetclawPaths paths, ChatNavigationState chatNavigationState, HealthCheckStepViewModel daemonReadiness)
    {
        _paths = paths;
        _daemonReadiness = daemonReadiness;
        _chatNavigationState = chatNavigationState;
        _step = new IdentityStepViewModel();
        _context = new WizardContext
        {
            Paths = paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = RequestRedraw,
            ExistingConfig = ConfigFileHelper.TryLoadJsonDictOrNull(paths.NetclawConfigPath, out _),
        };
        _orchestrator = new WizardOrchestrator([_step], _context, singleStepMode: true);

        // The form shows defaults for a file it cannot read; say so now, not when the save fails.
        if (FirstUnreadableConfigFile() is { } unreadable)
            _context.StatusMessage.Value = DescribeReadFailure(unreadable);
    }

    public WizardContext Context => _context;
    public IdentityStepViewModel Step => _step;
    public IdentityStepView StepView { get; } = new();
    public ReactiveProperty<bool> IsSaved { get; } = new(false);
    public Action? OnStepContentChanged { get; set; }

    public void GoNext()
    {
        if (!OperationCompletion.IsCompleted) return;
        if (IsSaved.Value)
        {
            OperationCompletion = StartGuidedChatAsync();
            return;
        }

        if (_orchestrator.GoNext())
        {
            _context.StatusMessage.Value = "";
            NotifyContentChanged();
            return;
        }

        OperationCompletion = SaveIdentityAsync();
    }

    private async Task SaveIdentityAsync()
    {
        var ct = _lifetime.Token;
        // Identity collected. Rewrite identity files only; built-in agents are left
        // untouched so a redo never clobbers customized agent definitions.
        try
        {
            // Capture before the write. The saved screen can precede the daemon reload.
            _daemonBeforeSave = await _daemonReadiness.CaptureDaemonStateAsync(ct);
            if (_daemonBeforeSave is { WasRunning: true, GenerationBefore: null })
                throw new InvalidOperationException("The daemon did not report a config generation. Retry after the daemon becomes ready.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            await InvokeAsync(() => { OperationCompletion = Task.CompletedTask; _context.StatusMessage.Value = $"Daemon probe failed: {ex.Message}"; NotifyContentChanged(); }, ct);
            return;
        }
        try
        {
            await InvokeAsync(() =>
            {
                // The editor writes only identity keys. It must succeed before the identity files change.
                var session = new ConfigEditorSession(_paths);
                session.Apply(_step.BuildContribution(_step));
                session.Save();

                _step.WriteIdentityFiles(_paths);
                // Release Enter before the page exposes the final screen; the dispatch acknowledgment can arrive later.
                OperationCompletion = Task.CompletedTask;
                IsSaved.Value = true;
                _context.StatusMessage.Value = "";
                NotifyContentChanged();
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            // A failed write keeps the form open and blocks chat.
            await InvokeAsync(() => { OperationCompletion = Task.CompletedTask; _context.StatusMessage.Value = DescribeWriteFailure(ex); NotifyContentChanged(); }, ct);
        }
    }

    // The framework message quotes the full path and is clipped on one status line, so
    // lead with the file name and a short reason.
    private string DescribeWriteFailure(Exception ex)
    {
        if (ex is JsonException)
            return DescribeReadFailure(FirstUnreadableConfigFile() ?? _paths.NetclawConfigPath);

        var reason = ex is UnauthorizedAccessException ? "permission denied" : "write failed";
        var failed = new[] { _paths.SoulPath, _paths.ToolingPath, _paths.AgentsPath, _paths.NetclawConfigPath }
            .FirstOrDefault(path => ex.Message.Contains(path, StringComparison.Ordinal));
        var target = failed is null ? "the identity files" : Path.GetFileName(failed);
        return $"Couldn't write {target}: {reason}. Fix it and press Enter to retry.";
    }

    // The editor session reads netclaw.json and secrets.json; name the one that does not parse.
    private string? FirstUnreadableConfigFile()
        => new[] { _paths.NetclawConfigPath, _paths.SecretsPath }
            .FirstOrDefault(path =>
            {
                ConfigFileHelper.TryLoadJsonDictOrNull(path, out var error);
                return error is not null;
            });

    private static string DescribeReadFailure(string path)
        => $"Couldn't read {Path.GetFileName(path)}: it has comments or is not valid JSON. Fix it and press Enter to retry.";

    /// <summary>
    /// Hands the onboarding trigger to chat, built from the identity values just saved.
    /// Only reachable after a successful save.
    /// </summary>
    private async Task StartGuidedChatAsync()
    {
        var ct = _lifetime.Token;
        try
        {
            await InvokeAsync(() => { _context.StatusMessage.Value = "Waiting for the daemon to apply the identity..."; RequestRedraw(); }, ct);
            var preparation = await _daemonReadiness.PrepareDaemonAsync(_daemonBeforeSave, ct);
            if (preparation.Passed != true)
            {
                await InvokeAsync(() => { OperationCompletion = Task.CompletedTask; _context.StatusMessage.Value = preparation.Label; RequestRedraw(); }, ct);
                return;
            }
            ct.ThrowIfCancellationRequested();
            await InvokeAsync(() =>
            {
                _chatNavigationState.StartOnboarding(_step.BuildOnboardingTrigger(_paths));
                Navigate?.Invoke(ChatViewModel.Route);
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception error)
        {
            await InvokeAsync(() => { OperationCompletion = Task.CompletedTask; _context.StatusMessage.Value = $"Daemon preparation failed: {error.Message}"; RequestRedraw(); }, ct);
        }
    }

    public void GoBack()
    {
        if (IsSaved.Value)
        {
            RequestQuit();
            return;
        }

        if (!OperationCompletion.IsCompleted) return;
        if (_orchestrator.GoBack())
        {
            _context.StatusMessage.Value = "";
            NotifyContentChanged();
            return;
        }

        // Esc at the first identity field returns to the existing-install menu.
        Navigate?.Invoke(InitExistingInstallViewModel.MenuRoute);
    }

    public void RequestQuit() { _lifetime.Cancel(); Shutdown(); }

    private void NotifyContentChanged()
    {
        OnStepContentChanged?.Invoke();
        RequestRedraw();
    }

    public override void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        IsSaved.Dispose();
        _orchestrator.Dispose();
        _context.Dispose();
        base.Dispose();
    }
}
