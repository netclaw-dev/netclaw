// -----------------------------------------------------------------------
// <copyright file="ShellLaunchEnvironmentApprovalTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tests.Utilities;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

/// <summary>
/// Shell approval with the variables that the launcher sets on each Bash process
/// (<c>TMPDIR</c>, <c>TMP</c>, <c>TEMP</c>, and <c>HOME</c>) and a relative <c>cd</c>.
/// </summary>
/// <remarks>
/// A command that names a launch variable gets the same decision as the same command
/// with the literal value. A statement that can change the variable removes that trust.
/// The facts apply only to the no-startup Bash 5.2 host.
/// </remarks>
[Collection(ShellApprovalMatrixCollection.Name)]
public sealed class ShellLaunchEnvironmentApprovalTests(ShellApprovalMatrixFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static bool IsPosix => TestPlatform.IsPosix;

    [SlopwatchSuppress("SW001", "The launch facts apply to the POSIX Bash host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "Launch facts apply to the POSIX Bash host.")]
    [InlineData("cat \"$TMPDIR/notes.txt\"", "cat {T}/notes.txt", true)]
    [InlineData("cat \"$TMP/notes.txt\" \"$TEMP/more.txt\"", "cat {T}/notes.txt {T}/more.txt", true)]
    [InlineData("cat $TMPDIR/notes.txt", "cat {T}/notes.txt", true)]
    [InlineData("cd \"$TMPDIR/out\" && ls", "cd {T}/out && ls", false)]
    [InlineData("cd ./sub && cat notes.txt", "cd {P}/sub && cat notes.txt", false)]
    [InlineData("cd ./sub; cat notes.txt", "cd {P}/sub; cat notes.txt", false)]
    [InlineData("cd sub && cat notes.txt", "cd {P}/sub && cat notes.txt", false)]
    [InlineData("cd sub; cat notes.txt", "cd {P}/sub; cat notes.txt", false)]
    public async Task Launch_variable_gets_the_decision_of_its_literal_value(
        string command,
        string literal,
        bool literalAllowed)
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);
        harness.CreateProjectDirectory("sub");

        var observed = await harness.EvaluateShellAsync(command, Ct);
        var expected = await harness.EvaluateShellAsync(Expand(literal, harness), Ct);

        Assert.Equal(literalAllowed, expected.Outcome == ApprovalOutcome.Allowed);
        AssertSameDecision(expected, observed);
    }

    [SlopwatchSuppress("SW001", "The launch facts apply to the POSIX Bash host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "Launch facts apply to the POSIX Bash host.")]
    public async Task Home_variable_gets_the_prompt_of_its_literal_value()
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);

        var observed = await harness.EvaluateShellAsync("cat \"$HOME/notes.txt\"", Ct);
        var expected = await harness.EvaluateShellAsync(Expand("cat {H}/notes.txt", harness), Ct);

        AssertSameDecision(expected, observed);
        Assert.False(observed.Prompt?.IsMessy ?? false);
    }

    [SlopwatchSuppress("SW001", "The launch facts apply to the POSIX Bash host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "Launch facts apply to the POSIX Bash host.")]
    public async Task Home_variable_uses_a_grant_for_its_literal_directory()
    {
        await using var harness = await CreateHarnessAsync(Approvals.PersistentAnywhere("cat"));

        var observed = await harness.EvaluateShellAsync("cat \"$HOME/notes.txt\"", Ct);

        Assert.Equal(ApprovalOutcome.Allowed, observed.Outcome);
        Assert.Equal(ApprovalAllowReason.StoredApproval, observed.AllowReason);
    }

    // Negative controls: each literal twin is allowed, but the source can change the
    // variable before the read, or a child shell reads its own startup files.
    [SlopwatchSuppress("SW001", "The launch facts apply to the POSIX Bash host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "Launch facts apply to the POSIX Bash host.")]
    [InlineData("TMPDIR=/etc; cat \"$TMPDIR/notes.txt\"")]
    [InlineData("export TMPDIR=/etc && cat \"$TMPDIR/notes.txt\"")]
    [InlineData("TMPDIR=/etc cat \"$TMPDIR/notes.txt\"")]
    [InlineData("unset TMPDIR; cat \"$TMPDIR/notes.txt\"")]
    [InlineData("read -r TMPDIR; cat \"$TMPDIR/notes.txt\"")]
    [InlineData("source ./env.sh; cat \"$TMPDIR/notes.txt\"")]
    [InlineData("export HOME=/etc; cd /etc && cat \"$HOME/passwd\"")]
    [InlineData("bash -lc 'cat \"$TMPDIR/notes.txt\"'")]
    public async Task Changed_launch_variable_is_not_trusted(string command)
    {
        await using var harness = await CreateHarnessAsync(Approvals.None);

        var literal = await harness.EvaluateShellAsync(Expand("cat {T}/notes.txt", harness), Ct);
        var observed = await harness.EvaluateShellAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.Allowed, literal.Outcome);
        Assert.NotEqual(ApprovalOutcome.Allowed, observed.Outcome);
    }

    // Negative controls: with grants for cd and cat, the literal twin is allowed. A CDPATH
    // that the source sets can send a relative cd to another directory, so the cd stays unresolved.
    [SlopwatchSuppress("SW001", "The launch facts apply to the POSIX Bash host.")]
    [Theory(SkipUnless = nameof(IsPosix), Skip = "Launch facts apply to the POSIX Bash host.")]
    [InlineData("CDPATH=/etc; cd sub && cat notes.txt")]
    [InlineData("export CDPATH=/etc && cd sub && cat notes.txt")]
    public async Task Changed_cdpath_keeps_a_relative_cd_unresolved(string command)
    {
        await using var harness = await CreateHarnessAsync(Approvals.PersistentAnywhere("cd", "cat", "export"));
        harness.CreateProjectDirectory("sub");

        var literal = await harness.EvaluateShellAsync(Expand("cd {P}/sub && cat notes.txt", harness), Ct);
        var relative = await harness.EvaluateShellAsync("cd sub && cat notes.txt", Ct);
        var observed = await harness.EvaluateShellAsync(command, Ct);

        Assert.Equal(ApprovalOutcome.Allowed, literal.Outcome);
        Assert.Equal(ApprovalOutcome.Allowed, relative.Outcome);
        Assert.NotEqual(ApprovalOutcome.Allowed, observed.Outcome);
    }

    [SlopwatchSuppress("SW001", "The launch facts apply to the POSIX Bash host.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "Launch facts apply to the POSIX Bash host.")]
    public async Task Unproved_bash_host_does_not_use_launch_facts()
    {
        await using var harness = await ShellApprovalHarness.CreateAsync(
            "launch-facts-unproved-host",
            new ShellApprovalInvocation("true", Host: ShellApprovalHost.Bash),
            Approvals.None,
            fixture.ActorSystem,
            Ct);

        var observed = await harness.EvaluateShellAsync("cat \"$TMPDIR/notes.txt\"", Ct);

        Assert.NotEqual(ApprovalOutcome.Allowed, observed.Outcome);
    }

    /// <summary>
    /// The launcher and the parser read one list of launch variables. This test runs
    /// the real shell and compares each value with the value that the parser resolved.
    /// It fails if a launcher value, the start directory, or <c>PWD</c> drifts.
    /// </summary>
    [SlopwatchSuppress("SW001", "This test runs the POSIX Bash launcher with a symbolic-link working directory.")]
    [Fact(SkipUnless = nameof(IsPosix), Skip = "This test runs the POSIX Bash launcher.")]
    public async Task Launched_shell_receives_the_values_that_the_parser_resolved()
    {
        using var directory = new DisposableTempDir();
        var real = Directory.CreateDirectory(Path.Combine(directory.Path, "real"));
        Directory.CreateDirectory(Path.Combine(real.FullName, "work", "sub"));
        var link = Path.Combine(directory.Path, "link");
        Directory.CreateSymbolicLink(link, real.FullName);
        // The start directory names a symbolic link, so a physical directory differs from the logical one.
        var workingDirectory = Path.Combine(link, "work");
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux, new Version(5, 2));
        var context = TestToolExecutionContext.CreateBound("launch/facts", directory.Path, TrustAudience.Personal);
        var temporary = context.Invocation.SessionStorage!.ManagedTemporary;
        const string command =
            "printf '%s\\n' \"$TMPDIR/a\" \"$TMP/b\" \"$TEMP/c\" \"$HOME/d\" && cd sub && pwd && cd ../.. && pwd";
        var policy = new ShellCommandPolicy(environment);

        var analysis = policy.Analyze(command, workingDirectory, temporary);
        var expected = analysis.Commands[0].Arguments
            .Select(static argument => Assert.IsType<ShellValueDomain.Exact>(argument.Value).Value)
            .Skip(1)
            .Concat(analysis.Commands
                .Where(static occurrence => occurrence.Clause.Verb.Tokens is ["pwd"])
                .Select(static occurrence => Assert.IsType<ShellValueDomain.Exact>(occurrence.WorkingDirectory).Value))
            .ToArray();

        var launch = new ShellProcessLaunch(command, workingDirectory, context.Invocation,
            policy, new ToolPathPolicy(environment, []), static _ => Task.CompletedTask);
        using var process = await launch.StartAsync(Ct);
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);

        var home = Assert.IsType<string>(environment.HomeDirectory);
        var temporaryDirectory = ShellExecutionEnvironment.GetTemporaryDirectoryValue(temporary);
        Assert.Equal(
            [
                $"{temporaryDirectory}/a", $"{temporaryDirectory}/b", $"{temporaryDirectory}/c", $"{home}/d",
                Path.Combine(workingDirectory, "sub"), link
            ],
            expected);
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(expected, output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    private Task<ShellApprovalHarness> CreateHarnessAsync(ApprovalState approvals)
        => ShellApprovalHarness.CreateAsync(
            "launch-facts",
            new ShellApprovalInvocation("true", Host: ShellApprovalHost.Bash52),
            approvals,
            fixture.ActorSystem,
            Ct);

    private static string Expand(string literal, ShellApprovalHarness harness)
        => literal
            .Replace("{T}", harness.ManagedTemporaryDirectory, StringComparison.Ordinal)
            .Replace("{P}", harness.ProjectDirectory, StringComparison.Ordinal)
            .Replace(
                "{H}",
                ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux, new Version(5, 2)).HomeDirectory,
                StringComparison.Ordinal);

    private static void AssertSameDecision(ApprovalObservation expected, ApprovalObservation observed)
    {
        Assert.Equal(expected.Outcome, observed.Outcome);
        Assert.Equal(expected.AllowReason, observed.AllowReason);
        Assert.Equal(expected.DenyReason, observed.DenyReason);
        Assert.Equal(expected.Prompt?.CandidateVerbs, observed.Prompt?.CandidateVerbs);
        Assert.Equal(expected.Prompt?.IsMessy, observed.Prompt?.IsMessy);
        Assert.Equal(expected.Prompt?.CandidateDirectories, observed.Prompt?.CandidateDirectories);
    }
}
