using System.Reflection;
using System.Text.RegularExpressions;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for configuring agent identity (name, communication style, user profile, webhook, workspaces).
/// 6 sub-steps: agent name → comm style → user name → timezone → workspaces directory → webhook URL.
/// </summary>
public sealed class IdentityStepViewModel : IWizardStepViewModel
{
    private int _currentSubStep;
    private int _highWaterSubStep;
    private WizardContext? _context;

    public string StepId => "identity";
    public string DisplayTitle => "Identity";

    // ── State ──
    public string AgentName { get; set; } = "Netclaw";
    public string? CommunicationStyle { get; set; }
    public string? UserName { get; set; }
    public string UserTimezone { get; set; } = TimeZoneInfo.Local.Id;
    public string WorkspacesDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".netclaw", "workspaces");
    public string? WebhookUrl { get; set; }

    public bool IsApplicable(WizardContext context) => true;

    public int CurrentSubStep => _currentSubStep;
    public int SubStepCount => 6;

    public string GetHelpText() => _currentSubStep switch
    {
        0 => "  Give your assistant a name, or keep the default.",
        1 => "  How should your assistant communicate?",
        2 => "  So your assistant knows what to call you.",
        3 => "  Used for time-aware responses and scheduling.",
        4 => "  Where your agent stores and discovers project workspaces. Press Enter to keep the default.",
        5 => "  Optional. Receive alerts when MCP servers disconnect or LLM providers fail. Press Enter to skip.",
        _ => ""
    };

    public bool TryAdvance()
    {
        if (_currentSubStep < SubStepCount - 1)
        {
            _currentSubStep++;
            _highWaterSubStep = _currentSubStep;
            return true;
        }
        return false; // step complete
    }

    public bool TryGoBack()
    {
        if (_currentSubStep > 0)
        {
            _currentSubStep--;
            return true;
        }
        return false;
    }

    public void OnEnter(WizardContext context, NavigationDirection direction)
    {
        _context = context;
        if (direction == NavigationDirection.Forward)
            _currentSubStep = 0;
        else if (direction == NavigationDirection.Back)
            _currentSubStep = _highWaterSubStep;
    }

    public void OnLeave() { }

    public void ContributeConfig(WizardConfigBuilder builder)
    {
        builder.Identity = new IdentityConfigSection
        {
            AgentName = AgentName,
            CommunicationStyle = CommunicationStyle ?? "Concise & casual",
            UserName = UserName,
            UserTimezone = UserTimezone
        };

        builder.Workspaces = new WorkspacesConfigSection
        {
            Directory = WorkspacesDirectory
        };

        if (!string.IsNullOrWhiteSpace(WebhookUrl))
        {
            builder.Notifications = new NotificationsConfigSection
            {
                WebhookUrl = WebhookUrl
            };
        }
    }

    public void ContributeSecrets(WizardSecretsBuilder builder) { }

    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
    {
        // Memory backend check (always SQLite)
        runner.Add(new HealthCheckItem("Memory backend (SQLite)", true));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Write SOUL.md, AGENTS.md, and TOOLING.md identity files. Called during config finalization.
    /// Reads templates from embedded resources and substitutes placeholders.
    /// </summary>
    public void WriteIdentityFiles(NetclawPaths paths)
    {
        var styleDescription = CommunicationStyle switch
        {
            "Concise & casual" => "Be concise and casual. Keep responses short and conversational.",
            "Concise & formal" => "Be concise and formal. Keep responses brief and professional.",
            "Detailed & casual" => "Be detailed and casual. Give thorough explanations in a friendly tone.",
            "Detailed & formal" => "Be detailed and formal. Give thorough, professional explanations.",
            _ => "Be concise and casual. Keep responses short and conversational."
        };

        var name = AgentName;
        var userName = string.IsNullOrWhiteSpace(UserName) ? "User" : UserName;
        var timezone = UserTimezone;

        var substitutions = new Dictionary<string, string>
        {
            ["{{AGENT_NAME}}"] = name,
            ["{{STYLE_DESCRIPTION}}"] = styleDescription,
            ["{{USER_NAME}}"] = userName,
            ["{{USER_TIMEZONE}}"] = timezone,
            ["{{SYSTEM_SKILLS_DIR}}"] = paths.SystemSkillsDirectory,
            ["{{IDENTITY_DIR}}"] = paths.IdentityDirectory,
            ["{{SOUL_PATH}}"] = paths.SoulPath,
            ["{{AGENTS_PATH}}"] = paths.AgentsPath,
            ["{{TOOLING_PATH}}"] = paths.ToolingPath,
            ["{{SOUL_DETAIL_DIR}}"] = paths.SoulDetailDirectory,
            ["{{AGENTS_DETAIL_DIR}}"] = paths.AgentsDetailDirectory,
            ["{{TOOLING_DETAIL_DIR}}"] = paths.ToolingDetailDirectory,
            ["{{SKILLS_DIR}}"] = paths.SkillsDirectory,
            ["{{WORKSPACES_DIR}}"] = WorkspacesDirectory
        };

        File.WriteAllText(paths.SoulPath, SubstitutePlaceholders(
            ReadEmbeddedTemplate("SOUL.template.md"), substitutions));

        File.WriteAllText(paths.AgentsPath, SubstitutePlaceholders(
            ReadEmbeddedTemplate("AGENTS.template.md"), substitutions));

        File.WriteAllText(paths.ToolingPath, SubstitutePlaceholders(
            ReadEmbeddedTemplate("TOOLING.template.md"), substitutions));
    }

    private static string ReadEmbeddedTemplate(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"Netclaw.Cli.Resources.identity.{fileName}";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static readonly Regex PlaceholderPattern = new(@"\{\{[A-Z_]+\}\}", RegexOptions.Compiled);

    private static string SubstitutePlaceholders(string template, Dictionary<string, string> substitutions)
    {
        // Single-pass replacement — one allocation for the result instead of N intermediate strings.
        return PlaceholderPattern.Replace(template, match =>
            substitutions.TryGetValue(match.Value, out var value) ? value : match.Value);
    }

    /// <summary>
    /// Builds the initial onboarding chat message for the first conversation.
    /// </summary>
    public string BuildOnboardingTrigger(NetclawPaths paths)
    {
        var userName = string.IsNullOrWhiteSpace(UserName) ? "User" : UserName;
        var commStyle = CommunicationStyle ?? "Concise & casual";
        var soulPath = paths.SoulPath;

        return $"""
            I just finished setting up. My name is {userName} and I chose "{commStyle}" as my communication style.

            This is our first conversation. I'd like you to get to know me so you can be more helpful. Please:

            1. Introduce yourself briefly
            2. Ask me what I'd primarily like to use you for
            3. Ask if there's anything else you should know about me — my background, how I work, tools I use, preferences, etc.
            4. After our conversation, update my profile in SOUL.md ({soulPath}) with what you've learned. Use file_read to check current content first, then file_write to update it. Keep the existing structure but enrich it with the details from our conversation.

            Keep it natural and conversational — don't ask everything at once.
            """;
    }

    /// <summary>
    /// Seeds default subagent definition files to the agents directory.
    /// Each agent is a single <c>.md</c> file with YAML frontmatter, matching
    /// the <c>SKILL.md</c> pattern and the Claude Code / OpenCode convention.
    /// Does not overwrite existing files so operator customizations are preserved.
    /// </summary>
    public void SeedBuiltInAgents(NetclawPaths paths)
    {
        var agentsDir = paths.AgentsDirectory;
        Directory.CreateDirectory(agentsDir);

        SeedAgentFile(agentsDir, "research-assistant.md", """
            ---
            name: research-assistant
            description: Deep web research with search and citation
            tools: [web_search, web_fetch, file_read, attach_file]
            modelRole: Compaction
            timeoutSeconds: 120
            visibility: user-facing
            ---

            You are a research assistant. Your job is to help the user by searching the
            web, gathering information from multiple sources, and synthesizing findings
            into clear, well-organized summaries.

            ## Guidelines

            - Search for information using web_search, then fetch relevant pages with web_fetch.
            - Cross-reference multiple sources when possible.
            - Always cite your sources with URLs.
            - Use file_read to inspect local reference material when needed.
            - Use attach_file when the parent session needs to deliver an existing file.
            - Be thorough but concise — focus on facts and actionable information.
            - Use markdown formatting for structure (headers, lists, code blocks).
            - If a search returns no useful results, say so rather than guessing.
            """);

        SeedAgentFile(agentsDir, "code-analyst.md", """
            ---
            name: code-analyst
            description: Analyze code, run commands, and review files
            tools: [file_read]
            modelRole: Compaction
            timeoutSeconds: 120
            visibility: user-facing
            ---

            You are a code analyst. Your job is to read source code, run build and test
            commands, and provide clear analysis of code quality, structure, and issues.

            ## Guidelines

            - Read files with file_read to understand code structure.
            - Report findings with file paths and line numbers.
            - Focus on actionable observations — bugs, performance issues, design concerns.
            - Use markdown formatting with code blocks for examples.
            - Do not modify code or run commands directly; return analysis for the parent session to act on.
            """);

        SeedAgentFile(agentsDir, "summarizer.md", """
            ---
            name: summarizer
            description: Summarize documents and content concisely
            tools: [file_read]
            modelRole: Compaction
            timeoutSeconds: 60
            visibility: user-facing
            ---

            You are a summarizer. Your job is to read content and produce concise,
            structured summaries that capture the essential information.

            ## Guidelines

            - Focus on key facts, decisions, and action items.
            - Use bullet points and headers for scannable structure.
            - Preserve important details like names, dates, numbers, and links.
            - Omit filler, repetition, and low-signal content.
            - Keep summaries under 500 words unless the source material is very long.
            - If summarizing code, highlight the main purpose, public API, and key patterns.
            """);
    }

    private static void SeedAgentFile(string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        if (File.Exists(path))
            return; // Do not overwrite operator customizations

        File.WriteAllText(path, content);
    }

    public void Dispose() { }
}
