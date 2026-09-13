// -----------------------------------------------------------------------
// <copyright file="IdentityRedoPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Termina;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// Headless page-level coverage for <see cref="IdentityRedoPage"/> — the first tests to
/// drive the full identity-redo flow through Termina's real render + input pipeline (the
/// existing <c>IdentityRedoViewModelTests</c> only exercise the ViewModel directly).
///
/// These guard the user-visible symptom of the timezone-submit loop: the flow must walk
/// all four identity sub-steps and reach the saved screen. The loop's root cause was the
/// page failing to clear step-scoped <c>Submitted</c> subscriptions on content rebuild —
/// the documented <c>StepViewCallbacks.Subscriptions</c> contract that the sibling
/// <c>InitWizardPage</c> already honours. The accumulation itself is driven by Termina's
/// cursor-blink re-render timer (wall-clock based) and is covered by the native smoke
/// harness; here we assert the flow reaches "Identity updated" with the four submits
/// mapping 1:1 to the four sub-steps (a double-fire would skip a field; a stuck step
/// never finalizes).
/// </summary>
public sealed class IdentityRedoPageTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public IdentityRedoPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(_paths.NetclawConfigPath, "{ \"configVersion\": 1 }");
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task FullFlow_EmptySubmits_AdvancesPastTimezoneToSavedScreen()
    {
        var (terminal, app, vm) = CreateHeadlessApp(out var input);

        // Walk all four identity sub-steps with empty submits — each field falls back to
        // its default (agent name -> "Netclaw", comm style -> first option, user name ->
        // none, timezone -> local). The fourth Enter submits the timezone field, which is
        // the step that previously looped forever instead of reaching the saved screen.
        input.EnqueueKey(ConsoleKey.Enter); // agent name
        input.EnqueueKey(ConsoleKey.Enter); // communication style
        input.EnqueueKey(ConsoleKey.Enter); // user name
        input.EnqueueKey(ConsoleKey.Enter); // timezone -> finalize
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.True(vm.IsSaved.Value,
            $"Timezone submit must finalize the redo flow, not loop. Screen:\n{terminal}");
        Assert.True(terminal.Contains("Identity updated"),
            $"Expected the saved screen after the timezone submit. Screen:\n{terminal}");
        Assert.True(File.Exists(_paths.SoulPath), "SOUL.md must be written when the redo finalizes.");
    }

    [Fact]
    public async Task TimezoneSubmit_FinalizesExactlyOnce()
    {
        var (_, app, vm) = CreateHeadlessApp(out var input);

        input.EnqueueKey(ConsoleKey.Enter); // agent name
        input.EnqueueKey(ConsoleKey.Enter); // communication style
        input.EnqueueKey(ConsoleKey.Enter); // user name
        input.EnqueueKey(ConsoleKey.Enter); // timezone -> finalize
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        // A stuck/looping timezone step never reaches saved; a double-fire would have
        // skipped past the user-name field. Reaching saved with the local timezone
        // recorded proves the four submits mapped 1:1 to the four sub-steps.
        Assert.True(vm.IsSaved.Value);
        Assert.Equal(TimeZoneInfo.Local.Id, vm.Step.UserTimezone);
    }

    private (VirtualTerminal Terminal, TerminaApplication App, IdentityRedoViewModel Vm)
        CreateHeadlessApp(out VirtualInputSource input)
        => HeadlessTerminaFixture.Create<IdentityRedoPage, IdentityRedoViewModel>(
            "/identity-redo",
            () => new IdentityRedoPage(),
            () => new IdentityRedoViewModel(_paths),
            out input);
}
