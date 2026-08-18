// -----------------------------------------------------------------------
// <copyright file="BuiltInSkillSeedingTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests;

/// <summary>
/// Validates that built-in skill files are present in the build output and can
/// be seeded to a skills directory correctly. Skills are sourced from
/// <c>feeds/skills/.system/files/</c> via the csproj Content items.
/// </summary>
public sealed class BuiltInSkillSeedingTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void BuiltInSkills_directory_exists_in_build_output()
    {
        var builtInDir = Path.Combine(AppContext.BaseDirectory, "BuiltInSkills");
        Assert.True(Directory.Exists(builtInDir), $"BuiltInSkills directory not found at {builtInDir}");
    }

    [Theory]
    [InlineData("netclaw-operations")]
    [InlineData("netclaw-memory")]
    [InlineData("search-citation")]
    [InlineData("skill-authoring")]
    [InlineData("subagent-authoring")]
    public void BuiltInSkills_contains_SKILL_md_for_each_system_skill(string skillName)
    {
        var skillPath = Path.Combine(AppContext.BaseDirectory, "BuiltInSkills", skillName, "SKILL.md");
        Assert.True(File.Exists(skillPath), $"Missing built-in skill: {skillPath}");

        var content = File.ReadAllText(skillPath);
        Assert.StartsWith("---", content, StringComparison.Ordinal); // YAML frontmatter
        Assert.Contains($"name: {skillName}", content, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltInSkills_includes_companion_files_for_search_citation()
    {
        var refsDir = Path.Combine(AppContext.BaseDirectory, "BuiltInSkills", "search-citation", "references");
        Assert.True(Directory.Exists(refsDir), $"search-citation/references/ directory missing at {refsDir}");

        var refFiles = Directory.GetFiles(refsDir, "*.md");
        Assert.True(refFiles.Length >= 3, $"Expected at least 3 reference files, found {refFiles.Length}");
    }

    [Fact]
    public void Operations_skill_and_project_reference_share_tool_and_directory_order()
    {
        var skillDirectory = Path.Combine(AppContext.BaseDirectory, "BuiltInSkills", "netclaw-operations");
        var skill = File.ReadAllText(Path.Combine(skillDirectory, "SKILL.md"));
        var projects = File.ReadAllText(Path.Combine(skillDirectory, "references", "projects.md"));

        Assert.Contains("version: \"2.56.0\"", skill, StringComparison.Ordinal);
        Assert.Contains("use `file_read` for a known local file read", skill, StringComparison.Ordinal);
        Assert.Contains("use `web_search` for external discovery", skill, StringComparison.Ordinal);
        Assert.Contains("use `shell_execute` for local search", skill, StringComparison.Ordinal);
        Assert.Contains("Do not delegate a known file operation", skill, StringComparison.Ordinal);
        Assert.Contains("do not use shell only to verify", skill, StringComparison.Ordinal);
        Assert.Contains("do not attempt a shell redirect first", skill, StringComparison.Ordinal);
        Assert.Contains("Start with the smallest single shell operation", skill, StringComparison.Ordinal);
        Assert.Contains("After an approval-required result", skill, StringComparison.Ordinal);
        Assert.Contains("A `Tool access denied:` result is terminal", skill, StringComparison.Ordinal);

        var statements = new[]
        {
            "For declared-project work, omit `WorkingDirectory`",
            "For one call in a named child directory",
            "Use `session_dir` only for disposable work outside a project",
            "Use an inline directory change only when",
            "Start with the smallest single shell operation",
            "After an approval-required result",
            "A `Tool access denied:` result is terminal",
            "Apply one `Tool execution deferred:` correction unchanged"
        };
        foreach (var statement in statements)
        {
            Assert.Contains(statement, skill, StringComparison.Ordinal);
            Assert.Contains(statement, projects, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CopyBuiltInSkills_seeds_to_empty_directory()
    {
        var skillsDir = Path.Combine(_dir.Path, "skills");
        Directory.CreateDirectory(skillsDir);

        // Invoke the seeding method
        CopyBuiltInSkillsHelper(skillsDir);

        // Verify all 3 skills were seeded
        var seededSkills = Directory.GetDirectories(skillsDir)
            .Select(Path.GetFileName)
            .OrderBy(n => n)
            .ToList();

        Assert.Contains("netclaw-operations", seededSkills);
        Assert.Contains("netclaw-memory", seededSkills);
        Assert.Contains("search-citation", seededSkills);
        Assert.Contains("skill-authoring", seededSkills);
        Assert.Contains("subagent-authoring", seededSkills);

        // Verify SKILL.md exists in each
        foreach (var skillDir in Directory.GetDirectories(skillsDir))
        {
            Assert.True(File.Exists(Path.Combine(skillDir, "SKILL.md")),
                $"Missing SKILL.md in {Path.GetFileName(skillDir)}");
        }

        // Verify companion files were copied
        Assert.True(File.Exists(Path.Combine(skillsDir, "search-citation", "references", "local-search.md")));
    }

    [Fact]
    public void CopyBuiltInSkills_does_not_overwrite_existing_files()
    {
        var skillsDir = Path.Combine(_dir.Path, "skills");
        var skillDir = Path.Combine(skillsDir, "netclaw-memory");
        var targetPath = Path.Combine(skillDir, "SKILL.md");

        Directory.CreateDirectory(skillDir);
        File.WriteAllText(targetPath, "custom content from feed sync");

        // Run seeding — should NOT overwrite
        CopyBuiltInSkillsHelper(skillsDir);

        Assert.Equal("custom content from feed sync", File.ReadAllText(targetPath));
    }

    /// <summary>
    /// Mirrors the <c>CopyBuiltInSkills</c> logic from Program.cs for testing.
    /// </summary>
    private static void CopyBuiltInSkillsHelper(string skillsDirectory)
    {
        var builtInDir = Path.Combine(AppContext.BaseDirectory, "BuiltInSkills");
        if (!Directory.Exists(builtInDir))
            return;

        foreach (var sourceFile in Directory.EnumerateFiles(builtInDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(builtInDir, sourceFile);
            var targetPath = Path.Combine(skillsDirectory, relativePath);

            if (File.Exists(targetPath))
                continue;

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourceFile, targetPath);
        }
    }
}
