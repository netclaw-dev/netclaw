// -----------------------------------------------------------------------
// <copyright file="InitExistingInstallViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tests.Cli;
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
        // bin/ holds the installed binaries; a full reset must purge data but keep it.
        var binaryPath = Path.Combine(_paths.BinDirectory, "netclaw");
        File.WriteAllText(binaryPath, "binary");

        var vm = Create();
        string? route = null;
        SetNavigate(vm, requestedRoute => route = requestedRoute);
        Select(vm, 2); // Start over
        Select(vm, 1); // Full reset → confirm 1
        Select(vm, 1); // Yes → confirm 2
        Select(vm, 1); // Yes → perform
        await WaitForProgressAsync(vm, 3);

        Assert.False(Directory.Exists(_paths.ConfigDirectory), "Config should be purged.");
        Assert.False(File.Exists(_paths.SqliteDbPath), "Memory db should be purged.");
        Assert.True(File.Exists(binaryPath), "Installed binaries in bin/ must survive a full reset.");
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
        await vm.ResetTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Null(route);
    }

    [Fact]
    public async Task Dispose_WhileDaemonStopIsInFlight_CancelsLaterProgressAndNavigation()
    {
        File.WriteAllText(_paths.NetclawConfigPath, "{}");

        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopCancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource<DaemonResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration stopCancellationRegistration = default;
        var deleteCalled = false;
        var vm = Create(
            (_, ct) =>
            {
                stopCancellationRegistration = ct.Register(
                    static state => ((TaskCompletionSource)state!).TrySetResult(),
                    stopCancellationObserved);
                stopStarted.TrySetResult();
                return releaseStop.Task;
            },
            _ => deleteCalled = true);
        string? route = null;
        SetNavigate(vm, requestedRoute => route = requestedRoute);

        try
        {
            StartFullReset(vm);
            await stopStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

            var dispose = Task.Run(() => vm.Dispose(), TestContext.Current.CancellationToken);
            await stopCancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);

            releaseStop.TrySetResult(new DaemonResult(true, "Daemon stopped."));
            await dispose.WaitAsync(TestContext.Current.CancellationToken);
            await vm.ResetTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            Assert.Null(route);
            Assert.False(deleteCalled, "Cancelling during daemon stop must not proceed into deletion.");
        }
        finally
        {
            stopCancellationRegistration.Dispose();
        }
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
        await vm.ResetTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

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
            if (path == _paths.ConfigDirectory)
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

        var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispose = Task.Run(() =>
        {
            disposeStarted.TrySetResult();
            vm.Dispose();
        }, TestContext.Current.CancellationToken);
        await disposeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        releaseDelete.TrySetResult();
        await dispose.WaitAsync(TestContext.Current.CancellationToken);
        await vm.ResetTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

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
        await vm.ResetTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

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
            if (path == _paths.ConfigDirectory)
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
        await vm.ResetTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task Reset_Aborts_WhenTheDaemonIsStillRunningAfterAFailedStop_AndDeletesNothing()
    {
        // Ownership could not be determined (a unit is installed but there is no user bus), so
        // the stop did nothing and a supervisor would restart the daemon on a wiped home.
        File.WriteAllText(_paths.NetclawConfigPath, "{}");
        File.WriteAllText(_paths.SqliteDbPath, "db");
        var deleted = new List<string>();
        var vm = new InitExistingInstallViewModel(
            _paths,
            _nav,
            (_, _) => Task.FromResult(new DaemonResult(false, "Could not determine whether systemd owns the daemon lifecycle: bus")),
            deleted.Add,
            _time,
            isDaemonRunning: () => true);
        string? route = null;
        SetNavigate(vm, requestedRoute => route = requestedRoute);

        StartFullReset(vm);
        await WaitForProgressMessageAsync(vm, "Reset failed:");
        await vm.ResetTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Contains("still running", vm.ProgressMessage.Value, StringComparison.Ordinal);
        Assert.Contains("Could not determine whether systemd owns", vm.ProgressMessage.Value, StringComparison.Ordinal);
        Assert.Contains("Nothing was deleted", vm.ProgressMessage.Value, StringComparison.Ordinal);
        Assert.Contains("login session", vm.ProgressMessage.Value, StringComparison.Ordinal);
        Assert.Contains("systemctl --user stop netclaw.service", vm.ProgressMessage.Value, StringComparison.Ordinal);
        Assert.Empty(deleted);
        Assert.True(File.Exists(_paths.SqliteDbPath));
        Assert.True(File.Exists(_paths.NetclawConfigPath));
        Assert.Null(route);
    }

    [Fact]
    public async Task Reset_Aborts_WhenTheUnitStateCannotBeRead_EvenThoughNoDaemonRuns()
    {
        // No session bus: the unit may be crash-looping and about to start a daemon on the wiped
        // home, so a reset with nothing visibly running must still refuse.
        File.WriteAllText(_paths.SqliteDbPath, "db");
        var unitPath = Path.Combine(_dir.Path, "netclaw.service");
        File.WriteAllText(unitPath, "[Service]\nExecStart=/opt/netclaw/netclawd\n");
        var systemd = new SystemdUserService(
            unitPath, new BusUnreachableRunner(), enabledOnThisPlatform: true, homePath: SystemdUserService.DefaultHomePath);
        var vm = new InitExistingInstallViewModel(
            _paths, _nav, new DaemonManager(_paths, TimeProvider.System), systemd, _time);

        StartFullReset(vm);
        await WaitForProgressMessageAsync(vm, "Reset failed:");
        await vm.ResetTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Contains("Nothing was deleted", vm.ProgressMessage.Value, StringComparison.Ordinal);
        Assert.True(File.Exists(_paths.SqliteDbPath));
    }

    private sealed class BusUnreachableRunner : ISystemCommandRunner
    {
        public Task<SystemCommandResult> RunAsync(string command, string arguments)
            => Task.FromResult(new SystemCommandResult(1, "Failed to connect to bus: No medium found"));
    }

    [Fact]
    public async Task Reset_Aborts_WhenTheStopReportedSuccessButTheDaemonIsStillRunning()
    {
        File.WriteAllText(_paths.SqliteDbPath, "db");
        var vm = new InitExistingInstallViewModel(
            _paths, _nav, DaemonStopped, DeleteDirectoryIfExists, _time, isDaemonRunning: () => true);

        StartFullReset(vm);
        await WaitForProgressMessageAsync(vm, "Reset failed:");
        await vm.ResetTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(_paths.SqliteDbPath));
    }

    [SlopwatchSuppress("SW001", "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    [Fact(SkipUnless = nameof(IsLinux), Skip = "Uses a copy of /bin/sleep as the stand-in daemon process.")]
    public async Task Reset_StopsTheDaemonThroughTheUnit_NotByKillingItDirectly()
    {
        // The unit's Restart=always would bring a directly killed daemon back under the data
        // being deleted, so the reset has to use the same stop as `netclaw daemon stop`.
        var fakeDaemon = Path.Combine(_dir.Path, "netclawd");
        File.Copy("/bin/sleep", fakeDaemon, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(fakeDaemon, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var daemon = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fakeDaemon, "600") { UseShellExecute = false })!;
        try
        {
            File.WriteAllText(_paths.PidFilePath, daemon.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // No lock file on purpose: a real daemon's lock dies with the process, but a lock held by
            // this test process would be released by an Exited callback that can run after the stop
            // has already seen the exit, making the reset abort as "still running".
            var unitPath = Path.Combine(_dir.Path, "netclaw.service");
            File.WriteAllText(unitPath, "[Service]\nExecStart=/opt/netclaw/netclawd\n");
            var runner = new RecordingCommandRunner(daemon.Id);
            var systemd = new SystemdUserService(unitPath, runner, enabledOnThisPlatform: true, homePath: _paths.BasePath,
                environReader: _ => null);
            using var vm = new InitExistingInstallViewModel(
                _paths, _nav, new DaemonManager(_paths, TimeProvider.System), systemd, _time);

            StartFullReset(vm);
            await CompleteResetAsync(vm);
            await runner.StopSeen.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            if (!daemon.HasExited)
                daemon.Kill();
        }
    }

    public static bool IsLinux => OperatingSystem.IsLinux();

    private sealed class RecordingCommandRunner(int mainPid) : ISystemCommandRunner
    {
        private readonly TaskCompletionSource _stopSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StopSeen => _stopSeen.Task;

        public Task<SystemCommandResult> RunAsync(string command, string arguments)
        {
            if (arguments.Contains("--user stop netclaw.service", StringComparison.Ordinal))
                _stopSeen.TrySetResult();

            return Task.FromResult(arguments.Contains("show", StringComparison.Ordinal)
                ? new SystemCommandResult(0, string.Empty, StandardOutput: $"MainPID={mainPid}\nEnvironment=\n")
                : new SystemCommandResult(0, string.Empty, StandardOutput: "active\n"));
        }
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
        // RunResetAsync registers the completion-pause timer before it publishes step 3, so once
        // step 3 is observed the fake timer is guaranteed registered on _time. That makes a single
        // advance fire it deterministically. The old advance/yield loop only ever masked the
        // advance-before-registration lost wakeup this helper used to hit on loaded CI runners.
        await WaitForProgressAsync(vm, 3);
        _time.Advance(InitExistingInstallViewModel.CompletionPause);
        await vm.ResetTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
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
