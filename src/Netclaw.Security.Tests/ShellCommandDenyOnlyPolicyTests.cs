// -----------------------------------------------------------------------
// <copyright file="ShellCommandDenyOnlyPolicyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using ShellSyntaxTree;
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class ShellCommandDenyOnlyPolicyTests
{
    private static readonly ShellExecutionEnvironment PowerShellEnvironment =
        ShellExecutionEnvironment.CreatePowerShell(
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            PwshDialect.PowerShell7);

    [Theory]
    [InlineData("netclaw daemon stop")]
    [InlineData("Stop-Process -Id $processId")]
    public void Static_categorical_facts_deny_with_dynamic_trailing_arguments(string source)
    {
        var policy = new ShellCommandPolicy(PowerShellEnvironment);

        var decision = policy.EvaluateDenyOnlyClauses(Collect(source));

        Assert.False(decision.Allowed);
    }

    [Theory]
    [InlineData("net`claw daemon stop")]
    [InlineData("& 'netclaw' daemon stop")]
    public void Static_command_head_uses_the_parser_decoded_identity(string source)
    {
        var policy = new ShellCommandPolicy(PowerShellEnvironment);

        var decision = policy.EvaluateDenyOnlyClauses(Collect(source));

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Decoded_wrapper_uses_parser_owned_null_span_provenance()
    {
        var policy = new ShellCommandPolicy(PowerShellEnvironment);
        var clauses = Collect("pwsh -Command 'netclaw daemon stop'");

        var decision = policy.EvaluateDenyOnlyClauses(clauses);

        Assert.Contains(clauses, clause => clause.IsCommandStringWrapped);
        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Decoded_wrapper_keeps_a_dynamic_argument_unknown()
    {
        var policy = new ShellCommandPolicy(PowerShellEnvironment);
        var clauses = Collect("pwsh -Command 'Start-Process pwsh -Verb $verb'");

        var decision = policy.EvaluateDenyOnlyClauses(clauses);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Inert_script_block_in_a_failed_parse_does_not_supply_deny_only_facts()
    {
        const string source =
            "Write-Output { netclaw daemon stop }; "
            + "Invoke-Command -ScriptBlock { Get-Date } -AsJob";
        var parsed = PowerShellEnvironment.Parse(source, @"C:\work");
        var analysis = new ShellCommandAnalyzer(PowerShellEnvironment).Analyze(
            source,
            @"C:\work");

        Assert.True(parsed.IsUnparseable);
        Assert.Empty(analysis.DenyOnlyClauses);
        Assert.True(new ShellCommandPolicy(PowerShellEnvironment).Evaluate(analysis).Allowed);
    }

    [Theory]
    [InlineData("Start-Process pwsh -Verb $verb")]
    [InlineData("Start-Process pwsh -Verb \"Run$part\"")]
    public void Dynamic_values_do_not_satisfy_static_elevation_facts(string source)
    {
        var policy = new ShellCommandPolicy(PowerShellEnvironment);

        var decision = policy.EvaluateDenyOnlyClauses(Collect(source));

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Static_argument_uses_the_parser_decoded_value()
    {
        var policy = new ShellCommandPolicy(PowerShellEnvironment);

        var decision = policy.EvaluateDenyOnlyClauses(
            Collect("Start-Process pwsh -Verb R`unAs"));

        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.PrivilegeEscalation, decision.DenyCategory);
    }

    [Theory]
    [InlineData("Remove-Item -Recurse:'$false' -Force /")]
    [InlineData("Remove-Item -Recurse:`$false -Force /")]
    public void Decoded_argument_preserves_authored_boolean_provenance(string source)
    {
        var policy = new ShellCommandPolicy(PowerShellEnvironment);

        var decision = policy.EvaluateDenyOnlyClauses(Collect(source));

        Assert.False(decision.Allowed);
        Assert.Equal(DenyCategory.SystemDestructive, decision.DenyCategory);
    }

    [Fact]
    public void Custom_pattern_requires_static_contiguous_facts()
    {
        var policy = new ShellCommandPolicy(
            PowerShellEnvironment,
            additionalDenyPatterns: ["custom-tool delete"]);

        Assert.False(policy.EvaluateDenyOnlyClauses(
            Collect("custom-tool delete $target")).Allowed);
        Assert.True(policy.EvaluateDenyOnlyClauses(
            Collect("custom-tool $operation delete")).Allowed);
    }

    [Fact]
    public void Interleaved_flag_keeps_the_full_policy_token_order()
    {
        var policy = new ShellCommandPolicy(PowerShellEnvironment);
        const string source = "netclaw --verbose daemon stop";

        var fullDecision = policy.Evaluate(source, @"C:\work");
        var denyOnlyDecision = policy.EvaluateDenyOnlyClauses(Collect(source));

        Assert.Equal(fullDecision.Allowed, denyOnlyDecision.Allowed);
        Assert.True(denyOnlyDecision.Allowed);
    }

    [Theory]
    [InlineData("$operation", false)]
    [InlineData("$otherOperation", true)]
    public void Legacy_custom_pattern_matches_only_the_exact_authored_dynamic_spelling(
        string operand,
        bool expectedAllowed)
    {
        var policy = new ShellCommandPolicy(
            PowerShellEnvironment,
            additionalDenyPatterns: ["custom-tool $operation"]);

        var decision = policy.Evaluate(
            $"custom-tool {operand}; $item++",
            @"C:\work");

        Assert.Equal(expectedAllowed, decision.Allowed);
        if (!expectedAllowed)
            Assert.Equal(DenyCategory.CustomDeny, decision.DenyCategory);
    }

    [Fact]
    public void Refined_rule_uses_a_static_tilde_path_but_not_a_dynamic_first_operand()
    {
        var policy = new ShellCommandPolicy(
            PowerShellEnvironment,
            additionalDenyPatterns: null,
            overrideRules:
            [
                new HardDenyRule
                {
                    Verb = ["custom-tool"],
                    ArgFlags = ["--delete"],
                    FirstPath = new PathConstraint { OneOf = ["~"] },
                    Reason = "test_rule"
                }
            ]);

        Assert.False(policy.EvaluateDenyOnlyClauses(
            Collect("custom-tool --delete ~")).Allowed);
        Assert.True(policy.EvaluateDenyOnlyClauses(
            Collect("custom-tool --delete $path ~")).Allowed);
    }

    [Fact]
    public void Dynamic_first_operand_does_not_normalize_to_root()
    {
        var policy = new ShellCommandPolicy(
            PowerShellEnvironment,
            additionalDenyPatterns: null,
            overrideRules:
            [
                new HardDenyRule
                {
                    Verb = ["custom-tool"],
                    FirstPath = new PathConstraint { OneOf = ["/"] },
                    Reason = "test_rule"
                }
            ]);

        var decision = policy.EvaluateDenyOnlyClauses(
            Collect("custom-tool $path /"));

        Assert.True(decision.Allowed);
    }

    [Theory]
    [InlineData("Write-Output 'netclaw daemon stop'")]
    [InlineData("Write-Output \"netclaw daemon stop\"")]
    [InlineData("Write-Output ok # netclaw daemon stop")]
    public void Quoted_data_and_comments_do_not_create_a_hard_deny(string source)
    {
        var policy = new ShellCommandPolicy(PowerShellEnvironment);

        var decision = policy.EvaluateDenyOnlyClauses(Collect(source));

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Collector_rejects_source_that_does_not_match_the_parser_facts()
    {
        var parsed = PowerShellEnvironment.Parse("netclaw daemon stop", @"C:\work");
        var clauses = new List<Clause>();

        ShellCommandAnalysis.CollectSourceAuthenticDenyOnlyClauses(
            parsed.Syntax,
            "netclaw daemon kill",
            clauses);

        Assert.Empty(clauses);
    }

    [Fact]
    public void Analysis_snapshots_deny_only_clauses()
    {
        var clauses = Collect("netclaw daemon stop").ToList();
        var analysis = new ShellCommandAnalysis(
            PowerShellEnvironment,
            "netclaw daemon stop",
            @"C:\work",
            commands: [],
            clauses,
            ShellAnalysisFailure.Unresolved,
            new HashSet<ClauseElement>(ReferenceEqualityComparer.Instance),
            syntaxProofComplete: false);

        clauses.Clear();

        Assert.Single(analysis.DenyOnlyClauses);
        Assert.False(analysis.IsResolved);
    }

    private static IReadOnlyList<Clause> Collect(string source)
    {
        var parsed = PowerShellEnvironment.Parse(source, @"C:\work");
        Assert.False(parsed.IsUnparseable, parsed.UnparseableReason);
        var clauses = new List<Clause>();
        ShellCommandAnalysis.CollectSourceAuthenticDenyOnlyClauses(
            parsed.Syntax,
            source,
            clauses);
        Assert.NotEmpty(clauses);
        return clauses;
    }
}
