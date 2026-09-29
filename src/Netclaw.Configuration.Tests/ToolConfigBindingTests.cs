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

        return ToolConfig.BindFromConfiguration(configuration.GetSection("Tools"), out warnings);
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
