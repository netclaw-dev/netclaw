// -----------------------------------------------------------------------
// <copyright file="InitExistingInstallViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Follow-up action requested from the existing-install menu that the init host
/// cannot service itself and must hand back to <c>Program</c> after the TUI exits
/// (mirrors <see cref="ConfigDashboardAction"/> / RunDoctor). In-host destinations
/// (the wizard, identity redo) are reached by routing instead.
/// </summary>
public enum InitFollowUpAction
{
    None,
    OpenConfigEditor,
}

/// <summary>Shared state carrying the existing-install menu's deferred action out of
/// the Termina host so <c>Program</c> can dispatch it.</summary>
public sealed class InitNavigationState
{
    public InitFollowUpAction PendingAction { get; set; }
}

/// <summary>
/// Existing-install menu shown when <c>netclaw init</c> runs against a config that
/// already exists (simplify-netclaw-init). Instead of silently re-walking setup or
/// refusing with a hidden <c>--force</c>, it offers an explicit action menu and an
/// in-place, double-confirmed start-over flow. Config is untouched until the operator
/// confirms a destructive action.
/// </summary>
public sealed class InitExistingInstallViewModel : ReactiveViewModel
{
    internal static readonly TimeSpan CompletionPause = TimeSpan.FromMilliseconds(600);

    public enum Phase
    {
        Menu,
        ResetScope,
        ResetConfirm1,
        ResetConfirm2,
        Progress,
    }

    public enum ResetScopeKind
    {
        SetupOnly,
        Full,
    }

    public sealed record MenuItem(string Label, string Description);

    private readonly NetclawPaths _paths;
    private readonly InitNavigationState _navigationState;
    private readonly Func<string, CancellationToken, Task<DaemonResult>> _stopDaemonAsync;
    private readonly Action<string> _deleteDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _resetCts = new();
    private volatile bool _disposed;
    private volatile bool _quitBlockedForDeletion;

    public InitExistingInstallViewModel(
        NetclawPaths paths,
        InitNavigationState navigationState,
        DaemonManager daemonManager,
        TimeProvider timeProvider)
        : this(paths, navigationState, daemonManager.StopAsync, DeleteDirectory, timeProvider)
    {
    }

    internal InitExistingInstallViewModel(
        NetclawPaths paths,
        InitNavigationState navigationState,
        Func<string, CancellationToken, Task<DaemonResult>> stopDaemonAsync,
        Action<string> deleteDirectory,
        TimeProvider timeProvider)
    {
        _paths = paths;
        _navigationState = navigationState;
        _stopDaemonAsync = stopDaemonAsync;
        _deleteDirectory = deleteDirectory;
        _timeProvider = timeProvider;
    }

    public const string IdentityRoute = "/init/identity";
    public const string WizardRoute = "/init";
    public const string MenuRoute = "/init/menu";

    public ReactiveProperty<Phase> CurrentPhase { get; } = new(Phase.Menu);
    public ReactiveProperty<int> SelectedIndex { get; } = new(0);
    public ReactiveProperty<string> StatusMessage { get; } = new("");

    private ResetScopeKind _scope = ResetScopeKind.SetupOnly;
    public ResetScopeKind Scope => _scope;

    // Progress-screen state
    public ReactiveProperty<int> CurrentProgressStep { get; } = new(-1);
    public ReactiveProperty<string> ProgressMessage { get; } = new("");
    public ReactiveProperty<bool> CanQuitProgress { get; } = new(true);
    internal Task? ResetTask { get; private set; }
    internal bool IsResetCancellationRequested => _resetCts.IsCancellationRequested;

    public static readonly IReadOnlyList<MenuItem> MenuItems =
    [
        new("Redo identity setup", "Re-run just the identity step; provider and settings are kept."),
        new("Open configuration editor", "Adjust settings in `netclaw config` instead."),
        new("Start over from scratch", "Reset and run the whole setup again."),
        new("Cancel", "Leave everything as-is and exit."),
    ];

    public static readonly IReadOnlyList<MenuItem> ScopeItems =
    [
        new("Reset setup only", "Re-run setup; keep memory, sessions, and skills."),
        new("Full reset", "Delete ALL Netclaw data: config, memory, sessions, secrets."),
        new("Cancel", "Go back without changing anything."),
    ];

    public IReadOnlyList<MenuItem> CurrentItems => CurrentPhase.Value switch
    {
        Phase.Menu => MenuItems,
        Phase.ResetScope => ScopeItems,
        _ => ConfirmItems(),
    };

    private IReadOnlyList<MenuItem> ConfirmItems()
    {
        var full = _scope == ResetScopeKind.Full;
        return
        [
            new("Cancel", "Go back without changing anything."),
            new(full ? "Yes, delete everything" : "Yes, reset setup",
                full
                    ? "Permanently deletes config, memory, sessions, and secrets. Cannot be undone."
                    : "Re-runs setup. Memory, sessions, and skills are kept."),
        ];
    }

    public void MoveSelection(int delta)
    {
        var count = CurrentItems.Count;
        if (count == 0) return;
        var next = Math.Clamp(SelectedIndex.Value + delta, 0, count - 1);
        if (next != SelectedIndex.Value) SelectedIndex.Value = next;
    }

    public void ActivateSelected()
    {
        switch (CurrentPhase.Value)
        {
            case Phase.Menu: ActivateMenu(SelectedIndex.Value); break;
            case Phase.ResetScope: ActivateScope(SelectedIndex.Value); break;
            case Phase.ResetConfirm1: ActivateConfirm(first: true); break;
            case Phase.ResetConfirm2: ActivateConfirm(first: false); break;
        }
    }

    private void ActivateMenu(int index)
    {
        switch (index)
        {
            case 0: Navigate?.Invoke(IdentityRoute); break;
            case 1: _navigationState.PendingAction = InitFollowUpAction.OpenConfigEditor; Shutdown(); break;
            case 2: EnterPhase(Phase.ResetScope); break;
            default: Shutdown(); break;
        }
    }

    private void ActivateScope(int index)
    {
        switch (index)
        {
            case 0: _scope = ResetScopeKind.SetupOnly; EnterPhase(Phase.ResetConfirm1); break;
            case 1: _scope = ResetScopeKind.Full; EnterPhase(Phase.ResetConfirm1); break;
            default: EnterPhase(Phase.Menu); break;
        }
    }

    private void ActivateConfirm(bool first)
    {
        if (SelectedIndex.Value == 0) { EnterPhase(Phase.ResetScope); return; }
        if (first) { EnterPhase(Phase.ResetConfirm2); return; }
        StartResetProgress();
    }

    private void StartResetProgress()
    {
        CurrentPhase.Value = Phase.Progress;
        CurrentProgressStep.Value = 0;
        CanQuitProgress.Value = true;
        ProgressMessage.Value = "Stopping daemon…";
        RequestRedraw();
        // Track the destructive work so tests and future callers can observe completion;
        // the reset itself runs off the input/render loop to keep Ctrl+Q responsive.
        ResetTask = Task.Run(() => RunResetAsync(_resetCts.Token), _resetCts.Token);
    }

    private async Task RunResetAsync(CancellationToken ct)
    {
        try
        {
            await StopDaemonBestEffortAsync(ct);
            if (ct.IsCancellationRequested)
                return;

            _quitBlockedForDeletion = true;
            PublishOnLoop(() =>
            {
                CanQuitProgress.Value = false;
                CurrentProgressStep.Value = 1;
                ProgressMessage.Value = _scope == ResetScopeKind.Full ? "Deleting all data…" : "Deleting setup files…";
                RequestRedraw();
            }, ct);

            if (ct.IsCancellationRequested)
            {
                _quitBlockedForDeletion = false;
                return;
            }

            if (_scope == ResetScopeKind.Full)
            {
                _deleteDirectory(_paths.BasePath);
            }
            else
            {
                _deleteDirectory(_paths.ConfigDirectory);
                _deleteDirectory(_paths.IdentityDirectory);
                _deleteDirectory(_paths.SoulDirectory);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _quitBlockedForDeletion = false;
            return;
        }
        catch (Exception ex)
        {
            _quitBlockedForDeletion = false;
            PublishResetFailure(ex, ct);
            return;
        }

        if (ct.IsCancellationRequested)
            return;

        _quitBlockedForDeletion = false;
        PublishOnLoop(() =>
        {
            CurrentProgressStep.Value = 3;
            CanQuitProgress.Value = true;
            ProgressMessage.Value = "Purge complete";
            if (StatusMessage.Value.StartsWith("Reset is deleting data;", StringComparison.Ordinal))
                StatusMessage.Value = "";
            RequestRedraw();
        }, ct);

        try
        {
            await Task.Delay(CompletionPause, _timeProvider, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }

        PublishOnLoop(() => Navigate?.Invoke(WizardRoute), ct);
    }

    /// <summary>Best-effort daemon stop; deletion still surfaces any locked-file failure.</summary>
    private async Task StopDaemonBestEffortAsync(CancellationToken ct)
    {
        Task<DaemonResult>? stopTask = null;
        try
        {
            stopTask = _stopDaemonAsync("factory-reset", ct);
            var result = await stopTask.WaitAsync(ct);
            if (!result.Success && !IsDaemonAlreadyStopped(result.Message))
                PublishStatus($"Daemon stop did not complete; reset will continue: {result.Message}", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ObserveLateStopFailure(stopTask);
        }
        catch (OperationCanceledException ex)
        {
            PublishStatus($"Daemon stop canceled; reset will continue: {ex.Message}", ct);
        }
        catch (Exception ex)
        {
            PublishStatus($"Daemon stop failed; reset will continue: {ex.Message}", ct);
        }
    }

    private static bool IsDaemonAlreadyStopped(string message)
        => message.StartsWith("Daemon is not running.", StringComparison.Ordinal);

    private static void ObserveLateStopFailure(Task<DaemonResult>? stopTask)
    {
        if (stopTask is null || stopTask.IsCompleted)
            return;

        _ = stopTask.ContinueWith(
            task => Debug.WriteLine($"Init reset daemon stop completed after cancellation: {task.Exception?.GetBaseException().Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void PublishResetFailure(Exception ex, CancellationToken ct)
        => PublishOnLoop(() =>
        {
            ProgressMessage.Value = $"Reset failed: {ex.Message}";
            CanQuitProgress.Value = true;
            if (StatusMessage.Value.StartsWith("Reset is deleting data;", StringComparison.Ordinal))
                StatusMessage.Value = "";
            RequestRedraw();
        }, ct);

    private void PublishStatus(string message, CancellationToken ct)
        => PublishOnLoop(() =>
        {
            StatusMessage.Value = message;
            RequestRedraw();
        }, ct);

    private void PublishOnLoop(Action action, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || _disposed)
            return;

        _ = InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested || _disposed)
                return;

            action();
        }, ct);
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    public void GoBack()
    {
        switch (CurrentPhase.Value)
        {
            case Phase.Menu: Shutdown(); break;
            case Phase.ResetScope: EnterPhase(Phase.Menu); break;
            case Phase.ResetConfirm1: EnterPhase(Phase.ResetScope); break;
            case Phase.ResetConfirm2: EnterPhase(Phase.ResetConfirm1); break;
            case Phase.Progress: break; // Don't allow backing out — it's running
        }
    }

    private void EnterPhase(Phase phase)
    {
        CurrentPhase.Value = phase;
        SelectedIndex.Value = 0;
        StatusMessage.Value = "";
        RequestRedraw();
    }

    public void RequestQuit()
    {
        if (CurrentPhase.Value == Phase.Progress && _quitBlockedForDeletion)
        {
            StatusMessage.Value = "Reset is deleting data; quit is disabled until deletion completes.";
            RequestRedraw();
            return;
        }

        if (CurrentPhase.Value == Phase.Progress)
            _resetCts.Cancel();

        Shutdown();
    }

    public override void Dispose()
    {
        _disposed = true;
        _resetCts.Cancel();
        try
        {
            ResetTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Init reset task drain on dispose faulted: {ex.Message}");
        }

        _resetCts.Dispose();
        CurrentPhase.Dispose();
        SelectedIndex.Dispose();
        StatusMessage.Dispose();
        CurrentProgressStep.Dispose();
        ProgressMessage.Dispose();
        CanQuitProgress.Dispose();
        base.Dispose();
    }
}
