// -----------------------------------------------------------------------
// <copyright file="HardDenyRuleTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class HardDenyRuleTests
{
    // -------------------------------------------------------------------
    // Validate: required fields
    // -------------------------------------------------------------------

    [Fact]
    public void Validate_succeeds_for_simple_verb_rule()
    {
        var rule = new HardDenyRule
        {
            Verb = ["docker", "rm"],
            Reason = "local_policy"
        };

        rule.Validate();
    }

    [Fact]
    public void Validate_succeeds_for_verb_prefix_rule()
    {
        var rule = new HardDenyRule
        {
            VerbPrefix = "mkfs",
            Reason = "filesystem_destruction"
        };

        rule.Validate();
    }

    [Fact]
    public void Validate_succeeds_for_raw_text_escape_hatch()
    {
        var rule = new HardDenyRule
        {
            RawText = ":(){ :|:& };:",
            Reason = "fork_bomb",
            EscapeHatch = true
        };

        rule.Validate();
    }

    [Fact]
    public void Validate_succeeds_for_refined_verb_rule()
    {
        var rule = new HardDenyRule
        {
            Verb = ["rm"],
            ArgFlags = ["-rf"],
            FirstPath = new PathConstraint { OneOf = ["/", "~"] },
            Reason = "destructive_root"
        };

        rule.Validate();
    }

    // -------------------------------------------------------------------
    // Validate: rejection
    // -------------------------------------------------------------------

    [Fact]
    public void Validate_rejects_rule_with_no_match_shape()
    {
        var rule = new HardDenyRule { Reason = "missing_shape" };
        var ex = Assert.Throws<InvalidDataException>(() => rule.Validate());
        Assert.Contains("no match shape", ex.Message);
    }

    [Fact]
    public void Validate_rejects_rule_with_multiple_match_shapes()
    {
        var rule = new HardDenyRule
        {
            Verb = ["git"],
            VerbPrefix = "git",
            Reason = "dual_shapes"
        };

        var ex = Assert.Throws<InvalidDataException>(() => rule.Validate());
        Assert.Contains("multiple match shapes", ex.Message);
    }

    [Fact]
    public void Validate_rejects_rule_with_verb_and_raw_text()
    {
        var rule = new HardDenyRule
        {
            Verb = ["git"],
            RawText = "anything",
            Reason = "dual_shapes"
        };

        Assert.Throws<InvalidDataException>(() => rule.Validate());
    }

    [Fact]
    public void Validate_rejects_refinement_without_verb()
    {
        var rule = new HardDenyRule
        {
            VerbPrefix = "mkfs",
            ArgFlags = ["-f"],
            Reason = "refinement_misuse"
        };

        var ex = Assert.Throws<InvalidDataException>(() => rule.Validate());
        Assert.Contains("Refinements only apply to verb-matched rules", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_rejects_first_path_without_verb()
    {
        var rule = new HardDenyRule
        {
            VerbPrefix = "anything",
            FirstPath = new PathConstraint { OneOf = ["/etc"] },
            Reason = "refinement_misuse"
        };

        Assert.Throws<InvalidDataException>(() => rule.Validate());
    }

    [Fact]
    public void Validate_rejects_empty_verb_list()
    {
        var rule = new HardDenyRule
        {
            Verb = [],
            Reason = "empty_verb"
        };

        Assert.Throws<InvalidDataException>(() => rule.Validate());
    }
}
