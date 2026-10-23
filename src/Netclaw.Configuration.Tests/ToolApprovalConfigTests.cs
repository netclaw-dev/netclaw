// -----------------------------------------------------------------------
// <copyright file="ToolApprovalConfigTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Configuration.Tests;

public sealed class ToolApprovalConfigTests
{
    [Fact]
    public void GetEffectiveMode_returns_default_mode_when_no_override()
    {
        var config = new ToolApprovalConfig { DefaultMode = ToolApprovalMode.Auto };

        Assert.Equal(ToolApprovalMode.Auto, config.GetEffectiveMode("shell_execute"));
        Assert.Equal(ToolApprovalMode.Auto, config.GetEffectiveMode("notion/create-pages"));
    }

    [Fact]
    public void GetEffectiveMode_exact_override_beats_server_default_and_default_mode()
    {
        var config = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Auto,
            McpServerDefaults = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["notion"] = ToolApprovalMode.Deny
            },
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["notion/search"] = ToolApprovalMode.Approval
            }
        };

        Assert.Equal(ToolApprovalMode.Approval, config.GetEffectiveMode("notion/search"));
    }

    [Fact]
    public void GetEffectiveMode_server_default_beats_default_mode_for_mcp_tools()
    {
        var config = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Auto,
            McpServerDefaults = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["notion"] = ToolApprovalMode.Approval
            }
        };

        Assert.Equal(ToolApprovalMode.Approval, config.GetEffectiveMode("notion/create-pages"));
        Assert.Equal(ToolApprovalMode.Approval, config.GetEffectiveMode("notion/archive"));
    }

    [Fact]
    public void GetEffectiveMode_server_default_does_not_leak_to_non_mcp_tools()
    {
        var config = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Auto,
            McpServerDefaults = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["shell_execute"] = ToolApprovalMode.Deny
            }
        };

        // No slash in name → server-default step is skipped.
        Assert.Equal(ToolApprovalMode.Auto, config.GetEffectiveMode("shell_execute"));
    }

    [Fact]
    public void GetEffectiveMode_server_default_does_not_leak_across_servers()
    {
        var config = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Auto,
            McpServerDefaults = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["notion"] = ToolApprovalMode.Deny
            }
        };

        Assert.Equal(ToolApprovalMode.Auto, config.GetEffectiveMode("memorizer/search_memories"));
    }

    [Fact]
    public void TryGetExplicitMode_reports_hit_when_override_present()
    {
        var config = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Approval,
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["file_write"] = ToolApprovalMode.Auto
            }
        };

        Assert.True(config.TryGetExplicitMode("file_write", out var mode));
        Assert.Equal(ToolApprovalMode.Auto, mode);
    }

    [Fact]
    public void TryGetExplicitMode_reports_miss_when_no_override_or_server_default()
    {
        var config = new ToolApprovalConfig { DefaultMode = ToolApprovalMode.Approval };

        Assert.False(config.TryGetExplicitMode("shell_execute", out _));
        Assert.False(config.TryGetExplicitMode("notion/create-pages", out _));
    }

    [Fact]
    public void TryGetExplicitMode_uses_server_default_for_slash_delimited_names()
    {
        var config = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Auto,
            McpServerDefaults = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["notion"] = ToolApprovalMode.Approval
            }
        };

        Assert.True(config.TryGetExplicitMode("notion/create-pages", out var mode));
        Assert.Equal(ToolApprovalMode.Approval, mode);
    }

    [Fact]
    public void TryGetExplicitMode_finds_override_written_with_LlmFacing_key()
    {
        // Operator who wrote `notion__create-pages` (the form they saw in
        // audit logs / LLM transcripts before the PR-3 audience split) gets
        // their override honored when the runtime queries with the canonical
        // form. Avoids a silent security misconfiguration.
        var config = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Auto,
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["notion__create-pages"] = ToolApprovalMode.Approval
            }
        };

        Assert.True(config.TryGetExplicitMode("notion/create-pages", out var mode));
        Assert.Equal(ToolApprovalMode.Approval, mode);
    }

    [Fact]
    public void TryGetExplicitMode_canonical_override_still_wins_when_both_forms_present()
    {
        // Canonical is the documented form — when both shapes exist the
        // exact match must take precedence, so an operator can use the
        // alias as a 'shadow' entry without it shadowing a canonical one.
        var config = new ToolApprovalConfig
        {
            DefaultMode = ToolApprovalMode.Auto,
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["notion/create-pages"] = ToolApprovalMode.Deny,
                ["notion__create-pages"] = ToolApprovalMode.Approval
            }
        };

        Assert.True(config.TryGetExplicitMode("notion/create-pages", out var mode));
        Assert.Equal(ToolApprovalMode.Deny, mode);
    }

    // Override keys match tool names without regard to case. With "File_Write": "Deny" the
    // tool fell back to the audience default, and a file_write ran.
    [Theory]
    [InlineData("File_Write", ToolApprovalMode.Deny)]
    [InlineData("FILE_WRITE", ToolApprovalMode.Approval)]
    [InlineData("file_write", ToolApprovalMode.Deny)]
    [InlineData("File_write", ToolApprovalMode.Auto)]
    public void An_override_key_matches_the_tool_name_without_regard_to_case(string key, ToolApprovalMode written)
    {
        // The default is the opposite of every written mode, so the written mode must be what is returned.
        var defaultMode = written == ToolApprovalMode.Auto ? ToolApprovalMode.Deny : ToolApprovalMode.Auto;
        var config = new ToolApprovalConfig
        {
            DefaultMode = defaultMode,
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal) { [key] = written }
        };

        Assert.True(config.TryGetExplicitMode("file_write", out var mode));
        Assert.Equal(written, mode);
        Assert.Equal(written, config.GetEffectiveMode("file_write"));
    }

    [Fact]
    public void An_override_for_one_tool_does_not_apply_to_another_tool_of_similar_name()
    {
        var config = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal) { ["File_Write"] = ToolApprovalMode.Deny }
        };

        Assert.Equal(ToolApprovalMode.Auto, config.GetEffectiveMode("file_edit"));
        Assert.Equal(ToolApprovalMode.Auto, config.GetEffectiveMode("file_write_2"));
    }

    [Theory]
    [InlineData(ToolApprovalMode.Auto, ToolApprovalMode.Approval, ToolApprovalMode.Approval)]
    [InlineData(ToolApprovalMode.Auto, ToolApprovalMode.Deny, ToolApprovalMode.Deny)]
    [InlineData(ToolApprovalMode.Approval, ToolApprovalMode.Deny, ToolApprovalMode.Deny)]
    public void Two_keys_that_differ_only_by_case_resolve_to_the_more_restrictive_mode_in_either_order(
        ToolApprovalMode first, ToolApprovalMode second, ToolApprovalMode expected)
    {
        foreach (var (lower, upper) in new[] { (first, second), (second, first) })
        {
            var config = new ToolApprovalConfig
            {
                DefaultMode = ToolApprovalMode.Auto,
                ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
                {
                    ["file_write"] = lower,
                    ["File_Write"] = upper
                }
            };

            Assert.Equal(expected, config.GetEffectiveMode("file_write"));
        }
    }

    [Fact]
    public void An_MCP_tool_override_matches_in_mixed_case_in_both_key_forms()
    {
        var canonical = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal) { ["Notion/Create-Pages"] = ToolApprovalMode.Deny }
        };
        var alias = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal) { ["NOTION__create-pages"] = ToolApprovalMode.Deny }
        };

        Assert.Equal(ToolApprovalMode.Deny, canonical.GetEffectiveMode("notion/create-pages"));
        Assert.Equal(ToolApprovalMode.Deny, alias.GetEffectiveMode("notion/create-pages"));
    }

    [Fact]
    public void The_canonical_MCP_key_still_wins_over_the_alias_when_their_case_differs()
    {
        var config = new ToolApprovalConfig
        {
            ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
            {
                ["Notion/Create-Pages"] = ToolApprovalMode.Approval,
                ["notion__create-pages"] = ToolApprovalMode.Deny
            }
        };

        Assert.Equal(ToolApprovalMode.Approval, config.GetEffectiveMode("notion/create-pages"));
    }

    [Fact]
    public void An_MCP_server_default_matches_the_server_name_without_regard_to_case()
    {
        var config = new ToolApprovalConfig
        {
            McpServerDefaults = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal) { ["Notion"] = ToolApprovalMode.Deny }
        };

        Assert.Equal(ToolApprovalMode.Deny, config.GetEffectiveMode("notion/search"));
    }

    [Theory]
    [InlineData("file_write", true)]
    [InlineData("File_Write", true)]
    [InlineData("notion/create-pages", true)]
    [InlineData("notion__create-pages", true)]
    [InlineData("some_future_tool", true)]
    [InlineData("", false)]
    [InlineData("file write", false)]
    [InlineData("file_write.", false)]
    [InlineData("file_write:control-plane", false)]
    [InlineData("tool*", false)]
    public void CanBeToolName_accepts_only_names_a_tool_can_have(string key, bool expected)
        => Assert.Equal(expected, ApprovalOverrideKeys.CanBeToolName(key));

    [Fact]
    public void Remove_and_Set_replace_every_spelling_of_a_key()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { ["Notion/Search"] = "Deny", ["other"] = "Auto" };

        ApprovalOverrideKeys.Set(map, "notion/search", "Approval");

        Assert.Equal(new Dictionary<string, string> { ["notion/search"] = "Approval", ["other"] = "Auto" }, map);
        Assert.True(ApprovalOverrideKeys.Remove(map, "NOTION/SEARCH"));
        Assert.Equal(["other"], map.Keys);
    }
}
