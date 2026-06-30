// -----------------------------------------------------------------------
// <copyright file="InitExistingInstallPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using R3;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

public sealed class InitExistingInstallPageTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 6, 27, 12, 0, 0, TimeSpan.Zero));

    public InitExistingInstallPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task ProgressScreen_RendersQueuedUpdates_AndOnlyCtrlQExits()
    {
        var deleteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (terminal, app, vm, services) = CreateHeadlessApp(
            out var input,
            (_, _) => Task.FromResult(new DaemonResult(true, "Daemon stopped.")),
            path =>
            {
                if (path == _paths.BasePath)
                {
                    deleteStarted.TrySetResult();
                    releaseDelete.Task.GetAwaiter().GetResult();
                }
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = app.RunAsync(cts.Token);
        var runCompleted = false;

        try
        {
            EnqueueFullReset(input);
            await deleteStarted.Task.WaitAsync(cts.Token);
            await WaitForConditionAsync(
                () => vm.CurrentProgressStep.Value == 1
                      && !vm.CanQuitProgress.Value
                      && terminal.Contains("Reset in progress"),
                cts.Token);

            input.EnqueueKey(ConsoleKey.Q, control: true);
            await WaitForConditionAsync(
                () => vm.StatusMessage.Value.StartsWith("Reset is deleting data;", StringComparison.Ordinal),
                cts.Token);
            Assert.False(run.IsCompleted, "Ctrl+Q must not interrupt the destructive delete phase.");

            releaseDelete.TrySetResult();
            await WaitForConditionAsync(
                () => vm.CurrentProgressStep.Value == 3
                      && vm.CanQuitProgress.Value
                      && terminal.Contains("✓ Purge complete")
                      && terminal.Contains("[Ctrl+Q] Quit"),
                cts.Token);

            var leftProgress = false;
            using var subscription = vm.CurrentPhase.Subscribe(phase =>
            {
                if (phase != InitExistingInstallViewModel.Phase.Progress)
                    leftProgress = true;
            });

            input.EnqueueKey(ConsoleKey.Escape);
            input.EnqueueKey(ConsoleKey.DownArrow);
            input.EnqueueKey(ConsoleKey.Enter);
            input.EnqueueKey(ConsoleKey.Q, control: true);

            await run.WaitAsync(cts.Token);
            runCompleted = true;

            Assert.False(leftProgress, "Progress mode should ignore non-Ctrl+Q keys until reset exits or is cancelled.");
        }
        finally
        {
            releaseDelete.TrySetResult();
            try
            {
                if (!runCompleted)
                {
                    for (var i = 0; i < 10 && vm.ResetTask is { IsCompleted: false }; i++)
                    {
                        vm.RequestQuit();
                        _time.Advance(InitExistingInstallViewModel.CompletionPause);
                        await Task.Yield();
                    }
                }

                if (vm.ResetTask is not null)
                    await vm.ResetTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            finally
            {
                services.Dispose();
            }
        }
    }

    private (VirtualTerminal Terminal, TerminaApplication App, InitExistingInstallViewModel Vm, ServiceProvider Services)
        CreateHeadlessApp(
            out VirtualInputSource input,
            Func<string, CancellationToken, Task<DaemonResult>> stopDaemonAsync,
            Action<string> deleteDirectory)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        InitExistingInstallViewModel? capturedVm = null;

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina(InitExistingInstallViewModel.MenuRoute, builder =>
        {
            builder.RegisterRoute<InitExistingInstallPage, InitExistingInstallViewModel>(
                InitExistingInstallViewModel.MenuRoute,
                _ => new InitExistingInstallPage(),
                _ =>
                {
                    capturedVm = new InitExistingInstallViewModel(
                        _paths,
                        new InitNavigationState(),
                        stopDaemonAsync,
                        deleteDirectory,
                        _time);
                    return capturedVm;
                });
        });

        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();

        return (terminal, app, capturedVm!, sp);
    }

    private static void EnqueueFullReset(VirtualInputSource input)
    {
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(ConsoleKey.Enter);
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, CancellationToken ct)
    {
        while (!predicate())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
