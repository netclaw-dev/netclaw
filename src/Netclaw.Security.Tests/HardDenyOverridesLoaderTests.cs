// -----------------------------------------------------------------------
// <copyright file="HardDenyOverridesLoaderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Security.Tests;

public sealed class HardDenyOverridesLoaderTests : IDisposable
{
    private readonly string _file;
    private readonly HardDenyOverridesLoader _loader = new();

    public HardDenyOverridesLoaderTests()
    {
        _file = Path.Combine(Path.GetTempPath(), $"netclaw-deny-overrides-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_file)) File.Delete(_file);
    }

    [Fact]
    public void Load_returns_empty_when_file_missing()
    {
        Assert.Empty(_loader.Load(_file));
    }

    [Fact]
    public void Load_returns_empty_when_path_null_or_blank()
    {
        Assert.Empty(_loader.Load(null!));
        Assert.Empty(_loader.Load("   "));
    }

    [Fact]
    public void Load_returns_empty_when_file_is_empty_string()
    {
        File.WriteAllText(_file, "");
        Assert.Empty(_loader.Load(_file));
    }

    [Fact]
    public void Load_returns_empty_when_file_is_empty_array()
    {
        File.WriteAllText(_file, "[]");
        Assert.Empty(_loader.Load(_file));
    }

    [Fact]
    public void Load_parses_simple_verb_rule()
    {
        File.WriteAllText(_file, """
        [
          { "verb": ["docker", "rm"], "reason": "local_policy" }
        ]
        """);

        var rules = _loader.Load(_file);
        Assert.Single(rules);
        Assert.Equal(["docker", "rm"], rules[0].Verb);
        Assert.Equal("local_policy", rules[0].Reason);
        Assert.Equal(DenyCategory.CustomDeny, rules[0].Category);
    }

    [Fact]
    public void Load_parses_refined_verb_rule()
    {
        File.WriteAllText(_file, """
        [
          {
            "verb": ["rm"],
            "argFlags": ["-rf"],
            "firstPath": { "oneOf": ["/", "~", "~/"] },
            "reason": "destructive_root"
          }
        ]
        """);

        var rules = _loader.Load(_file);
        Assert.Single(rules);
        Assert.Equal(["rm"], rules[0].Verb);
        Assert.Equal(["-rf"], rules[0].ArgFlags);
        Assert.NotNull(rules[0].FirstPath);
        Assert.Equal(["/", "~", "~/"], rules[0].FirstPath!.OneOf);
    }

    [Fact]
    public void Load_parses_raw_text_rule()
    {
        File.WriteAllText(_file, """
        [
          { "rawText": ":(){:|:&};:", "reason": "fork_bomb", "escapeHatch": true }
        ]
        """);

        var rules = _loader.Load(_file);
        Assert.Single(rules);
        Assert.Equal(":(){:|:&};:", rules[0].RawText);
        Assert.True(rules[0].EscapeHatch);
    }

    [Fact]
    public void Load_parses_verb_prefix_rule()
    {
        File.WriteAllText(_file, """
        [
          { "verbPrefix": "mkfs", "reason": "filesystem_destruction" }
        ]
        """);

        var rules = _loader.Load(_file);
        Assert.Single(rules);
        Assert.Equal("mkfs", rules[0].VerbPrefix);
    }

    [Fact]
    public void Load_parses_multiple_rules()
    {
        File.WriteAllText(_file, """
        [
          { "verb": ["docker", "rm"], "reason": "policy_a" },
          { "verbPrefix": "mkfs", "reason": "policy_b" },
          { "rawText": "danger", "reason": "policy_c" }
        ]
        """);

        var rules = _loader.Load(_file);
        Assert.Equal(3, rules.Count);
    }

    // -------------------------------------------------------------------
    // Failure: malformed JSON, malformed rules
    // -------------------------------------------------------------------

    [Fact]
    public void Load_throws_on_malformed_json()
    {
        File.WriteAllText(_file, "{not json");

        var ex = Assert.Throws<InvalidDataException>(() => _loader.Load(_file));
        Assert.Contains("malformed JSON", ex.Message);
        Assert.Contains("refuses to start", ex.Message);
    }

    [Fact]
    public void Load_throws_on_rule_with_no_match_shape()
    {
        File.WriteAllText(_file, """
        [
          { "reason": "missing_shape" }
        ]
        """);

        var ex = Assert.Throws<InvalidDataException>(() => _loader.Load(_file));
        Assert.Contains("rule at index 0", ex.Message);
        Assert.Contains("no match shape", ex.Message);
    }

    [Fact]
    public void Load_throws_on_rule_with_multiple_match_shapes()
    {
        File.WriteAllText(_file, """
        [
          { "verb": ["git"], "reason": "ok" },
          { "verb": ["bad"], "rawText": "x", "reason": "dual_shapes" }
        ]
        """);

        var ex = Assert.Throws<InvalidDataException>(() => _loader.Load(_file));
        Assert.Contains("rule at index 1", ex.Message);
        Assert.Contains("multiple match shapes", ex.Message);
    }

    [Fact]
    public void Load_throws_on_rule_missing_reason()
    {
        File.WriteAllText(_file, """
        [
          { "verb": ["git", "push"] }
        ]
        """);

        // System.Text.Json throws on missing required property; loader wraps
        // it into InvalidDataException with index context.
        Assert.Throws<InvalidDataException>(() => _loader.Load(_file));
    }

    [Fact]
    public void Load_throws_on_refinement_without_verb()
    {
        File.WriteAllText(_file, """
        [
          {
            "verbPrefix": "mkfs",
            "argFlags": ["-f"],
            "reason": "wrong_combo"
          }
        ]
        """);

        var ex = Assert.Throws<InvalidDataException>(() => _loader.Load(_file));
        Assert.Contains("rule at index 0", ex.Message);
    }
}
