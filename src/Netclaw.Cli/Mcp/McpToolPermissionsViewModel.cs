using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Json;
using Netclaw.Configuration;
using Netclaw.Tools;
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
    // Track pending server-level approval defaults: (audience, server) → mode
    private readonly Dictionary<(string Audience, string Server), ToolApprovalMode> _pendingServerDefaults = new();
    // Track pending per-tool approval overrides: (audience, server, tool) → mode.
    // A null value is the "inherit" sentinel: the entry is removed from
    // ToolOverrides on save so the effective mode falls through to the
    // server default / global default.
    private readonly Dictionary<(string Audience, string Server, string Tool), ToolApprovalMode?> _pendingToolOverrides = new();
    public bool HasUnsavedChanges =>
        _pendingGrants.Count > 0
        || _pendingServerAccess.Count > 0
        || _pendingServerDefaults.Count > 0
        || _pendingToolOverrides.Count > 0;

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

    public void SelectServer(McpServerName serverName)
    {
        SelectedServer = serverName.Value;
        _ = LoadToolsForServerAsync(serverName);
    }

    /// <summary>
    /// Test seam: wires up the view for a specific server and tool list
    /// without touching the daemon. Reloads the audience profiles from
    /// disk so tests can exercise <see cref="Save"/> end-to-end.
    /// </summary>
    internal void InitializeForTests(McpServerName serverName, IEnumerable<string> tools)
    {
        SelectedServer = serverName.Value;
        DiscoveredTools.Clear();
        DiscoveredTools.AddRange(tools);
        Profiles = LoadToolConfig().AudienceProfiles;
        if (!_pendingGrants.ContainsKey(serverName.Value))
            InitializePendingGrantsFromConfig(serverName);
        CurrentState.Value = ToolPermissionsState.ToolGrid;
    }

    internal void SetSelectedAudienceForTests(TrustAudience audience)
    {
        SelectedAudience = audience;
    }

    private async Task LoadToolsForServerAsync(McpServerName serverName)
    {
        StatusMessage.Value = $"Loading tools for {serverName.Value}...";
        NotifyStateChanged();

        try
        {
            var tools = await _daemonApi.GetMcpToolNamesAsync(serverName.Value, CancellationToken.None);
            DiscoveredTools.Clear();
            DiscoveredTools.AddRange(tools);

            // Initialize pending grants from current config if not already edited
            if (!_pendingGrants.ContainsKey(serverName.Value))
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

    private void InitializePendingGrantsFromConfig(McpServerName serverName)
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
                && grants.TryGetValue(serverName.Value, out var tools))
            {
                audienceGrants[name] = new HashSet<string>(tools, StringComparer.Ordinal);
            }
            // If no grants configured, don't add an entry (null = all tools)
        }

        if (audienceGrants.Count > 0)
            _pendingGrants[serverName.Value] = audienceGrants;
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

    private static readonly ToolApprovalMode[] ServerDefaultCycle =
        [ToolApprovalMode.Auto, ToolApprovalMode.Approval, ToolApprovalMode.Deny];

    /// <summary>
    /// Advances the server-default approval mode for the current audience/server
    /// pair through Auto → Approval → Deny → Auto. The starting point is the
    /// currently resolved effective default (pending or config).
    /// </summary>
    public void CycleServerDefault()
    {
        if (SelectedServer is null)
            return;

        var audience = AudienceName(SelectedAudience);
        var key = (audience, SelectedServer);

        var current = _pendingServerDefaults.TryGetValue(key, out var pending)
            ? pending
            : ResolveProfile(SelectedAudience).ApprovalPolicy?.McpServerDefaults.TryGetValue(SelectedServer, out var configMode) == true
                ? configMode
                : ToolApprovalMode.Auto;

        var idx = Array.IndexOf(ServerDefaultCycle, current);
        var next = ServerDefaultCycle[(idx + 1) % ServerDefaultCycle.Length];
        _pendingServerDefaults[key] = next;

        NotifyStateChanged();
    }

    /// <summary>
    /// Advances the per-tool approval override for <paramref name="toolName"/>
    /// under the current audience/server pair through
    /// inherit (null) → Auto → Approval → Deny → inherit. The "inherit" state
    /// removes any existing <c>ToolOverrides[{server}/{tool}]</c> entry on save.
    /// </summary>
    public void CycleToolOverride(ToolName toolName)
    {
        if (SelectedServer is null)
            return;

        var audience = AudienceName(SelectedAudience);
        var key = (audience, SelectedServer, toolName.Value);

        ToolApprovalMode? current;
        if (_pendingToolOverrides.TryGetValue(key, out var pending))
        {
            current = pending;
        }
        else
        {
            var exactKey = $"{SelectedServer}/{toolName.Value}";
            current = ResolveProfile(SelectedAudience).ApprovalPolicy?.ToolOverrides.TryGetValue(exactKey, out var configMode) == true
                ? configMode
                : null;
        }

        ToolApprovalMode? next = current switch
        {
            null => ToolApprovalMode.Auto,
            ToolApprovalMode.Auto => ToolApprovalMode.Approval,
            ToolApprovalMode.Approval => ToolApprovalMode.Deny,
            ToolApprovalMode.Deny => null,
            _ => null
        };

        _pendingToolOverrides[key] = next;

        NotifyStateChanged();
    }

    /// <summary>
    /// Returns the effective approval mode for a tool under the currently
    /// selected server and audience, plus a flag indicating whether the mode
    /// was inherited from the server default / global default (true) or came
    /// from an explicit per-tool entry (false).
    /// </summary>
    public (ToolApprovalMode Mode, bool IsInherited) GetEffectiveMode(ToolName toolName)
    {
        if (SelectedServer is null)
            return (ToolApprovalMode.Auto, true);

        // Precedence mirrors ToolApprovalConfig.TryGetExplicitMode but layers
        // pending edits on top of the config so the view reflects unsaved state.
        var audience = AudienceName(SelectedAudience);
        var profile = ResolveProfile(SelectedAudience);
        var approvalPolicy = profile.ApprovalPolicy;

        var toolKey = (audience, SelectedServer, toolName.Value);
        if (_pendingToolOverrides.TryGetValue(toolKey, out var pendingOverride))
        {
            if (pendingOverride is { } explicitMode)
                return (explicitMode, false);
            // inherit sentinel (null) — fall through.
        }
        else if (approvalPolicy is not null)
        {
            var exactKey = $"{SelectedServer}/{toolName.Value}";
            if (approvalPolicy.ToolOverrides.TryGetValue(exactKey, out var configExact))
                return (configExact, false);
        }

        var serverKey = (audience, SelectedServer);
        if (_pendingServerDefaults.TryGetValue(serverKey, out var pendingDefault))
            return (pendingDefault, true);

        if (approvalPolicy is not null
            && approvalPolicy.McpServerDefaults.TryGetValue(SelectedServer, out var configDefault))
            return (configDefault, true);

        return (approvalPolicy?.DefaultMode ?? ToolApprovalMode.Auto, true);
    }

    /// <summary>
    /// Returns the server-default approval mode for the currently selected
    /// audience/server pair, consulting pending edits first, then config.
    /// </summary>
    public ToolApprovalMode GetServerDefault()
    {
        if (SelectedServer is null)
            return ToolApprovalMode.Auto;

        var audience = AudienceName(SelectedAudience);
        if (_pendingServerDefaults.TryGetValue((audience, SelectedServer), out var pending))
            return pending;

        var approvalPolicy = ResolveProfile(SelectedAudience).ApprovalPolicy;
        if (approvalPolicy is not null
            && approvalPolicy.McpServerDefaults.TryGetValue(SelectedServer, out var configMode))
            return configMode;

        return ToolApprovalMode.Auto;
    }

    public bool IsToolGranted(ToolName toolName)
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
            return tools.Contains(toolName.Value);
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
        else if (!IsServerAllowed(new McpServerName(SelectedServer), profile))
        {
            return false;
        }

        // No grants dictionary at all = no per-tool filtering, all tools pass
        if (profile.McpServerToolGrants is null)
            return true;

        // Server not in grants = not yet configured, all tools pass
        if (!profile.McpServerToolGrants.TryGetValue(SelectedServer, out var configTools))
            return true;

        return configTools.Contains(toolName.Value, StringComparer.Ordinal);
    }

    public void ToggleAll()
    {
        if (SelectedServer is null)
            return;

        // If any tool is granted, deselect all. Otherwise select all.
        var anyGranted = DiscoveredTools.Any(t => IsToolGranted(new ToolName(t)));

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

    public void ToggleTool(ToolName toolName)
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

        if (!tools.Remove(toolName.Value))
            tools.Add(toolName.Value);

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
                var grants = ConfigFileHelper.GetOrCreateSection(audienceSection, "McpServerToolGrants");
                grants[serverName] = tools.Order(StringComparer.Ordinal).ToList();
            }
        }

        // Mirroring in-memory Profiles alongside the on-disk writes lets
        // GetEffectiveMode / GetServerDefault reflect saved values without
        // a full config reload.
        foreach (var ((audienceName, serverName), mode) in _pendingServerDefaults)
        {
            var (approvalSection, inMemoryPolicy) = GetOrCreateApprovalPolicy(profilesSection, audienceName);
            var serverDefaults = ConfigFileHelper.GetOrCreateSection(approvalSection, "McpServerDefaults");
            serverDefaults[serverName] = mode.ToString();
            inMemoryPolicy.McpServerDefaults[serverName] = mode;
        }

        foreach (var ((audienceName, serverName, toolName), mode) in _pendingToolOverrides)
        {
            var (approvalSection, inMemoryPolicy) = GetOrCreateApprovalPolicy(profilesSection, audienceName);
            var toolOverrides = ConfigFileHelper.GetOrCreateSection(approvalSection, "ToolOverrides");
            var exactKey = $"{serverName}/{toolName}";

            if (mode is null)
            {
                toolOverrides.Remove(exactKey);
                inMemoryPolicy.ToolOverrides.Remove(exactKey);
            }
            else
            {
                toolOverrides[exactKey] = mode.Value.ToString();
                inMemoryPolicy.ToolOverrides[exactKey] = mode.Value;
            }
        }

        ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, config);
        _pendingGrants.Clear();
        _pendingServerAccess.Clear();
        _pendingServerDefaults.Clear();
        _pendingToolOverrides.Clear();

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
        return IsServerAllowed(new McpServerName(SelectedServer), profile);
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

    private (Dictionary<string, object> ApprovalSection, ToolApprovalConfig InMemoryPolicy)
        GetOrCreateApprovalPolicy(Dictionary<string, object> profilesSection, string audienceName)
    {
        var audienceSection = ConfigFileHelper.GetOrCreateSection(profilesSection, audienceName);
        var approvalSection = ConfigFileHelper.GetOrCreateSection(audienceSection, "ApprovalPolicy");
        var profile = audienceName switch
        {
            "Public" => Profiles.Public,
            "Team" => Profiles.Team,
            _ => Profiles.Personal
        };
        profile.ApprovalPolicy ??= new ToolApprovalConfig();
        return (approvalSection, profile.ApprovalPolicy);
    }

    private static bool IsServerAllowed(McpServerName serverName, ToolAudienceProfile profile)
    {
        if (profile.McpServersMode == ToolProfileMode.All)
            return true;

        return profile.AllowedMcpServers.Contains(serverName.Value, StringComparer.OrdinalIgnoreCase);
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

            return JsonSerializer.Deserialize<ToolConfig>(toolsSection.GetRawText(), JsonDefaults.EnumAware)
                ?? new ToolConfig();
        }
        catch
        {
            return new ToolConfig();
        }
    }
}
