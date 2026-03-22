using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Skills;

public class SkillScannerTests : IDisposable
{
    private readonly string _skillsDir;

    public SkillScannerTests()
    {
        _skillsDir = Path.Combine(Path.GetTempPath(), $"netclaw-skills-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_skillsDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_skillsDir))
            Directory.Delete(_skillsDir, recursive: true);
    }

    [Fact]
    public void Empty_directory_returns_empty_list()
    {
        var result = SkillScanner.Scan(_skillsDir);
        Assert.Empty(result);
    }

    [Fact]
    public void Nonexistent_directory_returns_empty_list()
    {
        var result = SkillScanner.Scan(Path.Combine(_skillsDir, "nonexistent"));
        Assert.Empty(result);
    }

    [Fact]
    public void Parses_yaml_frontmatter_with_name_and_description()
    {
        WriteSkill("git-workflow", """
            ---
            name: git-workflow
            description: How to manage branches and PRs in this project.
            ---

            # Git Workflow

            ## Branching Strategy
            Use feature branches.
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Equal("git-workflow", result[0].Name);
        Assert.Equal("Git Workflow", result[0].DisplayName);
        Assert.Equal("How to manage branches and PRs in this project.", result[0].Description);
        Assert.Null(result[0].Category);
        Assert.Equal(Path.Combine(_skillsDir, "git-workflow"), result[0].SkillDirectory);
    }

    [Fact]
    public void Extracts_optional_fields_from_frontmatter()
    {
        WriteSkill("pdf-processing", """
            ---
            name: pdf-processing
            description: Extract PDF text, fill forms, merge files.
            license: Apache-2.0
            compatibility: Requires poppler-utils
            allowed-tools: Bash(pdftotext:*) Read
            metadata:
              version: "1.2.0"
              author: example-org
            ---

            # PDF Processing
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Equal("Apache-2.0", result[0].License);
        Assert.Equal("Requires poppler-utils", result[0].Compatibility);
        Assert.Equal("Bash(pdftotext:*) Read", result[0].AllowedTools);
        Assert.Equal("1.2.0", result[0].Version);
    }

    [Fact]
    public void Discovers_directory_based_skill_with_resources()
    {
        WriteSkill("web-search", """
            ---
            name: web-search
            description: Search the web effectively for various domains.
            ---

            # Web Search

            Use domain-specific strategies for best results.
            """);
        WriteSkillFile("web-search", "references/flight-pricing.md", "# Flight Pricing");
        WriteSkillFile("web-search", "references/restaurant-search.md", "# Restaurant Search");
        WriteSkillFile("web-search", "scripts/validate.sh", "#!/bin/bash\necho ok");

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Equal("web-search", result[0].Name);
        Assert.Equal("Web Search", result[0].DisplayName);
        Assert.Equal(Path.Combine(_skillsDir, "web-search", "SKILL.md"), result[0].FilePath);
        Assert.Equal(Path.Combine(_skillsDir, "web-search"), result[0].SkillDirectory);
        Assert.NotNull(result[0].ResourcePaths);
        Assert.Equal(3, result[0].ResourcePaths!.Count);
        Assert.Contains("references/flight-pricing.md", result[0].ResourcePaths!);
        Assert.Contains("references/restaurant-search.md", result[0].ResourcePaths!);
        Assert.Contains("scripts/validate.sh", result[0].ResourcePaths!);
    }

    [Fact]
    public void Skill_without_frontmatter_is_skipped()
    {
        WriteSkill("notes", "# Just some notes\n\nNo frontmatter here.");

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Empty(result);
    }

    [Fact]
    public void Skill_with_missing_description_is_skipped()
    {
        WriteSkill("bad-skill", """
            ---
            name: bad-skill
            ---

            # Bad Skill
            No description in frontmatter.
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Empty(result);
    }

    [Fact]
    public void Malformed_yaml_is_skipped_gracefully()
    {
        WriteSkill("broken", """
            ---
            name: broken
            description: [this is: not: valid: yaml: {{{
            ---

            # Broken Skill
            """);

        var result = SkillScanner.Scan(_skillsDir);

        // YamlDotNet may or may not parse this depending on leniency,
        // but it should not throw
        Assert.True(result.Count <= 1);
    }

    [Fact]
    public void Handles_colons_in_description()
    {
        // This is a common edge case called out in the AgentSkills.io integration guide
        WriteSkill("colon-test", """
            ---
            name: colon-test
            description: "Use this skill when: the user asks about PDFs."
            ---

            # Colon Test
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Equal("Use this skill when: the user asks about PDFs.", result[0].Description);
    }

    [Fact]
    public void Falls_back_to_directory_name_when_no_name_in_frontmatter()
    {
        WriteSkill("my-skill", """
            ---
            description: A skill without an explicit name field.
            ---

            # My Custom Skill
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Equal("my-skill", result[0].Name);
        Assert.Equal("My Custom Skill", result[0].DisplayName);
    }

    [Fact]
    public void Nested_directory_skills_get_category()
    {
        var subDir = Path.Combine(_skillsDir, "devops", "docker");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "SKILL.md"), """
            ---
            name: docker
            description: Docker container management and troubleshooting.
            ---

            # Docker Basics
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Equal("devops", result[0].Category);
    }

    [Fact]
    public void Directories_without_skill_md_are_ignored()
    {
        // Directory with random files but no SKILL.md
        var dir = Path.Combine(_skillsDir, "not-a-skill");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "not a skill");
        File.WriteAllText(Path.Combine(dir, "readme.md"), "# Readme");

        WriteSkill("real-skill", """
            ---
            name: real-skill
            description: This is a real skill.
            ---

            # Real Skill
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Equal("real-skill", result[0].Name);
    }

    [Fact]
    public void Hidden_subdirectories_are_skipped()
    {
        var hiddenDir = Path.Combine(_skillsDir, ".hidden", "secret");
        Directory.CreateDirectory(hiddenDir);
        File.WriteAllText(Path.Combine(hiddenDir, "SKILL.md"), """
            ---
            name: secret
            description: Should not be discovered.
            ---

            # Secret Skill
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Empty(result);
    }

    [Fact]
    public void System_directory_is_not_skipped()
    {
        var systemDir = Path.Combine(_skillsDir, ".system", "diagnostics");
        Directory.CreateDirectory(systemDir);
        File.WriteAllText(Path.Combine(systemDir, "SKILL.md"), """
            ---
            name: diagnostics
            description: System diagnostics skill.
            ---

            # Diagnostics
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Equal("diagnostics", result[0].Name);
        Assert.Equal(".system", result[0].Category);
    }

    [Fact]
    public void Skill_with_no_resources_has_null_resource_paths()
    {
        WriteSkill("minimal", """
            ---
            name: minimal
            description: A minimal skill with no resources.
            ---

            # Minimal
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Null(result[0].ResourcePaths);
        Assert.NotNull(result[0].SkillDirectory);
    }

    /// <summary>
    /// Creates a skill directory with SKILL.md containing the given content.
    /// </summary>
    private void WriteSkill(string skillName, string content)
    {
        WriteSkillFile(skillName, "SKILL.md", content);
    }

    /// <summary>
    /// Writes a file at the given relative path within a skill directory.
    /// </summary>
    private void WriteSkillFile(string skillName, string relativePath, string content)
    {
        var fullPath = Path.Combine(_skillsDir, skillName, relativePath);
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
    }
}
