// -----------------------------------------------------------------------
// <copyright file="SecurityAccessViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Config;
using Netclaw.Cli.Json;
using Netclaw.Cli.Mcp;
using Netclaw.Cli.Tui.Sections;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Media;
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
    FileTools,
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
        new(AudienceProfileRowKind.FileTools, "File tools", "Read, attach, write, and edit files."),
        new(AudienceProfileRowKind.WebAccess, "Web", "web_search and web_fetch."),
        new(AudienceProfileRowKind.Skills, "Skills", "Skill management tools."),
        new(AudienceProfileRowKind.Scheduling, "Scheduling", "Reminder tools."),
        new(AudienceProfileRowKind.ChangeWorkingDirectory, "Change workspace", "Allow workspace switching."),
        new(AudienceProfileRowKind.FileAccess, "File scope", "Filesystem scope for file tools."),
        new(AudienceProfileRowKind.IncomingAttachments, "Attachments", "Accepted channel attachment types."),
        new(AudienceProfileRowKind.McpPermissions, "MCP grants", "Managed separately."),
        new(AudienceProfileRowKind.ResetToDefault, "Reset overrides", "Restore this audience to the current posture baseline.")
    ];

    private static IReadOnlyList<string> FileTools => ToolAudienceProfileToolCatalog.FileTools;
    private static IReadOnlyList<string> WebTools => ToolAudienceProfileToolCatalog.WebTools;
    private static IReadOnlyList<string> SkillTools => ToolAudienceProfileToolCatalog.SkillTools;
    private static IReadOnlyList<string> SchedulingTools => ToolAudienceProfileToolCatalog.SchedulingTools;
    private static IReadOnlyList<string> WorkingDirectoryTools => ToolAudienceProfileToolCatalog.WorkingDirectoryTools;

    private readonly NetclawPaths _paths;
    private readonly McpToolPermissionsNavigationState? _mcpNavigationState;
    private readonly bool[] _enabledFeatures = new bool[FeatureConfigPaths.Length];
    private DeploymentPosture? _pendingPosture;

    public SecurityAccessViewModel(
        NetclawPaths paths,
        McpToolPermissionsNavigationState? mcpNavigationState = null)
    {
        _paths = paths;
        _mcpNavigationState = mcpNavigationState;
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

    /// <summary>
    /// Non-null when <c>Security.DeploymentPosture</c> holds an unrecognized value. The editor fails
    /// closed (<see cref="CurrentPosture"/> reports <see cref="DeploymentPosture.Public"/>) and
    /// surfaces this so the operator sees the config is corrupt instead of the editor silently
    /// assuming a posture.
    /// </summary>
    public string? PostureConfigWarning =>
        TryReadPosture(ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath), out _, out var invalid)
            ? null
            : $"Unknown deployment posture '{invalid}' in config — treating as Public (most restrictive). Fix Security.DeploymentPosture.";
    public string SelectedAudienceOverrideStatus => AudienceHasOverrides(SelectedAudience) ? "Customized overrides" : "No custom overrides";

    public void MoveSelection(int delta)
    {
        var items = Items;
        if (items.Count == 0)
            return;

        var next = Math.Clamp(SelectedIndex.Value + delta, 0, items.Count - 1);
        if (next != SelectedIndex.Value)
            SelectedIndex.Value = next;
    }

    // Each editor appends a "Done" row after its real items (index == item count), so navigation extends one
    // past the array; activation at that index backs out instead of acting on a row (see the action guards).
    public void MovePostureSelection(int delta) => Move(SelectedPostureIndex, delta, Postures.Length + 1);
    public void MoveCascadeSelection(int delta) => Move(SelectedCascadeIndex, delta, CascadeOptions.Length);
    public void MoveFeatureSelection(int delta) => Move(SelectedFeatureIndex, delta, FeatureConfigPaths.Length + 1);
    public void MoveAudienceSelection(int delta) => Move(SelectedAudienceIndex, delta, Audiences.Length + 1);
    public void MoveAudienceRow(int delta) => Move(SelectedAudienceRowIndex, delta, AudienceRows.Length + 1);

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
            case "Done":
                // Discoverable equivalent of Esc — back out to the config dashboard.
                GoBack();
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
        if (SelectedPostureIndex.Value >= Postures.Length)
        {
            GoBack();
            return;
        }

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
        if (SelectedFeatureIndex.Value >= FeatureConfigPaths.Length)
        {
            GoBack();
            return;
        }

        var index = SelectedFeatureIndex.Value;
        _enabledFeatures[index] = !_enabledFeatures[index];

        if (!TryApplyAndSave(BuildFeatureContribution(), "enabled features"))
        {
            // Roll the in-memory flip back so a failed save leaves the toggle state and disk in
            // agreement — BuildFeatureContribution serializes the whole array, so a toggle that
            // never reached disk must not "stick" in memory.
            _enabledFeatures[index] = !_enabledFeatures[index];
            return;
        }

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
        if (SelectedAudienceIndex.Value >= Audiences.Length)
        {
            GoBack();
            return;
        }

        SelectedAudienceRowIndex.Value = 0;
        Mode.Value = SecurityAccessEditorMode.AudienceProfile;
        StatusMessage.Value = "";
        RequestRedraw();
    }

    public bool IsSystemDefaultAudience(TrustAudience audience)
        => audience switch
        {
            TrustAudience.Personal => CurrentPosture == DeploymentPosture.Personal,
            TrustAudience.Team => CurrentPosture == DeploymentPosture.Team,
            TrustAudience.Public => CurrentPosture == DeploymentPosture.Public,
            _ => false
        };

    public string AudienceOverrideMarker(TrustAudience audience) => AudienceHasOverrides(audience) ? "Customized" : "";

    public bool IsAudienceToggleEnabled(AudienceProfileRowKind kind)
    {
        var profile = GetSelectedProfile();
        return kind switch
        {
            AudienceProfileRowKind.FileTools => ToolGroupEnabled(profile, FileTools),
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
            AudienceProfileRowKind.McpPermissions => "netclaw mcp permissions",
            AudienceProfileRowKind.ResetToDefault => "",
            _ => IsAudienceToggleEnabled(kind) ? "Enabled" : "Disabled"
        };
    }

    public void ActivateSelectedAudienceProfileRow()
    {
        if (SelectedAudienceRowIndex.Value >= AudienceRows.Length)
        {
            GoBack();
            return;
        }

        var row = AudienceRows[SelectedAudienceRowIndex.Value];
        switch (row.Kind)
        {
            case AudienceProfileRowKind.FileTools:
                ToggleToolGroup(row.Kind, FileTools);
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
                CycleFileAccess(1);
                return;
            case AudienceProfileRowKind.IncomingAttachments:
                CycleIncomingAttachments(1);
                return;
            case AudienceProfileRowKind.McpPermissions:
                _mcpNavigationState?.RequestInitialAudience(SelectedAudience);
                RouteRequested?.Invoke("/mcp-tools");
                Navigate?.Invoke("/mcp-tools");
                return;
            case AudienceProfileRowKind.ResetToDefault:
                ResetSelectedAudienceProfile();
                return;
        }
    }

    public void ChangeSelectedAudienceProfileRow(int direction)
    {
        if (SelectedAudienceRowIndex.Value >= AudienceRows.Length)
            return; // the Done row has no value to cycle with ←/→

        var row = AudienceRows[SelectedAudienceRowIndex.Value];
        switch (row.Kind)
        {
            case AudienceProfileRowKind.FileAccess:
                CycleFileAccess(direction);
                return;
            case AudienceProfileRowKind.IncomingAttachments:
                CycleIncomingAttachments(direction);
                return;
        }
    }

    public string AudienceRowHelp(AudienceProfileRowKind kind)
    {
        var profile = GetSelectedProfile();
        return kind switch
        {
            AudienceProfileRowKind.FileTools => "File tools grant read/list/attach/write/edit; File scope below limits where they can operate.",
            AudienceProfileRowKind.WebAccess => "Web grants web_search and web_fetch for this audience.",
            AudienceProfileRowKind.Skills => "Skills grants skill management and loading tools for this audience.",
            AudienceProfileRowKind.Scheduling => "Scheduling grants reminder create/list/cancel/history/run tools.",
            AudienceProfileRowKind.ChangeWorkingDirectory => "Change workspace lets sessions switch workspace roots.",
            AudienceProfileRowKind.FileAccess => DescribeFilesystemHelp(profile),
            AudienceProfileRowKind.IncomingAttachments => DescribeAttachmentHelp(profile.ChannelAttachments),
            AudienceProfileRowKind.McpPermissions => "MCP server and per-tool grants are managed in the dedicated MCP permissions editor.",
            AudienceProfileRowKind.ResetToDefault => "Reset overrides restores this audience to the current global posture baseline, including hidden MCP and approval settings.",
            _ => string.Empty
        };
    }

    public void ResetSelectedAudienceProfile()
    {
        var profiles = BuildPostureProfiles(CurrentPosture);
        SaveAudienceProfile(GetProfile(profiles, SelectedAudience));
        StatusMessage.Value = $"{AudienceLabel(SelectedAudience)} overrides reset to the {CurrentPosture} posture baseline.";
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

        if (!TryApplyAndSave(new SectionContribution(fieldActions), "security posture"))
            return;

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
        var profiles = LoadAudienceProfiles(out var malformed);
        var profile = GetProfile(profiles, SelectedAudience);
        var enabled = ToolGroupEnabled(profile, tools);
        EnsureAllowlist(profile);
        if (enabled)
            profile.AllowedTools.RemoveAll(tool => tools.Contains(tool, StringComparer.Ordinal));
        else
            AddTools(profile.AllowedTools, tools);

        if (!SaveAudienceProfileKeys(malformed,
                ("ToolsMode", profile.ToolsMode.ToString()),
                ("AllowedTools", profile.AllowedTools)))
            return;
        StatusMessage.Value = $"{AudienceLabel(SelectedAudience)} {AudienceRows.Single(row => row.Kind == kind).Label} {(enabled ? "disabled" : "enabled")}. Saved.";
        RequestRedraw();
    }

    private void CycleFileAccess(int direction)
    {
        var profiles = LoadAudienceProfiles(out var malformed);
        var profile = GetProfile(profiles, SelectedAudience);
        var next = CycleValue(CurrentFilesystemLevel(profile), FilesystemLevelsFor(SelectedAudience), direction);

        ApplyFilesystemLevel(profile, next);
        if (!SaveAudienceProfileKeys(malformed,
                ("ReadFiles", profile.ReadFiles),
                ("WriteFiles", profile.WriteFiles),
                ("AttachFiles", profile.AttachFiles)))
            return;
        StatusMessage.Value = $"{AudienceLabel(SelectedAudience)} file access set to {DescribeFilesystem(profile)}. Saved.";
        RequestRedraw();
    }

    private void CycleIncomingAttachments(int direction)
    {
        var profiles = LoadAudienceProfiles(out var malformed);
        var profile = GetProfile(profiles, SelectedAudience);
        var next = CycleValue(CurrentAttachmentLevel(profile.ChannelAttachments), AttachmentLevels, direction);

        profile.ChannelAttachments = BuildAttachmentPolicy(next);
        if (!SaveAudienceProfileKeys(malformed, ("ChannelAttachments", profile.ChannelAttachments)))
            return;
        StatusMessage.Value = $"{AudienceLabel(SelectedAudience)} attachments set to {DescribeAttachments(profile.ChannelAttachments)}. Saved.";
        RequestRedraw();
    }

    // An edit writes only the keys that it changed. A write of the whole profile would store the
    // posture defaults and the bound MCP and approval values as operator decisions, and it would
    // replace keys that this screen does not show. When the stored profiles do not bind, the screen
    // shows the posture baseline, and an edit of that view would replace what the operator stored.
    // The edit is refused then.
    private bool SaveAudienceProfileKeys(bool malformed, params (string Key, object Value)[] changed)
    {
        if (malformed)
        {
            StatusMessage.Value = "Tools.AudienceProfiles is unreadable, so this edit was not saved. "
                + "Fix the value in netclaw.json, or use \"Reset overrides\" to replace this audience with the posture baseline.";
            RequestRedraw();
            return false;
        }

        // Use the key spelling that the file already has. See ResolveExistingKeyPath.
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        var profilePath = $"Tools.AudienceProfiles.{AudienceConfigName(SelectedAudience)}";
        return TryApplyAndSave(
            new SectionContribution(
            [
                .. changed.Select(change => new SectionFieldAction(
                    ConfigFileHelper.ResolveExistingKeyPath(config, $"{profilePath}.{change.Key}"),
                    SectionFieldActionKind.Set,
                    change.Value))
            ]),
            "audience profile");
    }

    private bool SaveAudienceProfile(ToolAudienceProfile profile)
        => TryApplyAndSave(
            new SectionContribution(
            [
                new SectionFieldAction($"Tools.AudienceProfiles.{AudienceConfigName(SelectedAudience)}", SectionFieldActionKind.Set, profile)
            ]),
            "audience profile");

    // All ConfigEditorSession writes in this view-model funnel through here so a disk-full /
    // permission-denied / atomic-rename / malformed-config failure surfaces to the operator instead
    // of escalating as an unhandled exception that tears down the Termina event loop. Callers MUST
    // NOT advance their "Saved." status (or commit in-memory state) when this returns false.
    private bool TryApplyAndSave(SectionContribution contribution, string failureContext)
    {
        try
        {
            var session = new ConfigEditorSession(_paths);
            session.Apply(contribution);
            session.Save();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            StatusMessage.Value = $"Failed to save {failureContext}: {ex.Message}";
            RequestRedraw();
            return false;
        }
    }

    private ToolAudienceProfile GetSelectedProfile()
        => GetProfile(LoadAudienceProfiles(), SelectedAudience);

    private ToolAudienceProfiles LoadAudienceProfiles() => LoadAudienceProfiles(out _);

    // Reads the audience profiles the same way as the daemon: on top of the posture defaults, with
    // the daemon binder. A raw deserializer replaces a partial profile with an empty one, so an edit
    // of one row would then save empty allowlists for the keys that the file does not set (issue
    // #2362).
    //
    // The bind covers ONLY Tools.AudienceProfiles, with the posture from the fail-closed reader. A
    // fault in another key (an unknown posture, a bad Tools.WebFetch value) must not make the
    // profiles look unreadable: this screen is where the operator repairs the posture.
    //
    // `malformed` is true only when the profiles themselves do not bind. The posture baseline is
    // then returned for display, so the render path cannot throw. No caller may save it: a row edit
    // is refused, and only the explicit "Reset overrides" action replaces the stored profile.
    private ToolAudienceProfiles LoadAudienceProfiles(out bool malformed)
        => LoadAudienceProfiles(_paths, out malformed);

    private static ToolAudienceProfiles LoadAudienceProfiles(NetclawPaths paths, out bool malformed)
    {
        malformed = false;
        var config = ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);
        var posture = ReadPosture(config);
        var profilesPath = ConfigFileHelper.ResolveExistingKeyPath(config, "Tools.AudienceProfiles");
        if (!ConfigFileHelper.TryGetPathValue(config, profilesPath, out var raw) || raw is null)
            return BuildPostureProfiles(posture);

        if (raw is Dictionary<string, object>)
        {
            try
            {
                var tools = new Dictionary<string, object> { ["Tools"] = new Dictionary<string, object> { ["AudienceProfiles"] = raw } };
                using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(tools, JsonDefaults.ConfigFile));
                var section = new ConfigurationBuilder().AddJsonStream(stream).Build().GetSection("Tools");
                return ToolConfig.BindFromConfiguration(section, posture, out _).AudienceProfiles;
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or FormatException or ArgumentException)
            {
                malformed = true;
            }
        }
        else
        {
            // The binder ignores a scalar where it expects an object. The operator must see it.
            malformed = true;
        }

        return BuildPostureProfiles(posture);
    }

    // Unreadable profiles count as customized: a posture change must ask before it replaces them.
    private bool AudienceProfilesCustomized()
    {
        var existing = LoadAudienceProfiles(out var malformed);
        return malformed || !JsonEquivalent(existing, BuildPostureProfiles(CurrentPosture));
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
        => new(FeatureConfigPaths
            .Select((path, index) => new SectionFieldAction(path, SectionFieldActionKind.Set, _enabledFeatures[index]))
            .ToArray());

    private IReadOnlyList<SecurityAccessItem> BuildItems()
    {
        var config = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
        TryReadPosture(config, out var posture, out var invalidPosture);
        return
        [
            new("Security Posture",
                invalidPosture is null ? posture.ToString() : $"Unknown ('{invalidPosture}') — using Public",
                "Deployment trust stance."),
            new("Enabled Features", ReadEnabledFeaturesSummary(config), "Deployment-wide runtime feature gates."),
            new("Audience Profiles", ReadAudienceProfilesSummary(_paths, config), "Curated per-audience access rules."),
            new("Exposure Mode", ReadExposureModeSummary(config), "Daemon reachability and tunnel topology.", "/exposure-mode"),
            new("Done", "", "Return to Settings Areas.")
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

    private static string ReadAudienceProfilesSummary(NetclawPaths paths, Dictionary<string, object> config)
    {
        var existing = LoadAudienceProfiles(paths, out var malformed);
        if (malformed)
            return "Unreadable — re-save to repair";

        return JsonEquivalent(existing, BuildPostureProfiles(ReadPosture(config))) ? "No overrides" : "Customized";
    }

    private bool AudienceHasOverrides(TrustAudience audience)
    {
        var profiles = LoadAudienceProfiles();
        var current = GetProfile(profiles, audience);
        var defaults = GetProfile(BuildPostureProfiles(CurrentPosture), audience);
        return !JsonEquivalent(current, defaults);
    }

    // Posture reads route through the shared DeploymentPostureReader (fail-closed-to-Public on a
    // present-but-unparseable value) so the Security and Channels editors treat the same stored value
    // identically — see that type for the fail-closed rationale.
    private static bool TryReadPosture(Dictionary<string, object> config, out DeploymentPosture posture, out string? invalidValue)
        => DeploymentPostureReader.TryRead(config, out posture, out invalidValue);

    private static DeploymentPosture ReadPosture(Dictionary<string, object> config)
    {
        TryReadPosture(config, out var posture, out _);
        return posture;
    }

    private static string ReadExposureModeSummary(Dictionary<string, object> config)
    {
        if (!ConfigFileHelper.TryGetPathValue(config, "Daemon.ExposureMode", out var value))
            return "Local";

        ExposureMode mode;
        try
        {
            mode = DaemonConfig.ParseExposureMode(value?.ToString());
        }
        catch (InvalidOperationException)
        {
            // ParseExposureMode throws on an unrecognized string. The Items property is read on every
            // render frame, so a hand-edited/migrated ExposureMode must degrade to the raw value here
            // rather than crashing the Security & Access page permanently.
            return $"Unknown ('{value}')";
        }

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
        => ToolAudienceProfileDefaults.CreateProfilesForPosture(posture);

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
        profile.AllowedTools = [.. ToolAudienceProfileToolCatalog.ProfileManagedTools];
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

    private static string DescribeFilesystemHelp(ToolAudienceProfile profile)
        => CurrentFilesystemLevel(profile) switch
        {
            FilesystemLevel.Off => "Off: file tools stay granted, but no filesystem paths are available.",
            FilesystemLevel.AllFiles => "All files: unrestricted filesystem scope; intended only for Personal audiences.",
            _ => "Session only: file tools stay inside the current session workspace."
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

    private static string DescribeAttachmentHelp(ChannelAttachmentPolicy? policy)
        => CurrentAttachmentLevel(policy) switch
        {
            AttachmentLevel.None => "None: inbound channel attachments are rejected.",
            AttachmentLevel.Images => "Images: allows image uploads only.",
            AttachmentLevel.All => "All attachments: images, PDFs, documents, archives, media, and unknown file types.",
            _ => "Common work files: images, PDFs, documents, archives, and media; excludes unknown file types."
        };

    private static readonly FilesystemLevel[] PersonalFilesystemLevels =
    [
        FilesystemLevel.Off,
        FilesystemLevel.SessionOnly,
        FilesystemLevel.AllFiles
    ];

    private static readonly FilesystemLevel[] RestrictedFilesystemLevels =
    [
        FilesystemLevel.Off,
        FilesystemLevel.SessionOnly
    ];

    private static readonly AttachmentLevel[] AttachmentLevels =
    [
        AttachmentLevel.None,
        AttachmentLevel.Images,
        AttachmentLevel.CommonWorkFiles,
        AttachmentLevel.All
    ];

    private static IReadOnlyList<FilesystemLevel> FilesystemLevelsFor(TrustAudience audience)
        => audience == TrustAudience.Personal ? PersonalFilesystemLevels : RestrictedFilesystemLevels;

    private static T CycleValue<T>(T current, IReadOnlyList<T> values, int direction)
    {
        if (values.Count == 0)
            return current;

        var index = -1;
        for (var i = 0; i < values.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(values[i], current))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            index = 0;

        var next = (index + Math.Sign(direction) + values.Count) % values.Count;
        return values[next];
    }

    private static bool JsonEquivalent<T>(T left, T right)
        => JsonSerializer.Serialize(left, JsonDefaults.ConfigFile) == JsonSerializer.Serialize(right, JsonDefaults.ConfigFile);

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
