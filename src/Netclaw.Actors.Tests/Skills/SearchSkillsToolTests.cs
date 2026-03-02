using Netclaw.Actors.Skills;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Actors.Tests.Skills;

public class SearchSkillsToolTests : IDisposable
{
    private readonly string _skillsDir;

    public SearchSkillsToolTests()
    {
        _skillsDir = Path.Combine(Path.GetTempPath(), $"netclaw-skills-tool-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_skillsDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_skillsDir))
            Directory.Delete(_skillsDir, recursive: true);
    }

    [Fact]
    public async Task Returns_full_content_of_matching_skill()
    {
        var content = "# Test Skill\n\nThis is the full content.";
        File.WriteAllText(Path.Combine(_skillsDir, "test-skill.md"), content);

        var registry = new SkillRegistry();
        foreach (var entry in SkillScanner.Scan(_skillsDir))
            registry.Register(entry);

        var tool = new SearchSkillsTool(registry);
        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "test" },
            CancellationToken.None);

        Assert.Contains("This is the full content.", result);
        Assert.Contains("Test Skill", result);
    }

    [Fact]
    public async Task Returns_no_match_message_when_empty()
    {
        var registry = new SkillRegistry();
        var tool = new SearchSkillsTool(registry);

        var result = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["Query"] = "nonexistent" },
            CancellationToken.None);

        Assert.Contains("No skills found", result);
    }

    [Fact]
    public void Grant_category_is_builtin()
    {
        var registry = new SkillRegistry();
        var tool = new SearchSkillsTool(registry);

        Assert.Equal("builtin", tool.GrantCategory);
    }
}
