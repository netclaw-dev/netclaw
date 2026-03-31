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
    // Track pending server access changes: (audience, server) → allowed
    private readonly Dictionary<(string Audience, string Server), bool> _pendingServerAccess = new();
    public bool HasUnsavedChanges => _pendingGrants.Count > 0 || _pendingServerAccess.Count > 0;

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
            return false;

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

        // Check pending server access, then config
        var audName = AudienceName(SelectedAudience);
        if (_pendingServerAccess.TryGetValue((audName, SelectedServer), out var pendingAccess))
        {
            if (!pendingAccess)
                return false;
        }
        else if (!IsServerAllowed(SelectedServer, profile))
        {
            return false;
        }

        // No grants dictionary at all = no per-tool filtering, all tools pass
        if (profile.McpServerToolGrants is null)
            return true;

        // Server not in grants = not yet configured, all tools pass
        if (!profile.McpServerToolGrants.TryGetValue(SelectedServer, out var configTools))
            return true;

        return configTools.Contains(toolName, StringComparer.Ordinal);
    }

    public void ToggleAll()
    {
        if (SelectedServer is null)
            return;

        // If any tool is granted, deselect all. Otherwise select all.
        var anyGranted = DiscoveredTools.Any(IsToolGranted);

        var audienceName = SelectedAudience switch
        {
            TrustAudience.Public => "Public",
            TrustAudience.Team => "Team",
            _ => "Personal"
        };

        if (!_pendingGrants.TryGetValue(SelectedServer, out var serverGrants))
        {
            serverGrants = new Dictionary<string, HashSet<string>>();
            _pendingGrants[SelectedServer] = serverGrants;
        }

        serverGrants[audienceName] = anyGranted
            ? new HashSet<string>(StringComparer.Ordinal) // deselect all
            : new HashSet<string>(DiscoveredTools, StringComparer.Ordinal); // select all

        NotifyStateChanged();
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
            serverGrants = new Dictionary<string, HashSet<string>>();
            _pendingGrants[SelectedServer] = serverGrants;
        }

        if (!serverGrants.TryGetValue(audienceName, out var tools))
        {
            // Initialize from config if grants exist for this server,
            // otherwise start from all tools (matches IsToolGranted behavior)
            var profile = SelectedAudience switch
            {
                TrustAudience.Public => Profiles.Public,
                TrustAudience.Team => Profiles.Team,
                _ => Profiles.Personal
            };

            if (profile.McpServerToolGrants is { } existing
                && existing.TryGetValue(SelectedServer, out var configTools))
            {
                tools = new HashSet<string>(configTools, StringComparer.Ordinal);
            }
            else
            {
                // Server not yet configured — start with all tools granted
                tools = new HashSet<string>(DiscoveredTools, StringComparer.Ordinal);
            }

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

        // Write server access changes (AllowedMcpServers)
        foreach (var ((audienceName, serverName), allowed) in _pendingServerAccess)
        {
            var audienceSection = ConfigFileHelper.GetOrCreateSection(profilesSection, audienceName);

            var serverList = audienceSection.TryGetValue("AllowedMcpServers", out var existingList)
                && existingList is List<object> list
                    ? list.Select(o => o.ToString()!).ToList()
                    : [];

            if (allowed && !serverList.Contains(serverName, StringComparer.OrdinalIgnoreCase))
            {
                serverList.Add(serverName);
            }
            else if (!allowed)
            {
                serverList.RemoveAll(s => s.Equals(serverName, StringComparison.OrdinalIgnoreCase));
            }

            audienceSection["AllowedMcpServers"] = serverList;

            // Also update the in-memory profile so the UI reflects changes immediately
            var profile = audienceName switch
            {
                "Public" => Profiles.Public,
                "Team" => Profiles.Team,
                _ => Profiles.Personal
            };

            if (allowed && !profile.AllowedMcpServers.Contains(serverName, StringComparer.OrdinalIgnoreCase))
                profile.AllowedMcpServers.Add(serverName);
            else if (!allowed)
                profile.AllowedMcpServers.RemoveAll(s => s.Equals(serverName, StringComparison.OrdinalIgnoreCase));
        }

        // Write tool grant changes (McpServerToolGrants)
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
        _pendingServerAccess.Clear();

        StatusMessage.Value = "Saved to netclaw.json. Restart daemon to apply changes.";
        CurrentState.Value = ToolPermissionsState.ToolGrid;
        NotifyStateChanged();
    }

    public bool IsServerAllowedForSelectedAudience()
    {
        if (SelectedServer is null)
            return false;

        var audienceName = AudienceName(SelectedAudience);

        // Check pending changes first
        if (_pendingServerAccess.TryGetValue((audienceName, SelectedServer), out var pending))
            return pending;

        var profile = ResolveProfile(SelectedAudience);
        return IsServerAllowed(SelectedServer, profile);
    }

    public void ToggleServerAccess()
    {
        if (SelectedServer is null)
            return;

        var allowed = IsServerAllowedForSelectedAudience();
        var audienceName = AudienceName(SelectedAudience);
        _pendingServerAccess[(audienceName, SelectedServer)] = !allowed;

        if (!allowed)
        {
            // Enabling server for this audience — start with empty tool grants
            // so the operator explicitly selects which tools to expose
            if (!_pendingGrants.TryGetValue(SelectedServer, out var serverGrants))
            {
                serverGrants = new Dictionary<string, HashSet<string>>();
                _pendingGrants[SelectedServer] = serverGrants;
            }

            if (!serverGrants.ContainsKey(audienceName))
                serverGrants[audienceName] = new HashSet<string>(StringComparer.Ordinal);
        }

        NotifyStateChanged();
    }

    private static bool IsServerAllowed(string serverName, ToolAudienceProfile profile)
    {
        if (profile.McpServersMode == ToolProfileMode.All)
            return true;

        return profile.AllowedMcpServers.Contains(serverName, StringComparer.OrdinalIgnoreCase);
    }

    private ToolAudienceProfile ResolveProfile(TrustAudience audience) => audience switch
    {
        TrustAudience.Public => Profiles.Public,
        TrustAudience.Team => Profiles.Team,
        _ => Profiles.Personal
    };

    private static string AudienceName(TrustAudience audience) => audience switch
    {
        TrustAudience.Public => "Public",
        TrustAudience.Team => "Team",
        _ => "Personal"
    };

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
