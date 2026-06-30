// -----------------------------------------------------------------------
// <copyright file="InitExistingInstallViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using R3;
using Termina.Reactive;
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
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.Zero));

    public InitExistingInstallViewModelTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    private InitExistingInstallViewModel Create()
        => Create(DaemonStopped, DeleteDirectoryIfExists);

    private InitExistingInstallViewModel Create(
        Func<string, CancellationToken, Task<DaemonResult>> stopDaemonAsync,
        Action<string> deleteDirectory)
        => new(
        _paths,
        _nav,
        stopDaemonAsync,
        deleteDirectory,
        _time);

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
    public async Task FullReset_AfterBothConfirmations_DeletesEverything()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{}");
        File.WriteAllText(_paths.SqliteDbPath, "db");

        var vm = Create();
        string? route = null;
        SetNavigate(vm, requestedRoute => route = requestedRoute);
        Select(vm, 2); // Start over
        Select(vm, 1); // Full reset → confirm 1
        Select(vm, 1); // Yes → confirm 2
        Select(vm, 1); // Yes → perform
        await WaitForProgressAsync(vm, 3);

        Assert.False(Directory.Exists(_paths.BasePath));
        await CompleteResetAsync(vm);
        Assert.Equal(InitExistingInstallViewModel.WizardRoute, route);
    }

    [Fact]
    public async Task SetupOnlyReset_DeletesConfigButKeepsMemoryAndSessions()
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
        Directory.CreateDirectory(Path.Combine(_paths.SkillsDirectory, "local-skill"));
        File.WriteAllText(Path.Combine(_paths.SkillsDirectory, "local-skill", "SKILL.md"), "skill");

        var vm = Create();
        Select(vm, 2); // Start over
        Select(vm, 0); // Reset setup only → confirm 1
        Select(vm, 1); // Yes → confirm 2
        Select(vm, 1); // Yes → perform
        await WaitForProgressAsync(vm, 3);

        // Removed: config (incl. secrets, which lives under ConfigDirectory) + identity + soul.
        Assert.False(Directory.Exists(_paths.ConfigDirectory), "Config should be removed.");
        Assert.False(File.Exists(_paths.SecretsPath), "Secrets should be removed.");
        Assert.False(Directory.Exists(_paths.IdentityDirectory), "Identity files should be removed.");
        Assert.False(Directory.Exists(_paths.SoulDirectory), "Soul fragments should be removed.");

        // Preserved: operator data.
        Assert.True(File.Exists(_paths.SqliteDbPath), "Memory db should be preserved.");
        Assert.True(Directory.Exists(_paths.SessionsDirectory), "Sessions should be preserved.");
        Assert.True(File.Exists(Path.Combine(_paths.SkillsDirectory, "local-skill", "SKILL.md")),
            "Skills should be preserved.");
        await CompleteResetAsync(vm);
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

    [Fact]
    public async Task Dispose_DuringCompletionPause_CancelsWizardNavigation()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{}");

        var vm = Create();
        string? route = null;
        SetNavigate(vm, requestedRoute => route = requestedRoute);

        Select(vm, 2); // Start over
        Select(vm, 1); // Full reset → confirm 1
        Select(vm, 1); // Yes → confirm 2
        Select(vm, 1); // Yes → perform
        await WaitForProgressAsync(vm, 3);

        vm.Dispose();
        _time.Advance(InitExistingInstallViewModel.CompletionPause);
        await vm.ResetTask!.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Null(route);
    }

    [Fact]
    public async Task Dispose_WhileDaemonStopIsInFlight_CancelsLaterProgressAndNavigation()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{}");

        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource<DaemonResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteCalled = false;
        var vm = Create(
            (_, _) =>
            {
                stopStarted.TrySetResult();
                return releaseStop.Task;
            },
            _ => deleteCalled = true);
        string? route = null;
        SetNavigate(vm, requestedRoute => route = requestedRoute);

        StartFullReset(vm);
        await stopStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        using var disposeStarted = new ManualResetEventSlim();
        var dispose = Task.Run(() =>
        {
            disposeStarted.Set();
            vm.Dispose();
        }, TestContext.Current.CancellationToken);
        Assert.True(
            disposeStarted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
            "Dispose did not start.");

        releaseStop.TrySetResult(new DaemonResult(true, "Daemon stopped."));
        await dispose.WaitAsync(TestContext.Current.CancellationToken);
        await vm.ResetTask!.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Null(route);
        Assert.False(deleteCalled, "Cancelling during daemon stop must not proceed into deletion.");
    }

    [Fact]
    public async Task RequestQuit_WhileDaemonStopIsInFlight_CancelsBeforeDeletion()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{}");

        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource<DaemonResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleteCalled = false;
        var vm = Create(
            (_, _) =>
            {
                stopStarted.TrySetResult();
                return releaseStop.Task;
            },
            _ => deleteCalled = true);
        string? route = null;
        SetNavigate(vm, requestedRoute => route = requestedRoute);

        StartFullReset(vm);
        await stopStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        vm.RequestQuit();
        releaseStop.TrySetResult(new DaemonResult(true, "Daemon stopped."));
        await vm.ResetTask!.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(deleteCalled, "Ctrl+Q during daemon stop must cancel before deletion starts.");
        Assert.Null(route);

        vm.RequestQuit();
        Assert.False(
            vm.StatusMessage.Value.StartsWith("Reset is deleting data;", StringComparison.Ordinal),
            "Cancellation before deletion must not leave the deletion quit gate stuck on.");
    }

    [Fact]
    public async Task Dispose_WhileDeleteIsRunning_CancelsCompletionNavigation()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{}");

        var deleteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(DaemonStopped, path =>
        {
            if (path == _paths.BasePath)
            {
                deleteStarted.TrySetResult();
                releaseDelete.Task.GetAwaiter().GetResult();
                return;
            }

            DeleteDirectoryIfExists(path);
        });
        string? route = null;
        SetNavigate(vm, requestedRoute => route = requestedRoute);

        StartFullReset(vm);
        await deleteStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        using var disposeStarted = new ManualResetEventSlim();
        var dispose = Task.Run(() =>
        {
            disposeStarted.Set();
            vm.Dispose();
        }, TestContext.Current.CancellationToken);
        Assert.True(
            disposeStarted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
            "Dispose did not start.");

        releaseDelete.TrySetResult();
        await dispose.WaitAsync(TestContext.Current.CancellationToken);
        await vm.ResetTask!.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Null(route);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("unauthorized")]
    public async Task ResetFailure_ShowsErrorAndDoesNotNavigate(string failureKind)
    {
        var vm = Create(DaemonStopped, _ => throw failureKind switch
        {
            "io" => new IOException("locked file"),
            _ => new UnauthorizedAccessException("access denied"),
        });
        string? route = null;
        SetNavigate(vm, requestedRoute => route = requestedRoute);

        StartFullReset(vm);
        await WaitForProgressMessageAsync(vm, "Reset failed:");
        await vm.ResetTask!.WaitAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("Reset failed:", vm.ProgressMessage.Value, StringComparison.Ordinal);
        Assert.Null(route);
    }

    [Fact]
    public async Task ResetFailure_AfterBlockedQuit_ClearsQuitDisabledStatus()
    {
        var deleteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(DaemonStopped, path =>
        {
            if (path == _paths.BasePath)
            {
                deleteStarted.TrySetResult();
                releaseDelete.Task.GetAwaiter().GetResult();
                throw new IOException("locked file");
            }

            DeleteDirectoryIfExists(path);
        });

        StartFullReset(vm);
        await deleteStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        vm.RequestQuit();
        Assert.StartsWith("Reset is deleting data;", vm.StatusMessage.Value, StringComparison.Ordinal);

        releaseDelete.TrySetResult();
        await WaitForProgressMessageAsync(vm, "Reset failed:");
        await vm.ResetTask!.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(
            vm.StatusMessage.Value.StartsWith("Reset is deleting data;", StringComparison.Ordinal),
            "Failure state should not keep saying quit is disabled after quit becomes available again.");
    }

    [Fact]
    public async Task DaemonStopFailureResult_ShowsStatusAndContinuesReset()
    {
        var vm = Create(
            (_, _) => Task.FromResult(new DaemonResult(false, "daemon still running")),
            DeleteDirectoryIfExists);

        StartFullReset(vm);
        await WaitForStatusMessageAsync(vm, "Daemon stop did not complete;");
        await WaitForProgressAsync(vm, 3);

        Assert.Contains("daemon still running", vm.StatusMessage.Value, StringComparison.Ordinal);
        await CompleteResetAsync(vm);
    }

    private static void StartFullReset(InitExistingInstallViewModel vm)
    {
        Select(vm, 2); // Start over
        Select(vm, 1); // Full reset → confirm 1
        Select(vm, 1); // Yes → confirm 2
        Select(vm, 1); // Yes → perform
    }

    private async Task CompleteResetAsync(InitExistingInstallViewModel vm)
    {
        for (var i = 0; i < 10 && vm.ResetTask is { IsCompleted: false }; i++)
        {
            _time.Advance(InitExistingInstallViewModel.CompletionPause);
            await Task.Yield();
        }

        await vm.ResetTask!.WaitAsync(TestContext.Current.CancellationToken);
    }

    private static async Task WaitForProgressAsync(InitExistingInstallViewModel vm, int expectedStep)
    {
        bool Matches() => vm.CurrentProgressStep.Value >= expectedStep;

        if (Matches())
            return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = vm.CurrentProgressStep.Subscribe(step =>
        {
            if (step >= expectedStep)
                tcs.TrySetResult();
        });

        if (Matches())
            return;

        await tcs.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    private static async Task WaitForProgressMessageAsync(InitExistingInstallViewModel vm, string prefix)
    {
        bool Matches() => vm.ProgressMessage.Value.StartsWith(prefix, StringComparison.Ordinal);

        if (Matches())
            return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = vm.ProgressMessage.Subscribe(message =>
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal))
                tcs.TrySetResult();
        });

        if (Matches())
            return;

        await tcs.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    private static async Task WaitForStatusMessageAsync(InitExistingInstallViewModel vm, string prefix)
    {
        bool Matches() => vm.StatusMessage.Value.StartsWith(prefix, StringComparison.Ordinal);

        if (Matches())
            return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = vm.StatusMessage.Subscribe(message =>
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal))
                tcs.TrySetResult();
        });

        if (Matches())
            return;

        await tcs.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    private static void SetNavigate(ReactiveViewModel vm, Action<string> navigate)
    {
        var property = typeof(ReactiveViewModel).GetProperty(
            "Navigate",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(property);
        property!.SetValue(vm, navigate);
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static Task<DaemonResult> DaemonStopped(string reason, CancellationToken ct)
        => Task.FromResult(new DaemonResult(true, $"Daemon stopped for {reason}."));
}
