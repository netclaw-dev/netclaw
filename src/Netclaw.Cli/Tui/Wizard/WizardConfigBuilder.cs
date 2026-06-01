// -----------------------------------------------------------------------
// <copyright file="WizardConfigBuilder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Text.Json.Nodes;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Cli.Mcp;
using Netclaw.Cli.Secrets;
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
    public DiscordConfigSection? Discord { get; set; }
    public MattermostConfigSection? Mattermost { get; set; }
    public SecurityConfigSection Security { get; set; } = new();
    public SearchConfigSection? Search { get; set; }
    public ToolConfig? Tools { get; set; }
    public BrowserAutomationConfigSection? BrowserAutomation { get; set; }
    public IdentityConfigSection? Identity { get; set; }
    public WorkspacesConfigSection? Workspaces { get; set; }
    public NotificationsConfigSection? Notifications { get; set; }
    public List<ExternalSkillSource>? ExternalSkillSources { get; set; }
    public List<SkillFeedSource>? SkillFeedSources { get; set; }
    public DaemonConfigSection? Daemon { get; set; }
    public WebhooksConfigSection? Webhooks { get; set; }
    public FeatureSelectionsConfigSection? FeatureSelections { get; set; }

    /// <summary>
    /// Assemble the typed sections into netclaw.json and write it.
    /// </summary>
    public void WriteConfigFile()
    {
        _paths.EnsureDirectoriesExist();
        var config = BuildConfigDictionary();

        File.WriteAllText(_paths.NetclawConfigPath,
            JsonSerializer.Serialize(config, JsonDefaults.ConfigFile));
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

            if (Model.ContextWindow is { } contextWindow)
                modelEntry["ContextWindow"] = contextWindow;

            if (Model.Provenance is { } provenance)
                modelEntry["Provenance"] = provenance.ToString();

            if (Model.InputModalities is { } inputModalities)
                modelEntry["InputModalities"] = inputModalities.ToString();

            if (Model.OutputModalities is { } outputModalities)
                modelEntry["OutputModalities"] = outputModalities.ToString();

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

        // Discord section
        if (Discord is { Enabled: true })
        {
            var discordSection = new Dictionary<string, object>
            {
                ["Enabled"] = true
            };

            if (!string.IsNullOrWhiteSpace(Discord.DefaultChannelId))
                discordSection["DefaultChannelId"] = Discord.DefaultChannelId;

            if (Discord.AllowedChannelIds is { Count: > 0 })
            {
                discordSection["AllowedChannelIds"] = Discord.AllowedChannelIds.ToArray();

                if (!discordSection.ContainsKey("DefaultChannelId"))
                    discordSection["DefaultChannelId"] = Discord.AllowedChannelIds[0];
            }

            if (Discord.AllowDirectMessages)
                discordSection["AllowDirectMessages"] = true;

            if (Discord.AllowedUserIds is { Count: > 0 })
                discordSection["AllowedUserIds"] = Discord.AllowedUserIds.ToArray();

            if (Discord.ChannelAudiences is { Count: > 0 })
                discordSection["ChannelAudiences"] = new Dictionary<string, string>(Discord.ChannelAudiences);

            config["Discord"] = discordSection;
        }

        // Mattermost section
        if (Mattermost is { Enabled: true })
        {
            var mattermostSection = new Dictionary<string, object>
            {
                ["Enabled"] = true
            };

            if (!string.IsNullOrWhiteSpace(Mattermost.ServerUrl))
                mattermostSection["ServerUrl"] = Mattermost.ServerUrl;

            if (!string.IsNullOrWhiteSpace(Mattermost.CallbackUrl))
                mattermostSection["CallbackUrl"] = Mattermost.CallbackUrl;

            if (!string.IsNullOrWhiteSpace(Mattermost.DefaultChannelId))
                mattermostSection["DefaultChannelId"] = Mattermost.DefaultChannelId;

            if (Mattermost.AllowedChannelIds is { Count: > 0 })
            {
                mattermostSection["AllowedChannelIds"] = Mattermost.AllowedChannelIds.ToArray();

                if (!mattermostSection.ContainsKey("DefaultChannelId"))
                    mattermostSection["DefaultChannelId"] = Mattermost.AllowedChannelIds[0];
            }

            if (Mattermost.AllowDirectMessages)
                mattermostSection["AllowDirectMessages"] = true;

            if (Mattermost.AllowedUserIds is { Count: > 0 })
                mattermostSection["AllowedUserIds"] = Mattermost.AllowedUserIds.ToArray();

            if (Mattermost.ChannelAudiences is { Count: > 0 })
                mattermostSection["ChannelAudiences"] = new Dictionary<string, string>(Mattermost.ChannelAudiences);

            config["Mattermost"] = mattermostSection;
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

        // Workspaces section
        if (Workspaces is not null)
        {
            config["Workspaces"] = new Dictionary<string, object>
            {
                ["Directory"] = Workspaces.Directory
            };
        }

        // Skill sync
        config["SkillSync"] = new Dictionary<string, object>
        {
            ["DisableSystemSkillSync"] = false
        };

        // External skills
        if (ExternalSkillSources is { Count: > 0 })
        {
            var sourcesArray = ExternalSkillSources.Select(s =>
            {
                var entry = new Dictionary<string, object>
                {
                    ["Name"] = s.Name,
                    ["Enabled"] = s.Enabled,
                    ["AllowSymlinks"] = s.AllowSymlinks
                };

                if (s.WellKnown is not null)
                    entry["WellKnown"] = s.WellKnown;
                else if (s.Path is not null)
                    entry["Path"] = s.Path;

                return (object)entry;
            }).ToArray();

            config["ExternalSkills"] = new Dictionary<string, object>
            {
                ["Sources"] = sourcesArray
            };
        }

        // Skill feeds (private skill servers)
        if (SkillFeedSources is { Count: > 0 })
        {
            var feedsArray = SkillFeedSources.Select(f =>
            {
                var entry = new Dictionary<string, object>
                {
                    ["Name"] = f.Name,
                    ["Url"] = f.Url,
                    ["Enabled"] = f.Enabled
                };
                return (object)entry;
            }).ToArray();

            config["SkillFeeds"] = new Dictionary<string, object>
            {
                ["Feeds"] = feedsArray
            };
        }

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

        // Daemon section — only written for non-default exposure modes (local = omit)
        if (Daemon is not null && Daemon.ExposureMode != ExposureMode.Local)
        {
            var daemonSection = new Dictionary<string, object>
            {
                ["ExposureMode"] = Daemon.ExposureMode.ToWireValue()
            };

            if (!string.IsNullOrWhiteSpace(Daemon.Host))
                daemonSection["Host"] = Daemon.Host;

            if (Daemon.TrustedProxies.Count > 0)
                daemonSection["TrustedProxies"] = Daemon.TrustedProxies;

            config["Daemon"] = daemonSection;
        }

        // Webhooks section — only written when enabled (disabled = default, omit)
        if (Webhooks is { Enabled: true })
        {
            config["Webhooks"] = new Dictionary<string, object>
            {
                ["Enabled"] = true
            };
        }

        // Notifications
        if (Notifications is { WebhookUrl: not null })
        {
            config["Notifications"] = new Dictionary<string, object>
            {
                ["Webhooks"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["Url"] = Notifications.WebhookUrl,
                        ["Format"] = WebhookFormatDetection.InferFromUrl(Notifications.WebhookUrl).ToString()
                    }
                }
            };
        }

        // Feature selections — merge Enabled flags into existing or new sections
        if (FeatureSelections is not null)
        {
            MergeEnabledFlag(config, "Memory", FeatureSelections.MemoryEnabled);
            MergeEnabledFlag(config, "Search", FeatureSelections.SearchEnabled);
            MergeEnabledFlag(config, "SkillSync", FeatureSelections.SkillsEnabled);
            MergeEnabledFlag(config, "Scheduling", FeatureSelections.SchedulingEnabled);
            MergeEnabledFlag(config, "SubAgents", FeatureSelections.SubAgentsEnabled);
            MergeEnabledFlag(config, "Webhooks", FeatureSelections.WebhooksEnabled);
        }

        return config;
    }

    /// <summary>
    /// Merge an <c>Enabled</c> flag into an existing config section dictionary,
    /// or create a new section with just the flag if one does not exist.
    /// </summary>
    private static void MergeEnabledFlag(Dictionary<string, object> config, string sectionKey, bool enabled)
    {
        if (config.TryGetValue(sectionKey, out var existing) && existing is Dictionary<string, object> section)
        {
            section["Enabled"] = enabled;
        }
        else
        {
            config[sectionKey] = new Dictionary<string, object>
            {
                ["Enabled"] = enabled
            };
        }
    }
}

/// <summary>
/// Typed secrets builder for non-provider secrets (Slack tokens, search API keys, etc.).
/// </summary>
public sealed class WizardSecretsBuilder
{
    private readonly NetclawPaths _paths;
    private readonly Dictionary<string, object> _secrets = [];

    public WizardSecretsBuilder(NetclawPaths paths)
    {
        _paths = paths;
    }

    internal NetclawPaths Paths => _paths;

    /// <summary>Add a section to the secrets file.</summary>
    public void AddSection(string key, Dictionary<string, object> section)
    {
        _secrets[key] = section;
    }

    /// <summary>Add a top-level value to the secrets file (e.g., DeviceToken).</summary>
    public void AddValue(string key, object value) => _secrets[key] = value;

    /// <summary>Write secrets.json if any secrets were contributed.</summary>
    public void WriteSecretsFile()
    {
        if (_secrets.Count == 0)
            return;

        if (File.Exists(_paths.SecretsPath))
        {
            var existingText = File.ReadAllText(_paths.SecretsPath);
            var existingNode = JsonNode.Parse(existingText)?.AsObject();
            if (existingNode is not null)
            {
                foreach (var (key, value) in _secrets)
                {
                    var segments = SecretsJsonUpdater.ParseKeyPath(key);
                    var node = JsonSerializer.SerializeToNode(value, JsonDefaults.ConfigFile);
                    if (node is JsonObject obj)
                        SecretsJsonUpdater.MergeObject(existingNode, segments, obj);
                    else
                        SecretsJsonUpdater.UpsertNode(existingNode, segments, node);
                }

                SecretsFileWriter.Write(_paths.SecretsPath, existingNode.ToJsonString(JsonDefaults.ConfigFile),
                    protector: SensitiveStringTypeConverter.Protector);
                return;
            }
        }

        SecretsFileWriter.Write(_paths.SecretsPath, _secrets,
            options: JsonDefaults.ConfigFile, protector: SensitiveStringTypeConverter.Protector);
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
    public int? ContextWindow { get; init; }
    public ModelDiscoverySource? Provenance { get; init; }
    public ModelModality? InputModalities { get; init; }
    public ModelModality? OutputModalities { get; init; }
}

public sealed class SlackConfigSection
{
    public bool Enabled { get; init; }
    public List<string>? AllowedChannelIds { get; init; }
    public bool AllowDirectMessages { get; init; }
    public List<string>? AllowedUserIds { get; init; }
    public Dictionary<string, string>? ChannelAudiences { get; init; }
}

public sealed class DiscordConfigSection
{
    public bool Enabled { get; init; }
    public string? DefaultChannelId { get; init; }
    public List<string>? AllowedChannelIds { get; init; }
    public bool AllowDirectMessages { get; init; }
    public List<string>? AllowedUserIds { get; init; }
    public Dictionary<string, string>? ChannelAudiences { get; init; }
}

public sealed class MattermostConfigSection
{
    public bool Enabled { get; init; }
    public string? ServerUrl { get; init; }
    public string? CallbackUrl { get; init; }
    public string? DefaultChannelId { get; init; }
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

public sealed class WorkspacesConfigSection
{
    public required string Directory { get; init; }
}

public sealed class IdentityConfigSection
{
    public required string AgentName { get; init; }
    public required string CommunicationStyle { get; init; }
    public string? UserName { get; init; }
    public required string UserTimezone { get; init; }
}

public sealed class DaemonConfigSection
{
    public ExposureMode ExposureMode { get; init; } = ExposureMode.Local;

    /// <summary>
    /// Bind address for the daemon. Only emitted when set; the daemon defaults to
    /// <c>127.0.0.1</c> when absent. Required (non-loopback) for
    /// <see cref="ExposureMode.ReverseProxy"/>.
    /// </summary>
    public string? Host { get; init; }

    /// <summary>
    /// Trusted reverse-proxy source IPs / CIDR ranges. Only meaningful for
    /// <see cref="ExposureMode.ReverseProxy"/>, where at least one entry is required
    /// for the daemon to start. Emitted only when non-empty.
    /// </summary>
    public IReadOnlyList<string> TrustedProxies { get; init; } = [];
}

public sealed class WebhooksConfigSection
{
    public bool Enabled { get; init; }
}

public sealed class FeatureSelectionsConfigSection
{
    public bool MemoryEnabled { get; init; } = true;
    public bool SearchEnabled { get; init; } = true;
    public bool SkillsEnabled { get; init; } = true;
    public bool SchedulingEnabled { get; init; } = true;
    public bool SubAgentsEnabled { get; init; } = true;
    public bool WebhooksEnabled { get; init; } = true;
}
