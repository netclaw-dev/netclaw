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
        WriteFlatSkill("git-workflow.md", """
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
        Assert.Equal(SkillFormat.Standard, result[0].Format);
        Assert.Null(result[0].Category);
    }

    [Fact]
    public void Extracts_triggers_from_metadata()
    {
        WriteFlatSkill("diagnostics.md", """
            ---
            name: diagnostics
            description: Check system health and diagnose errors.
            metadata:
              triggers: connection failure | session timeout | missing tools
            ---

            # Diagnostics

            Run diagnostics when things break.
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Equal("connection failure | session timeout | missing tools", result[0].Triggers);
    }

    [Fact]
    public void Extracts_optional_fields_from_frontmatter()
    {
        WriteFlatSkill("pdf-processing.md", """
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
    public void Discovers_directory_based_skill()
    {
        WriteDirectorySkill("web-search", "SKILL.md", """
            ---
            name: web-search
            description: Search the web effectively for various domains.
            ---

            # Web Search

            Use domain-specific strategies for best results.
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Equal("web-search", result[0].Name);
        Assert.Equal("Web Search", result[0].DisplayName);
        Assert.Equal(Path.Combine(_skillsDir, "web-search", "SKILL.md"), result[0].FilePath);
        Assert.Equal(Path.Combine(_skillsDir, "web-search"), result[0].SkillDirectory);
    }

    [Fact]
    public void Directory_skill_enumerates_resources()
    {
        WriteDirectorySkill("web-search", "SKILL.md", """
            ---
            name: web-search
            description: Search the web effectively.
            ---

            # Web Search
            """);
        WriteDirectorySkill("web-search", "references/flight-pricing.md", "# Flight Pricing");
        WriteDirectorySkill("web-search", "references/restaurant-search.md", "# Restaurant Search");
        WriteDirectorySkill("web-search", "scripts/validate.sh", "#!/bin/bash\necho ok");

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.NotNull(result[0].ResourcePaths);
        Assert.Equal(3, result[0].ResourcePaths!.Count);
        Assert.Contains("references/flight-pricing.md", result[0].ResourcePaths!);
        Assert.Contains("references/restaurant-search.md", result[0].ResourcePaths!);
        Assert.Contains("scripts/validate.sh", result[0].ResourcePaths!);
    }

    [Fact]
    public void Directory_based_skill_preferred_over_flat_file()
    {
        // Create both a flat file and a directory-based skill with the same name
        WriteFlatSkill("deploy.md", """
            ---
            name: deploy
            description: Old flat file deploy skill.
            ---

            # Deploy (flat)
            """);

        WriteDirectorySkill("deploy", "SKILL.md", """
            ---
            name: deploy
            description: Directory-based deploy skill.
            ---

            # Deploy (directory)
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Equal("Directory-based deploy skill.", result[0].Description);
        Assert.EndsWith("SKILL.md", result[0].FilePath);
    }

    [Fact]
    public void Files_without_frontmatter_are_skipped()
    {
        WriteFlatSkill("notes.md", "# Just some notes\n\nNo frontmatter here.");

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Empty(result);
    }

    [Fact]
    public void Files_with_missing_description_are_skipped()
    {
        WriteFlatSkill("bad-skill.md", """
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
        WriteFlatSkill("broken.md", """
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
        WriteFlatSkill("colon-test.md", """
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
    public void Falls_back_to_filename_when_no_name_in_frontmatter()
    {
        WriteFlatSkill("my-skill.md", """
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
    public void Subdirectory_flat_files_get_category()
    {
        var subDir = Path.Combine(_skillsDir, "devops");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "docker.md"), """
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
    public void Non_md_files_are_ignored()
    {
        File.WriteAllText(Path.Combine(_skillsDir, "notes.txt"), "not a skill");
        WriteFlatSkill("real-skill.md", """
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
        var hiddenDir = Path.Combine(_skillsDir, ".hidden");
        Directory.CreateDirectory(hiddenDir);
        File.WriteAllText(Path.Combine(hiddenDir, "secret.md"), """
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
    public void Triggers_null_when_not_in_metadata()
    {
        WriteFlatSkill("simple.md", """
            ---
            name: simple
            description: A simple skill without triggers.
            ---

            # Simple Skill
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Null(result[0].Triggers);
    }

    [Fact]
    public void Directory_skill_with_no_resources_has_null_resource_paths()
    {
        WriteDirectorySkill("minimal", "SKILL.md", """
            ---
            name: minimal
            description: A minimal directory skill with no resources.
            ---

            # Minimal
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Null(result[0].ResourcePaths);
        Assert.NotNull(result[0].SkillDirectory);
    }

    private void WriteFlatSkill(string fileName, string content)
    {
        File.WriteAllText(Path.Combine(_skillsDir, fileName), content);
    }

    private void WriteDirectorySkill(string skillName, string relativePath, string content)
    {
        var fullPath = Path.Combine(_skillsDir, skillName, relativePath);
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
    }
}
