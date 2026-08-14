// -----------------------------------------------------------------------
// <copyright file="FileSystemPromptProviderAudienceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Configuration.Tests;

/// <summary>
/// Verifies that <see cref="FileSystemPromptProvider"/> enforces audience-dependent
/// content gating: Public audience gets a stripped AGENTS.md (from embedded resource),
/// no TOOLING.md, and no project instructions. Team/Personal get the full content.
/// </summary>
public sealed class FileSystemPromptProviderAudienceTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;
    private readonly FileSystemPromptProvider _provider;

    public FileSystemPromptProviderAudienceTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();

        // Write a TOOLING.md so we can verify it is suppressed for Public
        File.WriteAllText(_paths.ToolingPath, "# Host Environment\nShell: bash\nOS: Linux");

        _provider = new FileSystemPromptProvider(_paths);
    }

    public void Dispose()
    {
        _dir.Dispose();
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
        Assert.Contains("Skill Loading", prompt);
    }

    [Fact]
    public void Personal_rules_prefer_typed_shell_working_directory_and_retry_failed_project_scope()
    {
        var prompt = _provider.GetSystemPrompt(TrustAudience.Personal);

        Assert.Contains("`WorkingDirectory` argument", prompt);
        Assert.Contains("Do not prefix the command with an inline `cd`", prompt);
        Assert.Contains("Path arguments give the approval gate an exact candidate scope", prompt);
        Assert.Contains("safe-space root", prompt);
        Assert.DoesNotContain("path argument IS the declaration", prompt);
        Assert.Contains("before the first shell", prompt);
        Assert.Contains("Do not repeat", prompt);
        Assert.Contains("`project_dir` already names the correct project", prompt);
        Assert.Contains("changing directory is itself behavior", prompt);
        Assert.Contains("correct the path and retry the tool", prompt);
        Assert.Contains("Do not continue with a stale directory", prompt);
    }

    [Theory]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Personal)]
    public void Trusted_audiences_prefer_file_tools_for_known_content(TrustAudience audience)
    {
        var prompt = _provider.GetSystemPrompt(audience);

        Assert.Contains("Prefer file tools for known file reads", prompt);
        Assert.Contains("Do not use shell for those operations", prompt);
        Assert.Contains("Never use `cat`, `sed`, or `ls`", prompt);
        Assert.Contains("Use `shell_execute` for local repository search", prompt);
        Assert.Contains("Use built-in `web_search` for external discovery", prompt);
        Assert.Contains("Do not use shell HTTP clients", prompt);
    }

    [Fact]
    public void Public_audience_omits_shell_selection_guidance()
    {
        var prompt = _provider.GetSystemPrompt(TrustAudience.Public);

        Assert.DoesNotContain("Use `shell_execute` for local repository search", prompt);
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
        var projectDir = Path.Combine(_dir.Path, "myproject");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "CLAUDE.md"), "# Secret Project Rules");

        var prompt = _provider.GetSystemPrompt(TrustAudience.Public, projectDirectory: projectDir);

        Assert.DoesNotContain("Secret Project Rules", prompt);
    }

    [Fact]
    public void Personal_audience_includes_project_instructions()
    {
        var projectDir = Path.Combine(_dir.Path, "myproject");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "CLAUDE.md"), "# Secret Project Rules");

        var prompt = _provider.GetSystemPrompt(TrustAudience.Personal, projectDirectory: projectDir);

        Assert.Contains("Secret Project Rules", prompt);
    }

    [Fact]
    public void Placeholder_substitution_replaces_identity_tokens_without_exposing_skill_root()
    {
        var prompt = _provider.GetSystemPrompt(TrustAudience.Team);

        // Identity paths remain explicit because file tools edit them directly.
        // Skill access is logical, so the physical system-skill root stays absent.
        Assert.DoesNotContain("{{SYSTEM_SKILLS_DIR}}", prompt);
        Assert.DoesNotContain("{{IDENTITY_DIR}}", prompt);
        Assert.DoesNotContain("{{SOUL_PATH}}", prompt);
        Assert.DoesNotContain("{{AGENTS_PATH}}", prompt);
        Assert.DoesNotContain("{{TOOLING_PATH}}", prompt);

        Assert.Contains(_paths.IdentityDirectory, prompt);
        Assert.DoesNotContain(_paths.SystemSkillsDirectory, prompt);
    }

    [Theory]
    [InlineData(TrustAudience.Public)]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Personal)]
    public void Every_audience_gets_deployment_playbook_after_embedded_core(TrustAudience audience)
    {
        File.WriteAllText(_paths.AgentsPath,
            "Always review customer email before delivery. Identity: {{IDENTITY_DIR}}");

        var prompt = _provider.GetSystemPrompt(audience);

        var embeddedIndex = prompt.IndexOf("Operating Rules", StringComparison.Ordinal);
        var headingIndex = prompt.IndexOf("Deployment Mission and Operating Playbook", StringComparison.Ordinal);
        var playbookIndex = prompt.IndexOf("Always review customer email", StringComparison.Ordinal);
        Assert.True(embeddedIndex >= 0);
        Assert.True(headingIndex > embeddedIndex);
        Assert.True(playbookIndex > headingIndex);
        Assert.Contains(_paths.IdentityDirectory, prompt);
        Assert.DoesNotContain("{{IDENTITY_DIR}}", prompt);
    }

    [Theory]
    [InlineData(TrustAudience.Public)]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Personal)]
    public void Operating_rules_include_deployment_playbook_for_every_audience(TrustAudience audience)
    {
        File.WriteAllText(_paths.AgentsPath, "Use the deployment review checklist.");

        var rules = _provider.GetOperatingRules(audience);

        Assert.NotNull(rules);
        Assert.Contains("Operating Rules", rules);
        Assert.Contains("Use the deployment review checklist.", rules);
    }

    [Fact]
    public void Missing_deployment_playbook_uses_embedded_rules_only()
    {
        var rules = _provider.GetOperatingRules(TrustAudience.Team);

        Assert.NotNull(rules);
        Assert.Contains("Operating Rules", rules);
        Assert.DoesNotContain("Deployment Mission and Operating Playbook", rules);
    }

    [Fact]
    public void Unreadable_deployment_playbook_is_not_silently_skipped()
    {
        File.WriteAllText(_paths.AgentsPath, "Mission");
        using var locked = new FileStream(
            _paths.AgentsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Throws<IOException>(() => _provider.GetSystemPrompt(TrustAudience.Team));
    }
}
