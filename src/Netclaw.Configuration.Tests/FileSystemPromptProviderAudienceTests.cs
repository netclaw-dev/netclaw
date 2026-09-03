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

    [Theory]
    [InlineData(TrustAudience.Public)]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Personal)]
    public void Every_audience_receives_the_tool_rationale_contract(TrustAudience audience)
    {
        var prompt = _provider.GetSystemPrompt(audience);

        Assert.Contains("Every tool call must include a non-empty `_rationale` string.", prompt);
        Assert.Contains("Apply this rule to each parallel call", prompt);
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
    public void Personal_rules_apply_directory_order_and_bound_failed_project_scope_recovery()
    {
        var prompt = _provider.GetSystemPrompt(TrustAudience.Personal);

        var projectIndex = prompt.IndexOf("For declared-project work, omit `WorkingDirectory`", StringComparison.Ordinal);
        var childIndex = prompt.IndexOf("For one call in a named child directory", StringComparison.Ordinal);
        var scratchIndex = prompt.IndexOf("Use `session_dir` for disposable writable work outside a project", StringComparison.Ordinal);
        var transitionIndex = prompt.IndexOf("Use an inline directory change only when", StringComparison.Ordinal);
        Assert.True(projectIndex >= 0);
        Assert.True(childIndex > projectIndex);
        Assert.True(scratchIndex > childIndex);
        Assert.True(transitionIndex > scratchIndex);
        Assert.Contains("Program-specific directory options do not replace it", prompt);
        Assert.Contains("Keep the project root unless the user requests", prompt);
        Assert.Contains("A denied child-directory call does not permit a project change", prompt);
        Assert.Contains("Path arguments give the approval gate an exact candidate scope", prompt);
        Assert.Contains("safe-space root", prompt);
        Assert.DoesNotContain("path argument IS the declaration", prompt);
        Assert.Contains("before the first project tool call", prompt);
        Assert.Contains("The work needs a shell or file tool", prompt);
        Assert.Contains("Do not probe a named project path first", prompt);
        Assert.Contains("user-provided fallback before other tools", prompt);
        Assert.Contains("Use the task's first project path exactly", prompt);
        Assert.Contains("Do not substitute its parent", prompt);
        Assert.Contains("Do not repeat", prompt);
        Assert.Contains("`project_dir` already names the correct project", prompt);
        Assert.Contains("Only `Tool execution deferred:` permits one scope correction", prompt);
        Assert.Contains("Do not call `set_working_directory` to evade an access denial during the same user turn", prompt);
        Assert.Contains("correct an evident path error once", prompt);
        Assert.Contains("preserve the current scope and report the block", prompt);
        Assert.DoesNotContain("Recovery from a denied shell call", prompt);
        Assert.DoesNotContain("correct the path and retry the tool", prompt);
    }

    [Theory]
    [InlineData(TrustAudience.Team)]
    [InlineData(TrustAudience.Personal)]
    public void Trusted_audiences_prefer_file_tools_for_known_content(TrustAudience audience)
    {
        var prompt = _provider.GetSystemPrompt(audience);

        Assert.Contains("use `file_read` for a known local file read", prompt);
        Assert.Contains("use `file_list` for a known local directory listing", prompt);
        Assert.Contains("use `file_write` or `file_edit` for a known local file change", prompt);
        Assert.Contains("use `shell_execute` for local search", prompt);
        Assert.Contains("use `web_search` for external discovery", prompt);
        Assert.Contains("Do not substitute shell commands", prompt);
        Assert.Contains("Do not delegate a known file operation", prompt);
        Assert.Contains("do not use shell only to verify", prompt);
        Assert.Contains("do not attempt a shell redirect first", prompt);
        Assert.Contains("Start with the smallest single shell operation", prompt);
        Assert.Contains("Use one operation per call", prompt);
        Assert.Contains("Keep independent searches and diagnostics separate", prompt);
        Assert.Contains("do not join them with separators or labels", prompt);
        Assert.Contains("Add a pipeline only when the requested result requires it", prompt);
        Assert.Contains("disposable writable work outside a project", prompt);
        Assert.Contains("do not substitute platform temporary storage", prompt);
        Assert.Contains("If approval is required but no interactive requester is available", prompt);
        Assert.Contains("After an access denial, do not retry that call during the same user turn", prompt);
        Assert.Contains("A later explicit user request can start a new call", prompt);
        Assert.Contains("Apply one `Tool execution deferred:` correction unchanged", prompt);
        Assert.Contains("Use `load_tool` directly for a known exact tool name", prompt);
        Assert.Contains("Use `search_tools` when the capability is known", prompt);
    }

    [Fact]
    public void Public_audience_omits_shell_selection_guidance()
    {
        var prompt = _provider.GetSystemPrompt(TrustAudience.Public);

        Assert.DoesNotContain("use `shell_execute` for local search", prompt);
        Assert.DoesNotContain("session_dir", prompt);
        Assert.DoesNotContain("Keep shell approval friction bounded", prompt);
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
