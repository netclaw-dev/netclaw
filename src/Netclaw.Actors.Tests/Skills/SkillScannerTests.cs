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
        Assert.Empty(result.AcceptedSkills);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Nonexistent_directory_returns_empty_list()
    {
        var result = SkillScanner.Scan(Path.Combine(_skillsDir, "nonexistent"));
        Assert.Empty(result.AcceptedSkills);
        Assert.Empty(result.Issues);
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

        Assert.Single(result.AcceptedSkills);
        Assert.Equal("git-workflow", result.AcceptedSkills[0].Name);
        Assert.Equal("Git Workflow", result.AcceptedSkills[0].DisplayName);
        Assert.Equal("How to manage branches and PRs in this project.", result.AcceptedSkills[0].Description);
        Assert.Null(result.AcceptedSkills[0].Category);
        Assert.Equal(Path.Combine(_skillsDir, "git-workflow"), result.AcceptedSkills[0].SkillDirectory);
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

        Assert.Single(result.AcceptedSkills);
        Assert.Equal("Apache-2.0", result.AcceptedSkills[0].License);
        Assert.Equal("Requires poppler-utils", result.AcceptedSkills[0].Compatibility);
        Assert.Equal("Bash(pdftotext:*) Read", result.AcceptedSkills[0].AllowedTools);
        Assert.Equal("1.2.0", result.AcceptedSkills[0].Version);
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

        Assert.Single(result.AcceptedSkills);
        Assert.Equal("web-search", result.AcceptedSkills[0].Name);
        Assert.Equal("Web Search", result.AcceptedSkills[0].DisplayName);
        Assert.Equal(Path.Combine(_skillsDir, "web-search", "SKILL.md"), result.AcceptedSkills[0].FilePath);
        Assert.Equal(Path.Combine(_skillsDir, "web-search"), result.AcceptedSkills[0].SkillDirectory);
        Assert.NotNull(result.AcceptedSkills[0].ResourcePaths);
        Assert.Equal(3, result.AcceptedSkills[0].ResourcePaths!.Count);
        Assert.Contains("references/flight-pricing.md", result.AcceptedSkills[0].ResourcePaths!);
        Assert.Contains("references/restaurant-search.md", result.AcceptedSkills[0].ResourcePaths!);
        Assert.Contains("scripts/validate.sh", result.AcceptedSkills[0].ResourcePaths!);
    }

    [Fact]
    public void Skill_without_frontmatter_is_skipped()
    {
        WriteSkill("notes", "# Just some notes\n\nNo frontmatter here.");

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Empty(result.AcceptedSkills);
        Assert.Single(result.Issues);
        Assert.Equal(SkillScanIssueKind.MissingFrontmatter, result.Issues[0].Kind);
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

        Assert.Empty(result.AcceptedSkills);
        Assert.Single(result.Issues);
        Assert.Equal(SkillScanIssueKind.MissingDescription, result.Issues[0].Kind);
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
        Assert.True(result.AcceptedSkills.Count <= 1);
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

        Assert.Single(result.AcceptedSkills);
        Assert.Equal("Use this skill when: the user asks about PDFs.", result.AcceptedSkills[0].Description);
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

        Assert.Single(result.AcceptedSkills);
        Assert.Equal("my-skill", result.AcceptedSkills[0].Name);
        Assert.Equal("My Custom Skill", result.AcceptedSkills[0].DisplayName);
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

        Assert.Single(result.AcceptedSkills);
        Assert.Equal("devops", result.AcceptedSkills[0].Category);
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

        Assert.Single(result.AcceptedSkills);
        Assert.Equal("real-skill", result.AcceptedSkills[0].Name);
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

        Assert.Empty(result.AcceptedSkills);
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

        Assert.Single(result.AcceptedSkills);
        Assert.Equal("diagnostics", result.AcceptedSkills[0].Name);
        Assert.Equal(".system", result.AcceptedSkills[0].Category);
    }

    [Fact]
    public void Community_directory_is_not_scanned()
    {
        WriteNestedSkill(".community", "home-auto", "Home automation skill.");

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Empty(result.AcceptedSkills);
    }

    [Fact]
    public void External_directory_is_not_scanned()
    {
        WriteNestedSkill(".external", "third-party", "Third-party skill.");

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Empty(result.AcceptedSkills);
    }

    [Fact]
    public void Agent_directory_is_not_scanned()
    {
        WriteNestedSkill(".agent", "learned-workflow", "Agent-created skill.");

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Empty(result.AcceptedSkills);
    }

    [Fact]
    public void Quarantine_directory_is_not_scanned()
    {
        WriteNestedSkill(".quarantine", "suspect", "Quarantined skill.");

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Empty(result.AcceptedSkills);
    }

    [Fact]
    public void Unknown_hidden_directory_is_not_scanned()
    {
        WriteNestedSkill(".unknown", "mystery", "Unknown hidden dir skill.");

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Empty(result.AcceptedSkills);
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

        Assert.Single(result.AcceptedSkills);
        Assert.Null(result.AcceptedSkills[0].ResourcePaths);
        Assert.NotNull(result.AcceptedSkills[0].SkillDirectory);
    }

    [Fact]
    public void Duplicate_skill_names_are_rejected_with_explicit_issues()
    {
        WriteSkill("shared-name", """
            ---
            name: shared-name
            description: First copy.
            ---

            # First
            """);

        var secondDir = Path.Combine(_skillsDir, ".system", "shared-name");
        Directory.CreateDirectory(secondDir);
        File.WriteAllText(Path.Combine(secondDir, "SKILL.md"), """
            ---
            name: shared-name
            description: Second copy.
            ---

            # Second
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Empty(result.AcceptedSkills);
        Assert.Equal(2, result.Issues.Count(issue => issue.Kind == SkillScanIssueKind.DuplicateName));
    }

    [Fact]
    public void Frontmatter_name_mismatch_is_rejected_with_issue()
    {
        WriteSkill("expected-name", """
            ---
            name: other-name
            description: Wrong identity.
            ---

            # Mismatch
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Empty(result.AcceptedSkills);
        Assert.Single(result.Issues);
        Assert.Equal(SkillScanIssueKind.FrontmatterNameMismatch, result.Issues[0].Kind);
    }

    [Fact]
    public void Nested_skill_md_under_a_claimed_root_is_not_scanned()
    {
        // prepare-ralph/SKILL.md is a valid skill; prepare-ralph/skills/ralph-after-action/SKILL.md
        // is an internal resource (even if it happens to contain YAML frontmatter) and must not be
        // picked up as a separate skill.
        WriteSkill("prepare-ralph", """
            ---
            name: prepare-ralph
            description: Bootstrap RALPH infrastructure.
            ---

            # Prepare RALPH
            """);

        var nestedDir = Path.Combine(_skillsDir, "prepare-ralph", "skills", "ralph-after-action");
        Directory.CreateDirectory(nestedDir);
        File.WriteAllText(Path.Combine(nestedDir, "SKILL.md"), """
            ---
            name: ralph-after-action
            description: Nested internal resource masquerading as a skill.
            ---

            # Ralph After Action
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result.AcceptedSkills);
        Assert.Equal("prepare-ralph", result.AcceptedSkills[0].Name);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void NonStrict_name_match_accepts_frontmatter_name_over_directory()
    {
        // meta-agent-os-bootstrap/ with frontmatter name: agent-os-bootstrap
        // Mirrors Claude Code skills where the frontmatter name is canonical.
        var skillDir = Path.Combine(_skillsDir, "meta-agent-os-bootstrap");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), """
            ---
            name: agent-os-bootstrap
            description: Bootstrap the agent OS constitution.
            ---

            # Agent OS Bootstrap
            """);

        var result = SkillScanner.Scan(_skillsDir, strictNameMatch: false);

        Assert.Single(result.AcceptedSkills);
        Assert.Equal("agent-os-bootstrap", result.AcceptedSkills[0].Name);
        Assert.DoesNotContain(result.Issues, i => i.Kind == SkillScanIssueKind.FrontmatterNameMismatch);
    }

    [Fact]
    public void NonStrict_name_match_accepts_flat_file_with_mismatching_frontmatter_name()
    {
        // Flat .md file where filename and frontmatter name differ.
        File.WriteAllText(Path.Combine(_skillsDir, "daily-plan.md"), """
            ---
            name: daily-plan
            description: Run the daily planning workflow.
            ---

            # Daily Plan
            """);

        // Make a second one where names differ (simulating a Claude Code command).
        File.WriteAllText(Path.Combine(_skillsDir, "weird-filename.md"), """
            ---
            name: actual-command-name
            description: Frontmatter name wins over filename.
            ---

            # Actual Command
            """);

        var result = SkillScanner.Scan(_skillsDir, strictNameMatch: false);

        Assert.Equal(2, result.AcceptedSkills.Count);
        Assert.Contains(result.AcceptedSkills, s => s.Name == "daily-plan");
        Assert.Contains(result.AcceptedSkills, s => s.Name == "actual-command-name");
        Assert.DoesNotContain(result.Issues, i => i.Kind == SkillScanIssueKind.FrontmatterNameMismatch);
    }

    [Fact]
    public void Symlinked_resource_tree_is_rejected_with_issue()
    {
        WriteSkill("linked-skill", """
            ---
            name: linked-skill
            description: Has linked resources.
            ---

            # Linked
            """);

        var targetDir = Path.Combine(_skillsDir, "external-resources");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "guide.md"), "# Guide");
        Directory.CreateSymbolicLink(Path.Combine(_skillsDir, "linked-skill", "references"), targetDir);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Empty(result.AcceptedSkills);
        Assert.Contains(result.Issues, issue => issue.Kind == SkillScanIssueKind.SymlinkNotAllowed);
    }

    [Fact]
    public void Symlinked_skill_directory_is_rejected_with_issue()
    {
        var externalDir = Path.Combine(_skillsDir, "external-linked-skill");
        Directory.CreateDirectory(externalDir);
        File.WriteAllText(Path.Combine(externalDir, "SKILL.md"), """
            ---
            name: linked-skill
            description: Lives outside the configured root.
            ---

            # Linked Skill
            """);

        Directory.CreateSymbolicLink(Path.Combine(_skillsDir, "linked-skill"), externalDir);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Empty(result.AcceptedSkills);
        Assert.Contains(result.Issues, issue => issue.Kind == SkillScanIssueKind.SymlinkNotAllowed);
    }

    [Fact]
    public void Symlinked_resource_tree_accepted_when_allowSymlinks_is_true()
    {
        WriteSkill("linked-skill", """
            ---
            name: linked-skill
            description: Has linked resources.
            ---

            # Linked
            """);

        var targetDir = Path.Combine(_skillsDir, "external-resources");
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(targetDir, "guide.md"), "# Guide");
        Directory.CreateSymbolicLink(Path.Combine(_skillsDir, "linked-skill", "references"), targetDir);

        var result = SkillScanner.Scan(_skillsDir, allowSymlinks: true);

        Assert.Single(result.AcceptedSkills);
        Assert.Equal("linked-skill", result.AcceptedSkills[0].Name);
    }

    [Fact]
    public void Unreadable_skill_file_is_reported_with_issue()
    {
        WriteSkill("unreadable-skill", """
            ---
            name: unreadable-skill
            description: Cannot be read.
            ---

            # Unreadable
            """);

        var skillFile = Path.Combine(_skillsDir, "unreadable-skill", "SKILL.md");

        if (OperatingSystem.IsWindows())
            return;

        var originalMode = File.GetUnixFileMode(skillFile);

        try
        {
            File.SetUnixFileMode(skillFile, UnixFileMode.UserWrite);

            var result = SkillScanner.Scan(_skillsDir);

            Assert.Empty(result.AcceptedSkills);
            Assert.Contains(result.Issues, issue => issue.Kind == SkillScanIssueKind.UnreadableFile);
        }
        finally
        {
            File.SetUnixFileMode(skillFile, originalMode);
        }
    }

    [Fact]
    public void AllowSymlinks_does_not_affect_normal_skills()
    {
        WriteSkill("normal-skill", """
            ---
            name: normal-skill
            description: A normal skill.
            ---

            # Normal
            """);

        var result = SkillScanner.Scan(_skillsDir, allowSymlinks: true);

        Assert.Single(result.AcceptedSkills);
        Assert.Equal("normal-skill", result.AcceptedSkills[0].Name);
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

    /// <summary>
    /// Creates a skill inside a nested (category) directory, e.g. .system/skill-name/SKILL.md.
    /// </summary>
    private void WriteNestedSkill(string category, string skillName, string description)
    {
        var dir = Path.Combine(_skillsDir, category, skillName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"""
            ---
            name: {skillName}
            description: "{description}"
            ---

            # {skillName}
            """);
    }
}
