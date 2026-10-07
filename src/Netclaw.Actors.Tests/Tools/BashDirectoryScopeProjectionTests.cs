// -----------------------------------------------------------------------
// <copyright file="BashDirectoryScopeProjectionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Security;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class BashDirectoryScopeProjectionTests
{
    private static readonly ShellExecutionEnvironment BashEnvironment =
        ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);

    [Fact]
    public void Exact_diagnostic_chain_gives_each_occurrence_every_reachable_directory()
    {
        var projection = Project(
            "cd /tmp && gh api repos/example/project/actions/jobs/123456/logs "
            + "> slopwatch.log 2>&1; wc -c slopwatch.log; head -100 slopwatch.log");

        // A diagnostic runs in /tmp after the change, or in /work when cd fails.
        Assert.Equal(
            [
                "cd|/tmp|/work|",
                "gh api|/tmp|/tmp|",
                "wc|/tmp|/tmp|/tmp",
                "wc|/work|/work|/tmp",
                "head|/tmp|/tmp|/tmp",
                "head|/work|/work|/tmp",
            ],
            Describe(projection));
    }

    [Fact]
    public void Later_success_gated_transition_replaces_intent()
    {
        var projection = Project(
            "cd /tmp && inspect; head first.log; "
            + "cd /var/tmp && collect; wc second.log");

        Assert.Equal(
            [
                "head|/tmp|/tmp|/tmp",
                "head|/work|/work|/tmp",
                "wc|/tmp|/tmp|/var/tmp",
                "wc|/var/tmp|/var/tmp|/var/tmp",
                "wc|/work|/work|/var/tmp",
            ],
            Describe(projection).Where(static row => !row.EndsWith('|')));
    }

    [Theory]
    [InlineData("command cd /tmp && inspect; head result.log")]
    [InlineData("builtin cd /tmp && inspect; head result.log")]
    public void Parser_owned_directory_effect_establishes_intent(string command)
    {
        var projection = Project(command);

        Assert.True(projection.IsCausalList);
        Assert.Contains("head|/tmp|/tmp|/tmp", Describe(projection));
    }

    [Theory]
    [InlineData("cd /tmp && inspect; cd \"$1\"; head result.log")]
    [InlineData("cd /tmp && inspect || recover; head result.log")]
    [InlineData("(cd /tmp && inspect); head result.log")]
    [InlineData("cd /tmp && inspect; head result.log > copy.log")]
    [InlineData("cd /tmp && inspect; head /etc/passwd")]
    [InlineData("cd /tmp && inspect; \"$tool\" result.log")]
    [InlineData("cd /tmp && inspect; status-report \"$OPTS\"")]
    [InlineData("chdir /tmp && inspect; head result.log")]
    [InlineData("pushd /tmp && inspect; head result.log")]
    [InlineData("cd /tmp && inspect; pushd /other; head result.log")]
    [InlineData("cd /tmp && inspect; popd; head result.log")]
    [InlineData("cd /tmp extra && inspect; head result.log")]
    [InlineData("cd -z /tmp && inspect; head result.log")]
    [InlineData("pwd; cd /tmp && inspect; head result.log")]
    [InlineData("cd /tmp && inspect; cd /var/tmp; head result.log")]
    [InlineData("cd /tmp && inspect; head result.log | wc -c")]
    [InlineData("cd /tmp && inspect")]
    public void Unsupported_or_ambiguous_flow_does_not_publish_intent(string command)
    {
        Assert.False(
            TryProject(BashEnvironment, command, out var projection)
            && projection.IsCausalList);
    }

    [Fact]
    public void Native_power_shell_does_not_publish_bash_causal_intent()
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            "C:\\Program Files\\PowerShell\\7\\pwsh.exe",
            PwshDialect.PowerShell7);

        Assert.False(TryProject(
            environment,
            "Set-Location C:\\Temp; Get-Content result.log",
            out _));
    }

    [Fact]
    public void Link_below_the_platform_temporary_root_is_not_an_alias()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Only the platform temporary root itself can be an alias (R7). A link
        // that a process plants below it must not open a causal intent.
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"netclaw-causal-alias-{Guid.NewGuid():N}");
        var target = Path.Combine(testRoot, "target");
        var alias = Path.Combine(testRoot, "alias");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(alias, target);

        try
        {
            Assert.True(TryProject(
                BashEnvironment,
                $"cd {target} && inspect > result.log 2>&1; head result.log",
                out var direct));
            Assert.True(direct.IsCausalList);
            Assert.False(
                TryProject(
                    BashEnvironment,
                    $"cd {alias} && inspect > result.log 2>&1; head result.log",
                    out var linked)
                && linked.IsCausalList);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static BashDirectoryScopeProjection Project(string command)
    {
        Assert.True(TryProject(BashEnvironment, command, out var projection));
        return projection;
    }

    private static bool TryProject(
        ShellExecutionEnvironment environment,
        string command,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BashDirectoryScopeProjection? projection)
    {
        var policy = new ShellCommandPolicy(environment);
        return BashDirectoryScopeProjection.TryCreate(
            policy.Analyze(command, environment.PathStyle == ShellPathStyle.Posix ? "/work" : "C:\\work"),
            policy,
            new ShellApprovalMatcher(environment),
            out projection);
    }

    // verb|candidate directory|slice directory|intent directory
    private static IReadOnlyList<string> Describe(BashDirectoryScopeProjection projection)
        => projection.Slices
            .SelectMany(static slice => slice.Approval.Candidates.Select(candidate =>
                $"{candidate.Verb}|{candidate.Directory}|{slice.WorkingDirectory}|{slice.IntentDirectory}"))
            .ToArray();
}
