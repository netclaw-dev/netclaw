// -----------------------------------------------------------------------
// <copyright file="ApprovalsManagerPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Termina;
using Termina.Hosting;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Headless TUI tests for <see cref="ApprovalsManagerPage"/> using Termina's
/// <see cref="VirtualTerminal"/> and <see cref="VirtualInputSource"/>. Exercises
/// the full Termina rendering and input-routing pipeline against a temporary
/// <c>tool-approvals.json</c>.
/// </summary>
public sealed class ApprovalsManagerPageTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly ToolApprovalStore _store;

    public ApprovalsManagerPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _store = new ToolApprovalStore(_paths.ToolApprovalsPath);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task EmptyStore_RendersEmptyMessageInTerminal()
    {
        var (terminal, app, _) = CreateHeadlessApp(out var input);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("Approvals Manager"),
            $"Expected page title 'Approvals Manager' in terminal. Screen:\n{terminal}");
        Assert.True(terminal.Contains("No persistent approvals."),
            $"Expected empty-state message. Screen:\n{terminal}");
    }

    [Fact]
    public async Task SeededEntries_RenderedInList()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", "git push");
        _store.AddApproval(TrustAudience.Personal, "file_write", "/tmp/scratch/");
        _store.AddApproval(TrustAudience.Public, "shell_execute", "ls");

        var (terminal, app, _) = CreateHeadlessApp(out var input);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("personal"),
            $"Expected audience 'personal'. Screen:\n{terminal}");
        Assert.True(terminal.Contains("git push"),
            $"Expected pattern 'git push'. Screen:\n{terminal}");
        Assert.True(terminal.Contains("/tmp/scratch/"),
            $"Expected pattern '/tmp/scratch/'. Screen:\n{terminal}");
        Assert.True(terminal.Contains("ls"),
            $"Expected pattern 'ls' (public audience). Screen:\n{terminal}");
    }

    [Fact]
    public async Task PressingR_OnSelection_TransitionsToConfirmAndRevokesOnEnter()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", "git push");
        _store.AddApproval(TrustAudience.Personal, "shell_execute", "npm install");

        var (_, app, vm) = CreateHeadlessApp(out var input);

        // First displayed entry is sorted alphabetically by audience+tool+pattern,
        // so the selection at index 0 is "personal / shell_execute / git push".
        input.EnqueueKey(ConsoleKey.R);                 // Open revoke confirm.
        input.EnqueueKey(ConsoleKey.Enter);             // Confirm "Yes, revoke".
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var remaining = _store.GetApprovedPatterns(TrustAudience.Personal, "shell_execute");
        Assert.DoesNotContain("git push", remaining);
        Assert.Contains("npm install", remaining);
        Assert.Equal(ApprovalsManagerState.List, vm.CurrentState.Value);
    }

    [Fact]
    public async Task EscOnConfirm_CancelsRevoke()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", "git push");

        var (_, app, _) = CreateHeadlessApp(out var input);

        input.EnqueueKey(ConsoleKey.R);                 // Open revoke confirm.
        input.EnqueueKey(ConsoleKey.Escape);            // Cancel.
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(["git push"],
            _store.GetApprovedPatterns(TrustAudience.Personal, "shell_execute"));
    }

    [Fact]
    public async Task RevokingLastEntry_TransitionsListIntoEmptyState()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", "git push");

        var (terminal, app, vm) = CreateHeadlessApp(out var input);

        input.EnqueueKey(ConsoleKey.R);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.Equal(ApprovalsManagerState.Empty, vm.CurrentState.Value);
        Assert.True(terminal.Contains("No persistent approvals."),
            $"Expected empty state to render after final revoke. Screen:\n{terminal}");
    }

    private (VirtualTerminal Terminal, TerminaApplication App, ApprovalsManagerViewModel Vm)
        CreateHeadlessApp(out VirtualInputSource input)
    {
        var terminal = new VirtualTerminal(120, 40);
        var virtualInput = new VirtualInputSource();
        input = virtualInput;

        ApprovalsManagerViewModel? capturedVm = null;

        var services = new ServiceCollection();
        services.AddSingleton<IAnsiTerminal>(terminal);
        services.AddTerminaVirtualInput(virtualInput);
        services.AddTermina("/approvals", builder =>
        {
            builder.RegisterRoute<ApprovalsManagerPage, ApprovalsManagerViewModel>(
                "/approvals",
                _ => new ApprovalsManagerPage(),
                _ =>
                {
                    capturedVm = new ApprovalsManagerViewModel(_paths);
                    return capturedVm;
                });
        });

        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();

        return (terminal, app, capturedVm!);
    }
}
