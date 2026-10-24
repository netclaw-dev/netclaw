// -----------------------------------------------------------------------
// <copyright file="ShellAssignmentDigestTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Tools;
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ShellAssignmentDigestTests
{
    private static readonly ToolName ShellToolName = new("shell_execute");
    private static readonly ManagedTemporaryLocation TestTemporaryLocation = new(
        new SessionStorageEnvelopeRoot(
            Path.Combine(Path.GetTempPath(), "netclaw-shell-assignment-digest")));
    // The daemon environment holds "mode" and "other", so an assignment to them
    // reaches each child process and keeps its digest (decision F3).
    private static readonly ShellApprovalMatcher BashMatcher = new(
        CreateBash52([new("PATH", "/usr/bin"), new("mode", "inherited"), new("other", "inherited")]));

    // The daemon environment holds neither name, so a Bash assignment to them
    // stays in the shell unless the source exports it.
    private static readonly ShellApprovalMatcher InShellMatcher = new(
        CreateBash52([new("PATH", "/usr/bin")]));

    [Fact]
    public void Candidate_rejects_a_default_assignment_digest()
    {
        Assert.Throws<ArgumentNullException>(() => new ApprovalCandidate("inspect", "/work")
        {
            AssignmentDigest = default(ApprovalAssignmentDigest),
        });
    }

    [Fact]
    public void Assignment_digest_does_not_change_public_candidate_identity()
    {
        var first = new ApprovalCandidate("inspect", "/work")
        {
            AssignmentDigest = new ApprovalAssignmentDigest($"sha256:{new string('a', 64)}"),
        };
        var second = new ApprovalCandidate("inspect", "/work")
        {
            AssignmentDigest = new ApprovalAssignmentDigest($"sha256:{new string('b', 64)}"),
        };

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

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
                candidate.AssignmentDigest).Value);
    }

    [Fact]
    public void Assignment_name_value_and_scope_change_the_digest()
    {
        var shellState = ExtractSingle(BashMatcher, "mode='fast'; inspect item", "/work");
        var changedName = ExtractSingle(BashMatcher, "other='fast'; inspect item", "/work");
        var changedValue = ExtractSingle(BashMatcher, "mode='slow'; inspect item", "/work");
        var commandEnvironment = ExtractSingle(BashMatcher, "mode='fast' inspect item", "/work");

        var digests = new[] { shellState, changedName, changedValue, commandEnvironment }
            .Select(static candidate => candidate.AssignmentDigest)
            .ToHashSet();
        Assert.Equal(4, digests.Count);
    }

    // ShellSyntaxTree 0.4.0-beta.12 proves a reassignment. The command sees
    // only the live value, so the digest is the digest of that value.
    [Fact]
    public void Reassignment_uses_the_digest_of_the_live_value()
    {
        var reassigned = ExtractSingle(BashMatcher, "mode='one'; mode='two'; inspect item", "/work");
        var live = ExtractSingle(BashMatcher, "mode='two'; inspect item", "/work");
        var first = ExtractSingle(BashMatcher, "mode='one'; inspect item", "/work");

        Assert.NotNull(reassigned.AssignmentDigest);
        Assert.Equal(live.AssignmentDigest, reassigned.AssignmentDigest);
        Assert.NotEqual(first.AssignmentDigest, reassigned.AssignmentDigest);
    }

    [Fact]
    public void Bash_launch_proved_assignment_value_can_qualify_a_digest()
    {
        // A separate export keeps its operand literal while the assignment uses launch facts.
        const string Command = "DOTNET_ROOT=$TMPDIR/.dotnet; export DOTNET_ROOT; dotnet test";
        var environment = CreateBash52([new("PATH", "/usr/bin")]);
        var analysis = new ShellCommandAnalyzer(environment)
            .Analyze(Command, "/work", TestTemporaryLocation);
        Assert.True(analysis.IsResolved);

        var occurrence = Assert.Single(analysis.Commands, static command =>
            command.Clause.Verb.Tokens.FirstOrDefault() == "dotnet");
        var assignment = Assert.Single(occurrence.Assignments);
        Assert.Equal("DOTNET_ROOT", assignment.Name);
        Assert.IsType<ShellValueDomain.Unknown>(assignment.AuthoredValue);
        Assert.Equal(
            "DOTNET_ROOT=$TMPDIR/.dotnet",
            Command.Substring(assignment.SourceStart, assignment.SourceLength));
        Assert.Equal(
            ShellExecutionEnvironment.GetTemporaryDirectoryValue(TestTemporaryLocation) + "/.dotnet",
            Assert.IsType<ShellValueDomain.Exact>(assignment.EffectiveValue).Value);
        Assert.Equal(ShellVariableAssignmentScope.ShellState, assignment.Scope);
        Assert.True(assignment.MayAffectProcessEnvironment);

        Assert.True(ShellAssignmentDigestFactory.TryCreate(
            ApprovalShell.Bash,
            [assignment],
            out var digest));
        Assert.NotNull(digest);

        var matcher = new ShellApprovalMatcher(environment);
        var matcherAnalysis = matcher.AnalyzeInvocation(
            ShellToolName,
            Args(Command, "/work"),
            analysis);
        var candidate = Assert.Single(matcherAnalysis.Candidates, static item =>
            item.VerbTokens?.FirstOrDefault() == "dotnet");
        Assert.Equal(digest, candidate.AssignmentDigest);

        var tokens = Assert.IsAssignableFrom<IReadOnlyList<string>>(candidate.VerbTokens);
        var qualifiedGrant = ApprovalEntry.CreateTokenPrefix(
            ApprovalShell.Bash,
            tokens,
            candidate.Directory,
            assignmentDigest: digest);
        var plainGrant = ApprovalEntry.CreateTokenPrefix(
            ApprovalShell.Bash,
            tokens,
            candidate.Directory);
        Assert.True(ApprovalPatternMatching.MatchesShellApproval(
            candidate,
            cwd: "/work",
            [qualifiedGrant]));
        Assert.False(ApprovalPatternMatching.MatchesShellApproval(
            candidate,
            cwd: "/work",
            [plainGrant]));
    }

    [Fact]
    public void Bash_assignment_digest_binds_the_effective_launch_and_source_values()
    {
        var environment = CreateBash52([new("PATH", "/usr/bin")]);
        var temporaryDirectory = ShellExecutionEnvironment.GetTemporaryDirectoryValue(TestTemporaryLocation);

        var launchValue = ExtractAssignmentDigest(
            environment,
            "export DOTNET_ROOT=$TMPDIR/.dotnet; dotnet test",
            TestTemporaryLocation);
        var literalValue = ExtractAssignmentDigest(
            environment,
            $"export DOTNET_ROOT='{temporaryDirectory}/.dotnet'; dotnet test",
            TestTemporaryLocation);
        var differentLiteralValue = ExtractAssignmentDigest(
            environment,
            "export DOTNET_ROOT='/other/.dotnet'; dotnet test",
            TestTemporaryLocation);
        const string changedHomeCommand =
            "HOME=/other; export DOTNET_ROOT=$HOME/.dotnet; dotnet test";
        var changedHomeParsed = environment.ParseForApproval(
            changedHomeCommand,
            "/work",
            publishAuthoredSourceFacts: true,
            environment.CreateLaunchEnvironment(TestTemporaryLocation),
            parseCompoundRedirects: false);
        var changedHomeShellAnalysis = new ShellCommandAnalyzer(environment)
            .Analyze(changedHomeCommand, "/work", TestTemporaryLocation);
        var changedHomeAnalysis = new ShellApprovalMatcher(environment).AnalyzeInvocation(
            ShellToolName,
            Args(changedHomeCommand, "/work"),
            changedHomeShellAnalysis);

        Assert.Equal(launchValue, literalValue);
        Assert.NotEqual(launchValue, differentLiteralValue);
        Assert.True(changedHomeParsed.IsUnparseable);
        Assert.Equal(
            "bounded Bash assignment names must use the ordinary scalar boundary",
            changedHomeParsed.UnparseableReason);
        Assert.False(changedHomeShellAnalysis.IsResolved);
        Assert.True(changedHomeAnalysis.IsMessy);
        Assert.Empty(changedHomeAnalysis.Candidates);
    }

    [Fact]
    public void Launch_derived_git_directory_changes_keep_a_digest()
    {
        var environment = CreateBash52([new("PATH", "/usr/bin"), new("GIT_DIR", "/work/.git")]);
        const string command = "export GIT_DIR=$TMPDIR/.launch-value; git status";
        var analysis = new ShellCommandAnalyzer(environment)
            .Analyze(command, "/work", TestTemporaryLocation);
        Assert.True(analysis.IsResolved);

        var occurrence = Assert.Single(analysis.Commands, static item =>
            item.Clause.Verb.Tokens.FirstOrDefault() == "git");
        var assignment = Assert.Single(occurrence.Assignments, item => item.Name == "GIT_DIR");
        Assert.IsType<ShellValueDomain.Unknown>(assignment.AuthoredValue);
        Assert.Equal(
            ShellExecutionEnvironment.GetTemporaryDirectoryValue(TestTemporaryLocation) + "/.launch-value",
            Assert.IsType<ShellValueDomain.Exact>(assignment.EffectiveValue).Value);
        Assert.True(assignment.MayAffectProcessEnvironment);
        Assert.True(ShellAssignmentDigestFactory.TryCreate(
            ApprovalShell.Bash,
            [assignment],
            out var digest));
        Assert.NotNull(digest);
    }

    [Theory]
    [InlineData("PATH")]
    [InlineData("LD_PRELOAD")]
    public void Launch_derived_loader_and_search_path_changes_stay_one_time(string name)
    {
        var environment = CreateBash52([new("PATH", "/usr/bin")]);
        var command = $"export {name}=$HOME/.launch-value; dotnet test";
        var parsed = environment.ParseForApproval(
            command,
            "/work",
            publishAuthoredSourceFacts: true,
            environment.CreateLaunchEnvironment(temporary: null),
            parseCompoundRedirects: false);
        var shellAnalysis = new ShellCommandAnalyzer(environment).Analyze(command, "/work");
        var analysis = new ShellApprovalMatcher(environment).AnalyzeInvocation(
            ShellToolName,
            Args(command, "/work"),
            shellAnalysis);

        Assert.True(parsed.IsUnparseable, parsed.UnparseableReason);
        Assert.Equal(
            "bounded Bash assignment names must use the ordinary scalar boundary",
            parsed.UnparseableReason);
        Assert.False(shellAnalysis.IsResolved);
        Assert.Equal(ShellAnalysisFailure.Unresolved, shellAnalysis.Failure);
        Assert.True(analysis.IsMessy);
        Assert.Empty(analysis.Candidates);
    }

    [Theory]
    [InlineData("export DOTNET_ROOT=$(printf /tmp); dotnet test")]
    [InlineData("unset HOME; export DOTNET_ROOT=$HOME/.dotnet; dotnet test")]
    [InlineData("read HOME; export DOTNET_ROOT=$HOME/.dotnet; dotnet test")]
    [InlineData("if grep needle file; then HOME=/other; fi; export DOTNET_ROOT=$HOME/.dotnet; dotnet test")]
    [InlineData(". /tmp/netclaw.env; export DOTNET_ROOT=$HOME/.dotnet; dotnet test")]
    public void Bash_assignment_digest_rejects_values_without_exact_effect_facts(string command)
    {
        var environment = CreateBash52([new("PATH", "/usr/bin")]);
        var analysis = new ShellCommandAnalyzer(environment).Analyze(command, "/work");
        var occurrence = analysis.Commands.SingleOrDefault(static item =>
            item.Clause.Verb.Tokens.FirstOrDefault() == "dotnet");
        if (occurrence is null)
        {
            Assert.False(analysis.IsResolved);
            return;
        }

        Assert.NotEmpty(occurrence.Assignments);
        Assert.Contains(occurrence.Assignments, static assignment =>
            assignment.EffectiveValue is ShellValueDomain.Unknown);
        Assert.False(ShellAssignmentDigestFactory.TryCreate(
            ApprovalShell.Bash,
            occurrence.Assignments,
            out _));
    }

    [Fact]
    public void Bash_launch_proved_assignment_does_not_hide_a_protected_path()
    {
        const string Command =
            "export TOKEN=$HOME/protected.txt; cat \"$TOKEN\"";
        var environment = CreateBash52([new("PATH", "/usr/bin")]);
        var analysis = new ShellCommandAnalyzer(environment).Analyze(Command, "/work");
        Assert.True(analysis.IsResolved);
        var protectedPath = environment.HomeDirectory + "/protected.txt";

        Assert.True(new ToolPathPolicy(environment, [protectedPath])
            .CommandReferencesDeniedPath(analysis));
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
        var environment = ShellExecutionEnvironment.CreatePowerShell(executable, dialect);
        var shellAnalysis = new ShellCommandAnalyzer(environment)
            .Analyze("$mode='fast'; inspect item", "C:/work");
        Assert.True(shellAnalysis.IsResolved);
        var occurrence = Assert.Single(shellAnalysis.Commands, static command =>
            command.Clause.Verb.Tokens.FirstOrDefault() == "inspect");
        var assignment = Assert.Single(occurrence.Assignments);
        var authored = Assert.IsType<ShellValueDomain.Exact>(assignment.AuthoredValue);
        var effective = Assert.IsType<ShellValueDomain.Exact>(assignment.EffectiveValue);
        Assert.Equal(authored.Value, effective.Value);
        Assert.Equal("fast", effective.Value);

        var invocation = new ShellApprovalMatcher(environment).AnalyzeInvocation(
            ShellToolName,
            Args("$mode='fast'; inspect item", "C:/work"),
            shellAnalysis);
        Assert.False(invocation.IsMessy, invocation.DisplayText);
        var candidate = Assert.Single(invocation.Candidates);

        Assert.Equal(
            "sha256:9f7f62fe7f1f23b11f416a1f3e7babaed28073eb2c50e926291f808c87267fd2",
            Assert.IsType<ApprovalAssignmentDigest>(
                candidate.AssignmentDigest).Value);
    }

    [Fact]
    public void Unknown_Bash_host_does_not_use_a_launch_value_for_an_assignment_digest()
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        const string Command = "export DOTNET_ROOT=$HOME/.dotnet; dotnet test";
        var analysis = new ShellCommandAnalyzer(environment).Analyze(Command, "/work");
        var occurrence = analysis.Commands.SingleOrDefault(static command =>
            command.Clause.Verb.Tokens.FirstOrDefault() == "dotnet");
        if (occurrence is null)
        {
            Assert.False(analysis.IsResolved);
            return;
        }

        Assert.NotEmpty(occurrence.Assignments);
        Assert.Contains(occurrence.Assignments, static assignment =>
            assignment.EffectiveValue is ShellValueDomain.Unknown);
        Assert.False(ShellAssignmentDigestFactory.TryCreate(
            ApprovalShell.Bash,
            occurrence.Assignments,
            out _));
    }

    [Theory]
    [InlineData("mode=fast; export other=$mode; inspect item")]
    [InlineData("export mode=fast; export other=$mode; inspect item")]
    public void Bash_source_binding_exact_value_keeps_unproved_command_one_time(string command)
    {
        var environment = CreateBash52([new("PATH", "/usr/bin")]);
        var analysis = new ShellCommandAnalyzer(environment).Analyze(command, "/work");
        Assert.True(analysis.IsResolved);

        var occurrence = Assert.Single(analysis.Commands, static item =>
            item.Clause.Verb.Tokens.FirstOrDefault() == "inspect");
        var assignment = Assert.Single(occurrence.Assignments, static item => item.Name == "other");
        Assert.IsType<ShellValueDomain.Unknown>(assignment.AuthoredValue);
        Assert.Equal("fast", Assert.IsType<ShellValueDomain.Exact>(assignment.EffectiveValue).Value);
        Assert.True(assignment.MayAffectProcessEnvironment);

        var matcher = new ShellApprovalMatcher(environment);
        var invocation = matcher.AnalyzeInvocation(
            ShellToolName,
            Args(command, "/work"),
            analysis);
        Assert.True(invocation.IsMessy);
        Assert.Empty(invocation.Candidates);
        Assert.NotEmpty(invocation.CommandCandidates);
        Assert.Contains(invocation.CommandCandidates, static candidate =>
            candidate.VerbTokens is null or { Count: 0 });
    }

    [Theory]
    [InlineData("mode=fast; export other=$mode; inspect item")]
    [InlineData("export mode=fast; export other=$mode; inspect item")]
    public void Unknown_Bash_state_rejects_source_bound_effective_values(string command)
    {
        var environment = ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux);
        var analysis = new ShellCommandAnalyzer(environment).Analyze(command, "/work");

        Assert.False(analysis.IsResolved);
        var invocation = new ShellApprovalMatcher(environment).AnalyzeInvocation(
            ShellToolName,
            Args(command, "/work"),
            analysis);
        Assert.True(invocation.IsMessy);
        Assert.Empty(invocation.Candidates);
        Assert.Empty(invocation.CommandCandidates);
    }

    [Theory]
    [InlineData("$first='fast'; $mode=$first; inspect item")]
    [InlineData("$env:MODE=$HOME; inspect item")]
    public void PowerShell_dynamic_assignment_sources_keep_the_call_one_time(string command)
    {
        var environment = ShellExecutionEnvironment.CreatePowerShell(
            "C:\\PowerShell\\7\\pwsh.exe",
            PwshDialect.PowerShell7);
        var analysis = new ShellCommandAnalyzer(environment).Analyze(command, "C:/work");

        Assert.False(analysis.IsResolved);
        var invocation = new ShellApprovalMatcher(environment).AnalyzeInvocation(
            ShellToolName,
            Args(command, "C:/work"),
            analysis);
        Assert.True(invocation.IsMessy);
        Assert.Empty(invocation.Candidates);
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
        Assert.NotNull(candidate.AssignmentDigest);
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
    [InlineData("bash -lc \"export DOTNET_ROOT=$HOME/.dotnet; dotnet test\"")]
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
        Assert.Null(candidate.AssignmentDigest);
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
    public void PowerShell_empty_reconciliation_cannot_discharge_an_assignment_node()
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
            static candidate => Assert.Null(candidate.AssignmentDigest));
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
    public void Unsupported_assignment_forms_keep_the_call_one_time(string command)
    {
        var analysis = BashMatcher.AnalyzeInvocation(
            ShellToolName,
            Args(command, "/work"));

        Assert.True(analysis.IsMessy);
        Assert.Empty(analysis.Candidates);
    }

    // Owner decision F3: Bash passes a shell variable that no path exports, and
    // that the launch environment does not hold, to no program. The digest
    // skips it, so a plain grant covers the command.
    [Theory]
    [InlineData("mode='fast'; inspect item")]
    [InlineData("mode=$(date); inspect item")]
    [InlineData("mode='one'; mode='two'; inspect item")]
    public void Bash_assignment_that_stays_in_the_shell_has_no_digest(string command)
    {
        Assert.Null(ExtractInspect(InShellMatcher, command).AssignmentDigest);
    }

    // SECURITY: each of these assignments can reach the program, so it keeps
    // its digest and a plain grant does not cover the command.
    [Theory]
    [InlineData("mode='fast'; export mode; inspect item")]
    [InlineData("if true; then export mode; fi; mode='fast'; inspect item")]
    [InlineData("mode='fast' inspect item")]
    public void Bash_assignment_that_can_reach_the_program_keeps_its_digest(string command)
    {
        Assert.NotNull(ExtractInspect(InShellMatcher, command).AssignmentDigest);
    }

    // SECURITY: GIT_DIR in the daemon environment is exported, so Bash passes
    // the new value to git. Without it in the environment, the value stays in
    // the shell.
    [Fact]
    public void Assignment_to_a_name_in_the_daemon_environment_keeps_its_digest()
    {
        var withGitDirectory = new ShellApprovalMatcher(
            CreateBash52([new("PATH", "/usr/bin"), new("GIT_DIR", "/work/.git")]));

        Assert.NotNull(ExtractSingle(withGitDirectory, "GIT_DIR=/tmp/x; git status", "/work").AssignmentDigest);
        Assert.Null(ExtractSingle(InShellMatcher, "GIT_DIR=/tmp/x; git status", "/work").AssignmentDigest);
    }

    // SECURITY: "set -a" exports each later assignment, so the source is unresolved.
    [Fact]
    public void Allexport_keeps_the_call_one_time()
    {
        var analysis = InShellMatcher.AnalyzeInvocation(ShellToolName, Args("set -a; mode='fast'; env", "/work"));

        Assert.True(analysis.IsMessy);
        Assert.Empty(analysis.Candidates);
    }

    // The candidate of the "inspect item" command of a source with more commands.
    private static ApprovalCandidate ExtractInspect(ShellApprovalMatcher matcher, string command)
    {
        var analysis = matcher.AnalyzeInvocation(ShellToolName, Args(command, "/work"));

        Assert.False(analysis.IsMessy, analysis.DisplayText);
        return Assert.Single(analysis.Candidates, static candidate => candidate.VerbTokens?[0] == "inspect");
    }

    private static ApprovalAssignmentDigest ExtractAssignmentDigest(
        ShellExecutionEnvironment environment,
        string command,
        ManagedTemporaryLocation temporary)
    {
        var analysis = new ShellCommandAnalyzer(environment)
            .Analyze(command, "/work", temporary);
        Assert.True(analysis.IsResolved);
        var occurrence = Assert.Single(analysis.Commands, static item =>
            item.Clause.Verb.Tokens.FirstOrDefault() == "dotnet");
        Assert.True(ShellAssignmentDigestFactory.TryCreate(
            ApprovalShell.Bash,
            occurrence.Assignments,
            out var digest));
        return Assert.IsType<ApprovalAssignmentDigest>(digest);
    }

    private static ShellExecutionEnvironment CreateBash52(IEnumerable<KeyValuePair<string, string>> environment)
        => ShellExecutionEnvironment.CreateBash(ShellPlatform.Linux, new Version(5, 2), environment);

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
