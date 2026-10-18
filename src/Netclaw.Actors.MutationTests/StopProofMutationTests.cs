// -----------------------------------------------------------------------
// <copyright file="StopProofMutationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Actors.MutationTests;

/// <summary>
/// Kills mutants in the proof that <see cref="DaemonManager.StopAsync"/> needs before it sends a
/// signal (#2527 rule 2). The proof gates SIGTERM to a process, so a mutant that accepts a weak proof
/// lets a command for one home stop the daemon of another home. A mutant in the handling of a stale
/// PID file can signal a process that is not a daemon.
/// </summary>
/// <remarks>
/// The tests use a stand-in daemon: a process that shows as <c>netclawd</c>, a port in the config of the
/// home, and a recording listener. They need Linux (the stand-in is a link to <c>sleep</c>), as the
/// Linux CI runner of the mutation gates has. They are the xUnit 2 twin of the stop tests in
/// <c>Netclaw.Cli.Tests</c>, because Stryker's adapter does not support xUnit 3.
/// </remarks>
public sealed class StopProofMutationTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly List<StandInDaemon> _standIns = [];
    private readonly List<Process> _processes = [];

    // The name holds a space and an ampersand, so a notice that does not escape its home breaks.
    private string ThisHome => Path.Combine(_dir.Path, "this home&x");

    private string OtherHome => Path.Combine(_dir.Path, "other-home");

    public void Dispose()
    {
        foreach (var standIn in _standIns)
            standIn.Dispose();
        foreach (var process in _processes)
        {
            if (!process.HasExited)
                process.Kill();
            process.Dispose();
        }

        _dir.Dispose();
    }

    // ── a daemon that confirms itself ───────────────────────────────────────

    [Fact]
    public async Task A_daemon_that_confirms_the_pid_and_the_home_is_stopped()
    {
        var daemon = StartDaemon(ThisHome);

        var result = await ManagerFor(ThisHome).StopAsync("cli-stop", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(daemon.WaitForExit(10000));
        var notice = Assert.Single(daemon.ShutdownNotices);
        Assert.Equal(200, notice.Status);
        Assert.Equal(daemon.Pid.ToString(CultureInfo.InvariantCulture), notice.Pid);
        Assert.True(notice.Home is not null && StandInDaemon.SameDirectory(notice.Home, ThisHome));
        Assert.False(File.Exists(Path.Combine(ThisHome, "netclaw.pid")));
    }

    [Fact]
    public async Task A_200_for_another_pid_is_no_proof_even_when_the_environment_matches()
    {
        var daemon = StartDaemon(ThisHome);
        daemon.AnswerAsLegacyDaemon(daemon.Pid + 1);

        var result = await ManagerFor(ThisHome).StopAsync("cli-stop", CancellationToken.None);

        AssertRefused(result, daemon, ThisHome);
    }

    [Fact]
    public async Task A_200_for_the_right_pid_is_proof_even_when_the_environment_names_another_home()
    {
        var daemon = StartDaemon(OtherHome);
        var listener = ListenFor(ThisHome);
        listener.AnswerAsLegacyDaemon(daemon.Pid);
        File.WriteAllText(Path.Combine(ThisHome, "netclaw.pid"), daemon.Pid.ToString(CultureInfo.InvariantCulture));

        var result = await ManagerFor(ThisHome).StopAsync("cli-stop", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(daemon.WaitForExit(10000));
    }

    [Fact]
    public async Task The_refusal_is_written_to_stderr()
    {
        var daemon = StartDaemon(OtherHome);
        ListenFor(ThisHome).StopListening();
        File.WriteAllText(Path.Combine(ThisHome, "netclaw.pid"), daemon.Pid.ToString(CultureInfo.InvariantCulture));

        var (result, stderr) = await StopCapturingStderrAsync(ThisHome);

        AssertRefused(result, daemon, ThisHome);
        Assert.Contains("Nothing was stopped.", stderr, StringComparison.Ordinal);
        Assert.Contains($"PID {daemon.Pid}", stderr, StringComparison.Ordinal);
        // No answer from the port is reported as well, as it was before the proof.
        Assert.Contains("could not notify daemon of shutdown reason", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_409_is_never_overruled_by_the_environment()
    {
        var daemon = StartDaemon(ThisHome);
        daemon.AnswerEveryNoticeWith(HttpStatusCode.Conflict);

        var result = await ManagerFor(ThisHome).StopAsync("cli-stop", CancellationToken.None);

        AssertRefused(result, daemon, ThisHome);
    }

    // ── a daemon that does not give a usable answer ─────────────────────────

    [Theory]
    [InlineData(500)]
    [InlineData(404)]
    [InlineData(204)]
    public async Task Another_status_leaves_the_decision_to_the_environment_which_matches(int status)
    {
        var daemon = StartDaemon(ThisHome);
        daemon.AnswerEveryNoticeWith((HttpStatusCode)status);

        var result = await ManagerFor(ThisHome).StopAsync("cli-stop", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(daemon.WaitForExit(10000));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(204)]
    public async Task Another_status_leaves_the_decision_to_the_environment_which_names_another_home(int status)
    {
        var daemon = StartDaemon(OtherHome);
        ListenFor(ThisHome).AnswerEveryNoticeWith((HttpStatusCode)status);
        File.WriteAllText(Path.Combine(ThisHome, "netclaw.pid"), daemon.Pid.ToString(CultureInfo.InvariantCulture));

        var result = await ManagerFor(ThisHome).StopAsync("cli-stop", CancellationToken.None);

        AssertRefused(result, daemon, ThisHome);
    }

    [Fact]
    public async Task No_answer_and_an_environment_that_matches_stops_the_daemon()
    {
        var daemon = StartDaemon(ThisHome);
        daemon.StopListening();

        var result = await ManagerFor(ThisHome).StopAsync("cli-stop", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(daemon.WaitForExit(10000));
    }

    [Fact]
    public async Task No_answer_and_an_environment_for_another_home_stops_nothing()
    {
        var daemon = StartDaemon(OtherHome);
        ListenFor(ThisHome).StopListening();
        File.WriteAllText(Path.Combine(ThisHome, "netclaw.pid"), daemon.Pid.ToString(CultureInfo.InvariantCulture));

        var result = await ManagerFor(ThisHome).StopAsync("cli-stop", CancellationToken.None);

        AssertRefused(result, daemon, ThisHome);
    }

    [Fact]
    public async Task No_answer_and_an_environment_home_that_differs_only_in_case_stops_nothing()
    {
        // Paths on Linux are case-sensitive: a home that differs in case is another home.
        var daemon = StartDaemon(Path.Combine(_dir.Path, "THIS HOME&X"));
        ListenFor(ThisHome).StopListening();
        File.WriteAllText(Path.Combine(ThisHome, "netclaw.pid"), daemon.Pid.ToString(CultureInfo.InvariantCulture));

        var result = await ManagerFor(ThisHome).StopAsync("cli-stop", CancellationToken.None);

        AssertRefused(result, daemon, ThisHome);
    }

    [Fact]
    public async Task No_answer_and_a_process_with_no_home_variable_stops_nothing()
    {
        // A process with no NETCLAW_HOME serves the default home. This home is not the default home.
        var daemon = StartDaemon(ThisHome, declareHome: false);
        daemon.StopListening();

        var result = await ManagerFor(ThisHome).StopAsync("cli-stop", CancellationToken.None);

        AssertRefused(result, daemon, ThisHome);
    }

    [Theory]
    [InlineData("trailing-separator")]
    [InlineData("dot-segment")]
    public async Task No_answer_and_another_spelling_of_this_home_in_the_environment_stops_the_daemon(string spelling)
    {
        Directory.CreateDirectory(Path.Combine(ThisHome, "sub"));
        var environmentHome = spelling == "trailing-separator"
            ? ThisHome + Path.DirectorySeparatorChar
            : Path.Combine(ThisHome, "sub", "..");
        var daemon = StartDaemon(environmentHome);
        daemon.StopListening();

        var result = await ManagerFor(ThisHome).StopAsync("cli-stop", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(daemon.WaitForExit(10000));
    }

    [Fact]
    public async Task No_answer_and_a_link_to_this_home_in_the_command_stops_the_daemon()
    {
        var daemon = StartDaemon(ThisHome);
        daemon.StopListening();
        var link = Path.Combine(_dir.Path, "link-to-this-home");
        Directory.CreateSymbolicLink(link, ThisHome);

        var result = await ManagerFor(link).StopAsync("cli-stop", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(daemon.WaitForExit(10000));
    }

    [Fact]
    public async Task No_answer_and_a_chain_of_links_to_this_home_stops_the_daemon()
    {
        var daemon = StartDaemon(ThisHome);
        daemon.StopListening();
        var first = Path.Combine(_dir.Path, "first-link");
        var second = Path.Combine(_dir.Path, "second-link");
        Directory.CreateSymbolicLink(first, ThisHome);
        Directory.CreateSymbolicLink(second, first);

        var result = await ManagerFor(second).StopAsync("cli-stop", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(daemon.WaitForExit(10000));
    }

    [Fact]
    public async Task No_answer_and_a_process_with_no_home_variable_stops_when_this_is_the_default_home()
    {
        // A process with no NETCLAW_HOME serves the default home: $HOME/.netclaw. HOME is a temp folder
        // here, so the real home of the user is never read.
        var profile = Path.Combine(_dir.Path, "profile");
        var defaultHome = Path.Combine(profile, ".netclaw");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", profile);
        try
        {
            var daemon = StartDaemon(defaultHome, declareHome: false);
            daemon.StopListening();

            var result = await ManagerFor(defaultHome).StopAsync("cli-stop", CancellationToken.None);

            Assert.True(result.Success, result.Message);
            Assert.True(daemon.WaitForExit(10000));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", originalHome);
        }
    }

    // ── a stale PID file ────────────────────────────────────────────────────

    [Fact]
    public async Task A_PID_file_for_a_process_that_has_gone_is_removed_and_nothing_is_sent()
    {
        var daemon = StartDaemon(ThisHome);
        daemon.Process!.Kill();
        Assert.True(daemon.WaitForExit(10000));

        var result = await ManagerFor(ThisHome).StopAsync("cli-stop", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Daemon is not running.", result.Message);
        Assert.False(File.Exists(Path.Combine(ThisHome, "netclaw.pid")));
        Assert.Empty(daemon.ShutdownNotices);
    }

    [Fact]
    public async Task A_PID_file_for_a_process_that_is_not_a_daemon_is_removed_and_the_process_is_left_alone()
    {
        ListenFor(ThisHome);
        var psi = new ProcessStartInfo("tail") { UseShellExecute = false };
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("/dev/null");
        var notADaemon = Process.Start(psi)!;
        _processes.Add(notADaemon);
        File.WriteAllText(Path.Combine(ThisHome, "netclaw.pid"), notADaemon.Id.ToString(CultureInfo.InvariantCulture));

        var result = await ManagerFor(ThisHome).StopAsync("cli-stop", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Daemon is not running.", result.Message);
        Assert.False(File.Exists(Path.Combine(ThisHome, "netclaw.pid")));
        Assert.False(notADaemon.HasExited);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private StandInDaemon StartDaemon(string home, bool declareHome = true)
    {
        Directory.CreateDirectory(home);
        var standIn = StandInDaemon.Start(home, _dir.Path, declareHome);
        _standIns.Add(standIn);
        return standIn;
    }

    // This home gets a port and a listener but no process, so no notice can reach port 5199.
    private StandInDaemon ListenFor(string home)
    {
        Directory.CreateDirectory(home);
        var standIn = StandInDaemon.ListenOnly(home);
        _standIns.Add(standIn);
        return standIn;
    }

    private static async Task<(DaemonResult Result, string Stderr)> StopCapturingStderrAsync(string home)
    {
        var original = Console.Error;
        using var captured = new StringWriter(CultureInfo.InvariantCulture);
        Console.SetError(captured);
        try
        {
            var result = await ManagerFor(home).StopAsync("cli-stop", CancellationToken.None);
            return (result, captured.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    private static DaemonManager ManagerFor(string home)
        => new(new NetclawPaths(home), TimeProvider.System, new UnsupervisedHost());

    // The refusal sends no signal, removes nothing, and names the PID and the home.
    private static void AssertRefused(DaemonResult result, StandInDaemon daemon, string home)
    {
        Assert.False(result.Success, result.Message);
        Assert.Contains("Nothing was stopped.", result.Message, StringComparison.Ordinal);
        Assert.Contains($"PID {daemon.Pid}", result.Message, StringComparison.Ordinal);
        Assert.Contains(home, result.Message, StringComparison.Ordinal);
        Assert.False(daemon.HasExited, "The daemon got a signal.");
        Assert.True(File.Exists(Path.Combine(home, "netclaw.pid")), "The refusal removed the PID file.");
    }

    private sealed class UnsupervisedHost : IContainerSupervisor
    {
        public bool IsExternallySupervised => false;
    }
}
