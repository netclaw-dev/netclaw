// -----------------------------------------------------------------------
// <copyright file="ApprovalsManagerPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
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
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));

    public ApprovalsManagerPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        _store = new ToolApprovalStore(
            _paths.ToolApprovalsPath,
            _time,
            new ApprovalStoreMigrationContext(ApprovalShell.Bash),
            TimeSpan.Zero);
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
    public void Refresh_reports_one_bounded_version_two_omission()
    {
        File.WriteAllText(_paths.ToolApprovalsPath, """
            {
              "version": 2,
              "audiences": {
                "personal": {
                  "shell_execute": [
                    { "verb": " git push", "directory": null },
                    { "verb": "git status", "directory": null }
                  ]
                }
              }
            }
            """);
        var viewModel = new ApprovalsManagerViewModel(_paths, _time);

        viewModel.Refresh();
        var firstMessage = viewModel.StatusMessage.Value;
        viewModel.StatusMessage.Value = "ready";
        viewModel.Refresh();

        Assert.Equal(
            "Approval store version-2 conversion omitted 1 unrepresentable entries.",
            firstMessage);
        Assert.Equal("ready", viewModel.StatusMessage.Value);
    }

    private static ApprovalEntry Verb(string verb) => ApprovalEntry.CreateTokenPrefix(
        ApprovalShell.Bash,
        verb.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public async Task SeededEntries_RenderedInList()
    {
        var scratchDirectory = Path.Combine(
            Assert.IsType<string>(Path.GetPathRoot(_dir.Path)),
            "scratch");
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));
        _store.AddApproval(
            TrustAudience.Personal,
            "file_write",
            new ApprovalEntry("file_write") { Directory = scratchDirectory });
        _store.AddApproval(TrustAudience.Public, "shell_execute", Verb("ls"));

        var (terminal, app, _) = CreateHeadlessApp(out var input);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("personal"),
            $"Expected audience 'personal'. Screen:\n{terminal}");
        Assert.True(terminal.Contains("Bash token-prefix \"git push\" anywhere"),
            $"Expected typed git push entry. Screen:\n{terminal}");
        Assert.True(terminal.Contains(scratchDirectory),
            $"Expected directory '{scratchDirectory}'. Screen:\n{terminal}");
        Assert.True(terminal.Contains("Bash token-prefix \"ls\" anywhere"),
            $"Expected typed ls entry (public audience). Screen:\n{terminal}");
    }

    [Fact]
    public async Task ListView_ShowsRelativeCreationTime()
    {
        // The grant is stamped at the fake clock; advancing it makes the
        // rendered relative age deterministic.
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));
        _time.Advance(TimeSpan.FromDays(3));

        var (terminal, app, _) = CreateHeadlessApp(out var input);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(terminal.Contains("Added"),
            $"Expected an 'Added' column header. Screen:\n{terminal}");
        Assert.True(terminal.Contains("added 3 days ago"),
            $"Expected relative creation time 'added 3 days ago'. Screen:\n{terminal}");
    }

    [Fact]
    public async Task LongList_FillsTerminalHeight_AndShowsScrollbar()
    {
        // Seed far more entries than the stock SelectionListNode's 10-row cap.
        for (var i = 0; i < 120; i++)
            _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb($"cmd{i:D3}"));

        var (terminal, app, _) = CreateHeadlessApp(out var input);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var screen = terminal.ToString();
        var visible = Enumerable.Range(0, 120)
            .Count(i => screen.Contains($"cmd{i:D3}", StringComparison.Ordinal));

        // A 40-row VirtualTerminal has room for ~30 list rows once panel chrome,
        // header, status bar, and key bindings are subtracted. The pre-fix bug
        // capped this at 10 regardless of terminal size (issues #1 and #3).
        Assert.True(visible > 20,
            $"Expected the list to fill the terminal (>20 rows); only {visible} visible. Screen:\n{terminal}");

        // The overflow scrollbar track must be present so the user can see
        // there are more results below the fold (issue #2).
        Assert.Contains('░', screen);
    }

    [Fact]
    public async Task PressingR_OnSelection_TransitionsToConfirmAndRevokesOnEnter()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("npm install"));

        var (_, app, vm) = CreateHeadlessApp(out var input);

        // First displayed entry is sorted alphabetically by audience+tool+verb,
        // so the selection at index 0 is "personal / shell_execute / git push anywhere".
        input.EnqueueKey(ConsoleKey.R);                 // Open revoke confirm.
        input.EnqueueKey(ConsoleKey.Enter);             // Confirm "Yes, revoke".
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var remaining = _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute");
        Assert.DoesNotContain(remaining, e => e.Verb == "git push");
        Assert.Contains(remaining, e => e.Verb == "npm install");
        Assert.Equal(ApprovalsManagerState.List, vm.CurrentState.Value);
    }

    [Theory]
    [InlineData(ConsoleKey.R)]
    [InlineData(ConsoleKey.Delete)]
    public async Task RevokeKey_OnSecondRow_RevokesHighlightedEntry(ConsoleKey revokeKey)
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("alpha"));
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("bravo"));

        var (_, app, _) = CreateHeadlessApp(out var input);

        input.EnqueueKey(ConsoleKey.DownArrow);
        input.EnqueueKey(revokeKey);
        input.EnqueueKey(ConsoleKey.Enter);
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var remaining = _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute");
        Assert.Contains(remaining, entry => entry.Verb == "alpha");
        Assert.DoesNotContain(remaining, entry => entry.Verb == "bravo");
    }

    [Fact]
    public async Task EscOnConfirm_CancelsRevoke()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));

        var (_, app, _) = CreateHeadlessApp(out var input);

        input.EnqueueKey(ConsoleKey.R);                 // Open revoke confirm.
        input.EnqueueKey(ConsoleKey.Escape);            // Cancel.
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        var entries = _store.GetApprovedEntries(TrustAudience.Personal, "shell_execute");
        Assert.Single(entries);
        Assert.Equal("git push", entries[0].Verb);
    }

    [Fact]
    public async Task RevokingLastEntry_TransitionsListIntoEmptyState()
    {
        _store.AddApproval(TrustAudience.Personal, "shell_execute", Verb("git push"));

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
                    capturedVm = new ApprovalsManagerViewModel(_paths, _time);
                    return capturedVm;
                });
        });

        var sp = services.BuildServiceProvider();
        var app = sp.GetRequiredService<TerminaApplication>();

        return (terminal, app, capturedVm!);
    }
}
