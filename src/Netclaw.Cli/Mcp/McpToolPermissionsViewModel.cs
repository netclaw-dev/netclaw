// -----------------------------------------------------------------------
// <copyright file="McpToolPermissionsViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Cli.Daemon;
using Netclaw.Cli.Json;
using Netclaw.Cli.Tui;
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
    private bool _initializedForTests;
    private readonly McpToolPermissionsNavigationState? _navigationState;
    private readonly TuiNavigation? _navigation;

    public McpToolPermissionsViewModel(
        NetclawPaths paths,
        DaemonApi daemonApi,
        McpToolPermissionsNavigationState? navigationState = null,
        TuiNavigation? navigation = null)
    {
        _paths = paths;
        _daemonApi = daemonApi;
        _navigationState = navigationState;
        _navigation = navigation;
    }

    public ReactiveProperty<ToolPermissionsState> CurrentState { get; } = new(ToolPermissionsState.Loading);
    internal ReactiveProperty<int> StateVersion { get; } = new(0);
    public ReactiveProperty<string> StatusMessage { get; } = new("");
    public bool HasSaveError { get; private set; }

    // Server list state
    public List<(string Name, string Status, int ToolCount)> Servers { get; } = [];

    // Tool grid state
    public string? SelectedServer { get; private set; }
    public List<string> DiscoveredTools { get; } = [];
    public TrustAudience SelectedAudience { get; private set; } = TrustAudience.Personal;
    public ToolAudienceProfiles Profiles { get; private set; } = ToolAudienceProfileDefaults.CreateProfiles();

    // Track pending changes
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _pendingGrants = [];
    // Track pending server access changes: (audience, server) → allowed
    private readonly Dictionary<(string Audience, string Server), bool> _pendingServerAccess = [];
    // Track pending server-level approval defaults: (audience, server) → mode
    private readonly Dictionary<(string Audience, string Server), ToolApprovalMode> _pendingServerDefaults = [];
    // Track pending per-tool approval overrides: (audience, server, tool) → mode.
    // A null value is the "inherit" sentinel: the entry is removed from
    // ToolOverrides on save so the effective mode falls through to the
    // server default / global default.
    private readonly Dictionary<(string Audience, string Server, string Tool), ToolApprovalMode?> _pendingToolOverrides = [];
    public bool HasUnsavedChanges =>
        _pendingGrants.Count > 0
        || _pendingServerAccess.Count > 0
        || _pendingServerDefaults.Count > 0
        || _pendingToolOverrides.Count > 0;

    public override void OnActivated()
    {
        base.OnActivated();
        ApplyPendingNavigationState();
        if (_initializedForTests) return;
        _ = LoadServersAsync();
    }

    internal async Task LoadServersAsync()
    {
        StatusMessage.Value = "Loading MCP server statuses...";

        JsonElement statuses;
        try
        {
            statuses = await _daemonApi.GetMcpServerStatusesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusMessage.Value = $"Could not reach daemon: {ex.Message}";
            NotifyStateChanged();
            return;
        }

        Servers.Clear();

        // A 200 response whose body is not the expected object shape (or a server entry missing
        // its "state") would otherwise throw out of this fire-and-forget task. Surface it as a
        // status message like the daemon-call path does, rather than crashing page activation.
        try
        {
            foreach (var prop in statuses.EnumerateObject())
            {
                var state = prop.Value.TryGetProperty("state", out var stateEl)
                    ? stateEl.GetString() ?? "unknown"
                    : "unknown";
                var toolCount = prop.Value.TryGetProperty("toolCount", out var tc) ? tc.GetInt32() : 0;
                Servers.Add((prop.Name, state, toolCount));
            }
        }
        catch (Exception ex)
        {
            StatusMessage.Value = $"Could not read MCP server statuses: {ex.Message}";
            NotifyStateChanged();
            return;
        }

        try
        {
            Profiles = LoadToolConfig().AudienceProfiles;
        }
        catch (Exception ex)
        {
            StatusMessage.Value = $"Could not load MCP permissions config: {ex.Message}";
            NotifyStateChanged();
            return;
        }

        if (Servers.Count == 0)
            StatusMessage.Value = "No MCP servers connected. Start the daemon and configure servers first.";
        else
        {
            StatusMessage.Value = "";
            CurrentState.Value = ToolPermissionsState.ServerList;
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
        _initializedForTests = true;
        ApplyPendingNavigationState();
        SelectedServer = serverName.Value;
        DiscoveredTools.Clear();
        DiscoveredTools.AddRange(tools);
        Profiles = LoadToolConfig().AudienceProfiles;
        if (!_pendingGrants.ContainsKey(serverName.Value))
            InitializePendingGrantsFromConfig(serverName);
        CurrentState.Value = ToolPermissionsState.ToolGrid;
        NotifyStateChanged();
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

    // Most-trusted-first cycling order (Personal → Team → Public). Sourced
    // from TrustAudiences.All so new audiences flow through automatically.
    private static readonly ImmutableArray<TrustAudience> AudienceValues =
        [.. TrustAudiences.All.Reverse()];

    public void CycleAudience()
    {
        var idx = AudienceValues.IndexOf(SelectedAudience);
        SelectedAudience = AudienceValues[(idx + 1) % AudienceValues.Length];
        NotifyStateChanged();
    }

    public void CycleAudienceBack()
    {
        var idx = AudienceValues.IndexOf(SelectedAudience);
        SelectedAudience = AudienceValues[(idx - 1 + AudienceValues.Length) % AudienceValues.Length];
        NotifyStateChanged();
    }

    private static readonly ToolApprovalMode[] ServerDefaultCycle =
        [ToolApprovalMode.Auto, ToolApprovalMode.Approval, ToolApprovalMode.Deny];

    public void CycleServerDefault() => CycleServerDefaultCore(+1);

    public void CycleServerDefaultBack() => CycleServerDefaultCore(-1);

    private void CycleServerDefaultCore(int direction)
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
        _pendingServerDefaults[key] = ServerDefaultCycle[(idx + direction + ServerDefaultCycle.Length) % ServerDefaultCycle.Length];

        NotifyStateChanged();
    }

    private static readonly ToolApprovalMode?[] ToolOverrideCycle =
        [null, ToolApprovalMode.Auto, ToolApprovalMode.Approval, ToolApprovalMode.Deny];

    public void CycleToolOverride(ToolName toolName) => CycleToolOverrideCore(toolName, +1);

    public void CycleToolOverrideBack(ToolName toolName) => CycleToolOverrideCore(toolName, -1);

    private void CycleToolOverrideCore(ToolName toolName, int direction)
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

        var idx = Array.IndexOf(ToolOverrideCycle, current);
        _pendingToolOverrides[key] = ToolOverrideCycle[(idx + direction + ToolOverrideCycle.Length) % ToolOverrideCycle.Length];

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

        var audienceName = AudienceName(SelectedAudience);

        // Check pending grants first
        if (_pendingGrants.TryGetValue(SelectedServer, out var serverGrants)
            && serverGrants.TryGetValue(audienceName, out var tools))
        {
            return tools.Contains(toolName.Value);
        }

        // No pending grants = check config
        var profile = ResolveProfile(SelectedAudience);

        if (_pendingServerAccess.TryGetValue((audienceName, SelectedServer), out var pendingAccess))
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

        var audienceName = AudienceName(SelectedAudience);

        if (!_pendingGrants.TryGetValue(SelectedServer, out var serverGrants))
        {
            serverGrants = [];
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

        var audienceName = AudienceName(SelectedAudience);

        if (!_pendingGrants.TryGetValue(SelectedServer, out var serverGrants))
        {
            serverGrants = [];
            _pendingGrants[SelectedServer] = serverGrants;
        }

        if (!serverGrants.TryGetValue(audienceName, out var tools))
        {
            var profile = ResolveProfile(SelectedAudience);

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

    public bool Save()
    {
        HasSaveError = false;

        if (!HasUnsavedChanges)
        {
            StatusMessage.Value = "No unsaved changes.";
            NotifyStateChanged();
            return true;
        }

        try
        {
            var (config, _) = ConfigFileHelper.LoadConfigFiles(_paths);
            var toolsSection = ConfigFileHelper.GetOrCreateSection(config, "Tools");
            var profilesSection = ConfigFileHelper.GetOrCreateSection(toolsSection, "AudienceProfiles");

            SaveServerAccess(config, profilesSection);
            SaveToolGrants(profilesSection);
            SaveServerDefaults(profilesSection);
            SaveToolOverrides(profilesSection);

            ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, config);
            _pendingGrants.Clear();
            _pendingServerAccess.Clear();
            _pendingServerDefaults.Clear();
            _pendingToolOverrides.Clear();

            StatusMessage.Value = "✓ Saved to netclaw.json. Restart daemon to apply changes.";
            CurrentState.Value = ToolPermissionsState.ToolGrid;
            NotifyStateChanged();
            return true;
        }
        catch (Exception ex)
        {
            HasSaveError = true;
            StatusMessage.Value = $"Save failed: {ex.Message}";
            NotifyStateChanged();
            return false;
        }
    }

    private void SaveServerAccess(Dictionary<string, object> config, Dictionary<string, object> profilesSection)
    {
        var knownServers = GetKnownMcpServers(config);

        // Accumulate per-audience working lists WITHOUT mutating the live in-memory profile objects
        // (Profiles.Public/Team/Personal back the runtime ACL queries — IsServerAllowed, etc. — so
        // coercing them here would leave the ACL in a post-save state if Save throws before the file
        // write). Seed each audience's working list from its ORIGINAL profile the first time it is
        // touched; later changes for the same audience build on the working list rather than re-reading
        // a profile that an earlier iteration would have coerced.
        var workingLists = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var ((audienceName, serverName), allowed) in _pendingServerAccess)
        {
            var audienceSection = ConfigFileHelper.GetOrCreateSection(profilesSection, audienceName);
            if (!workingLists.TryGetValue(audienceName, out var serverList))
            {
                var profile = ResolveProfile(AudienceFromName(audienceName));
                serverList = profile.McpServersMode == ToolProfileMode.All
                    ? knownServers.ToList()
                    : profile.AllowedMcpServers.ToList();
                workingLists[audienceName] = serverList;
            }

            if (allowed)
                AddServer(serverList, serverName);
            else
                serverList.RemoveAll(s => s.Equals(serverName, StringComparison.OrdinalIgnoreCase));

            audienceSection["McpServersMode"] = ToolProfileMode.Allowlist.ToString();
            audienceSection["AllowedMcpServers"] = serverList;
        }
    }

    private IReadOnlyList<string> GetKnownMcpServers(Dictionary<string, object> config)
    {
        var names = new List<string>();
        foreach (var server in Servers)
            AddServer(names, server.Name);

        if (ConfigFileHelper.TryGetPathValue(config, "McpServers", out var configuredServers)
            && configuredServers is Dictionary<string, object> configuredServerMap)
        {
            foreach (var serverName in configuredServerMap.Keys)
                AddServer(names, serverName);
        }

        return names;
    }

    private static void AddServer(List<string> serverList, string serverName)
    {
        if (!serverList.Contains(serverName, StringComparer.OrdinalIgnoreCase))
            serverList.Add(serverName);
    }

    private void SaveToolGrants(Dictionary<string, object> profilesSection)
    {
        foreach (var (serverName, audienceGrants) in _pendingGrants)
        {
            foreach (var (audienceName, tools) in audienceGrants)
            {
                var audienceSection = ConfigFileHelper.GetOrCreateSection(profilesSection, audienceName);
                var grants = ConfigFileHelper.GetOrCreateSection(audienceSection, "McpServerToolGrants");
                grants[serverName] = tools.Order(StringComparer.Ordinal).ToList();
            }
        }
    }

    private void SaveServerDefaults(Dictionary<string, object> profilesSection)
    {
        foreach (var ((audienceName, serverName), mode) in _pendingServerDefaults)
        {
            var (approvalSection, inMemoryPolicy) = GetOrCreateApprovalPolicy(profilesSection, audienceName);
            var serverDefaults = ConfigFileHelper.GetOrCreateSection(approvalSection, "McpServerDefaults");
            serverDefaults[serverName] = mode.ToString();
            inMemoryPolicy.McpServerDefaults[serverName] = mode;
        }
    }

    private void SaveToolOverrides(Dictionary<string, object> profilesSection)
    {
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
    }

    public void DiscardChanges()
    {
        _pendingGrants.Clear();
        _pendingServerAccess.Clear();
        _pendingServerDefaults.Clear();
        _pendingToolOverrides.Clear();
        StatusMessage.Value = "";
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
            if (!_pendingGrants.TryGetValue(SelectedServer, out var serverGrants))
            {
                serverGrants = [];
                _pendingGrants[SelectedServer] = serverGrants;
            }

            serverGrants[audienceName] = new HashSet<string>(DiscoveredTools, StringComparer.Ordinal);
        }
        else if (_pendingGrants.TryGetValue(SelectedServer, out var existingGrants))
        {
            existingGrants.Remove(audienceName);
            if (existingGrants.Count == 0)
                _pendingGrants.Remove(SelectedServer);
        }

        NotifyStateChanged();
    }

    private (Dictionary<string, object> ApprovalSection, ToolApprovalConfig InMemoryPolicy)
        GetOrCreateApprovalPolicy(Dictionary<string, object> profilesSection, string audienceName)
    {
        var audienceSection = ConfigFileHelper.GetOrCreateSection(profilesSection, audienceName);
        var approvalSection = ConfigFileHelper.GetOrCreateSection(audienceSection, "ApprovalPolicy");
        var profile = ResolveProfile(AudienceFromName(audienceName));
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

    private static TrustAudience AudienceFromName(string audienceName) => audienceName switch
    {
        "Public" => TrustAudience.Public,
        "Team" => TrustAudience.Team,
        _ => TrustAudience.Personal
    };

    public void RequestQuit()
    {
        if (_navigation?.TryGoBack() == true)
            return;

        Shutdown();
    }

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

    private void ApplyPendingNavigationState()
    {
        if (_navigationState?.ConsumeInitialAudience() is { } audience)
            SelectedAudience = audience;
    }

    public override void Dispose()
    {
        CurrentState.Dispose();
        StateVersion.Dispose();
        StatusMessage.Dispose();
        base.Dispose();
    }

    private ToolConfig LoadToolConfig()
    {
        if (!File.Exists(_paths.NetclawConfigPath))
            return new ToolConfig();

        var text = File.ReadAllText(_paths.NetclawConfigPath);
        using var doc = JsonDocument.Parse(text);

        if (!doc.RootElement.TryGetProperty("Tools", out var toolsSection))
            return new ToolConfig();

        return JsonSerializer.Deserialize<ToolConfig>(toolsSection.GetRawText(), JsonDefaults.EnumAware)
            ?? throw new InvalidDataException("Tools section could not be deserialized.");
    }
}
