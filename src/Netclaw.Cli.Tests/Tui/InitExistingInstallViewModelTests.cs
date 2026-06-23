// -----------------------------------------------------------------------
// <copyright file="InitExistingInstallViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

using Phase = InitExistingInstallViewModel.Phase;
using ResetScopeKind = InitExistingInstallViewModel.ResetScopeKind;

/// <summary>
/// Behavioral coverage for the existing-install menu and its double-confirmed
/// start-over flow (simplify-netclaw-init §3–4). Drives the ViewModel phase machine
/// directly; the Termina rendering is exercised separately by the smoke tapes.
/// </summary>
public sealed class InitExistingInstallViewModelTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly InitNavigationState _nav = new();

    public InitExistingInstallViewModelTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    private InitExistingInstallViewModel Create() => new(_paths, _nav);

    private static void Select(InitExistingInstallViewModel vm, int index)
    {
        vm.SelectedIndex.Value = index;
        vm.ActivateSelected();
    }

    [Fact]
    public void OpenConfigEditor_SetsPendingHandoffAction()
    {
        var vm = Create();

        Select(vm, 1); // "Open configuration editor"

        Assert.Equal(InitFollowUpAction.OpenConfigEditor, _nav.PendingAction);
    }

    [Fact]
    public void StartOver_EntersResetScopeAtTop()
    {
        var vm = Create();

        Select(vm, 2); // "Start over from scratch"

        Assert.Equal(Phase.ResetScope, vm.CurrentPhase.Value);
        Assert.Equal(0, vm.SelectedIndex.Value);
    }

    [Fact]
    public void DestructiveReset_RequiresTwoConfirmationsBeforeDeleting()
    {
        var vm = Create();

        Select(vm, 2); // Start over → ResetScope
        Select(vm, 1); // Full reset → ResetConfirm1

        Assert.Equal(Phase.ResetConfirm1, vm.CurrentPhase.Value);
        Assert.Equal(ResetScopeKind.Full, vm.Scope);
        // Each confirmation defaults to Cancel so a stray Enter never deletes.
        Assert.Equal(0, vm.SelectedIndex.Value);

        Select(vm, 1); // "Yes" on the FIRST confirmation → only advances to confirm 2

        Assert.Equal(Phase.ResetConfirm2, vm.CurrentPhase.Value);
        Assert.True(Directory.Exists(_paths.ConfigDirectory),
            "Config must still exist after only one confirmation.");
    }

    [Fact]
    public void FullReset_AfterBothConfirmations_DeletesEverything()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{}");
        File.WriteAllText(_paths.SqliteDbPath, "db");

        var vm = Create();
        Select(vm, 2); // Start over
        Select(vm, 1); // Full reset → confirm 1
        Select(vm, 1); // Yes → confirm 2
        Select(vm, 1); // Yes → perform

        Assert.False(Directory.Exists(_paths.BasePath));
    }

    [Fact]
    public void SetupOnlyReset_DeletesConfigButKeepsMemoryAndSessions()
    {
        // Setup-only reset removes everything the bootstrap wizard writes
        // (config + secrets + identity + soul) while leaving operator data
        // (memory db, sessions) intact. Seed both sides of that boundary.
        File.WriteAllText(_paths.NetclawConfigPath, "{}");
        File.WriteAllText(_paths.SecretsPath, "{}");
        Directory.CreateDirectory(_paths.IdentityDirectory);
        File.WriteAllText(_paths.SoulPath, "soul");
        Directory.CreateDirectory(_paths.SoulDirectory);
        File.WriteAllText(Path.Combine(_paths.SoulDirectory, "fragment.md"), "detail");
        File.WriteAllText(_paths.SqliteDbPath, "db");
        Directory.CreateDirectory(_paths.SessionsDirectory);

        var vm = Create();
        Select(vm, 2); // Start over
        Select(vm, 0); // Reset setup only → confirm 1
        Select(vm, 1); // Yes → confirm 2
        Select(vm, 1); // Yes → perform

        // Removed: config (incl. secrets, which lives under ConfigDirectory) + identity + soul.
        Assert.False(Directory.Exists(_paths.ConfigDirectory), "Config should be removed.");
        Assert.False(File.Exists(_paths.SecretsPath), "Secrets should be removed.");
        Assert.False(Directory.Exists(_paths.IdentityDirectory), "Identity files should be removed.");
        Assert.False(Directory.Exists(_paths.SoulDirectory), "Soul fragments should be removed.");

        // Preserved: operator data.
        Assert.True(File.Exists(_paths.SqliteDbPath), "Memory db should be preserved.");
        Assert.True(Directory.Exists(_paths.SessionsDirectory), "Sessions should be preserved.");
    }

    [Fact]
    public void ConfirmationCancel_ReturnsToScope()
    {
        var vm = Create();
        Select(vm, 2); // Start over
        Select(vm, 1); // Full reset → confirm 1
        Select(vm, 0); // Cancel → back to scope

        Assert.Equal(Phase.ResetScope, vm.CurrentPhase.Value);
    }

    [Fact]
    public void GoBack_WalksPhasesBackToMenu()
    {
        var vm = Create();
        Select(vm, 2); // ResetScope
        Select(vm, 1); // ResetConfirm1
        Select(vm, 1); // ResetConfirm2

        vm.GoBack();
        Assert.Equal(Phase.ResetConfirm1, vm.CurrentPhase.Value);
        vm.GoBack();
        Assert.Equal(Phase.ResetScope, vm.CurrentPhase.Value);
        vm.GoBack();
        Assert.Equal(Phase.Menu, vm.CurrentPhase.Value);
    }
}
