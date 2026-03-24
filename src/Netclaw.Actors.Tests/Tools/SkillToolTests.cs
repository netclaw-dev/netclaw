using Netclaw.Actors.Skills;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Security.Skills;
using Xunit;

namespace Netclaw.Actors.Tests.Tools;

public class SkillToolTests : IDisposable
{
    private readonly string _skillsDir;
    private readonly NetclawPaths _paths;
    private readonly SkillRegistry _registry;
    private readonly SkillIndexContextLayer _indexLayer;

    public SkillToolTests()
    {
        _skillsDir = Path.Combine(Path.GetTempPath(), $"netclaw-skill-tools-test-{Guid.NewGuid():N}");
        _paths = new NetclawPaths(_skillsDir);
        _paths.EnsureDirectoriesExist();
        _registry = new SkillRegistry();
        _indexLayer = new SkillIndexContextLayer();
    }

    public void Dispose()
    {
        if (Directory.Exists(_skillsDir))
            Directory.Delete(_skillsDir, true);
    }

    // ── skill_load ────────────────────────────────────────────────────

    [Fact]
    public async Task SkillLoad_ReturnsBodyForKnownSkill()
    {
        WriteSkill("test-skill", """
            ---
            name: test-skill
            description: A test skill.
            metadata:
              version: "1.0.0"
            ---

            # Test Skill

            Do the thing.
            """);
        ScanSkills();

        var tool = new SkillLoadTool(_registry);
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Name"] = "test-skill" });

        Assert.Contains("Test Skill", result);
        Assert.Contains("Do the thing.", result);
        Assert.Contains("1.0.0", result);
    }

    [Fact]
    public async Task SkillLoad_ReturnsErrorForUnknownSkill()
    {
        ScanSkills();
        var tool = new SkillLoadTool(_registry);
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Name"] = "nonexistent" });

        Assert.Contains("not found", result);
    }

    // ── skill_read_resource ───────────────────────────────────────────

    [Fact]
    public async Task SkillReadResource_ReadsValidPath()
    {
        WriteSkill("my-skill", """
            ---
            name: my-skill
            description: Test skill.
            ---
            # My Skill
            """);
        WriteFile("my-skill", "references/guide.md", "# Guide Content");
        ScanSkills();

        var tool = new SkillReadResourceTool(_registry);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["SkillName"] = "my-skill",
            ["ResourcePath"] = "references/guide.md"
        });

        Assert.Equal("# Guide Content", result);
    }

    [Fact]
    public async Task SkillReadResource_RejectsPathTraversal()
    {
        WriteSkill("my-skill", """
            ---
            name: my-skill
            description: Test skill.
            ---
            # My Skill
            """);
        ScanSkills();

        var tool = new SkillReadResourceTool(_registry);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["SkillName"] = "my-skill",
            ["ResourcePath"] = "../../etc/passwd"
        });

        Assert.Contains("not allowed", result);
    }

    [Fact]
    public async Task SkillReadResource_RejectsAbsolutePath()
    {
        WriteSkill("my-skill", """
            ---
            name: my-skill
            description: Test skill.
            ---
            # My Skill
            """);
        ScanSkills();

        var tool = new SkillReadResourceTool(_registry);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["SkillName"] = "my-skill",
            ["ResourcePath"] = "/etc/passwd"
        });

        Assert.Contains("not allowed", result);
    }

    [Fact]
    public async Task SkillReadResource_RejectsDisallowedPrefix()
    {
        WriteSkill("my-skill", """
            ---
            name: my-skill
            description: Test skill.
            ---
            # My Skill
            """);
        ScanSkills();

        var tool = new SkillReadResourceTool(_registry);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["SkillName"] = "my-skill",
            ["ResourcePath"] = "SKILL.md"
        });

        Assert.Contains("must start with", result);
    }

    // ── skill_manage ──────────────────────────────────────────────────

    [Fact]
    public async Task SkillManage_Create_ValidatesName()
    {
        ScanSkills();
        var tool = CreateManageTool();
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Action"] = "create",
            ["Name"] = "Invalid Name!",
            ["Content"] = "---\nname: x\ndescription: test\n---\n# X"
        });

        Assert.Contains("lowercase", result);
    }

    [Fact]
    public async Task SkillManage_Create_ValidatesFrontmatter()
    {
        ScanSkills();
        var tool = CreateManageTool();
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Action"] = "create",
            ["Name"] = "valid-name",
            ["Content"] = "no frontmatter here"
        });

        Assert.Contains("frontmatter", result);
    }

    [Fact]
    public async Task SkillManage_Create_RequiresDescription()
    {
        ScanSkills();
        var tool = CreateManageTool();
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Action"] = "create",
            ["Name"] = "valid-name",
            ["Content"] = "---\nname: valid-name\n---\n# No Description"
        });

        Assert.Contains("description", result);
    }

    [Fact]
    public async Task SkillManage_Edit_RejectsSystemSkill()
    {
        var systemDir = Path.Combine(_paths.SkillsDirectory, ".system", "sys-skill");
        Directory.CreateDirectory(systemDir);
        File.WriteAllText(Path.Combine(systemDir, "SKILL.md"), """
            ---
            name: sys-skill
            description: System skill.
            ---
            # System
            """);
        ScanSkills();

        var tool = CreateManageTool();
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Action"] = "edit",
            ["Name"] = "sys-skill",
            ["Content"] = "---\nname: sys-skill\ndescription: hacked\n---\n# Hacked"
        });

        Assert.Contains("read-only", result);
    }

    [Fact]
    public async Task SkillManage_Patch_ReplacesUniqueMatch()
    {
        WriteSkill("patch-test", """
            ---
            name: patch-test
            description: Test patching.
            ---

            # Patch Test

            Original content here.
            """);
        ScanSkills();

        var tool = CreateManageTool();
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Action"] = "patch",
            ["Name"] = "patch-test",
            ["OldString"] = "Original content",
            ["NewString"] = "Updated content"
        });

        Assert.Contains("Patch applied", result);

        var content = File.ReadAllText(
            Path.Combine(_paths.SkillsDirectory, "patch-test", "SKILL.md"));
        Assert.Contains("Updated content", content);
    }

    [Fact]
    public async Task SkillManage_WriteFile_ValidatesPath()
    {
        WriteSkill("wf-test", """
            ---
            name: wf-test
            description: Write file test.
            ---
            # WF
            """);
        ScanSkills();

        var tool = CreateManageTool();
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Action"] = "write_file",
            ["Name"] = "wf-test",
            ["FilePath"] = "baddir/file.md",
            ["FileContent"] = "content"
        });

        Assert.Contains("must start with", result);
    }

    [Fact]
    public async Task SkillManage_Delete_RemovesSkillDirectory()
    {
        WriteSkill("delete-me", """
            ---
            name: delete-me
            description: Will be deleted.
            ---
            # Delete Me
            """);
        ScanSkills();

        var tool = CreateManageTool();
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["Action"] = "delete",
            ["Name"] = "delete-me"
        });

        Assert.Contains("deleted", result);
        Assert.False(Directory.Exists(
            Path.Combine(_paths.SkillsDirectory, "delete-me")));
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private SkillManageTool CreateManageTool()
        => new(_registry, _indexLayer, _paths, new NoOpSkillContentScanner());

    private void WriteSkill(string name, string content)
    {
        var dir = Path.Combine(_paths.SkillsDirectory, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), content);
    }

    private void WriteFile(string skillName, string relativePath, string content)
    {
        var fullPath = Path.Combine(_paths.SkillsDirectory, skillName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private void ScanSkills()
    {
        _registry.Clear();
        foreach (var skill in SkillScanner.Scan(_paths.SkillsDirectory))
            _registry.Register(skill);
    }
}
