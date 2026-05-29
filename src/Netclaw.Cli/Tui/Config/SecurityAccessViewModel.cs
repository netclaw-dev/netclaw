// -----------------------------------------------------------------------
// <copyright file="SecurityAccessViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Cli.Tui.Sections;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui.Config;

public sealed record SecurityAccessItem(string Label, string Summary, string Description, string? Route = null);

public enum SecurityAccessEditorMode
{
    Menu,
    Posture,
    PostureCascade,
    Features,
    AudienceList,
    AudienceProfile
}

public sealed record SecurityPostureOption(DeploymentPosture Value, string Label, string Description);
public sealed record SecurityAudienceOption(TrustAudience Value, string Label, string Description);
public sealed record SecurityCascadeOption(string Label, string Description);
public sealed record AudienceProfileRow(AudienceProfileRowKind Kind, string Label, string Description);

public enum AudienceProfileRowKind
{
    ReadFiles,
    EditFiles,
    WebAccess,
    Skills,
    Scheduling,
    ChangeWorkingDirectory,
    FileAccess,
    IncomingAttachments,
    McpPermissions,
    ResetToDefault
}

public sealed class SecurityAccessViewModel : ReactiveViewModel
{
    private const int FeatureCount = 6;
    private const string ShellToolName = "shell_execute";
    private static readonly string[] FeatureConfigPaths =
    [
        "Memory.Enabled",
        "Search.Enabled",
        "SkillSync.Enabled",
        "Scheduling.Enabled",
        "SubAgents.Enabled",
        "Webhooks.Enabled"
    ];

    private static readonly SecurityPostureOption[] Postures =
    [
        new(DeploymentPosture.Personal, "Personal", "Just me. Local-only by default. Tools have wide access."),
        new(DeploymentPosture.Team, "Team", "Small team via Slack/Discord. Audience-restricted tools."),
        new(DeploymentPosture.Public, "Public", "Open to untrusted users. Strict defaults and access controls.")
    ];

    private static readonly SecurityAudienceOption[] Audiences =
    [
        new(TrustAudience.Personal, "Personal", "Operator/local sessions."),
        new(TrustAudience.Team, "Team", "Trusted internal channels."),
        new(TrustAudience.Public, "Public", "Untrusted external users.")
    ];

    private static readonly SecurityCascadeOption[] CascadeOptions =
    [
        new("Cancel - keep current posture", "Do not change posture or audience profiles."),
        new("Apply new posture, overwrite profiles", "Reset all audience profiles to posture defaults."),
        new("Apply new posture, keep custom profiles", "Only change deployment posture and shell defaults.")
    ];

    private static readonly AudienceProfileRow[] AudienceRows =
    [
        new(AudienceProfileRowKind.ReadFiles, "Read files", "Read and list files within the file scope."),
        new(AudienceProfileRowKind.EditFiles, "Edit files", "Write or patch files within the file scope."),
        new(AudienceProfileRowKind.WebAccess, "Web access", "Use web_search and web_fetch."),
        new(AudienceProfileRowKind.Skills, "Skills", "Manage and load skills."),
        new(AudienceProfileRowKind.Scheduling, "Scheduling", "Create, list, cancel, and inspect reminders."),
        new(AudienceProfileRowKind.ChangeWorkingDirectory, "Change working directory", "Let sessions switch workspace roots."),
        new(AudienceProfileRowKind.FileAccess, "File access", "Cycle Off, Session only, or All files."),
        new(AudienceProfileRowKind.IncomingAttachments, "Incoming attachments", "Cycle attachment categories accepted from channels."),
        new(AudienceProfileRowKind.McpPermissions, "MCP permissions", "Managed in netclaw mcp permissions."),
        new(AudienceProfileRowKind.ResetToDefault, "Reset to posture default", "Replace this full audience profile with the posture default.")
    ];

    private static readonly string[] ReadFileTools = ["file_read", "file_list", "attach_file"];
    private static readonly string[] EditFileTools = ["file_write", "file_edit"];
    private static readonly string[] WebTools = ["web_search", "web_fetch"];
    private static readonly string[] SkillTools = ["skill_manage"];
    private static readonly string[] SchedulingTools = ["set_reminder", "list_reminders", "cancel_reminder", "get_reminder_history"];
    private static readonly string[] WorkingDirectoryTools = ["set_working_directory"];
    private static readonly string[] KnownFirstPartyTools =
    [
        "file_read", "file_list", "file_write", "file_edit", "attach_file",
        "web_search", "web_fetch", "skill_manage", "set_reminder",
        "list_reminders", "cancel_reminder", "get_reminder_history",
        "set_working_directory", ShellToolName, "set_webhook", "delete_webhook",
        "list_webhooks", "send_slack_message", "lookup_slack_user",
        "send_discord_message", "send_mattermost_message", "lookup_mattermost_user",
        "spawn_agent", "search_tools", "load_tool", "skill_load",
        "skill_read_resource", "store_memory", "get_memories", "update_memory",
        "find_memories", "check_background_job"
    ];

    private readonly NetclawPaths _paths;
    private readonly bool[] _enabledFeatures = new bool[FeatureCount];
    private DeploymentPosture? _pendingPosture;

    public SecurityAccessViewModel(NetclawPaths paths)
    {
        _paths = paths;
        LoadEnabledFeatures();
    }

    internal Action<string>? RouteRequested { get; set; }
    internal bool ShutdownRequestedForTest { get; private set; }

    public ReactiveProperty<string> StatusMessage { get; } = new("");
    public ReactiveProperty<SecurityAccessEditorMode> Mode { get; } = new(SecurityAccessEditorMode.Menu);
    public ReactiveProperty<int> SelectedIndex { get; } = new(0);
    public ReactiveProperty<int> SelectedPostureIndex { get; } = new(0);
    public ReactiveProperty<int> SelectedCascadeIndex { get; } = new(0);
    public ReactiveProperty<int> SelectedFeatureIndex { get; } = new(0);
    public ReactiveProperty<int> SelectedAudienceIndex { get; } = new(0);
    public ReactiveProperty<int> SelectedAudienceRowIndex { get; } = new(0);

    public IReadOnlyList<SecurityAccessItem> Items => BuildItems();
    public IReadOnlyList<SecurityPostureOption> PostureOptions => Postures;
    public IReadOnlyList<SecurityCascadeOption> PostureCascadeOptions => CascadeOptions;
    public IReadOnlyList<SecurityAudienceOption> AudienceOptions => Audiences;
    public IReadOnlyList<AudienceProfileRow> ProfileRows => AudienceRows;
    public IReadOnlyList<string> FeatureNames => FeatureSelectionStepViewModel.FeatureNames;
    public IReadOnlyList<string> FeatureDescriptions => FeatureSelectionStepViewModel.FeatureDescriptions;
    public TrustAudience SelectedAudience => Audiences[SelectedAudienceIndex.Value].Value;
    public DeploymentPosture CurrentPosture => ReadPosture(ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath));

    public void MoveSelection(int delta)
    {
        var items = Items;
        if (items.Count == 0)
            return;

        var next = Math.Clamp(SelectedIndex.Value + delta, 0, items.Count - 1);
        if (next != SelectedIndex.Value)
            SelectedIndex.Value = next;
    }

    public void MovePostureSelection(int delta) => Move(SelectedPostureIndex, delta, Postures.Length);
    public void MoveCascadeSelection(int delta) => Move(SelectedCascadeIndex, delta, CascadeOptions.Length);
    public void MoveFeatureSelection(int delta) => Move(SelectedFeatureIndex, delta, FeatureCount);
    public void MoveAudienceSelection(int delta) => Move(SelectedAudienceIndex, delta, Audiences.Length);
    public void MoveAudienceRow(int delta) => Move(SelectedAudienceRowIndex, delta, AudienceRows.Length);

    public void ActivateSelected()
    {
        switch (Mode.Value)
        {
            case SecurityAccessEditorMode.Menu:
                var items = Items;
                if (items.Count > 0)
                    Activate(items[SelectedIndex.Value]);
                break;
            case SecurityAccessEditorMode.Posture:
                ApplySelectedPosture();
                break;
            case SecurityAccessEditorMode.PostureCascade:
                ApplySelectedCascadeOption();
                break;
            case SecurityAccessEditorMode.Features:
                ToggleSelectedFeature();
                break;
            case SecurityAccessEditorMode.AudienceList:
                OpenSelectedAudienceProfile();
                break;
            case SecurityAccessEditorMode.AudienceProfile:
                ActivateSelectedAudienceProfileRow();
                break;
        }
    }

    internal void Activate(SecurityAccessItem item)
    {
        switch (item.Label)
        {
            case "Security Posture":
                OpenPostureEditor();
                return;
            case "Enabled Features":
                OpenFeatureEditor();
                return;
            case "Audience Profiles":
                OpenAudienceList();
                return;
        }

        if (item.Route is not null)
        {
            RouteRequested?.Invoke(item.Route);
            Navigate?.Invoke(item.Route);
            return;
        }

        StatusMessage.Value = $"{item.Label} is not implemented yet in `netclaw config`.";
        RequestRedraw();
    }

    public void GoBack()
    {
        switch (Mode.Value)
        {
            case SecurityAccessEditorMode.AudienceProfile:
                Mode.Value = SecurityAccessEditorMode.AudienceList;
                StatusMessage.Value = "";
                RequestRedraw();
                return;
            case SecurityAccessEditorMode.PostureCascade:
                Mode.Value = SecurityAccessEditorMode.Posture;
                _pendingPosture = null;
                StatusMessage.Value = "";
                RequestRedraw();
                return;
            case SecurityAccessEditorMode.Posture:
            case SecurityAccessEditorMode.Features:
            case SecurityAccessEditorMode.AudienceList:
                Mode.Value = SecurityAccessEditorMode.Menu;
                StatusMessage.Value = "";
                RequestRedraw();
                return;
        }

        RouteRequested?.Invoke("/config");
        Navigate?.Invoke("/config");
    }

    public void OpenPostureEditor()
    {
        var current = CurrentPosture;
        var index = Array.FindIndex(Postures, option => option.Value == current);
        SelectedPostureIndex.Value = index < 0 ? 0 : index;
        Mode.Value = SecurityAccessEditorMode.Posture;
        StatusMessage.Value = "";
        RequestRedraw();
    }

    public void ApplySelectedPosture()
    {
        var posture = Postures[SelectedPostureIndex.Value].Value;
        if (posture == CurrentPosture)
        {
            StatusMessage.Value = $"{posture} posture is already active.";
            RequestRedraw();
            return;
        }

        _pendingPosture = posture;
        if (AudienceProfilesCustomized())
        {
            SelectedCascadeIndex.Value = 0;
            Mode.Value = SecurityAccessEditorMode.PostureCascade;
            StatusMessage.Value = "";
            RequestRedraw();
            return;
        }

        SavePosture(posture, overwriteProfiles: true);
    }

    public void ApplySelectedCascadeOption()
    {
        if (_pendingPosture is not { } posture)
        {
            Mode.Value = SecurityAccessEditorMode.Posture;
            return;
        }

        switch (SelectedCascadeIndex.Value)
        {
            case 0:
                _pendingPosture = null;
                Mode.Value = SecurityAccessEditorMode.Posture;
                StatusMessage.Value = "Posture change cancelled.";
                RequestRedraw();
                break;
            case 1:
                SavePosture(posture, overwriteProfiles: true);
                break;
            case 2:
                SavePosture(posture, overwriteProfiles: false);
                break;
        }
    }

    public void OpenFeatureEditor()
    {
        LoadEnabledFeatures();
        Mode.Value = SecurityAccessEditorMode.Features;
        StatusMessage.Value = "";
        RequestRedraw();
    }

    public bool IsFeatureEnabled(int index) => _enabledFeatures[index];

    public void ToggleSelectedFeature()
    {
        var index = SelectedFeatureIndex.Value;
        _enabledFeatures[index] = !_enabledFeatures[index];

        var session = new ConfigEditorSession(_paths);
        session.Apply(BuildFeatureContribution());
        session.Save();

        var state = _enabledFeatures[index] ? "enabled" : "disabled";
        StatusMessage.Value = $"{FeatureNames[index]} {state}. Saved.";
        RequestRedraw();
    }

    public void OpenAudienceList()
    {
        Mode.Value = SecurityAccessEditorMode.AudienceList;
        StatusMessage.Value = "";
        RequestRedraw();
    }

    public void OpenSelectedAudienceProfile()
    {
        SelectedAudienceRowIndex.Value = 0;
        Mode.Value = SecurityAccessEditorMode.AudienceProfile;
        StatusMessage.Value = "";
        RequestRedraw();
    }

    public string AudienceSummary(TrustAudience audience)
    {
        var profiles = LoadAudienceProfiles();
        var current = GetProfile(profiles, audience);
        var defaults = GetProfile(BuildPostureProfiles(CurrentPosture), audience);
        return JsonEquivalent(current, defaults) ? $"Default for posture: {CurrentPosture}" : "Customized";
    }

    public bool IsAudienceToggleEnabled(AudienceProfileRowKind kind)
    {
        var profile = GetSelectedProfile();
        return kind switch
        {
            AudienceProfileRowKind.ReadFiles => ToolGroupEnabled(profile, ReadFileTools),
            AudienceProfileRowKind.EditFiles => ToolGroupEnabled(profile, EditFileTools),
            AudienceProfileRowKind.WebAccess => ToolGroupEnabled(profile, WebTools),
            AudienceProfileRowKind.Skills => ToolGroupEnabled(profile, SkillTools),
            AudienceProfileRowKind.Scheduling => ToolGroupEnabled(profile, SchedulingTools),
            AudienceProfileRowKind.ChangeWorkingDirectory => ToolGroupEnabled(profile, WorkingDirectoryTools),
            _ => false
        };
    }

    public string AudienceValue(AudienceProfileRowKind kind)
    {
        var profile = GetSelectedProfile();
        return kind switch
        {
            AudienceProfileRowKind.FileAccess => DescribeFilesystem(profile),
            AudienceProfileRowKind.IncomingAttachments => DescribeAttachments(profile.ChannelAttachments),
            AudienceProfileRowKind.McpPermissions => "Manage separately",
            AudienceProfileRowKind.ResetToDefault => "",
            _ => IsAudienceToggleEnabled(kind) ? "Enabled" : "Disabled"
        };
    }

    public void ActivateSelectedAudienceProfileRow()
    {
        var row = AudienceRows[SelectedAudienceRowIndex.Value];
        switch (row.Kind)
        {
            case AudienceProfileRowKind.ReadFiles:
                ToggleToolGroup(row.Kind, ReadFileTools);
                return;
            case AudienceProfileRowKind.EditFiles:
                ToggleToolGroup(row.Kind, EditFileTools);
                return;
            case AudienceProfileRowKind.WebAccess:
                ToggleToolGroup(row.Kind, WebTools);
                return;
            case AudienceProfileRowKind.Skills:
                ToggleToolGroup(row.Kind, SkillTools);
                return;
            case AudienceProfileRowKind.Scheduling:
                ToggleToolGroup(row.Kind, SchedulingTools);
                return;
            case AudienceProfileRowKind.ChangeWorkingDirectory:
                ToggleToolGroup(row.Kind, WorkingDirectoryTools);
                return;
            case AudienceProfileRowKind.FileAccess:
                CycleFileAccess();
                return;
            case AudienceProfileRowKind.IncomingAttachments:
                CycleIncomingAttachments();
                return;
            case AudienceProfileRowKind.McpPermissions:
                StatusMessage.Value = "Run `netclaw mcp permissions` to edit MCP server and tool grants.";
                RequestRedraw();
                return;
            case AudienceProfileRowKind.ResetToDefault:
                ResetSelectedAudienceProfile();
                return;
        }
    }

    public void ResetSelectedAudienceProfile()
    {
        var profiles = BuildPostureProfiles(CurrentPosture);
        SaveAudienceProfile(GetProfile(profiles, SelectedAudience));
        StatusMessage.Value = $"{AudienceLabel(SelectedAudience)} profile reset to {CurrentPosture} defaults.";
        RequestRedraw();
    }

    public void RequestQuit()
    {
        ShutdownRequestedForTest = true;
        Shutdown();
    }

    public override void Dispose()
    {
        StatusMessage.Dispose();
        Mode.Dispose();
        SelectedIndex.Dispose();
        SelectedPostureIndex.Dispose();
        SelectedCascadeIndex.Dispose();
        SelectedFeatureIndex.Dispose();
        SelectedAudienceIndex.Dispose();
        SelectedAudienceRowIndex.Dispose();
        base.Dispose();
    }

    private void SavePosture(DeploymentPosture posture, bool overwriteProfiles)
    {
        var shellMode = posture == DeploymentPosture.Personal
            ? ShellExecutionMode.HostAllowed
            : ShellExecutionMode.Off;

        var fieldActions = new List<SectionFieldAction>
        {
            new("Security.DeploymentPosture", SectionFieldActionKind.Set, posture.ToString()),
            new("Security.ShellExecutionMode", SectionFieldActionKind.Set, shellMode.ToString()),
            new("Security.StrictDefaults", SectionFieldActionKind.Set, true),
            new("Tools.ShellMode", SectionFieldActionKind.Set, shellMode.ToString())
        };

        if (overwriteProfiles)
            fieldActions.Add(new SectionFieldAction("Tools.AudienceProfiles", SectionFieldActionKind.Set, BuildPostureProfiles(posture)));

        var session = new ConfigEditorSession(_paths);
        session.Apply(new SectionContribution(fieldActions));
        session.Save();

        _pendingPosture = null;
        StatusMessage.Value = overwriteProfiles
            ? $"{posture} posture saved and audience profiles reset."
            : $"{posture} posture saved; custom audience profiles preserved.";
        Mode.Value = posture == DeploymentPosture.Personal
            ? SecurityAccessEditorMode.Menu
            : SecurityAccessEditorMode.Features;
        LoadEnabledFeatures();
        RequestRedraw();
    }

    private void ToggleToolGroup(AudienceProfileRowKind kind, IReadOnlyList<string> tools)
    {
        var profiles = LoadAudienceProfiles();
        var profile = GetProfile(profiles, SelectedAudience);
        var enabled = ToolGroupEnabled(profile, tools);
        EnsureAllowlist(profile);
        if (enabled)
            profile.AllowedTools.RemoveAll(tool => tools.Contains(tool, StringComparer.Ordinal));
        else
            AddTools(profile.AllowedTools, tools);

        SaveAudienceProfile(profile);
        StatusMessage.Value = $"{AudienceLabel(SelectedAudience)} {AudienceRows.Single(row => row.Kind == kind).Label} {(enabled ? "disabled" : "enabled")}. Saved.";
        RequestRedraw();
    }

    private void CycleFileAccess()
    {
        var profiles = LoadAudienceProfiles();
        var profile = GetProfile(profiles, SelectedAudience);
        var next = CurrentFilesystemLevel(profile) switch
        {
            FilesystemLevel.Off => FilesystemLevel.SessionOnly,
            FilesystemLevel.SessionOnly => FilesystemLevel.AllFiles,
            _ => FilesystemLevel.Off
        };

        ApplyFilesystemLevel(profile, next);
        SaveAudienceProfile(profile);
        StatusMessage.Value = $"{AudienceLabel(SelectedAudience)} file access set to {DescribeFilesystem(profile)}. Saved.";
        RequestRedraw();
    }

    private void CycleIncomingAttachments()
    {
        var profiles = LoadAudienceProfiles();
        var profile = GetProfile(profiles, SelectedAudience);
        var next = CurrentAttachmentLevel(profile.ChannelAttachments) switch
        {
            AttachmentLevel.None => AttachmentLevel.Images,
            AttachmentLevel.Images => AttachmentLevel.CommonWorkFiles,
            AttachmentLevel.CommonWorkFiles => AttachmentLevel.All,
            _ => AttachmentLevel.None
        };

        profile.ChannelAttachments = BuildAttachmentPolicy(next);
        SaveAudienceProfile(profile);
        StatusMessage.Value = $"{AudienceLabel(SelectedAudience)} attachments set to {DescribeAttachments(profile.ChannelAttachments)}. Saved.";
        RequestRedraw();
    }

    private void SaveAudienceProfile(ToolAudienceProfile profile)
    {
        var session = new ConfigEditorSession(_paths);
        session.Apply(new SectionContribution(
        [
            new SectionFieldAction($"Tools.AudienceProfiles.{AudienceConfigName(SelectedAudience)}", SectionFieldActionKind.Set, profile)
        ]));
        session.Save();
    }

    private ToolAudienceProfile GetSelectedProfile()
        => GetProfile(LoadAudienceProfiles(), SelectedAudience);

    private ToolAudienceProfiles LoadAudienceProfiles()
    {
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        if (!ConfigFileHelper.TryGetPathValue(config, "Tools.AudienceProfiles", out var value) || value is null)
            return BuildPostureProfiles(ReadPosture(config));

        return ConvertConfigObject<ToolAudienceProfiles>(value, "Tools.AudienceProfiles");
    }

    private bool AudienceProfilesCustomized()
    {
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        if (!ConfigFileHelper.TryGetPathValue(config, "Tools.AudienceProfiles", out var value) || value is null)
            return false;

        var existing = ConvertConfigObject<ToolAudienceProfiles>(value, "Tools.AudienceProfiles");
        var defaults = BuildPostureProfiles(ReadPosture(config));
        return !JsonEquivalent(existing, defaults);
    }

    private void LoadEnabledFeatures()
    {
        Array.Fill(_enabledFeatures, true);
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        for (var i = 0; i < FeatureConfigPaths.Length; i++)
        {
            if (ConfigFileHelper.TryGetPathValue(config, FeatureConfigPaths[i], out var value) && value is bool enabled)
                _enabledFeatures[i] = enabled;
        }
    }

    private SectionContribution BuildFeatureContribution()
        => new(
        [
            new SectionFieldAction(FeatureConfigPaths[0], SectionFieldActionKind.Set, _enabledFeatures[0]),
            new SectionFieldAction(FeatureConfigPaths[1], SectionFieldActionKind.Set, _enabledFeatures[1]),
            new SectionFieldAction(FeatureConfigPaths[2], SectionFieldActionKind.Set, _enabledFeatures[2]),
            new SectionFieldAction(FeatureConfigPaths[3], SectionFieldActionKind.Set, _enabledFeatures[3]),
            new SectionFieldAction(FeatureConfigPaths[4], SectionFieldActionKind.Set, _enabledFeatures[4]),
            new SectionFieldAction(FeatureConfigPaths[5], SectionFieldActionKind.Set, _enabledFeatures[5])
        ]);

    private IReadOnlyList<SecurityAccessItem> BuildItems()
    {
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        return
        [
            new("Security Posture", ReadPosture(config).ToString(), "Deployment trust stance."),
            new("Enabled Features", ReadEnabledFeaturesSummary(config), "Deployment-wide runtime feature gates."),
            new("Audience Profiles", ReadAudienceProfilesSummary(config), "Curated per-audience access rules."),
            new("Exposure Mode", ReadExposureModeSummary(config), "Daemon reachability and tunnel topology.", "/exposure-mode")
        ];
    }

    private static string ReadEnabledFeaturesSummary(Dictionary<string, object> config)
    {
        var enabled = 0;
        foreach (var path in FeatureConfigPaths)
        {
            var flag = true;
            if (ConfigFileHelper.TryGetPathValue(config, path, out var value) && value is bool configuredFlag)
                flag = configuredFlag;

            if (flag)
                enabled++;
        }

        return $"{enabled}/{FeatureConfigPaths.Length} enabled";
    }

    private static string ReadAudienceProfilesSummary(Dictionary<string, object> config)
    {
        if (!ConfigFileHelper.TryGetPathValue(config, "Tools.AudienceProfiles", out var value) || value is null)
            return "Defaults";

        var existing = ConvertConfigObject<ToolAudienceProfiles>(value, "Tools.AudienceProfiles");
        var defaults = BuildPostureProfiles(ReadPosture(config));
        return JsonEquivalent(existing, defaults) ? "Defaults" : "Customized";
    }

    private static DeploymentPosture ReadPosture(Dictionary<string, object> config)
    {
        if (ConfigFileHelper.TryGetPathValue(config, "Security.DeploymentPosture", out var value)
            && value is string posture
            && Enum.TryParse<DeploymentPosture>(posture, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return DeploymentPosture.Personal;
    }

    private static string ReadExposureModeSummary(Dictionary<string, object> config)
    {
        var mode = ExposureMode.Local;
        if (ConfigFileHelper.TryGetPathValue(config, "Daemon.ExposureMode", out var value))
            mode = DaemonConfig.ParseExposureMode(value?.ToString());

        return mode switch
        {
            ExposureMode.Local => "Local",
            ExposureMode.ReverseProxy => "Reverse Proxy",
            ExposureMode.TailscaleServe => "Tailscale Serve",
            ExposureMode.TailscaleFunnel => "Tailscale Funnel",
            ExposureMode.CloudflareTunnel => "Cloudflare Tunnel",
            _ => mode.ToString()
        };
    }

    private static ToolAudienceProfiles BuildPostureProfiles(DeploymentPosture posture)
    {
        var profiles = ToolAudienceProfileDefaults.CreateProfiles();
        if (posture == DeploymentPosture.Personal)
        {
            profiles.Personal.ApprovalPolicy = new ToolApprovalConfig
            {
                ToolOverrides = new Dictionary<string, ToolApprovalMode>(StringComparer.Ordinal)
                {
                    [ShellToolName] = ToolApprovalMode.Approval
                }
            };
        }

        return profiles;
    }

    private static ToolAudienceProfile GetProfile(ToolAudienceProfiles profiles, TrustAudience audience)
        => audience switch
        {
            TrustAudience.Personal => profiles.Personal,
            TrustAudience.Team => profiles.Team,
            TrustAudience.Public => profiles.Public,
            _ => profiles.Public
        };

    private static string AudienceLabel(TrustAudience audience)
        => audience switch
        {
            TrustAudience.Personal => "Personal",
            TrustAudience.Team => "Team",
            TrustAudience.Public => "Public",
            _ => audience.ToString()
        };

    private static string AudienceConfigName(TrustAudience audience) => AudienceLabel(audience);

    private static bool ToolGroupEnabled(ToolAudienceProfile profile, IReadOnlyList<string> tools)
        => profile.ToolsMode == ToolProfileMode.All
           || tools.All(tool => profile.AllowedTools.Contains(tool, StringComparer.Ordinal));

    private static void EnsureAllowlist(ToolAudienceProfile profile)
    {
        if (profile.ToolsMode == ToolProfileMode.Allowlist)
            return;

        profile.ToolsMode = ToolProfileMode.Allowlist;
        profile.AllowedTools = [.. KnownFirstPartyTools];
    }

    private static void AddTools(List<string> target, IReadOnlyList<string> tools)
    {
        foreach (var tool in tools)
        {
            if (!target.Contains(tool, StringComparer.Ordinal))
                target.Add(tool);
        }
    }

    private static FilesystemLevel CurrentFilesystemLevel(ToolAudienceProfile profile)
    {
        var modes = new[] { profile.ReadFiles.Mode, profile.WriteFiles.Mode, profile.AttachFiles.Mode };
        if (modes.All(static mode => mode == ToolFilesystemMode.All))
            return FilesystemLevel.AllFiles;
        if (modes.All(static mode => mode == ToolFilesystemMode.None))
            return FilesystemLevel.Off;
        return FilesystemLevel.SessionOnly;
    }

    private static void ApplyFilesystemLevel(ToolAudienceProfile profile, FilesystemLevel level)
    {
        profile.ReadFiles = BuildFilesystemAccess(level);
        profile.WriteFiles = BuildFilesystemAccess(level);
        profile.AttachFiles = BuildFilesystemAccess(level);
    }

    private static ToolFilesystemAccessProfile BuildFilesystemAccess(FilesystemLevel level)
        => level switch
        {
            FilesystemLevel.Off => new ToolFilesystemAccessProfile { Mode = ToolFilesystemMode.None, Roots = [] },
            FilesystemLevel.AllFiles => new ToolFilesystemAccessProfile { Mode = ToolFilesystemMode.All, Roots = [] },
            _ => ToolAudienceProfileDefaults.CreateSessionScopedFilesystemAccess()
        };

    private static string DescribeFilesystem(ToolAudienceProfile profile)
        => CurrentFilesystemLevel(profile) switch
        {
            FilesystemLevel.Off => "Off",
            FilesystemLevel.AllFiles => "All files",
            _ => "Session only"
        };

    private static AttachmentLevel CurrentAttachmentLevel(ChannelAttachmentPolicy? policy)
    {
        if (policy is null || policy.AllowedCategories.Count == 0)
            return AttachmentLevel.None;

        var categories = policy.AllowedCategories;
        if (Enum.GetValues<AttachmentCategory>().All(category => categories.Contains(category)))
            return AttachmentLevel.All;
        if (categories.Count == 1 && categories.Contains(AttachmentCategory.Image))
            return AttachmentLevel.Images;
        return AttachmentLevel.CommonWorkFiles;
    }

    private static ChannelAttachmentPolicy BuildAttachmentPolicy(AttachmentLevel level)
        => level switch
        {
            AttachmentLevel.None => ChannelAttachmentPolicy.Empty,
            AttachmentLevel.Images => ToolAudienceProfileDefaults.CreatePublicChannelAttachments(),
            AttachmentLevel.All => ToolAudienceProfileDefaults.CreatePersonalChannelAttachments(),
            _ => ToolAudienceProfileDefaults.CreateTeamChannelAttachments()
        };

    private static string DescribeAttachments(ChannelAttachmentPolicy? policy)
        => CurrentAttachmentLevel(policy) switch
        {
            AttachmentLevel.None => "None",
            AttachmentLevel.Images => "Images",
            AttachmentLevel.All => "All attachments",
            _ => "Common work files"
        };

    private static bool JsonEquivalent<T>(T left, T right)
        => JsonSerializer.Serialize(left, JsonDefaults.ConfigFile) == JsonSerializer.Serialize(right, JsonDefaults.ConfigFile);

    private static T ConvertConfigObject<T>(object value, string path)
    {
        try
        {
            var json = value is JsonElement element
                ? element.GetRawText()
                : JsonSerializer.Serialize(value, JsonDefaults.ConfigFile);
            return JsonSerializer.Deserialize<T>(json, JsonDefaults.ConfigRead)
                   ?? throw new InvalidOperationException($"{path} was empty.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Unable to read {path} from config.", ex);
        }
    }

    private static void Move(ReactiveProperty<int> index, int delta, int count)
    {
        if (count == 0)
            return;

        var next = Math.Clamp(index.Value + delta, 0, count - 1);
        if (next != index.Value)
            index.Value = next;
    }

    private enum FilesystemLevel
    {
        Off,
        SessionOnly,
        AllFiles
    }

    private enum AttachmentLevel
    {
        None,
        Images,
        CommonWorkFiles,
        All
    }
}
