// -----------------------------------------------------------------------
// <copyright file="ShellAssignmentConstraintTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ShellAssignmentConstraintTests
{
    private static readonly ToolName ShellToolName = new("shell_execute");
    private static readonly ShellApprovalMatcher BashMatcher = new(
        ShellExecutionEnvironment.CreateBash(
            ShellPlatform.Linux,
            new Version(5, 2)));

    [Fact]
    public void Bash_shell_state_uses_the_canonical_version_one_digest()
    {
        var candidate = ExtractSingle(
            BashMatcher,
            "mode='fast'; inspect item",
            "/work");

        Assert.Equal(
            "sha256:d17b5c9682ce3e864678f5c69763b677e40eb33d88f4ec54ed7106cf1d1a25c5",
            Assert.IsType<ApprovalAssignmentDigest>(
                candidate.AssignmentConstraint.Digest).Value);
    }

    [Fact]
    public void Assignment_name_value_and_scope_change_the_digest()
    {
        var shellState = ExtractSingle(BashMatcher, "mode='fast'; inspect item", "/work");
        var changedName = ExtractSingle(BashMatcher, "other='fast'; inspect item", "/work");
        var changedValue = ExtractSingle(BashMatcher, "mode='slow'; inspect item", "/work");
        var commandEnvironment = ExtractSingle(BashMatcher, "mode='fast' inspect item", "/work");

        var digests = new[] { shellState, changedName, changedValue, commandEnvironment }
            .Select(static candidate => candidate.AssignmentConstraint.Digest)
            .ToHashSet();
        Assert.Equal(4, digests.Count);
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7, "C:\\PowerShell\\7\\pwsh.exe")]
    [InlineData(
        PwshDialect.WindowsPowerShell51,
        "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe")]
    public void PowerShell_assignment_uses_its_canonical_shell_and_environment_facts(
        PwshDialect dialect,
        string executable)
    {
        var matcher = new ShellApprovalMatcher(
            ShellExecutionEnvironment.CreatePowerShell(executable, dialect));

        var candidate = ExtractSingle(
            matcher,
            "$mode='fast'; inspect item",
            "C:/work");

        Assert.Equal(
            "sha256:9f7f62fe7f1f23b11f416a1f3e7babaed28073eb2c50e926291f808c87267fd2",
            Assert.IsType<ApprovalAssignmentDigest>(
                candidate.AssignmentConstraint.Digest).Value);
    }

    [Fact]
    public void PowerShell_assignment_with_an_absolute_directory_remains_reusable()
    {
        var matcher = new ShellApprovalMatcher(
            ShellExecutionEnvironment.CreatePowerShell(
                "C:\\PowerShell\\7\\pwsh.exe",
                PwshDialect.PowerShell7));

        var candidate = ExtractSingle(
            matcher,
            "$mode = 'release'; Set-Location 'C:\\work\\project\\tasks'",
            "C:\\work\\project");

        Assert.Equal("Set-Location", candidate.Verb);
        Assert.Equal("C:/work/project/tasks", candidate.Directory);
        Assert.Equal(
            ApprovalAssignmentConstraintKind.ExactDigest,
            candidate.AssignmentConstraint.Kind);
    }

    [Fact]
    public void Invalid_UTF16_assignment_value_keeps_the_call_one_time()
    {
        var analysis = BashMatcher.AnalyzeInvocation(
            ShellToolName,
            Args("mode='\ud800'; inspect item", "/work"));

        Assert.True(analysis.IsMessy);
        Assert.Empty(analysis.Candidates);
    }

    [Fact]
    public void Bash_multiline_display_keeps_the_assignment_visible()
    {
        var analysis = BashMatcher.AnalyzeInvocation(
            ShellToolName,
            Args("mode='fast';\ninspect item", "/work"));

        Assert.False(analysis.IsMessy, analysis.DisplayText);
        Assert.Contains("mode='fast'", analysis.DisplayText, StringComparison.Ordinal);
        Assert.Contains("⏎", analysis.DisplayText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bash -lc \"mode='fast'; inspect item\"")]
    [InlineData("/bin/bash -lc \"mode='fast'; inspect item\"")]
    [InlineData("dash -c \"mode='fast'; inspect item\"")]
    [InlineData("/bin/dash -c \"mode='fast'; inspect item\"")]
    [InlineData("ksh -c \"mode='fast'; inspect item\"")]
    [InlineData("command bash -lc \"mode='fast'; inspect item\"")]
    public void Assignment_in_a_fallback_shell_wrapper_stays_one_time(string command)
    {
        var analysis = BashMatcher.AnalyzeInvocation(
            ShellToolName,
            Args(command, "/work"));

        Assert.True(analysis.IsMessy);
        Assert.Empty(analysis.Candidates);
    }

    [Theory]
    [InlineData("env bash -lc \"mode='fast'; inspect item\"")]
    [InlineData("nohup bash -lc \"mode='fast'; inspect item\"")]
    [InlineData("timeout 5 bash -lc \"mode='fast'; inspect item\"")]
    [InlineData("nice -n 5 bash -lc \"mode='fast'; inspect item\"")]
    public void Assignment_in_a_prefixed_fallback_shell_wrapper_stays_one_time(string command)
    {
        var analysis = BashMatcher.AnalyzeInvocation(
            ShellToolName,
            Args(command, "/work"));

        Assert.True(analysis.IsMessy);
        Assert.Empty(analysis.Candidates);
    }

    [Fact]
    public void Fallback_wrapper_with_an_assignment_keeps_hard_deny_review()
    {
        var policy = new ShellCommandPolicy(BashMatcher.Environment);

        var decision = policy.Evaluate(
            "bash -lc \"mode='fast'; netclaw daemon stop\"",
            "/work");

        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.SelfDestructive, decision.DenyCategory);
    }

    [Fact]
    public void Fallback_wrapper_without_assignments_remains_reusable()
    {
        var analysis = BashMatcher.AnalyzeInvocation(
            ShellToolName,
            Args("bash -lc \"inspect item\"", "/work"));

        Assert.False(analysis.IsMessy);
        var candidate = Assert.Single(analysis.Candidates);
        Assert.Equal(ApprovalAssignmentConstraint.None, candidate.AssignmentConstraint);
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7, "C:\\PowerShell\\7\\pwsh.exe")]
    [InlineData(
        PwshDialect.WindowsPowerShell51,
        "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe")]
    public void PowerShell_multiline_display_keeps_the_assignment_visible(
        PwshDialect dialect,
        string executable)
    {
        var matcher = new ShellApprovalMatcher(
            ShellExecutionEnvironment.CreatePowerShell(executable, dialect));
        var analysis = matcher.AnalyzeInvocation(
            ShellToolName,
            Args("$mode='fast';\ninspect item", "C:/work"));

        Assert.False(analysis.IsMessy, analysis.DisplayText);
        Assert.Contains("$mode='fast'", analysis.DisplayText, StringComparison.Ordinal);
        Assert.Contains("⏎", analysis.DisplayText, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShell_assignment_span_can_discharge_only_one_opaque_node()
    {
        const string Source = "$mode='fast'; inspect item";
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            "C:\\PowerShell\\7\\pwsh.exe",
            PwshDialect.PowerShell7);
        var parsed = environment.Parse(Source, "C:/work");
        var block = Assert.IsType<ShellBlockSyntax>(parsed.Syntax);
        var list = Assert.IsType<CommandListSyntax>(Assert.Single(block.Statements));
        var opaqueAssignmentNode = list.Items[0].Command;
        Assert.IsNotType<SimpleCommandSyntax>(opaqueAssignmentNode);
        Assert.True(ShellCommandAnalysis.AssignmentSyntaxReconciliation.TryCreate(
            Source,
            parsed.Commands,
            out var reconciliation));

        Assert.True(reconciliation.TryConsume(opaqueAssignmentNode));
        Assert.True(reconciliation.AllConsumed);
        Assert.False(reconciliation.TryConsume(opaqueAssignmentNode));
        Assert.False(reconciliation.TryConsume(list.Items[1].Command));

        const string NoAssignmentSource = "inspect item";
        var noAssignment = environment.Parse(NoAssignmentSource, "C:/work");
        Assert.True(ShellCommandAnalysis.AssignmentSyntaxReconciliation.TryCreate(
            NoAssignmentSource,
            noAssignment.Commands,
            out var emptyReconciliation));
        Assert.False(ShellCommandAnalysis.TryCollectKnownExecutionRegionArguments(
            opaqueAssignmentNode,
            new HashSet<ClauseElement>(),
            emptyReconciliation));
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7, "C:\\PowerShell\\7\\pwsh.exe")]
    [InlineData(
        PwshDialect.WindowsPowerShell51,
        "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe")]
    public void PowerShell_finite_redirect_loop_checks_each_projected_path(
        PwshDialect dialect,
        string executable)
    {
        const string Command =
            "foreach ($item in @('C:/one/a.txt', 'C:/two/b.txt')) "
            + "{ Write-Output x > $item }";
        var matcher = new ShellApprovalMatcher(
            ShellExecutionEnvironment.CreatePowerShell(executable, dialect));
        var arguments = Args(Command, "C:/work");
        var shellAnalysis = new ShellCommandAnalyzer(matcher.Environment)
            .Analyze(Command, "C:/work");
        var analysis = matcher.AnalyzeInvocation(
            ShellToolName,
            arguments,
            shellAnalysis);

        Assert.False(
            analysis.IsMessy,
            $"failure={shellAnalysis.Failure}; dynamic={shellAnalysis.HasDynamicSyntax}; "
            + $"tree={shellAnalysis.RequiresExactTreeApproval}; commands={shellAnalysis.Commands.Count}; "
            + $"candidates={analysis.Candidates.Count}; display={analysis.DisplayText}");
        Assert.Equal(2, analysis.Candidates.Count);
        Assert.All(
            analysis.Candidates,
            static candidate => Assert.Equal(
                ApprovalAssignmentConstraint.None,
                candidate.AssignmentConstraint));
        var grants = analysis.Candidates.Select(static candidate =>
            ApprovalEntry.CreateTokenPrefix(
                ApprovalShell.PowerShell,
                Assert.IsAssignableFrom<IReadOnlyList<string>>(candidate.VerbTokens),
                candidate.Directory)).ToArray();

        Assert.False(matcher.IsApproved(
            ShellToolName,
            arguments,
            [grants[0]],
            cwd: "C:/work"));
        Assert.True(matcher.IsApproved(
            ShellToolName,
            arguments,
            grants,
            cwd: "C:/work"));
    }

    [Theory]
    [InlineData(PwshDialect.PowerShell7, "C:\\PowerShell\\7\\pwsh.exe")]
    [InlineData(
        PwshDialect.WindowsPowerShell51,
        "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe")]
    public void PowerShell_loop_operand_without_public_path_facts_stays_one_time(
        PwshDialect dialect,
        string executable)
    {
        const string Command =
            "foreach ($item in @('C:/work/one.txt', 'C:/work/two.txt')) "
            + "{ Get-Item -LiteralPath $item }";
        var matcher = new ShellApprovalMatcher(
            ShellExecutionEnvironment.CreatePowerShell(executable, dialect));
        var analysis = matcher.AnalyzeInvocation(
            ShellToolName,
            Args(Command, "C:/work"));

        Assert.True(analysis.IsMessy);
        Assert.Empty(analysis.Candidates);
    }

    [Theory]
    [InlineData("foreach ($HOME in @('one.txt')) { Get-Item $HOME }")]
    [InlineData("foreach ($item in @('Env:/PATH')) { Get-Item $item }")]
    [InlineData("foreach ($item in @('one.txt')) { Set-Variable item 'two.txt'; Get-Item $item }")]
    public void PowerShell_unsafe_loop_forms_keep_the_call_one_time(string command)
    {
        var matcher = new ShellApprovalMatcher(
            ShellExecutionEnvironment.CreatePowerShell(
                "C:\\PowerShell\\7\\pwsh.exe",
                PwshDialect.PowerShell7));
        var analysis = matcher.AnalyzeInvocation(
            ShellToolName,
            Args(command, "C:/work"));

        Assert.True(analysis.IsMessy);
        Assert.Empty(analysis.Candidates);
    }

    [Theory]
    [InlineData("mode=$other; inspect item")]
    [InlineData("PATH=/other inspect item")]
    [InlineData("mode='one'; mode='two'; inspect item")]
    public void Unsupported_assignment_forms_keep_the_call_one_time(string command)
    {
        var analysis = BashMatcher.AnalyzeInvocation(
            ShellToolName,
            Args(command, "/work"));

        Assert.True(analysis.IsMessy);
        Assert.Empty(analysis.Candidates);
    }

    private static ApprovalCandidate ExtractSingle(
        ShellApprovalMatcher matcher,
        string command,
        string workingDirectory)
    {
        var shellAnalysis = new ShellCommandAnalyzer(matcher.Environment)
            .Analyze(command, workingDirectory);
        var analysis = matcher.AnalyzeInvocation(
            ShellToolName,
            Args(command, workingDirectory),
            shellAnalysis);

        Assert.False(
            analysis.IsMessy,
            $"failure={shellAnalysis.Failure}; dynamic={shellAnalysis.HasDynamicSyntax}; "
            + $"tree={shellAnalysis.RequiresExactTreeApproval}; commands={shellAnalysis.Commands.Count}; "
            + $"candidates={analysis.Candidates.Count}; display={analysis.DisplayText}");
        return Assert.Single(analysis.Candidates);
    }

    private static Dictionary<string, object?> Args(
        string command,
        string workingDirectory) =>
        new()
        {
            ["Command"] = command,
            ["WorkingDirectory"] = workingDirectory,
        };
}
