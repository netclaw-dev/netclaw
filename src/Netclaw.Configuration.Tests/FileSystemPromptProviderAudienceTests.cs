using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Verifies that <see cref="FileSystemPromptProvider"/> enforces audience-dependent
/// content gating: Public audience gets a stripped AGENTS.md (from embedded resource),
/// no TOOLING.md, and no project instructions. Team/Personal get the full content.
/// </summary>
public sealed class FileSystemPromptProviderAudienceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly NetclawPaths _paths;
    private readonly FileSystemPromptProvider _provider;

    public FileSystemPromptProviderAudienceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _paths = new NetclawPaths(_tempDir);
        _paths.EnsureDirectoriesExist();

        // Write a TOOLING.md so we can verify it is suppressed for Public
        File.WriteAllText(_paths.ToolingPath, "# Host Environment\nShell: bash\nOS: Linux");

        _provider = new FileSystemPromptProvider(_paths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Public_audience_gets_stripped_agents_without_team_only_content()
    {
        var prompt = _provider.GetSystemPrompt(TrustAudience.Public);

        // The public AGENTS.md is a stripped-down version that omits internal
        // sections like Search Decision Rules, Scheduling, Identity Files, etc.
        Assert.DoesNotContain("Search Decision Rules", prompt);
        Assert.DoesNotContain("Identity Files", prompt);
        Assert.DoesNotContain("media_dir", prompt);
        Assert.DoesNotContain("session_dir", prompt);
        Assert.DoesNotContain("inbox/", prompt);
        Assert.DoesNotContain("{{SYSTEM_SKILLS_DIR}}", prompt);
        Assert.DoesNotContain("{{IDENTITY_DIR}}", prompt);

        // But it still contains the core operating rules shared with all audiences
        Assert.Contains("Operating Rules", prompt);
        Assert.Contains("Autonomy Rules", prompt);
        Assert.Contains("Grounding Rules", prompt);
    }

    [Theory]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Personal)]
    public void Team_and_Personal_audience_get_full_agents_with_all_sections(TrustAudience audience)
    {
        var prompt = _provider.GetSystemPrompt(audience);

        // Full AGENTS.md includes sections that Public does not get
        Assert.Contains("Search Decision Rules", prompt);
        Assert.Contains("Identity Files", prompt);
        Assert.Contains("Scheduling", prompt);
        Assert.Contains("Skill Reference", prompt);
    }

    [Fact]
    public void Public_audience_does_not_include_tooling()
    {
        var prompt = _provider.GetSystemPrompt(TrustAudience.Public);

        // TOOLING.md content is written in the fixture — verify it is suppressed
        Assert.DoesNotContain("Host Environment", prompt);
        Assert.DoesNotContain("Shell: bash", prompt);
    }

    [Theory]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Personal)]
    public void Team_and_Personal_audience_include_tooling(TrustAudience audience)
    {
        var prompt = _provider.GetSystemPrompt(audience);

        Assert.Contains("Host Environment", prompt);
        Assert.Contains("Shell: bash", prompt);
    }

    [Fact]
    public void Public_audience_does_not_include_project_instructions()
    {
        // Create a project directory with a CLAUDE.md
        var projectDir = Path.Combine(_tempDir, "myproject");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "CLAUDE.md"), "# Secret Project Rules");

        var prompt = _provider.GetSystemPrompt(TrustAudience.Public, projectDirectory: projectDir);

        Assert.DoesNotContain("Secret Project Rules", prompt);
    }

    [Fact]
    public void Personal_audience_includes_project_instructions()
    {
        var projectDir = Path.Combine(_tempDir, "myproject");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "CLAUDE.md"), "# Secret Project Rules");

        var prompt = _provider.GetSystemPrompt(TrustAudience.Personal, projectDirectory: projectDir);

        Assert.Contains("Secret Project Rules", prompt);
    }

    [Fact]
    public void Placeholder_substitution_replaces_path_tokens_for_team()
    {
        var prompt = _provider.GetSystemPrompt(TrustAudience.Team);

        // Full AGENTS.md contains placeholders like {{SYSTEM_SKILLS_DIR}} that
        // should be resolved to actual paths from NetclawPaths
        Assert.DoesNotContain("{{SYSTEM_SKILLS_DIR}}", prompt);
        Assert.DoesNotContain("{{IDENTITY_DIR}}", prompt);
        Assert.DoesNotContain("{{SOUL_PATH}}", prompt);
        Assert.DoesNotContain("{{AGENTS_PATH}}", prompt);
        Assert.DoesNotContain("{{TOOLING_PATH}}", prompt);

        // Verify the actual paths appear in the substituted output
        Assert.Contains(_paths.SystemSkillsDirectory, prompt);
        Assert.Contains(_paths.IdentityDirectory, prompt);
    }
}
