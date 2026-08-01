// -----------------------------------------------------------------------
// <copyright file="ShellCommandPolicyOverrideTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// Tests for ShellCommandPolicy's HardDenyRule override-rule support
/// (the new ctor accepting structured operator overrides).
/// Existing string-pattern + default-rule tests live in their own file.
/// </summary>
public sealed class ShellCommandPolicyOverrideTests
{
    [Fact]
    public void Override_verb_rule_denies_matching_command()
    {
        var policy = new ShellCommandPolicy(
            ShellExecutionEnvironment.Current,
            additionalDenyPatterns: null,
            overrideRules:
            [
                new HardDenyRule { Verb = ["docker", "rm"], Reason = "local_policy" }
            ]);

        var decision = policy.Evaluate("docker rm my-container");
        Assert.False(decision.Allowed);
        Assert.Equal("local_policy", decision.DenyReason);
    }

    [Fact]
    public void Override_verb_rule_allows_non_matching_command()
    {
        var policy = new ShellCommandPolicy(
            ShellExecutionEnvironment.Current,
            additionalDenyPatterns: null,
            overrideRules:
            [
                new HardDenyRule { Verb = ["docker", "rm"], Reason = "local_policy" }
            ]);

        Assert.True(policy.Evaluate("docker ps").Allowed);
        Assert.True(policy.Evaluate("docker run nginx").Allowed);
    }

    [Fact]
    public void Override_verb_prefix_rule_denies_family()
    {
        // Note: shipped defaults already include `mkfs` prefix; this verifies
        // the override mechanism correctly translates a verbPrefix rule.
        var policy = new ShellCommandPolicy(
            ShellExecutionEnvironment.Current,
            additionalDenyPatterns: null,
            overrideRules:
            [
                new HardDenyRule { VerbPrefix = "danger.", Reason = "test_family" }
            ]);

        Assert.False(policy.Evaluate("danger.ext4 /dev/sda").Allowed);
        Assert.False(policy.Evaluate("danger.xfs /dev/sda").Allowed);
        Assert.True(policy.Evaluate("safer something").Allowed);
    }

    [Fact]
    public void Override_raw_text_rule_denies_substring_match()
    {
        var policy = new ShellCommandPolicy(
            ShellExecutionEnvironment.Current,
            additionalDenyPatterns: null,
            overrideRules:
            [
                new HardDenyRule
                {
                    RawText = "MAGIC_DENY_TOKEN",
                    Reason = "raw_test",
                    EscapeHatch = true
                }
            ]);

        Assert.False(policy.Evaluate("echo MAGIC_DENY_TOKEN").Allowed);
        Assert.True(policy.Evaluate("echo hello").Allowed);
    }

    [Fact]
    public void Override_refined_verb_with_arg_flag_requires_flag_present()
    {
        var policy = new ShellCommandPolicy(
            ShellExecutionEnvironment.Current,
            additionalDenyPatterns: null,
            overrideRules:
            [
                new HardDenyRule
                {
                    Verb = ["tar"],
                    ArgFlags = ["--delete"],
                    Reason = "tar_delete_blocked"
                }
            ]);

        Assert.False(policy.Evaluate("tar --delete -f archive.tar foo").Allowed);
        Assert.True(policy.Evaluate("tar -czf out.tar foo").Allowed);
    }

    [Fact]
    public void Override_refined_verb_arg_flag_matches_combined_short_flags()
    {
        // -rf as a required flag should match against tokens like -rfv that
        // pack multiple short flags into one combined token.
        var policy = new ShellCommandPolicy(
            ShellExecutionEnvironment.Current,
            additionalDenyPatterns: null,
            overrideRules:
            [
                new HardDenyRule
                {
                    Verb = ["custom-tool"],
                    ArgFlags = ["-rf"],
                    Reason = "combined_flag_test"
                }
            ]);

        Assert.False(policy.Evaluate("custom-tool -rfv /tmp").Allowed);
        Assert.False(policy.Evaluate("custom-tool -rf /tmp").Allowed);
        Assert.True(policy.Evaluate("custom-tool -v /tmp").Allowed);
    }

    [Fact]
    public void Override_refined_verb_with_first_path_constraint()
    {
        var policy = new ShellCommandPolicy(
            ShellExecutionEnvironment.Current,
            additionalDenyPatterns: null,
            overrideRules:
            [
                new HardDenyRule
                {
                    Verb = ["custom-tool"],
                    FirstPath = new PathConstraint { OneOf = ["/", "/etc"] },
                    Reason = "first_path_test"
                }
            ]);

        Assert.False(policy.Evaluate("custom-tool /").Allowed);
        Assert.False(policy.Evaluate("custom-tool /etc").Allowed);
        // First non-flag arg is /tmp, which is not in the allowed list → not denied.
        Assert.True(policy.Evaluate("custom-tool /tmp").Allowed);
    }

    [Fact]
    public void Shipped_defaults_remain_active_alongside_overrides()
    {
        // Override does not weaken or remove shipped defaults. The shipped
        // defaults asserted below (rm -rf /, the fork bomb) are Bash/POSIX
        // shapes, so pin the Bash grammar to exercise them deterministically on
        // any host — the override-mechanism-preserves-defaults invariant is
        // grammar-independent.
        var policy = new ShellCommandPolicy(
            ShellExecutionEnvironment.Bash(),
            additionalDenyPatterns: null,
            overrideRules:
            [
                new HardDenyRule { Verb = ["docker", "rm"], Reason = "local_policy" }
            ]);

        // Shipped default: netclaw daemon stop
        Assert.False(policy.Evaluate("netclaw daemon stop").Allowed);
        // Shipped default: rm -rf /
        Assert.False(policy.Evaluate("rm -rf /").Allowed);
        // Shipped default fork bomb (raw string pattern)
        Assert.False(policy.Evaluate(":(){ :|:& };:").Allowed);
    }

    [Fact]
    public void Invalid_override_rule_throws_at_construction_time()
    {
        // Loader normally validates; this verifies the policy ctor also
        // rejects invalid rules as a defense-in-depth check.
        Assert.Throws<InvalidDataException>(() =>
            new ShellCommandPolicy(
                ShellExecutionEnvironment.Current,
                additionalDenyPatterns: null,
                overrideRules:
                [
                    new HardDenyRule { Reason = "no_shape" }
                ]));
    }

    [Fact]
    public void Combined_string_patterns_and_override_rules_both_apply()
    {
        var policy = new ShellCommandPolicy(
            ShellExecutionEnvironment.Current,
            additionalDenyPatterns: ["legacy-bad-tool"],
            overrideRules:
            [
                new HardDenyRule { Verb = ["modern", "bad"], Reason = "modern_policy" }
            ]);

        Assert.False(policy.Evaluate("legacy-bad-tool args").Allowed);
        Assert.False(policy.Evaluate("modern bad command").Allowed);
        Assert.True(policy.Evaluate("safe command").Allowed);
    }
}
