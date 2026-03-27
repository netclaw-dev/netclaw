using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Cli.Config;
using Netclaw.Cli.Mcp;
using Netclaw.Configuration;
using Netclaw.Configuration.Secrets;

namespace Netclaw.Cli.Tui.Wizard;

/// <summary>
/// Typed config builder that replaces the manual dictionary assembly in WriteConfig().
/// Each step contributes its section via <see cref="IWizardStepViewModel.ContributeConfig"/>.
/// </summary>
public sealed class WizardConfigBuilder
{
    private readonly NetclawPaths _paths;

    public WizardConfigBuilder(NetclawPaths paths)
    {
        _paths = paths;
    }

    // ── Typed sections populated by steps ──

    public ProviderConfigSection? Provider { get; set; }
    public ModelConfigSection? Model { get; set; }
    public SlackConfigSection? Slack { get; set; }
    public SecurityConfigSection Security { get; set; } = new();
    public SearchConfigSection? Search { get; set; }
    public ToolConfig? Tools { get; set; }
    public BrowserAutomationConfigSection? BrowserAutomation { get; set; }
    public IdentityConfigSection? Identity { get; set; }
    public NotificationsConfigSection? Notifications { get; set; }

    /// <summary>
    /// Assemble the typed sections into netclaw.json and write it.
    /// </summary>
    public void WriteConfigFile()
    {
        _paths.EnsureDirectoriesExist();
        var config = BuildConfigDictionary();

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(config, jsonOptions));
    }

    /// <summary>
    /// Assemble the non-secret config dictionary from typed sections.
    /// </summary>
    internal Dictionary<string, object> BuildConfigDictionary()
    {
        var config = new Dictionary<string, object>
        {
            ["configVersion"] = 1
        };

        // Provider section
        if (Provider is not null)
        {
            var providerEntry = new Dictionary<string, object>
            {
                ["Type"] = Provider.TypeKey
            };

            if (Provider.AuthMethod != AuthMethod.None)
                providerEntry["AuthMethod"] = Provider.AuthMethod.ToString();

            if (!string.IsNullOrWhiteSpace(Provider.Endpoint))
                providerEntry["Endpoint"] = Provider.Endpoint;

            config["Providers"] = new Dictionary<string, object>
            {
                [Provider.TypeKey] = providerEntry
            };
        }

        // Models section
        if (Model is not null)
        {
            var modelEntry = new Dictionary<string, object>
            {
                ["Provider"] = Model.Provider
            };

            if (!string.IsNullOrWhiteSpace(Model.ModelId))
                modelEntry["ModelId"] = Model.ModelId;

            config["Models"] = new Dictionary<string, object>
            {
                ["Main"] = modelEntry
            };
        }

        // Slack section
        if (Slack is { Enabled: true })
        {
            var slackSection = new Dictionary<string, object>
            {
                ["Enabled"] = true,
                ["SocketMode"] = true
            };

            if (Slack.AllowedChannelIds is { Count: > 0 })
            {
                slackSection["AllowedChannelIds"] = Slack.AllowedChannelIds.ToArray();
                slackSection["DefaultChannelId"] = Slack.AllowedChannelIds[0];
            }

            if (Slack.AllowDirectMessages)
                slackSection["AllowDirectMessages"] = true;

            if (Slack.AllowedUserIds is { Count: > 0 })
                slackSection["AllowedUserIds"] = Slack.AllowedUserIds.ToArray();

            if (Slack.ChannelAudiences is { Count: > 0 })
                slackSection["ChannelAudiences"] = new Dictionary<string, string>(Slack.ChannelAudiences);

            config["Slack"] = slackSection;
        }

        // Search section
        if (Search is not null && Search.Backend != SearchBackend.DuckDuckGo)
        {
            var searchSection = new Dictionary<string, object>
            {
                ["Backend"] = Search.Backend.ToWireValue()
            };

            if (Search.Backend == SearchBackend.SearXng && !string.IsNullOrWhiteSpace(Search.SearXngEndpoint))
                searchSection["SearXngEndpoint"] = Search.SearXngEndpoint;

            config["Search"] = searchSection;
        }

        // Security section
        config["Security"] = new Dictionary<string, object>
        {
            ["DeploymentPosture"] = Security.DeploymentPosture.ToString(),
            ["ShellExecutionMode"] = Security.ShellExecutionMode.ToString(),
            ["StrictDefaults"] = true
        };

        // Tools section
        if (Tools is not null)
            config["Tools"] = Tools;

        // Skill sync
        config["SkillSync"] = new Dictionary<string, object>
        {
            ["DisableSystemSkillSync"] = false
        };

        // MCP servers (browser automation)
        if (BrowserAutomation is { Enabled: true })
        {
            var (profileName, entry) = BrowserAutomationMcpProfiles.Create(BrowserAutomation.Backend);
            config["McpServers"] = new Dictionary<string, object>
            {
                [profileName] = new Dictionary<string, object?>
                {
                    ["Transport"] = entry.Transport,
                    ["Command"] = entry.Command,
                    ["Arguments"] = entry.Arguments,
                    ["EnvironmentVariables"] = entry.EnvironmentVariables,
                    ["Enabled"] = entry.Enabled,
                    ["GrantCategory"] = entry.GrantCategory
                }
            };
        }

        // Notifications
        if (Notifications is { WebhookUrl: not null })
        {
            config["Notifications"] = new Dictionary<string, object>
            {
                ["Webhooks"] = new object[]
                {
                    new Dictionary<string, object> { ["Url"] = Notifications.WebhookUrl }
                }
            };
        }

        return config;
    }
}

/// <summary>
/// Typed secrets builder for non-provider secrets (Slack tokens, search API keys, etc.).
/// </summary>
public sealed class WizardSecretsBuilder
{
    private readonly NetclawPaths _paths;
    private readonly Dictionary<string, object> _secrets = new();

    public WizardSecretsBuilder(NetclawPaths paths)
    {
        _paths = paths;
    }

    /// <summary>Add a section to the secrets file.</summary>
    public void AddSection(string key, Dictionary<string, object> section)
    {
        _secrets[key] = section;
    }

    /// <summary>Write secrets.json if any secrets were contributed.</summary>
    public void WriteSecretsFile()
    {
        if (_secrets.Count == 0)
            return;

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        SecretsFileWriter.Write(_paths.SecretsPath, _secrets,
            options: jsonOptions, protector: SensitiveStringTypeConverter.Protector);
    }
}

// ── Typed config section records ──

public sealed class ProviderConfigSection
{
    public required string TypeKey { get; init; }
    public AuthMethod AuthMethod { get; init; } = AuthMethod.None;
    public string? Endpoint { get; init; }
}

public sealed class ModelConfigSection
{
    public required string Provider { get; init; }
    public string? ModelId { get; init; }
}

public sealed class SlackConfigSection
{
    public bool Enabled { get; init; }
    public List<string>? AllowedChannelIds { get; init; }
    public bool AllowDirectMessages { get; init; }
    public List<string>? AllowedUserIds { get; init; }
    public Dictionary<string, string>? ChannelAudiences { get; init; }
}

public sealed class SecurityConfigSection
{
    public DeploymentPosture DeploymentPosture { get; set; } = DeploymentPosture.Personal;
    public ShellExecutionMode ShellExecutionMode { get; set; } = ShellExecutionMode.HostAllowed;
}

public sealed class SearchConfigSection
{
    public required SearchBackend Backend { get; init; }
    public string? SearXngEndpoint { get; init; }
}

public sealed class BrowserAutomationConfigSection
{
    public bool Enabled { get; init; }
    public required BrowserAutomationBackend Backend { get; init; }
}

public sealed class NotificationsConfigSection
{
    public string? WebhookUrl { get; init; }
}

public sealed class IdentityConfigSection
{
    public required string AgentName { get; init; }
    public required string CommunicationStyle { get; init; }
    public string? UserName { get; init; }
    public required string UserTimezone { get; init; }
}
