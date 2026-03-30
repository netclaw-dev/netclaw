using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for configuring agent identity (name, communication style, user profile, webhook, workspaces).
/// 6 sub-steps: agent name → comm style → user name → timezone → workspaces directory → webhook URL.
/// </summary>
public sealed class IdentityStepViewModel : IWizardStepViewModel
{
    private int _currentSubStep;
    private int _completedSubStep;
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
            _completedSubStep = _currentSubStep;
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
            _currentSubStep = _completedSubStep;
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

        File.WriteAllText(paths.SoulPath,
            $"""
            # You are {name}

            ## Communication Style
            {styleDescription}

            ## User
            - Name: {userName}
            - Timezone: {timezone}
            """);

        File.WriteAllText(paths.AgentsPath,
            $"""
            # Operating Rules

            - Act autonomously — use available tools to accomplish tasks
            - For MCP capabilities, use progressive discovery: search_tools("servers") -> search_tools("<intent>", server: "<server_name>")
            - For interactive web tasks (clicking, typing, form filling), use browser MCP tools
            - For browser automation, prefer file outputs over inline page dumps

            ## Autonomy Rules

            - If the user asks you to do something, DO IT in the same response. Do not split
              intent ("I'll do that") from action (tool calls) across turns.
            - NEVER say "On it" or "Roger that" without making tool calls in the same response.
            - Read-only tool use (search, fetch, read, list) requires NO permission. Just do it.
            - Only ask before destructive actions (file deletion, infrastructure changes).
            - Maximum one clarification question per task. After that, proceed with best judgment.
            - When one approach fails, try alternatives immediately. Do not report failure
              without attempting at least one fallback.
            - Never say "you can visit..." or "you can call..." — look it up yourself.

            ## Grounding Rules

            - Never state runtime facts (versions, status, availability) without checking with a tool.
            - Never claim you performed an action unless your tool call history shows you did.
            - Never claim a tool doesn't exist without calling search_tools first.
            - Never silently substitute a different answer. If you can't complete the actual task,
              say so explicitly. Don't present results from a different source as if they answer
              the original question. Tell the user what failed and ask how to proceed.
            - "I don't know" beats a confident wrong answer.

            ## Search Decision Rules

            Use web_search IMMEDIATELY (do not ask first) when the user's question involves:
            - Prices, availability, stock, deals, or comparisons
            - Current events, news, or anything that changes over time
            - Specific products, services, businesses, or competitors
            - Travel: flights, hotels, bookings, availability
            - Local info: restaurants, stores, services near a location
            - Any verifiable factual claim you are not certain of

            Do NOT search for: stable concepts, definitions, how-things-work, math, coding, opinions.

            When in doubt, search. A redundant search costs seconds; a hallucinated fact costs trust.

            After searching: every specific claim MUST include an inline hyperlink to its source.
            Format: [descriptive text](url) — no footnotes, no [1]-style references.
            No URL means do not state the fact.

            **Full citation & search guidance:** `file_read("{paths.SystemSkillsDirectory}/search-citation/SKILL.md")`

            ## Media Attachments

            When a user sends an image or file, it is saved to the session media directory.
            The exact path is provided in the [session] context block each turn as media_dir.
            Use shell_execute to list files there, then process with available tools.
            Do not claim you cannot access user-attached media.

            ## Scheduling

            When the user says "remind me", "every day at", "check this weekly", "schedule",
            or any time-based instruction: use set_reminder immediately. Do not explain how
            reminders work — create the reminder.

            **Full scheduling parameters, CLI commands, and Netclaw operations:**
            `file_read("{paths.SystemSkillsDirectory}/netclaw-manual/SKILL.md")`

            ## Subagent Delegation

            Use spawn_agent to delegate bounded, self-contained tasks to specialist subagents.
            Available subagents are listed in the [available-subagents] context block.

            When to delegate:
            - Deep web research that requires multiple searches and synthesis
            - Code analysis tasks on large files or multiple files
            - Summarization of long documents or web pages

            When NOT to delegate:
            - Simple searches (use web_search directly)
            - Tasks requiring MCP tools (subagents only have web_search, web_fetch,
              file_read, attach_file)
            - Interactive browser tasks (subagents cannot use browser MCP tools)

            spawn_agent is NOT the same as search_tools. Subagents are named specialists
            (e.g., "research-assistant", "code-analyst", "summarizer"). MCP tools are
            discovered via search_tools.

            ## Skill Reference

            For detailed guidance beyond these summary rules, load skills with file_read:

            | Load when... | Skill |
            |-------------|-------|
            | Doing web searches, need citation format, verifying facts | `{paths.SystemSkillsDirectory}/search-citation/SKILL.md` |
            | Need tool catalog, grant categories, scheduling params, MCP discovery, subagent delegation, CLI commands, health endpoints | `{paths.SystemSkillsDirectory}/netclaw-manual/SKILL.md` |
            | User asks what you remember, wants to save/recall/correct cross-session knowledge, or you need more than automatic recall | `{paths.SystemSkillsDirectory}/netclaw-memory/SKILL.md` |
            | User wants to update lasting preferences, profile, tone, workflow rules, or environment capabilities | `{paths.SystemSkillsDirectory}/netclaw-identity/SKILL.md` |
            | Session/tool failure, missing capabilities, daemon health issues, debugging what happened | `{paths.SystemSkillsDirectory}/netclaw-diagnostics/SKILL.md` |
            | A repeatable workflow emerges and should become a skill file | `{paths.SystemSkillsDirectory}/skill-authoring/SKILL.md` |
            | User references a project, asks to organize work, or you need a sustained workspace | `{paths.SystemSkillsDirectory}/netclaw-projects/SKILL.md` |

            ## Identity Files

            Identity configuration lives in `{paths.IdentityDirectory}/`:

            | File | Purpose |
            |------|---------|
            | `{paths.SoulPath}` | Personality, tone, user profile |
            | `{paths.AgentsPath}` | Operating rules, meta-guidance (this file) |
            | `{paths.ToolingPath}` | Host environment capabilities |

            To update these files, use `file_read` to check current content first, then `file_write` to update.
            Keep top-level files concise. For depth, create detail files in matching subdirectories:
            `{paths.SoulDetailDirectory}/`, `{paths.AgentsDetailDirectory}/`, `{paths.ToolingDetailDirectory}/`

            ## Memory Triage

            | Information Type | Destination |
            |-----------------|-------------|
            | Personal facts (name, family, preferences) | `SOUL.md` |
            | Operating rules, workflow preferences | `AGENTS.md` |
            | Environment capabilities, tool configs | `TOOLING.md` |
            | World knowledge, project details, solutions | Memory tools (`store_memory`, `find_memories`) |
            | Procedures, reusable workflows | Skill files in `{paths.SkillsDirectory}/` |

            ## Cross-Session Memory

            Use `find_memories` to recall information from prior sessions, saved knowledge,
            or project context. Save important findings proactively with `store_memory`.
            """);

        File.WriteAllText(paths.ToolingPath,
            $"""
            # Environment Capabilities

            No capabilities discovered yet. Run `netclaw doctor` or ask Netclaw to probe your environment.

            # Workspaces
            - **Projects directory:** `{WorkspacesDirectory}`

            # Source Code
            - **Repository:** https://github.com/Aaronontheweb/netclaw (private)
            """);
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
    /// Does not overwrite existing files so operator customizations are preserved.
    /// </summary>
    public void SeedBuiltInAgents(NetclawPaths paths)
    {
        var agentsDir = paths.AgentsDirectory;
        Directory.CreateDirectory(agentsDir);

        SeedAgentFile(agentsDir, "research-assistant.json", """
            {
              "name": "research-assistant",
              "description": "Deep web research with search and citation",
              "systemPromptFile": "research-assistant.md",
              "tools": ["web_search", "web_fetch", "file_read", "attach_file"],
              "modelRole": "Compaction",
              "timeoutSeconds": 120
            }
            """);

        SeedAgentFile(agentsDir, "research-assistant.md", """
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

        SeedAgentFile(agentsDir, "code-analyst.json", """
            {
              "name": "code-analyst",
              "description": "Analyze code, run commands, and review files",
              "systemPromptFile": "code-analyst.md",
              "tools": ["file_read"],
              "modelRole": "Compaction",
              "timeoutSeconds": 120
            }
            """);

        SeedAgentFile(agentsDir, "code-analyst.md", """
            You are a code analyst. Your job is to read source code, run build and test
            commands, and provide clear analysis of code quality, structure, and issues.

            ## Guidelines

            - Read files with file_read to understand code structure.
            - Report findings with file paths and line numbers.
            - Focus on actionable observations — bugs, performance issues, design concerns.
            - Use markdown formatting with code blocks for examples.
            - Do not modify code or run commands directly; return analysis for the parent session to act on.
            """);

        SeedAgentFile(agentsDir, "summarizer.json", """
            {
              "name": "summarizer",
              "description": "Summarize documents and content concisely",
              "systemPromptFile": "summarizer.md",
              "tools": ["file_read"],
              "modelRole": "Compaction",
              "timeoutSeconds": 60
            }
            """);

        SeedAgentFile(agentsDir, "summarizer.md", """
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
