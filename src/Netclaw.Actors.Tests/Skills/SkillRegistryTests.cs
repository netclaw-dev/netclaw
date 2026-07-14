// -----------------------------------------------------------------------
// <copyright file="SkillRegistryTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Skills;

public class SkillRegistryTests
{
    private static SkillEntry MakeEntry(
        string name,
        string description = "desc",
        string? category = null,
        bool disableModelInvocation = false,
        string? allowedTools = null,
        string? subagent = null,
        bool hasSubagentMetadata = false,
        string? subagentError = null) =>
        new(name, name, description, $"/skills/{name}/SKILL.md", $"/skills/{name}", category)
        {
            DisableModelInvocation = disableModelInvocation,
            AllowedTools = allowedTools,
            Subagent = subagent,
            HasSubagentRoutingMetadata = hasSubagentMetadata,
            SubagentMetadataError = subagentError
        };

    [Fact]
    public void Search_matches_by_name()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("git-workflow"));
        registry.Register(MakeEntry("docker-deploy"));

        var results = registry.Search("git");

        Assert.Single(results);
        Assert.Equal("git-workflow", results[0].Name);
    }

    [Fact]
    public void Search_matches_by_description()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("deploy", "How to deploy containers"));

        var results = registry.Search("containers");

        Assert.Single(results);
    }

    [Fact]
    public void Search_is_case_insensitive()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("Git-Workflow"));

        var results = registry.Search("GIT");

        Assert.Single(results);
    }

    [Fact]
    public void Search_returns_empty_for_no_match()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("git-workflow"));

        var results = registry.Search("kubernetes");

        Assert.Empty(results);
    }

    [Fact]
    public void Search_respects_max_results()
    {
        var registry = new SkillRegistry();
        for (var i = 0; i < 10; i++)
            registry.Register(MakeEntry($"skill-{i}", "common keyword"));

        var results = registry.Search("common", maxResults: 3);

        Assert.Equal(3, results.Count);
    }

    // --- Index format tests ---

    [Fact]
    public void GenerateIndex_returns_empty_when_no_skills()
    {
        var registry = new SkillRegistry();
        Assert.Equal(string.Empty, registry.GenerateIndex());
    }

    [Fact]
    public void GenerateIndex_includes_skill_with_description()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "Short description"));

        var index = registry.GenerateIndex();

        Assert.Contains("my-skill: Short description", index);
    }

    [Fact]
    public void GenerateIndex_includes_logical_catalog_header()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "desc"));

        var index = registry.GenerateIndex();

        Assert.Contains("[skills]|invoke via /name", index);
    }

    [Fact]
    public void GenerateIndex_groups_by_category()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("netclaw-memory", "Memory guidance", ".system"));
        registry.Register(MakeEntry("my-workflow", "Workflow help"));

        var index = registry.GenerateIndex();

        Assert.Contains("|.system:", index);
        Assert.Contains("netclaw-memory: Memory guidance", index);
        Assert.Contains("|user:", index);
        Assert.Contains("my-workflow: Workflow help", index);
    }

    [Fact]
    public void DisableModelInvocation_skill_excluded_from_index()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("ops", "Operations routing", ".system",
            disableModelInvocation: true));
        registry.Register(MakeEntry("memory", "Memory guidance", ".system"));

        var index = registry.GenerateIndex();

        Assert.DoesNotContain("ops:", index);
        Assert.Contains("memory: Memory guidance", index);
    }

    [Fact]
    public void Clear_resets_index()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "A user skill"));

        Assert.NotEqual(string.Empty, registry.GenerateIndex());

        registry.Clear();

        Assert.Equal(string.Empty, registry.GenerateIndex());
    }

    // --- Slash-command dispatch tests ---

    [Fact]
    public void TryResolveSlashCommand_resolves_known_command()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("netclaw-operations", "Operations routing"));

        var resolved = registry.TryResolveSlashCommand("/netclaw-operations check health",
            out var skill, out var remainder);

        Assert.True(resolved);
        Assert.NotNull(skill);
        Assert.Equal("netclaw-operations", skill!.Name);
        Assert.Equal("check health", remainder);
    }

    [Fact]
    public void TryResolveSlashCommand_returns_false_for_unknown_command()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("netclaw-operations", "Operations routing"));

        var resolved = registry.TryResolveSlashCommand("/nonexistent",
            out var skill, out var remainder);

        Assert.False(resolved);
        Assert.Null(skill);
    }

    [Fact]
    public void TryResolveSlashCommand_returns_false_for_non_slash_input()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "desc"));

        var resolved = registry.TryResolveSlashCommand("just a regular message",
            out _, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void TryResolveSlashCommand_handles_command_with_no_arguments()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("diag", "Diagnostics"));

        var resolved = registry.TryResolveSlashCommand("/diag", out var skill, out var remainder);

        Assert.True(resolved);
        Assert.Equal("diag", skill!.Name);
        Assert.Equal(string.Empty, remainder);
    }

    [Fact]
    public void Non_user_invocable_skill_excluded_from_slash_commands()
    {
        var registry = new SkillRegistry();
        var entry = new SkillEntry("bg-skill", "bg-skill", "Background guidance",
            "/skills/bg-skill/SKILL.md", "/skills/bg-skill", null)
        {
            UserInvocable = false
        };
        registry.Register(entry);

        var resolved = registry.TryResolveSlashCommand("/bg-skill", out _, out _);

        Assert.False(resolved);
    }

    [Fact]
    public void GetAvailableSlashCommands_lists_user_invocable_skills()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("ops", "Operations"));
        registry.Register(new SkillEntry("hidden", "hidden", "Hidden",
            "/skills/hidden/SKILL.md", "/skills/hidden", null) { UserInvocable = false });

        var commands = registry.GetAvailableSlashCommands();

        Assert.Single(commands);
        Assert.Equal("/ops", commands[0].Command);
    }

    [Fact]
    public void Clear_resets_slash_commands()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("ops", "Operations"));

        Assert.True(registry.TryResolveSlashCommand("/ops", out _, out _));

        registry.Clear();

        Assert.False(registry.TryResolveSlashCommand("/ops", out _, out _));
    }

    [Fact]
    public void ActivationRouter_returns_routed_when_valid_metadata_subagent_present()
    {
        var skill = MakeEntry("ops", subagent: "operations-helper", hasSubagentMetadata: true);

        var decision = SkillActivationRouter.Resolve(skill);

        Assert.False(decision.IsError);
        Assert.Equal(SkillActivationPath.Routed, decision.Path);
        Assert.Equal("operations-helper", decision.RoutedSubagent);
    }

    [Fact]
    public void ActivationRouter_returns_inline_when_metadata_subagent_absent()
    {
        var skill = MakeEntry("ops");

        var decision = SkillActivationRouter.Resolve(skill);

        Assert.False(decision.IsError);
        Assert.Equal(SkillActivationPath.Inline, decision.Path);
    }

    [Fact]
    public void ActivationRouter_returns_deterministic_error_when_metadata_subagent_invalid()
    {
        var skill = MakeEntry("ops", hasSubagentMetadata: true, subagentError: "value must not be empty.");

        var decision = SkillActivationRouter.Resolve(skill);

        Assert.True(decision.IsError);
        Assert.Contains("invalid metadata.subagent", decision.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/ops", decision.ErrorMessage!, StringComparison.Ordinal);
    }

    // --- Logical index contract ---

    [Fact]
    public void GenerateIndex_uses_logical_skill_tools_without_physical_roots()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "desc"));

        var index = registry.GenerateIndex();

        Assert.Contains("skill_load(name)", index);
        Assert.Contains("skill_read_resource(skillName, resourcePath)", index);
        Assert.DoesNotContain("file_read", index);
        Assert.DoesNotContain("/home/", index);
    }

    [Fact]
    public void GenerateIndex_explains_routed_skill_task_requirement()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "desc"));

        var index = registry.GenerateIndex();

        Assert.Contains("routed to a subagent require a concrete task", index);
    }

    [Fact]
    public void GenerateIndex_does_not_expose_skill_file_path()
    {
        var registry = new SkillRegistry();
        registry.Register(MakeEntry("my-skill", "desc"));

        var index = registry.GenerateIndex();

        Assert.DoesNotContain("SKILL.md", index);
        Assert.DoesNotContain("root", index, StringComparison.OrdinalIgnoreCase);
    }
}
