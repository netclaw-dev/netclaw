using Netclaw.Actors.Skills;
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
    public void Extracts_heading_and_description_comment()
    {
        File.WriteAllText(Path.Combine(_skillsDir, "git-workflow.md"),
            """
            # Git Workflow
            <!-- description: How to manage branches and PRs -->

            ## Branching Strategy
            Use feature branches.
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Equal("git-workflow", result[0].Name);
        Assert.Equal("Git Workflow", result[0].DisplayName);
        Assert.Equal("How to manage branches and PRs", result[0].Description);
        Assert.Null(result[0].Category);
    }

    [Fact]
    public void Falls_back_to_first_paragraph_when_no_description_comment()
    {
        File.WriteAllText(Path.Combine(_skillsDir, "deploy.md"),
            """
            # Deploy Guide

            This skill explains how to deploy services to production.

            ## Steps
            1. Build
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Equal("This skill explains how to deploy services to production.", result[0].Description);
    }

    [Fact]
    public void Subdirectory_sets_category()
    {
        var subDir = Path.Combine(_skillsDir, "devops");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "docker.md"),
            """
            # Docker Basics
            <!-- description: Docker container management -->
            """);

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Equal("devops", result[0].Category);
    }

    [Fact]
    public void Non_md_files_are_ignored()
    {
        File.WriteAllText(Path.Combine(_skillsDir, "notes.txt"), "not a skill");
        File.WriteAllText(Path.Combine(_skillsDir, "skill.md"), "# Real Skill\n\nThis is a skill.");

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Equal("skill", result[0].Name);
    }

    [Fact]
    public void Falls_back_to_title_cased_name_when_no_heading()
    {
        File.WriteAllText(Path.Combine(_skillsDir, "my-cool-tool.md"),
            "Some content without a heading.");

        var result = SkillScanner.Scan(_skillsDir);

        Assert.Single(result);
        Assert.Equal("My Cool Tool", result[0].DisplayName);
    }
}
