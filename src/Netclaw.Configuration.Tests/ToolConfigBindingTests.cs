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
