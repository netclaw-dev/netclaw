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
}
