// -----------------------------------------------------------------------
// <copyright file="BashCausalApprovalIntentTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Tools;
using Netclaw.Security;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public sealed class BashCausalApprovalIntentTests
{
    private static readonly ShellExecutionEnvironment BashEnvironment =
        ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);

    [Fact]
    public void Exact_diagnostic_chain_projects_prerequisites_and_intent_consumers()
    {
        var projected = Project(
            "cd /tmp && gh api repos/example/project/actions/jobs/123456/logs "
            + "> slopwatch.log 2>&1; wc -c slopwatch.log; head -100 slopwatch.log");

        Assert.Collection(
            projected,
            candidate => AssertPrerequisite(candidate, "cd"),
            candidate => AssertPrerequisite(candidate, "gh api"),
            candidate => AssertConsumer(candidate, "wc", "/tmp", ["/work"], [0, 1]),
            candidate => AssertConsumer(candidate, "head", "/tmp", ["/work"], [0, 1]));
    }

    [Fact]
    public void Later_success_gated_transition_replaces_intent_and_prerequisites()
    {
        var projected = Project(
            "cd /tmp && inspect; head first.log; "
            + "cd /var/tmp && collect; wc second.log");

        Assert.Equal(6, projected.Count);
        AssertConsumer(projected[2], "head", "/tmp", ["/work"], [0, 1]);
        AssertConsumer(projected[5], "wc", "/var/tmp", ["/work", "/tmp"], [3, 4]);
    }

    [Theory]
    [InlineData("command cd /tmp && inspect; head result.log")]
    [InlineData("builtin cd /tmp && inspect; head result.log")]
    public void Parser_owned_directory_effect_establishes_intent(string command)
    {
        var projected = Project(command);

        Assert.Equal(3, projected.Count);
        Assert.IsType<ShellWorkingDirectoryEffect.ChangesOnSuccess>(
            projected[0].SourceOccurrence.WorkingDirectoryEffect);
        AssertConsumer(projected[2], "head", "/tmp", ["/work"], [0, 1]);
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
        Assert.False(TryProject(BashEnvironment, command, out _));
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
    public void Captured_temporary_alias_allows_redirect_projection_without_allowing_other_aliases()
    {
        if (OperatingSystem.IsWindows())
            return;

        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"netclaw-causal-alias-{Guid.NewGuid():N}");
        var canonicalTemp = Path.Combine(testRoot, "canonical-temp");
        var authoredTemp = Path.Combine(testRoot, "authored-temp");
        var otherTarget = Path.Combine(testRoot, "other-target");
        var otherAlias = Path.Combine(testRoot, "other-alias");
        Directory.CreateDirectory(canonicalTemp);
        Directory.CreateDirectory(otherTarget);
        Directory.CreateSymbolicLink(authoredTemp, canonicalTemp);
        Directory.CreateSymbolicLink(otherAlias, otherTarget);

        try
        {
            var policy = new TemporaryPathCorrectionPolicy(
                BashEnvironment,
                authoredTemp,
                HostPlatformTemporaryPathInspector.Instance);
            var allowedCommand =
                $"cd {authoredTemp} && inspect > result.log 2>&1; head result.log";
            var otherCommand =
                $"cd {otherAlias} && inspect > result.log 2>&1; head result.log";

            Assert.True(policy.IsEligiblePlatformTemporaryPath(authoredTemp));
            Assert.True(policy.IsEligiblePlatformTemporaryPath(canonicalTemp));
            Assert.True(policy.IsEligiblePlatformTemporaryPath(Path.Combine(authoredTemp, "result.log")));

            Assert.True(TryProject(
                BashEnvironment,
                allowedCommand,
                policy.IsEligiblePlatformTemporaryPath,
                out _));
            Assert.False(TryProject(
                BashEnvironment,
                otherCommand,
                policy.IsEligiblePlatformTemporaryPath,
                out _));
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static IReadOnlyList<BashCausalApprovalCandidate> Project(string command)
    {
        Assert.True(TryProject(BashEnvironment, command, out var projected));
        return projected;
    }

    private static bool TryProject(
        ShellExecutionEnvironment environment,
        string command,
        out IReadOnlyList<BashCausalApprovalCandidate> projected)
        => TryProject(
            environment,
            command,
            TemporaryPathCorrectionPolicy.Create(environment).IsEligiblePlatformTemporaryPath,
            out projected);

    private static bool TryProject(
        ShellExecutionEnvironment environment,
        string command,
        Func<string, bool> isAllowedHostPath,
        out IReadOnlyList<BashCausalApprovalCandidate> projected)
    {
        var analysis = new ShellCommandAnalyzer(environment).Analyze(command, "/work");
        return BashCausalApprovalIntent.TryProject(
            environment,
            analysis,
            new ShellApprovalMatcher(environment),
            isAllowedHostPath,
            out projected);
    }

    private static void AssertPrerequisite(
        BashCausalApprovalCandidate candidate,
        string verb)
    {
        Assert.Equal(ShellPolicyCandidateRole.CausalPrerequisite, candidate.Role);
        Assert.Equal(verb, candidate.Candidate.Verb);
        Assert.Null(candidate.IntentDirectory);
        Assert.Empty(candidate.PrerequisiteIndexes);
    }

    private static void AssertConsumer(
        BashCausalApprovalCandidate candidate,
        string verb,
        string intentDirectory,
        IReadOnlyList<string> fallbackDirectories,
        IReadOnlyList<int> prerequisiteIndexes)
    {
        Assert.Equal(ShellPolicyCandidateRole.CausalIntentConsumer, candidate.Role);
        Assert.Equal(verb, candidate.Candidate.Verb);
        Assert.Null(candidate.Candidate.Directory);
        Assert.Equal(intentDirectory, candidate.IntentDirectory);
        Assert.Equal(fallbackDirectories, candidate.FallbackDirectories);
        Assert.Equal(prerequisiteIndexes, candidate.PrerequisiteIndexes);
    }
}
