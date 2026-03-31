using System.Text.Json;
using System.Text.Json.Serialization;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Configuration;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Mcp;

public enum ToolPermissionsState
{
    Loading,
    ServerList,
    ToolGrid,
    Saving
}

public sealed class McpToolPermissionsViewModel : ReactiveViewModel
{
    private static readonly JsonSerializerOptions EnumJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly NetclawPaths _paths;
    private readonly DaemonApi _daemonApi;

    public McpToolPermissionsViewModel(NetclawPaths paths, DaemonApi daemonApi)
    {
        _paths = paths;
        _daemonApi = daemonApi;
    }

    public ReactiveProperty<ToolPermissionsState> CurrentState { get; } = new(ToolPermissionsState.Loading);
    internal ReactiveProperty<int> StateVersion { get; } = new(0);
    public ReactiveProperty<string> StatusMessage { get; } = new("");

    // Server list state
    public List<(string Name, string Status, int ToolCount)> Servers { get; } = [];

    // Tool grid state
    public string? SelectedServer { get; private set; }
    public List<string> DiscoveredTools { get; } = [];
    public TrustAudience SelectedAudience { get; private set; } = TrustAudience.Personal;
    public ToolAudienceProfiles Profiles { get; private set; } = ToolAudienceProfileDefaults.CreateProfiles();

    // Track pending changes
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _pendingGrants = new();
    public bool HasUnsavedChanges => _pendingGrants.Count > 0;

    public override void OnActivated()
    {
        base.OnActivated();
        _ = LoadServersAsync();
    }

    private async Task LoadServersAsync()
    {
        StatusMessage.Value = "Loading MCP server statuses...";

        try
        {
            var statuses = await _daemonApi.GetMcpServerStatusesAsync(CancellationToken.None);
            Servers.Clear();

            foreach (var prop in statuses.EnumerateObject())
            {
                var state = prop.Value.GetProperty("state").GetString() ?? "unknown";
                var toolCount = prop.Value.TryGetProperty("toolCount", out var tc) ? tc.GetInt32() : 0;
                Servers.Add((prop.Name, state, toolCount));
            }

            Profiles = LoadToolConfig().AudienceProfiles;

            if (Servers.Count == 0)
            {
                StatusMessage.Value = "No MCP servers connected. Start the daemon and configure servers first.";
            }
            else
            {
                StatusMessage.Value = "";
                CurrentState.Value = ToolPermissionsState.ServerList;
            }
        }
        catch (Exception ex)
        {
            StatusMessage.Value = $"Could not reach daemon: {ex.Message}";
        }

        NotifyStateChanged();
    }

    public void SelectServer(string serverName)
    {
        SelectedServer = serverName;
        _ = LoadToolsForServerAsync(serverName);
    }

    private async Task LoadToolsForServerAsync(string serverName)
    {
        StatusMessage.Value = $"Loading tools for {serverName}...";
        NotifyStateChanged();

        try
        {
            var tools = await _daemonApi.GetMcpToolNamesAsync(serverName, CancellationToken.None);
            DiscoveredTools.Clear();
            DiscoveredTools.AddRange(tools);

            // Initialize pending grants from current config if not already edited
            if (!_pendingGrants.ContainsKey(serverName))
                InitializePendingGrantsFromConfig(serverName);

            StatusMessage.Value = "";
            CurrentState.Value = ToolPermissionsState.ToolGrid;
        }
        catch (Exception ex)
        {
            StatusMessage.Value = $"Error loading tools: {ex.Message}";
        }

        NotifyStateChanged();
    }

    private void InitializePendingGrantsFromConfig(string serverName)
    {
        var audienceGrants = new Dictionary<string, HashSet<string>>();

        foreach (var (name, profile) in new[]
        {
            ("Public", Profiles.Public),
            ("Team", Profiles.Team),
            ("Personal", Profiles.Personal)
        })
        {
            if (profile.McpServerToolGrants is { } grants
                && grants.TryGetValue(serverName, out var tools))
            {
                audienceGrants[name] = new HashSet<string>(tools, StringComparer.Ordinal);
            }
            // If no grants configured, don't add an entry (null = all tools)
        }

        if (audienceGrants.Count > 0)
            _pendingGrants[serverName] = audienceGrants;
    }

    private static readonly TrustAudience[] AudienceValues =
        [TrustAudience.Personal, TrustAudience.Team, TrustAudience.Public];

    public void CycleAudience()
    {
        var idx = Array.IndexOf(AudienceValues, SelectedAudience);
        SelectedAudience = AudienceValues[(idx + 1) % AudienceValues.Length];
        NotifyStateChanged();
    }

    public void CycleAudienceBack()
    {
        var idx = Array.IndexOf(AudienceValues, SelectedAudience);
        SelectedAudience = AudienceValues[(idx - 1 + AudienceValues.Length) % AudienceValues.Length];
        NotifyStateChanged();
    }

    public bool IsToolGranted(string toolName)
    {
        if (SelectedServer is null)
            return true;

        var audienceName = SelectedAudience switch
        {
            TrustAudience.Public => "Public",
            TrustAudience.Team => "Team",
            _ => "Personal"
        };

        // Check pending grants first
        if (_pendingGrants.TryGetValue(SelectedServer, out var serverGrants)
            && serverGrants.TryGetValue(audienceName, out var tools))
        {
            return tools.Contains(toolName);
        }

        // No pending grants = check config
        var profile = SelectedAudience switch
        {
            TrustAudience.Public => Profiles.Public,
            TrustAudience.Team => Profiles.Team,
            _ => Profiles.Personal
        };

        if (profile.McpServerToolGrants is null)
            return true; // No grants = all tools

        if (!profile.McpServerToolGrants.TryGetValue(SelectedServer, out var configTools))
            return true; // Server not in grants = all tools

        return configTools.Contains(toolName, StringComparer.Ordinal);
    }

    public void ToggleTool(string toolName)
    {
        if (SelectedServer is null)
            return;

        var audienceName = SelectedAudience switch
        {
            TrustAudience.Public => "Public",
            TrustAudience.Team => "Team",
            _ => "Personal"
        };

        if (!_pendingGrants.TryGetValue(SelectedServer, out var serverGrants))
        {
            // First edit: snapshot all tools as granted
            serverGrants = new Dictionary<string, HashSet<string>>();
            var allTools = new HashSet<string>(DiscoveredTools, StringComparer.Ordinal);
            serverGrants[audienceName] = allTools;
            _pendingGrants[SelectedServer] = serverGrants;
        }

        if (!serverGrants.TryGetValue(audienceName, out var tools))
        {
            // First edit for this audience: snapshot all tools as granted
            tools = new HashSet<string>(DiscoveredTools, StringComparer.Ordinal);
            serverGrants[audienceName] = tools;
        }

        if (!tools.Remove(toolName))
            tools.Add(toolName);

        NotifyStateChanged();
    }

    public void Save()
    {
        if (!HasUnsavedChanges)
            return;

        var (config, _) = ConfigFileHelper.LoadConfigFiles(_paths);
        var toolsSection = ConfigFileHelper.GetOrCreateSection(config, "Tools");
        var profilesSection = ConfigFileHelper.GetOrCreateSection(toolsSection, "AudienceProfiles");

        foreach (var (serverName, audienceGrants) in _pendingGrants)
        {
            foreach (var (audienceName, tools) in audienceGrants)
            {
                var audienceSection = ConfigFileHelper.GetOrCreateSection(profilesSection, audienceName);

                var grants = audienceSection.TryGetValue("McpServerToolGrants", out var existing)
                    && existing is Dictionary<string, object> dict
                        ? dict
                        : new Dictionary<string, object>();

                grants[serverName] = tools.Order(StringComparer.Ordinal).ToList();
                audienceSection["McpServerToolGrants"] = grants;
            }
        }

        ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, config);
        _pendingGrants.Clear();

        StatusMessage.Value = "Saved to netclaw.json. Restart daemon to apply changes.";
        CurrentState.Value = ToolPermissionsState.ToolGrid;
        NotifyStateChanged();
    }

    public void RequestQuit() => Shutdown();

    public void GoBack()
    {
        switch (CurrentState.Value)
        {
            case ToolPermissionsState.ToolGrid:
                SelectedServer = null;
                DiscoveredTools.Clear();
                CurrentState.Value = ToolPermissionsState.ServerList;
                break;
            default:
                return;
        }

        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        StateVersion.Value++;
        RequestRedraw();
    }

    private ToolConfig LoadToolConfig()
    {
        if (!File.Exists(_paths.NetclawConfigPath))
            return new ToolConfig();

        try
        {
            var text = File.ReadAllText(_paths.NetclawConfigPath);
            using var doc = JsonDocument.Parse(text);

            if (!doc.RootElement.TryGetProperty("Tools", out var toolsSection))
                return new ToolConfig();

            return JsonSerializer.Deserialize<ToolConfig>(toolsSection.GetRawText(), EnumJsonOptions)
                ?? new ToolConfig();
        }
        catch
        {
            return new ToolConfig();
        }
    }
}
