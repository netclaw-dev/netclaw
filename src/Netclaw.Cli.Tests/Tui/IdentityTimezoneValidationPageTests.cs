// -----------------------------------------------------------------------
// <copyright file="IdentityTimezoneValidationPageTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Reminders;
using Netclaw.Cli.Tui;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Termina;
using Termina.Input;
using Termina.Terminal;
using Xunit;

namespace Netclaw.Cli.Tests.Tui;

/// <summary>
/// The identity timezone field must refuse a zone that the reminder scheduler cannot
/// resolve. An existing install may already hold such a value: the form still opens
/// with it shown, and only the save is blocked.
/// </summary>
public sealed class IdentityTimezoneValidationPageTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly HealthCheckStepViewModel _readiness = new();

    public IdentityTimezoneValidationPageTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
        File.WriteAllText(
            _paths.NetclawConfigPath,
            """{ "configVersion": 1, "Identity": { "UserTimezone": "Not/AZone" } }""");
    }

    public void Dispose() { _readiness.Dispose(); _dir.Dispose(); }

    private (VirtualTerminal Terminal, TerminaApplication App, IdentityRedoViewModel Vm)
        CreateHeadlessApp(out VirtualInputSource input)
        => HeadlessTerminaFixture.Create<IdentityRedoPage, IdentityRedoViewModel>(
            "/identity-redo",
            _ => new IdentityRedoPage(),
            () => new IdentityRedoViewModel(_paths, new ChatNavigationState(), _readiness),
            out input);

    [Fact]
    public async Task Unknown_stored_timezone_is_shown_and_blocks_the_save()
    {
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var (terminal, app, vm) = CreateHeadlessApp(out var input);

        input.EnqueueKey(ConsoleKey.Enter); // agent name
        input.EnqueueKey(ConsoleKey.Enter); // communication style
        input.EnqueueKey(ConsoleKey.Enter); // user name
        input.EnqueueKey(ConsoleKey.Enter); // timezone: "Not/AZone" is rejected
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.False(vm.IsSaved.Value, $"An unknown timezone must not be saved. Screen:\n{terminal}");
        Assert.Equal("Not/AZone", vm.Step.UserTimezone);
        Assert.True(terminal.Contains("Unknown time zone 'Not/AZone'. Use an IANA id such as America/Chicago."),
            $"Expected the unknown-zone message. Screen:\n{terminal}");
        Assert.False(File.Exists(_paths.SoulPath), "SOUL.md must not be written for an invalid timezone.");
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Pressing_enter_again_on_a_rejected_timezone_is_rejected_again()
    {
        var configBefore = File.ReadAllText(_paths.NetclawConfigPath);
        var (terminal, app, vm) = CreateHeadlessApp(out var input);

        input.EnqueueKey(ConsoleKey.Enter); // agent name
        input.EnqueueKey(ConsoleKey.Enter); // communication style
        input.EnqueueKey(ConsoleKey.Enter); // user name
        input.EnqueueKey(ConsoleKey.Enter); // timezone: rejected
        input.EnqueueKey(ConsoleKey.Enter); // the rejected text must still be in the field
        input.EnqueueKey(ConsoleKey.Q, false, false, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await app.RunAsync(cts.Token);

        Assert.False(vm.IsSaved.Value, $"A second Enter must not save the host default. Screen:\n{terminal}");
        Assert.Equal("Not/AZone", vm.Step.UserTimezone);
        Assert.False(File.Exists(_paths.SoulPath), "SOUL.md must not be written for an invalid timezone.");
        Assert.Equal(configBefore, File.ReadAllText(_paths.NetclawConfigPath));
    }

    [Fact]
    public async Task Blank_timezone_input_maps_to_a_zone_the_scheduler_accepts()
    {
        var (terminal, app, vm) = CreateHeadlessApp(out var input);

        input.EnqueueKey(ConsoleKey.Enter); // agent name
        input.EnqueueKey(ConsoleKey.Enter); // communication style
        input.EnqueueKey(ConsoleKey.Enter); // user name
        // Pre-filled inputs start with the cursor at the end, so Backspace clears the field.
        for (var i = 0; i < "Not/AZone".Length; i++)
            input.EnqueueKey(ConsoleKey.Backspace);
        input.EnqueueKey(ConsoleKey.Enter); // blank timezone -> default
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = app.RunAsync(cts.Token);
        await IdentityRedoPageTests.WaitForTextAsync(terminal, "Identity updated", cts.Token);
        input.EnqueueKey(ConsoleKey.Q, control: true);
        await run;

        Assert.True(vm.IsSaved.Value, $"A blank timezone must fall back to a usable default. Screen:\n{terminal}");
        Assert.True(SchedulerTimeZones.TryResolve(vm.Step.UserTimezone, out _, out _),
            $"Default '{vm.Step.UserTimezone}' must resolve.");
    }

    [Theory]
    [InlineData("America/Chicago", true)]
    [InlineData("Europe/Berlin", true)]
    [InlineData("Central Standard Time", false)]
    [InlineData("Not/AZone", false)]
    public void Validator_accepts_iana_ids_and_rejects_windows_names_on_every_os(string zone, bool accepted)
    {
        Assert.Equal(accepted, IdentityStepViewModel.ValidateTimezone(zone) is null);
    }
}
