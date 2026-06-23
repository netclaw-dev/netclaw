// -----------------------------------------------------------------------
// <copyright file="InitExistingInstallViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
    public enum Phase
    {
        Menu,
        ResetScope,
        ResetConfirm1,
        ResetConfirm2,
    }

    public enum ResetScopeKind
    {
        SetupOnly,
        Full,
    }

    public sealed record MenuItem(string Label, string Description);

    private readonly NetclawPaths _paths;
    private readonly InitNavigationState _navigationState;

    public InitExistingInstallViewModel(NetclawPaths paths, InitNavigationState navigationState)
    {
        _paths = paths;
        _navigationState = navigationState;
    }

    /// <summary>Route the wizard launches for "Redo identity setup".</summary>
    public const string IdentityRoute = "/init/identity";

    /// <summary>Route launched for a fresh setup after a confirmed reset.</summary>
    public const string WizardRoute = "/init";

    /// <summary>This menu's own route (identity redo returns here on Esc).</summary>
    public const string MenuRoute = "/init/menu";

    public ReactiveProperty<Phase> CurrentPhase { get; } = new(Phase.Menu);
    public ReactiveProperty<int> SelectedIndex { get; } = new(0);
    public ReactiveProperty<string> StatusMessage { get; } = new("");

    private ResetScopeKind _scope = ResetScopeKind.SetupOnly;
    public ResetScopeKind Scope => _scope;

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

    /// <summary>Items for the current phase (drives the rendered list).</summary>
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
        if (count == 0)
            return;

        var next = Math.Clamp(SelectedIndex.Value + delta, 0, count - 1);
        if (next != SelectedIndex.Value)
            SelectedIndex.Value = next;
    }

    /// <summary>Enter on the highlighted row.</summary>
    public void ActivateSelected()
    {
        switch (CurrentPhase.Value)
        {
            case Phase.Menu:
                ActivateMenu(SelectedIndex.Value);
                break;
            case Phase.ResetScope:
                ActivateScope(SelectedIndex.Value);
                break;
            case Phase.ResetConfirm1:
                ActivateConfirm(first: true);
                break;
            case Phase.ResetConfirm2:
                ActivateConfirm(first: false);
                break;
        }
    }

    private void ActivateMenu(int index)
    {
        switch (index)
        {
            case 0: // Redo identity setup
                Navigate?.Invoke(IdentityRoute);
                break;
            case 1: // Open configuration editor
                _navigationState.PendingAction = InitFollowUpAction.OpenConfigEditor;
                Shutdown();
                break;
            case 2: // Start over from scratch
                EnterPhase(Phase.ResetScope);
                break;
            default: // Cancel
                Shutdown();
                break;
        }
    }

    private void ActivateScope(int index)
    {
        switch (index)
        {
            case 0:
                _scope = ResetScopeKind.SetupOnly;
                EnterPhase(Phase.ResetConfirm1);
                break;
            case 1:
                _scope = ResetScopeKind.Full;
                EnterPhase(Phase.ResetConfirm1);
                break;
            default: // Cancel
                EnterPhase(Phase.Menu);
                break;
        }
    }

    // Confirm rows are [Cancel, Yes]. Default selection is Cancel (index 0), so a stray
    // Enter never deletes — the operator must move to "Yes" and confirm twice.
    private void ActivateConfirm(bool first)
    {
        if (SelectedIndex.Value == 0) // Cancel
        {
            EnterPhase(Phase.ResetScope);
            return;
        }

        if (first)
        {
            EnterPhase(Phase.ResetConfirm2);
            return;
        }

        PerformReset();
    }

    private void PerformReset()
    {
        try
        {
            if (_scope == ResetScopeKind.Full)
            {
                DeleteDirectory(_paths.BasePath);
            }
            else
            {
                // Setup-only: remove what the bootstrap wizard writes (config + secrets +
                // identity), preserving memory, sessions, and skills. SecretsPath lives
                // under ConfigDirectory, so deleting it covers secrets too.
                DeleteDirectory(_paths.ConfigDirectory);
                DeleteDirectory(_paths.IdentityDirectory);
                DeleteDirectory(_paths.SoulDirectory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage.Value = $"Reset failed: {ex.Message}";
            RequestRedraw();
            return;
        }

        // Fresh setup from the top.
        Navigate?.Invoke(WizardRoute);
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    /// <summary>Esc: step back one phase, or quit from the menu.</summary>
    public void GoBack()
    {
        switch (CurrentPhase.Value)
        {
            case Phase.Menu:
                Shutdown();
                break;
            case Phase.ResetScope:
                EnterPhase(Phase.Menu);
                break;
            case Phase.ResetConfirm1:
                EnterPhase(Phase.ResetScope);
                break;
            case Phase.ResetConfirm2:
                EnterPhase(Phase.ResetConfirm1);
                break;
        }
    }

    private void EnterPhase(Phase phase)
    {
        CurrentPhase.Value = phase;
        // Confirm phases default to Cancel (index 0); menus/scope start at the top.
        SelectedIndex.Value = 0;
        StatusMessage.Value = "";
        RequestRedraw();
    }

    public void RequestQuit() => Shutdown();

    public override void Dispose()
    {
        CurrentPhase.Dispose();
        SelectedIndex.Dispose();
        StatusMessage.Dispose();
        base.Dispose();
    }
}
