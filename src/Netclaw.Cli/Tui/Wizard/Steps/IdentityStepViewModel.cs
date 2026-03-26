using Netclaw.Configuration;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Wizard step for configuring agent identity (name, communication style, user profile, webhook).
/// 5 sub-steps: agent name → comm style → user name → timezone → webhook URL.
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
    public string? WebhookUrl { get; set; }

    public bool IsApplicable(WizardContext context) => true;

    public int CurrentSubStep => _currentSubStep;
    public int SubStepCount => 5;

    public string GetHelpText() => _currentSubStep switch
    {
        0 => "  Give your assistant a name, or keep the default.",
        1 => "  How should your assistant communicate?",
        2 => "  So your assistant knows what to call you.",
        3 => "  Used for time-aware responses and scheduling.",
        4 => "  Optional. Receive alerts when MCP servers disconnect or LLM providers fail. Press Enter to skip.",
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
        if (direction == NavigationDirection.Back)
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
    /// Write SOUL.md and AGENTS.md identity files. Called during config finalization.
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
            """);
    }

    public void Dispose() { }
}
