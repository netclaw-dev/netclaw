// -----------------------------------------------------------------------
// <copyright file="ToolConfigBindingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Pins the list rules of <see cref="ToolConfig.BindFromConfiguration"/>. The Microsoft
/// configuration binder adds configured items to a list that already has default items.
/// A configured Tools list must replace the default list, so an operator can narrow tool
/// grants, file roots, attachment categories, and the HTTP allow list.
/// </summary>
[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class ToolConfigBindingTests : IDisposable
{
    private const string TeamTools = "AudienceProfiles:Team:AllowedTools";
    private const string TeamCategories = "AudienceProfiles:Team:ChannelAttachments:AllowedCategories";
    private const string ReadRoots = "AudienceProfiles:GlobalReadRoots";

    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    public static TheoryData<string, string?, string?, string[]?, string?> ListRules => new()
    {
        // key, JSON value (null = key absent), environment value, expected list, expected error
        { TeamTools, """["file_read", "file_list"]""", null, ["file_read", "file_list"], null },
        { "AudienceProfiles:Public:AllowedTools", """["file_read"]""", null, ["file_read"], null },
        { "AudienceProfiles:Public:ReadFiles:Roots", """["/srv/a"]""", null, ["/srv/a"], null },
        { "AudienceProfiles:Public:WriteFiles:Roots", """["/srv/a"]""", null, ["/srv/a"], null },
        { "AudienceProfiles:Public:AttachFiles:Roots", """["/srv/a"]""", null, ["/srv/a"], null },
        { "AudienceProfiles:Team:ReadFiles:Roots", """["/srv/a"]""", null, ["/srv/a"], null },
        { "AudienceProfiles:Team:WriteFiles:Roots", """["/srv/a"]""", null, ["/srv/a"], null },
        { "AudienceProfiles:Team:AttachFiles:Roots", """["/srv/a"]""", null, ["/srv/a"], null },
        { "AudienceProfiles:Public:ChannelAttachments:AllowedCategories", """[]""", null, [], null },
        { TeamCategories, """["Pdf"]""", null, ["Pdf"], null },
        { "AudienceProfiles:Personal:ChannelAttachments:AllowedCategories", """["Pdf"]""", null, ["Pdf"], null },
        { ReadRoots, """["{skills_dir}"]""", null, ["{skills_dir}"], null },
        { "WebFetch:HttpAllowList", """["localhost"]""", null, ["localhost"], null },
        { TeamTools, null, null, [.. ToolAudienceProfileToolCatalog.TeamDefaultAllowedTools], null },
        { ReadRoots, null, null, ["{skills_dir}", "{identity_dir}", "{workspaces_dir}"], null },
        { TeamTools, "[]", null, [], null },
        { TeamTools, "null", null, [], null },
        { ReadRoots, "{}", null, [], null },
        { ReadRoots, null, "", [], null },
        { TeamTools, """["file_read"]""", "", null, "has list items and also a value" },
        { TeamTools, "\"file_read\"", null, null, "must be a list" },
        { TeamTools, """["file_read", { "name": "file_list" }]""", null, null, "has an item that is not a valid String" },
        { ReadRoots, """["{skills_dir}", ["/srv/a"]]""", null, null, "has an item that is not a valid String" },
        { TeamCategories, """["Image", "Bogus"]""", null, null, "is not a valid AttachmentCategory name" },
        { TeamCategories, """["Image", "Pdf, Document"]""", null, null, "is not a valid AttachmentCategory name" },
        { TeamCategories, """["Image", "3"]""", null, null, "is not a valid AttachmentCategory name" },
        { TeamCategories, """["image", "PDF"]""", null, ["Image", "Pdf"], null },
    };

    [Theory]
    [MemberData(nameof(ListRules))]
    public void Tools_list_rules(string key, string? jsonValue, string? environmentValue, string[]? expected, string? error)
    {
        var prefix = $"NETCLAW_TEST_{Guid.NewGuid():N}_";
        var variable = $"{prefix}Tools__{key.Replace(":", "__", StringComparison.Ordinal)}";
        if (environmentValue is not null)
            Environment.SetEnvironmentVariable(variable, environmentValue);
        try
        {
            if (error is not null)
            {
                var ex = Assert.Throws<InvalidOperationException>(() => Bind(key, jsonValue, prefix, out _));
                Assert.Contains($"Tools.{key.Replace(':', '.')}", ex.Message, StringComparison.Ordinal);
                Assert.Contains(error, ex.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("Bogus", ex.Message, StringComparison.Ordinal);
                return;
            }

            var toolConfig = Bind(key, jsonValue, prefix, out var warnings);

            Assert.Equal(expected, ReadList(toolConfig, key));
            if (jsonValue is "null" or "{}")
                Assert.Equal($"Tools.{key.Replace(':', '.')} is null or an empty object; treating it as an empty list.", Assert.Single(warnings));
            else
                Assert.Empty(warnings);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"\"")]
    public void AllowedMcpServers_with_no_value_binds_as_an_empty_list(string value)
    {
        var toolConfig = Bind("AudienceProfiles:Team:AllowedMcpServers", value, "NETCLAW_TEST_UNUSED_", out var warnings);

        Assert.Empty(toolConfig.AudienceProfiles.Team.AllowedMcpServers);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("ToolOverrides", "null")]
    [InlineData("ToolOverrides", "[]")]
    [InlineData("ToolOverrides", "\"\"")]
    [InlineData("McpServerDefaults", "null")]
    [InlineData("McpServerDefaults", "[]")]
    [InlineData("McpServerDefaults", "\"\"")]
    public void An_approval_map_with_no_value_keeps_the_posture_defaults(string map, string value)
    {
        var baseline = Bind("AudienceProfiles:Team:ApprovalPolicy:" + map, "{}", "NETCLAW_TEST_UNUSED_", out _);
        var toolConfig = Bind("AudienceProfiles:Team:ApprovalPolicy:" + map, value, "NETCLAW_TEST_UNUSED_", out var warnings);

        var expected = baseline.AudienceProfiles.Team.ApprovalPolicy!;
        var actual = toolConfig.AudienceProfiles.Team.ApprovalPolicy!;
        Assert.Equal(expected.ToolOverrides, actual.ToolOverrides);
        Assert.Equal(expected.McpServerDefaults, actual.McpServerDefaults);
        Assert.Empty(warnings);
    }

    public static TheoryData<TrustAudience, string[], bool> LegacyAllowLists()
    {
        string[] team0254 = [.. ToolAudienceProfileToolCatalog.LegacyTeamDefaultAllowedTools[1]];
        return new()
        {
            // Each older shipped default maps to today's default. Order does not matter.
            { TrustAudience.Public, [.. ToolAudienceProfileToolCatalog.LegacyPublicDefaultAllowedTools[0]], true },
            { TrustAudience.Public, [.. ToolAudienceProfileToolCatalog.LegacyPublicDefaultAllowedTools[1].Reverse()], true },
            { TrustAudience.Team, [.. ToolAudienceProfileToolCatalog.LegacyTeamDefaultAllowedTools[0]], true },
            { TrustAudience.Team, [.. team0254.Reverse()], true },
            // Any other list is operator intent and is applied as written, never widened.
            { TrustAudience.Team, [.. team0254.Where(tool => tool != ToolAudienceProfileToolCatalog.WebFetch)], false },
            { TrustAudience.Team, [.. team0254, ToolAudienceProfileToolCatalog.SetWebhook], false },
            { TrustAudience.Team, [.. team0254.Select(tool => tool == ToolAudienceProfileToolCatalog.FileRead ? "File_Read" : tool)], false },
            { TrustAudience.Team, [.. team0254, ToolAudienceProfileToolCatalog.FileRead], false },
        };
    }

    [Theory]
    [MemberData(nameof(LegacyAllowLists))]
    public void Older_default_allowlists_map_to_the_current_default(TrustAudience audience, string[] tools, bool maps)
    {
        var json = $"[{string.Join(", ", tools.Select(tool => $"\"{tool}\""))}]";

        var toolConfig = Bind($"AudienceProfiles:{audience}:AllowedTools", json, "NETCLAW_TEST_UNUSED_", out var warnings);

        var profile = ToolAudienceProfileDefaults.GetResolvedProfile(toolConfig.AudienceProfiles, audience);
        if (!maps)
        {
            Assert.Equal(tools, profile.AllowedTools);
            Assert.Empty(warnings);
            return;
        }

        var current = ToolAudienceProfileDefaults.CurrentDefaultAllowedTools(audience);
        Assert.Equal(current, profile.AllowedTools);
        var warning = Assert.Single(warnings);
        Assert.Contains($"Tools.AudienceProfiles.{audience}.AllowedTools is an older Netclaw default list", warning, StringComparison.Ordinal);
        Assert.Contains("netclaw doctor --fix", warning, StringComparison.Ordinal);
        foreach (var added in current.Except(tools))
            Assert.Contains(added, warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TrustAudience.Public, false)]
    [InlineData(TrustAudience.Public, true)]
    [InlineData(TrustAudience.Team, false)]
    [InlineData(TrustAudience.Team, true)]
    public void A_copy_of_the_current_default_binds_as_written_with_no_warning(TrustAudience audience, bool reversed)
    {
        var current = ToolAudienceProfileDefaults.CurrentDefaultAllowedTools(audience);
        string[] tools = reversed ? [.. current.Reverse()] : [.. current];
        var json = $"[{string.Join(", ", tools.Select(tool => $"\"{tool}\""))}]";

        var toolConfig = Bind($"AudienceProfiles:{audience}:AllowedTools", json, "NETCLAW_TEST_UNUSED_", out var warnings);

        Assert.Equal(tools, ToolAudienceProfileDefaults.GetResolvedProfile(toolConfig.AudienceProfiles, audience).AllowedTools);
        Assert.Empty(warnings);
        Assert.True(ToolAudienceProfileDefaults.IsLegacyDefaultAllowedTools(audience, tools));
    }

    // The legacy tables must record every default list that a release ships, because
    // `netclaw doctor --fix` deletes only an exact shipped list, and the daemon maps only an exact
    // older list. The newest row is a literal list, so a change to a default fails here until
    // someone adds a row for the new list. Do not build the newest row from the default.
    [Theory]
    [InlineData(TrustAudience.Public)]
    [InlineData(TrustAudience.Team)]
    public void The_newest_legacy_row_is_the_current_default(TrustAudience audience)
    {
        var table = audience == TrustAudience.Public
            ? ToolAudienceProfileToolCatalog.LegacyPublicDefaultAllowedTools
            : ToolAudienceProfileToolCatalog.LegacyTeamDefaultAllowedTools;

        Assert.Equal(
            ToolAudienceProfileDefaults.CurrentDefaultAllowedTools(audience).Order(StringComparer.Ordinal),
            table[^1].Order(StringComparer.Ordinal));
        Assert.Equal(table[^1].Count, table[^1].Distinct(StringComparer.Ordinal).Count());
    }

    // netclaw init writes only the posture. The daemon applies the posture defaults, so the
    // Personal posture rule applies when netclaw.json has no ApprovalPolicy for Personal.
    public static TheoryData<string, ToolApprovalMode?> PersonalShellApprovalCases() => new()
    {
        // Security section, Personal.ApprovalPolicy JSON (empty = absent), expected shell_execute mode
        { """{ "DeploymentPosture": "Personal", "StrictDefaults": true }""", ToolApprovalMode.Approval },
        { """{ "StrictDefaults": false }""", ToolApprovalMode.Approval },
        { """{ "DeploymentPosture": "Team", "StrictDefaults": true }""", null },
        { """{ "DeploymentPosture": "Public", "StrictDefaults": true }""", null },
        { """{ "StrictDefaults": true }""", null },
    };

    [Theory]
    [MemberData(nameof(PersonalShellApprovalCases))]
    public void The_posture_sets_the_personal_shell_approval_default(string security, ToolApprovalMode? expected)
    {
        var tools = BindPolicy($$"""{ "Security": {{security}}, "Tools": { "ShellMode": "HostAllowed" } }""").Tools;

        var personal = tools.AudienceProfiles.Personal;
        if (expected is null)
        {
            Assert.Null(personal.ApprovalPolicy);
            return;
        }

        Assert.True(personal.ApprovalPolicy!.TryGetExplicitMode(ToolAudienceProfileToolCatalog.ShellExecute, out var mode));
        Assert.Equal(expected, mode);
    }

    public static TheoryData<string, ToolApprovalMode> PersonalApprovalPolicyShapes() => new()
    {
        // `netclaw mcp` writes only the MCP keys. The posture rule stays.
        { """{ "McpServerDefaults": { "memorizer": "Auto" } }""", ToolApprovalMode.Approval },
        { "null", ToolApprovalMode.Approval },
        { "{}", ToolApprovalMode.Approval },
        // An explicit operator choice replaces the posture default.
        { """{ "ToolOverrides": { "shell_execute": "Auto" } }""", ToolApprovalMode.Auto },
        { """{ "ToolOverrides": { "shell_execute": "Deny" } }""", ToolApprovalMode.Deny },
    };

    [Theory]
    [MemberData(nameof(PersonalApprovalPolicyShapes))]
    public void A_configured_personal_approval_policy_binds_on_the_posture_default(string approvalPolicy, ToolApprovalMode expected)
    {
        var tools = BindPolicy(
            $$"""
            {
              "Security": { "DeploymentPosture": "Personal", "StrictDefaults": true },
              "Tools": { "AudienceProfiles": { "Personal": { "ApprovalPolicy": {{approvalPolicy}} } } }
            }
            """).Tools;

        Assert.True(tools.AudienceProfiles.Personal.ApprovalPolicy!.TryGetExplicitMode(ToolAudienceProfileToolCatalog.ShellExecute, out var mode));
        Assert.Equal(expected, mode);
    }

    // The binder reads a colon in a key as a section separator. "file_write:control-plane" becomes
    // a section "file_write" with a child and no value, which binds as the enum default (Auto).
    // Such a section must not become an override of the whole tool.
    [Theory]
    [InlineData(TrustAudience.Personal)]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Public)]
    public void A_colon_key_in_ToolOverrides_does_not_become_an_override_of_the_tool(TrustAudience audience)
    {
        var policy = BindApprovalPolicy(audience,
            """
            {
              "DefaultMode": "Approval",
              "ToolOverrides": {
                "file_write:control-plane": "Approval",
                "file_edit:control-plane": "Deny",
                "mcp:memorizer:store": "Approval"
              }
            }
            """);

        Assert.DoesNotContain(policy.ToolOverrides.Keys, static key => key is "file_write" or "file_edit" or "mcp");
        Assert.Equal(ToolApprovalMode.Approval, policy.GetEffectiveMode("file_write"));
        Assert.Equal(ToolApprovalMode.Approval, policy.GetEffectiveMode("file_edit"));
        Assert.Equal(ToolApprovalMode.Approval, policy.GetEffectiveMode("mcp"));
        Assert.Equal(ToolApprovalMode.Approval, policy.GetEffectiveMode("memorizer/store"));
    }

    [Fact]
    public void A_colon_key_in_McpServerDefaults_does_not_become_a_server_default()
    {
        var policy = BindApprovalPolicy(TrustAudience.Team,
            """{ "DefaultMode": "Approval", "McpServerDefaults": { "notion:pages": "Auto", "github": "Deny" } }""");

        Assert.Equal(["github"], policy.McpServerDefaults.Keys);
        Assert.Equal(ToolApprovalMode.Approval, policy.GetEffectiveMode("notion/create-pages"));
        Assert.Equal(ToolApprovalMode.Deny, policy.GetEffectiveMode("github/create-issue"));
    }

    [Fact]
    public void Ordinary_keys_bind_beside_a_colon_key()
    {
        var policy = BindApprovalPolicy(TrustAudience.Team,
            """
            {
              "DefaultMode": "Approval",
              "ToolOverrides": {
                "shell_execute": "Deny",
                "notion/create-pages": "Auto",
                "github__create_issue": "Auto",
                "file_write:control-plane": "Deny"
              }
            }
            """);

        Assert.Equal(ToolApprovalMode.Deny, policy.GetEffectiveMode("shell_execute"));
        Assert.Equal(ToolApprovalMode.Auto, policy.GetEffectiveMode("notion/create-pages"));
        Assert.Equal(ToolApprovalMode.Auto, policy.GetEffectiveMode("github/create_issue"));
        Assert.Equal(ToolApprovalMode.Approval, policy.GetEffectiveMode("file_write"));
        Assert.Equal(3, policy.ToolOverrides.Count);
    }

    [Fact]
    public void An_explicit_key_beside_a_colon_key_of_the_same_tool_still_wins()
    {
        var policy = BindApprovalPolicy(TrustAudience.Team,
            """
            {
              "DefaultMode": "Auto",
              "ToolOverrides": { "file_write": "Approval", "file_write:control-plane": "Deny" }
            }
            """);

        Assert.Equal(ToolApprovalMode.Approval, policy.ToolOverrides["file_write"]);
        Assert.Equal(ToolApprovalMode.Approval, policy.GetEffectiveMode("file_write"));
    }

    [Fact]
    public void A_colon_key_does_not_remove_a_posture_default_override()
    {
        var tools = BindPolicy(
            """
            {
              "Security": { "DeploymentPosture": "Personal", "StrictDefaults": true },
              "Tools": { "AudienceProfiles": { "Personal": { "ApprovalPolicy": { "ToolOverrides": { "shell_execute:x": "Auto" } } } } }
            }
            """).Tools;

        Assert.Equal(ToolApprovalMode.Approval, tools.AudienceProfiles.Personal.ApprovalPolicy!.GetEffectiveMode("shell_execute"));
    }

    [Fact]
    public void A_colon_key_in_a_JSON_source_yields_one_warning_naming_it()
    {
        var warnings = BindPolicy(
            """
            {
              "Security": { "DeploymentPosture": "Personal", "StrictDefaults": true },
              "Tools": { "AudienceProfiles": { "Team": { "ApprovalPolicy": { "ToolOverrides": { "shell_execute": "Deny", "file_write:control-plane": "Approval" } } } } }
            }
            """).ToolWarnings;

        Assert.Equal(
            ["Tools.AudienceProfiles.Team.ApprovalPolicy.ToolOverrides key 'file_write:control-plane' was ignored because a ':' in a key splits it into nested sections. No override was created, so the default approval mode applies. Write an MCP tool as server/tool."],
            warnings);
    }

    [Fact]
    public void A_colon_key_from_an_environment_style_source_yields_one_warning_naming_it()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:DeploymentPosture"] = "Personal",
                ["Tools:AudienceProfiles:Personal:ApprovalPolicy:McpServerDefaults:notion:pages"] = "Approval",
            })
            .Build();

        var warnings = PolicyConfiguration.Bind(configuration).ToolWarnings;

        Assert.Equal(
            ["Tools.AudienceProfiles.Personal.ApprovalPolicy.McpServerDefaults key 'notion:pages' was ignored because a ':' in a key splits it into nested sections. No override was created, so the default approval mode applies. Write an MCP tool as server/tool."],
            warnings);
    }

    [Fact]
    public void Ordinary_override_keys_yield_no_warning()
    {
        var warnings = BindPolicy(
            """
            {
              "Security": { "DeploymentPosture": "Personal", "StrictDefaults": true },
              "Tools": { "AudienceProfiles": { "Team": { "ApprovalPolicy": { "ToolOverrides": { "shell_execute": "Deny", "notion/create-pages": "Auto" }, "McpServerDefaults": { "github": "Deny" } } } } }
            }
            """).ToolWarnings;

        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData(TrustAudience.Personal, "File_Write", ToolApprovalMode.Deny)]
    [InlineData(TrustAudience.Team, "FILE_WRITE", ToolApprovalMode.Approval)]
    [InlineData(TrustAudience.Public, "File_write", ToolApprovalMode.Auto)]
    public void A_wrong_case_override_key_applies_to_the_tool(TrustAudience audience, string key, ToolApprovalMode written)
    {
        var defaultMode = written == ToolApprovalMode.Auto ? ToolApprovalMode.Deny : ToolApprovalMode.Auto;
        var policy = BindApprovalPolicy(audience,
            $$"""{ "DefaultMode": "{{defaultMode}}", "ToolOverrides": { "{{key}}": "{{written}}" } }""");

        Assert.Equal(written, policy.GetEffectiveMode("file_write"));
    }

    // A posture default and the operator's spelling of the same key differ only by case. The
    // operator's key replaces the default, as the same spelling would. If both stayed, the
    // restrictive default would win and "Shell_Execute": "Auto" could not remove the prompt.
    [Fact]
    public void A_wrong_case_override_replaces_the_posture_default_for_that_tool()
    {
        var policy = BindApprovalPolicy(TrustAudience.Personal, """{ "ToolOverrides": { "Shell_Execute": "Auto" } }""");

        Assert.Equal(ToolApprovalMode.Auto, policy.GetEffectiveMode("shell_execute"));
        Assert.DoesNotContain("shell_execute", policy.ToolOverrides.Keys);
    }

    // Configuration providers merge keys without regard to case, so a pair of keys cannot reach the
    // policy. A JSON file with such a pair stops binding, which is loud and fails closed. The daemon
    // and every CLI command, `netclaw doctor` included, stop with the key named.
    [Fact]
    public void Two_override_keys_that_differ_only_by_case_stop_binding()
    {
        var exception = Assert.ThrowsAny<Exception>(() => BindApprovalPolicy(
            TrustAudience.Team,
            """{ "ToolOverrides": { "file_write": "Auto", "File_Write": "Deny" } }"""));

        Assert.Contains("duplicate", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_override_key_that_cannot_be_a_tool_name_is_a_warning_that_names_the_key()
    {
        var warnings = BindPolicy(
            """
            {
              "Security": { "DeploymentPosture": "Personal", "StrictDefaults": true },
              "Tools": { "AudienceProfiles": { "Team": { "ApprovalPolicy": { "ToolOverrides": { "file write": "Deny", "File_Write": "Deny", "some_future_tool": "Deny" } } } } }
            }
            """).ToolWarnings;

        var warning = Assert.Single(warnings);
        Assert.Contains("Tools.AudienceProfiles.Team.ApprovalPolicy.ToolOverrides key 'file write'", warning);
        Assert.Contains("cannot be a tool name", warning);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void An_override_key_with_no_value_is_ignored_and_the_warning_says_so(string value)
    {
        var warnings = BindPolicy(
            $$"""
            {
              "Security": { "DeploymentPosture": "Personal", "StrictDefaults": true },
              "Tools": { "AudienceProfiles": { "Team": { "ApprovalPolicy": { "ToolOverrides": { "file_write": {{value}} } } } } }
            }
            """).ToolWarnings;

        var warning = Assert.Single(warnings);
        Assert.Equal(
            "Tools.AudienceProfiles.Team.ApprovalPolicy.ToolOverrides key 'file_write' has no value and was ignored. No override was created, so the default approval mode applies.",
            warning);
    }

    [Fact]
    public void A_key_whose_spelling_differs_from_the_tool_gets_one_notice_and_still_applies()
    {
        var policy = BindPolicy(
            """
            {
              "Security": { "DeploymentPosture": "Personal", "StrictDefaults": true },
              "McpServers": { "notion": { "Transport": "http", "Url": "https://example.test/mcp" } },
              "Tools": { "AudienceProfiles": { "Personal": { "ApprovalPolicy": {
                "ToolOverrides": { "Shell_Execute": "Auto", "Notion/Search": "Deny", "file_read": "Auto" },
                "McpServerDefaults": { "Notion": "Approval" } } } } }
            }
            """);

        Assert.Equal(
            [
                "Tools.AudienceProfiles.Personal.ApprovalPolicy.ToolOverrides key 'Notion/Search' now applies to the tools of MCP server 'notion' (keys are matched without regard to case).",
                "Tools.AudienceProfiles.Personal.ApprovalPolicy.ToolOverrides key 'Shell_Execute' now applies to tool 'shell_execute' (keys are matched without regard to case).",
                "Tools.AudienceProfiles.Personal.ApprovalPolicy.McpServerDefaults key 'Notion' now applies to MCP server 'notion' (keys are matched without regard to case)."
            ],
            policy.ToolNotices);
        Assert.Equal(ToolApprovalMode.Auto, policy.Tools.AudienceProfiles.Personal.ApprovalPolicy!.GetEffectiveMode("shell_execute"));
    }

    [Fact]
    public void Keys_spelled_like_the_tool_get_no_notice()
    {
        var notices = BindPolicy(
            """
            {
              "Security": { "DeploymentPosture": "Personal", "StrictDefaults": true },
              "McpServers": { "GitHub": { "Transport": "http", "Url": "https://example.test/mcp" } },
              "Tools": { "AudienceProfiles": { "Team": { "ApprovalPolicy": { "ToolOverrides": { "shell_execute": "Deny", "GitHub/Create_Issue": "Deny" }, "McpServerDefaults": { "GitHub": "Deny" } } } } }
            }
            """).ToolNotices;

        Assert.Empty(notices);
    }

    private ToolApprovalConfig BindApprovalPolicy(TrustAudience audience, string approvalPolicy)
    {
        var tools = BindPolicy(
            $$"""
            {
              "Security": { "DeploymentPosture": "Personal", "StrictDefaults": true },
              "Tools": { "AudienceProfiles": { "{{audience}}": { "ApprovalPolicy": {{approvalPolicy}} } } }
            }
            """).Tools;
        return ToolAudienceProfileDefaults.GetResolvedProfile(tools.AudienceProfiles, audience).ApprovalPolicy!;
    }

    [Fact]
    public void An_undefined_posture_stops_binding()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => ToolConfig.BindFromConfiguration(configuration.GetSection("Tools"), (DeploymentPosture)42, out _));
    }

    [Fact]
    public void Only_the_reviewed_allow_lists_have_default_items()
    {
        // A list with default items needs a row in ToolConfig.DefaultedLists and a review: null
        // and {} give an empty list, which is safe only for an allow list. The other types that
        // the daemon binds use the plain binder, which is safe only with empty defaults.
        var withDefaults = new List<string>();
        CollectListsWithDefaultItems(new ToolConfig(), "Tools", withDefaults);
        Assert.Equal(
            ToolConfig.DefaultedLists.Select(list => "Tools." + list.Key.Replace(':', '.')).Order(StringComparer.Ordinal),
            withDefaults.Order(StringComparer.Ordinal));

        Type[] plainBinderTypes =
        [
            typeof(SecurityPolicyConfig), typeof(WebhooksConfig), typeof(SearchConfig), typeof(SubAgentConfig),
            typeof(MemoryConfig), typeof(SkillSyncConfig), typeof(SchedulingConfig), typeof(ExternalSkillsConfig),
            typeof(SkillFeedsConfig), typeof(NotificationsConfig), typeof(McpServerEntry)
        ];
        withDefaults.Clear();
        foreach (var type in plainBinderTypes)
            CollectListsWithDefaultItems(Activator.CreateInstance(type)!, type.Name, withDefaults);
        Assert.Empty(withDefaults);
    }

    private PolicyConfiguration BindPolicy(string json)
    {
        var configPath = Path.Combine(_dir.Path, "netclaw.json");
        File.WriteAllText(configPath, json);
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: false, reloadOnChange: false)
            .Build();
        return PolicyConfiguration.Bind(configuration);
    }

    private ToolConfig Bind(string key, string? jsonValue, string environmentPrefix, out IReadOnlyList<string> warnings)
    {
        // Wrap the value in the nested objects that the key names.
        var segments = key.Split(':');
        var json = jsonValue is null
            ? "{}"
            : string.Concat(segments.Select(segment => $"{{ \"{segment}\": ")) + jsonValue + new string('}', segments.Length);
        var configPath = Path.Combine(_dir.Path, "netclaw.json");
        File.WriteAllText(configPath, $$"""{ "Tools": {{json}} }""");

        // Same source order as the daemon (DaemonConfigurationSourcesTests pins the real order).
        // A unique prefix keeps process environment variables away from parallel tests.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: true, reloadOnChange: false)
            .AddJsonFile(Path.Combine(_dir.Path, "secrets.json"), optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(environmentPrefix)
            .Build();

        var bound = PolicyConfiguration.Bind(configuration);
        warnings = bound.ToolWarnings;
        return bound.Tools;
    }

    private static string[] ReadList(ToolConfig config, string key)
    {
        var profiles = config.AudienceProfiles;
        IEnumerable list = key switch
        {
            "AudienceProfiles:Public:AllowedTools" => profiles.Public.AllowedTools,
            "AudienceProfiles:Public:ReadFiles:Roots" => profiles.Public.ReadFiles.Roots,
            "AudienceProfiles:Public:WriteFiles:Roots" => profiles.Public.WriteFiles.Roots,
            "AudienceProfiles:Public:AttachFiles:Roots" => profiles.Public.AttachFiles.Roots,
            "AudienceProfiles:Public:ChannelAttachments:AllowedCategories" => profiles.Public.ChannelAttachments.AllowedCategories,
            TeamTools => profiles.Team.AllowedTools,
            "AudienceProfiles:Team:ReadFiles:Roots" => profiles.Team.ReadFiles.Roots,
            "AudienceProfiles:Team:WriteFiles:Roots" => profiles.Team.WriteFiles.Roots,
            "AudienceProfiles:Team:AttachFiles:Roots" => profiles.Team.AttachFiles.Roots,
            TeamCategories => profiles.Team.ChannelAttachments.AllowedCategories,
            "AudienceProfiles:Personal:ChannelAttachments:AllowedCategories" => profiles.Personal.ChannelAttachments.AllowedCategories,
            ReadRoots => profiles.GlobalReadRoots,
            "WebFetch:HttpAllowList" => config.WebFetch.HttpAllowList,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown list key.")
        };
        return [.. list.Cast<object>().Select(item => item.ToString()!)];
    }

    // Walks by type, not only by default value. A config type behind a null default (for
    // example ApprovalPolicy) or inside a list or dictionary gets a new instance, because the
    // binder creates one when the key is configured. Its default list items then count too.
    private static void CollectListsWithDefaultItems(object target, string path, List<string> found)
        => CollectListsWithDefaultItems(target, path, found, [target.GetType()]);

    private static void CollectListsWithDefaultItems(object target, string path, List<string> found, HashSet<Type> visiting)
    {
        foreach (var property in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;

            var propertyPath = $"{path}.{property.Name}";
            var type = property.PropertyType;
            var value = property.GetValue(target);
            if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
            {
                if (value is IEnumerable items && items.Cast<object>().Any())
                    found.Add(propertyPath);

                // A list item or a dictionary value of a config type is also bound from configuration.
                var elementType = type.IsArray ? type.GetElementType() : type.GetGenericArguments().LastOrDefault();
                if (elementType is not null)
                    WalkConfigType(elementType, $"{propertyPath}[]", found, visiting);
            }
            else if (value is not null && IsConfigType(type) && visiting.Add(type))
            {
                CollectListsWithDefaultItems(value, propertyPath, found, visiting);
                visiting.Remove(type);
            }
            else if (value is null)
            {
                WalkConfigType(Nullable.GetUnderlyingType(type) ?? type, propertyPath, found, visiting);
            }
        }
    }

    private static void WalkConfigType(Type type, string path, List<string> found, HashSet<Type> visiting)
    {
        if (!IsConfigType(type) || type.GetConstructor(Type.EmptyTypes) is null || !visiting.Add(type))
            return;

        CollectListsWithDefaultItems(Activator.CreateInstance(type)!, path, found, visiting);
        visiting.Remove(type);
    }

    private static bool IsConfigType(Type type)
        => type.IsClass && type != typeof(string)
            && type.Namespace?.StartsWith("Netclaw", StringComparison.Ordinal) == true
            && !typeof(IEnumerable).IsAssignableFrom(type);
}
