// -----------------------------------------------------------------------
// <copyright file="IdentityStepViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Cli.Config;
using Netclaw.Cli.Tui.Sections;
using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for configuring agent identity (name, communication style, user profile).
/// 4 sub-steps: agent name → comm style → user name → timezone.
/// Workspaces directory and notification webhooks are post-install settings owned by
/// <c>netclaw config</c> (Workspaces Directory; Telemetry &amp; Alerting → outbound webhooks),
/// so the first-run wizard does not collect them.
/// </summary>
[NoDoctorChecks("Identity is synthetic and init-owned. Doctor coverage applies to the underlying config and generated identity files instead.")]
public sealed class IdentityStepViewModel : IWizardStepViewModel, ISectionEditor
{
    private int _currentSubStep;
    private int _highWaterSubStep;
    private WizardContext? _context;

    public string StepId => WizardStepIds.Identity;
    public string DisplayTitle => "Identity";
    public string SectionId => StepId;
    public string DisplayName => DisplayTitle;
    public string? Category => null;
    public bool ShowInMenu => false;
    public IReadOnlyList<string> RelevantDoctorChecks => [];

    // ── State ──
    public string AgentName { get; set; } = "Netclaw";
    public string? CommunicationStyle { get; set; }
    public string? UserName { get; set; }
    public string UserTimezone { get; set; } = TimeZoneInfo.Local.Id;

    public bool IsApplicable(WizardContext context) => true;

    public int CurrentSubStep => _currentSubStep;
    public int SubStepCount => 4;

    public string GetHelpText() => _currentSubStep switch
    {
        0 => "  Give your assistant a name, or keep the default.",
        1 => "  How should your assistant communicate?",
        2 => "  So your assistant knows what to call you.",
        3 => "  Used for time-aware responses and scheduling.",
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
        PrefillFromExistingConfig(context);
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
    }

    public void ContributeSecrets(WizardSecretsBuilder builder) { }

    public Task ContributeHealthChecksAsync(HealthCheckRunner runner, CancellationToken ct)
    {
        // Memory backend check (always SQLite)
        runner.Add(new HealthCheckItem("Memory backend (SQLite)", true));
        return Task.CompletedTask;
    }

    public SectionStatus GetStatus(WizardContext context)
        => HasPersistedIdentity(context) ? SectionStatus.Configured : SectionStatus.NotConfigured;

    public string Summary(WizardContext context)
    {
        var name = !string.IsNullOrWhiteSpace(AgentName) ? AgentName : ReadString(context, "Identity.AgentName");
        var timezone = !string.IsNullOrWhiteSpace(UserTimezone) ? UserTimezone : ReadString(context, "Identity.UserTimezone");
        return string.IsNullOrWhiteSpace(name) ? "Not configured" : string.IsNullOrWhiteSpace(timezone) ? name : $"{name} ({timezone})";
    }

    public IWizardStepViewModel CreateEditor(IServiceProvider services)
        => ActivatorUtilities.CreateInstance<IdentityStepViewModel>(services);

    public SectionContribution BuildContribution(IWizardStepViewModel editor)
    {
        var vm = (IdentityStepViewModel)editor;
        return new SectionContribution(
        [
            new SectionFieldAction("Identity.AgentName", SectionFieldActionKind.Set, vm.AgentName),
            new SectionFieldAction("Identity.CommunicationStyle", SectionFieldActionKind.Set, vm.CommunicationStyle ?? "Concise & casual"),
            string.IsNullOrWhiteSpace(vm.UserName)
                ? new SectionFieldAction("Identity.UserName", SectionFieldActionKind.Delete)
                : new SectionFieldAction("Identity.UserName", SectionFieldActionKind.Set, vm.UserName),
            new SectionFieldAction("Identity.UserTimezone", SectionFieldActionKind.Set, vm.UserTimezone)
        ]);
    }

    /// <summary>
    /// Write SOUL.md and TOOLING.md identity files and seed the deployment playbook.
    /// Reads templates from embedded resources and substitutes placeholders.
    /// Existing AGENTS.md content is operator-owned and is never overwritten.
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
            // Workspaces dir is no longer collected in init; use the resolved default
            // (configured Workspaces.Directory or {BasePath}/workspaces) for the templates.
            ["{{WORKSPACES_DIR}}"] = paths.WorkspacesDirectory
        };

        File.WriteAllText(paths.SoulPath, SubstitutePlaceholders(
            ReadEmbeddedTemplate("SOUL.template.md"), substitutions));

        File.WriteAllText(paths.ToolingPath, SubstitutePlaceholders(
            ReadEmbeddedTemplate("TOOLING.template.md"), substitutions));

        if (!File.Exists(paths.AgentsPath))
        {
            File.WriteAllText(paths.AgentsPath, SubstitutePlaceholders(
                ReadEmbeddedTemplate("AGENTS.template.md"), substitutions));
        }
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
        var agentsPath = paths.AgentsPath;

        return $"""
            I just finished setting up. My name is {userName} and I chose "{commStyle}" as my communication style.

            This is our first conversation. I'd like you to learn both who I am and what mission this deployment should perform. Please:

            1. Introduce yourself briefly
            2. Ask me what I'd primarily like to use you for
            3. Ask what successful work looks like, which workflows recur, which skills you should use, when you should delegate, and what mistakes or quality problems you must catch before delivering work
            4. Ask what else you should know about me — my background, how I work, tools I use, and communication preferences
            5. Keep operator and personality context in SOUL.md ({soulPath}). Keep the deployment mission, workflows, skill-selection rules, delegation practices, and review gates in AGENTS.md ({agentsPath}). Never put secrets or audience-private data in AGENTS.md.
            6. When you understand the mission, summarize the playbook you propose and ask me to confirm it before writing either file.
            7. After I confirm, use file_read on both files, preserve their existing structure and useful content, then use file_write to update them. Tell me the new playbook will apply on my next message.

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
            - Return each authorized file path that the parent session should deliver.
            - Be thorough but concise — focus on facts and actionable information.
            - Use markdown formatting for structure (headers, lists, code blocks).
            - If a search returns no useful results, say so rather than guessing.
            """);

        SeedAgentFile(agentsDir, "code-analyst.md", """
            ---
            name: code-analyst
            description: Analyze code, run commands, and review files
            modelRole: Compaction
            timeoutSeconds: 120
            visibility: user-facing
            ---

            You are a code analyst. Your job is to read source code, run build and test
            commands, and provide clear analysis of code quality, structure, and issues.

            ## Guidelines

            - Read files with file_read to understand code structure.
            - Use shell_execute to run git, build, and test commands as needed.
            - Report findings with file paths and line numbers.
            - Focus on actionable observations — bugs, performance issues, design concerns.
            - Use markdown formatting with code blocks for examples.
            """);

        SeedAgentFile(agentsDir, "summarizer.md", """
            ---
            name: summarizer
            description: Summarize documents and content concisely
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

    private void PrefillFromExistingConfig(WizardContext context)
    {
        if (context.ExistingConfig is null)
            return;

        AgentName = ReadString(context, "Identity.AgentName") ?? AgentName;
        CommunicationStyle ??= ReadString(context, "Identity.CommunicationStyle");
        UserName ??= ReadString(context, "Identity.UserName");
        UserTimezone = ReadString(context, "Identity.UserTimezone") ?? UserTimezone;
    }

    private static bool HasPersistedIdentity(WizardContext context)
        => !string.IsNullOrWhiteSpace(ReadString(context, "Identity.AgentName"));

    private static string? ReadString(WizardContext context, string path)
        => context.ExistingConfig is not null
           && ConfigFileHelper.TryGetPathValue(context.ExistingConfig, path, out var value)
            ? value as string
            : null;

    public void Dispose() { }
}
