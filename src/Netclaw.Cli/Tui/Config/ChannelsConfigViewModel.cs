// -----------------------------------------------------------------------
// <copyright file="ChannelsConfigViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Actors.Channels;
using Netclaw.Channels.Slack;
using Netclaw.Cli.Config;
using Netclaw.Cli.Discord;
using Netclaw.Cli.Mattermost;
using Netclaw.Cli.Tui.Sections;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Configuration;
using Netclaw.Providers;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui.Config;

public sealed class ChannelsConfigViewModel : ReactiveViewModel
{
    private readonly NetclawPaths _paths;
    private readonly ISlackProbe _slackProbe;
    private readonly IDiscordProbe _discordProbe;
    private readonly IMattermostProbe _mattermostProbe;
    private readonly TuiNavigation? _navigation;
    private readonly ChannelsConfigPersistenceMapper _mapper = new();
    private readonly ChannelsEditorValidationAdapter _validator = new();
    private readonly WizardContext _context;
    private readonly HashSet<ChannelType> _knownProviders;
    private readonly Dictionary<ChannelType, Dictionary<string, TrustAudience>> _channelAudiences = [];
    private ChannelType _activeAdapterType = ChannelType.Slack;
    private string? _editingAudienceId;
    private string? _editingAudienceLabel;
    private bool _editingAudienceIsDm;
    private int _managementMenuIndex;
    private int _channelRowIndex;
    private int _audienceSelectionIndex;
    private int _directMessagesRowIndex;
    private int _resetConfirmIndex;
    private CancellationTokenSource? _labelResolutionCts;
    private Task? _labelRefreshTask;

    public ChannelsConfigViewModel(
        NetclawPaths paths,
        ISlackProbe slackProbe,
        IDiscordProbe discordProbe,
        IMattermostProbe mattermostProbe,
        TuiNavigation? navigation = null)
    {
        _paths = paths;
        _slackProbe = slackProbe;
        _discordProbe = discordProbe;
        _mattermostProbe = mattermostProbe;
        _navigation = navigation;
        Status = new ReactiveProperty<ConfigStatusMessage>(new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral));
        Step = new ChannelPickerStepViewModel(slackProbe, discordProbe)
        {
            DoneActionText = "return to Settings Areas",
            DoneKeyActionLabel = "Done",
            DoneKey = ConsoleKey.D,
            ShowDoneAction = false,
            ShowDonePickerRow = true,
            DonePickerRowLabel = "Done adding channels",
            PreserveDisabledAdapterDrafts = true
        };
        _context = new WizardContext
        {
            Paths = paths,
            Registry = new ProviderDescriptorRegistry([]),
            RequestRedraw = RequestRedraw,
            ExistingConfig = ConfigFileHelper.LoadJsonDictOrNull(paths.NetclawConfigPath),
            SelectedPosture = LoadDeploymentPosture(paths)
        };

        Step.OnEnter(_context, NavigationDirection.Forward);
        var draft = _mapper.Load(paths);
        _knownProviders = [.. draft.KnownProviders];
        LoadAudienceDrafts(draft);
        _mapper.ApplyToStep(Step, draft);
    }

    public ChannelPickerStepViewModel Step { get; }
    public ChannelPickerStepView StepView { get; } = new();
    public WizardContext Context => _context;
    public ReactiveProperty<bool> IsSaved { get; } = new(false);
    internal ReactiveProperty<ChannelsConfigScreen> Screen { get; } = new(ChannelsConfigScreen.Picker);
    internal ReactiveProperty<ConfigStatusMessage> Status { get; }
    public Action? OnStepContentChanged { get; set; }

    internal bool ShutdownRequestedForTest { get; private set; }

    internal ChannelType ActiveAdapterType => _activeAdapterType;
    internal string ActiveAdapterName => GetAdapterDisplayName(_activeAdapterType);
    internal int ManagementMenuIndex => _managementMenuIndex;
    internal int ChannelRowIndex => _channelRowIndex;
    internal int AudienceSelectionIndex => _audienceSelectionIndex;
    internal int DirectMessagesRowIndex => _directMessagesRowIndex;
    internal int ResetConfirmIndex => _resetConfirmIndex;
    internal string? AddChannelInput { get; set; }
    internal string? AllowedUsersInput { get; set; }
    internal bool DirectMessagesEnabled { get; set; }
    internal string? BotTokenInput { get; set; }
    internal string? AppTokenInput { get; set; }
    internal string? ServerUrlInput { get; set; }
    internal string? CallbackUrlInput { get; set; }
    internal int CredentialFieldIndex { get; set; }

    internal static IReadOnlyList<TrustAudience> AudienceOptions { get; } =
    [
        TrustAudience.Personal,
        TrustAudience.Team,
        TrustAudience.Public
    ];

    public void GoNext()
    {
        if (Step.IsInSubFlow)
        {
            var activeAdapter = Step.ActiveAdapterType;
            if (Step.TryAdvance())
            {
                if (!Step.IsInSubFlow && activeAdapter is { } completedAdapter)
                {
                    OpenChannelPermissionsAfterInitialSetup(completedAdapter);
                    AutosaveCompletedAction($"{GetAdapterDisplayName(completedAdapter)} channel setup saved.");
                }

                NotifyContentChanged();
            }

            return;
        }

        ReturnToDashboard();
    }

    public void GoBack()
    {
        if (Screen.Value != ChannelsConfigScreen.Picker)
        {
            GoBackWithinManagement();
            return;
        }

        if (Step.IsInSubFlow && Step.TryGoBack())
        {
            Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
            NotifyContentChanged();
            return;
        }

        ReturnToDashboard();
    }

    public bool Save()
        => SaveAsync().GetAwaiter().GetResult();

    public async Task<bool> SaveAsync(CancellationToken ct = default)
        => await SaveAsync("Channels saved.", probeChannelAccess: true, ct);

    private async Task<bool> SaveAsync(string successMessage, bool probeChannelAccess, CancellationToken ct = default)
    {
        // A background channel-label refresh may be in flight; cancel and await it before we
        // validate, write, or reset adapter state so it cannot race this save.
        await CancelAndAwaitLabelRefreshAsync();

        var validation = ValidateCurrentStep();
        if (validation.HasErrors)
        {
            Status.Value = BuildValidationErrorStatus(validation, "Fix channel validation errors before saving.");
            RequestRedraw();
            return false;
        }

        // Autosave of a completed action persists immediately and does NOT block the UI loop on a
        // network channel-access probe: the action that triggered it was already validated (add
        // resolves before adding; toggle/remove/audience do not introduce a channel), unresolved
        // names stay inert in the ACL, and the background label refresh re-validates asynchronously.
        // Only an explicit Save runs the probe and may block on a genuine probe failure.
        IReadOnlyList<string> unresolved = [];
        if (probeChannelAccess)
        {
            Status.Value = new ConfigStatusMessage("Validating channel access...", ConfigStatusTone.Neutral);
            RequestRedraw();

            var dynamicValidation = await ValidateChannelAccessAsync(ct);
            if (dynamicValidation.Result.HasErrors)
            {
                // Only a genuine probe failure (bad token / unreachable, surfaced as an
                // ErrorMessage) blocks here — we could not validate at all, so persisting
                // nothing is correct. Merely-unresolved channel names are NOT errors: they
                // persist verbatim and are flagged non-blockingly (see ValidateChannelAccessAsync).
                Status.Value = BuildValidationErrorStatus(dynamicValidation.Result, "Fix channel validation errors before saving.");
                RequestRedraw();
                return false;
            }

            unresolved = dynamicValidation.Unresolved;
        }

        WriteChannelConfigToDisk();

        var savedDraft = _mapper.Load(_paths);
        _knownProviders.Clear();
        foreach (var provider in savedDraft.KnownProviders)
            _knownProviders.Add(provider);

        LoadAudienceDrafts(savedDraft);
        Step.OnEnter(_context, NavigationDirection.Forward);
        _mapper.ApplyToStep(Step, savedDraft);
        IsSaved.Value = true;
        Status.Value = BuildSaveStatus(successMessage, unresolved);
        NotifyContentChanged();
        return true;
    }

    // Writes the current in-memory Step (tokens + channels + audiences) to disk. The reload
    // that SaveAsync performs afterward is deliberately NOT done here so callers that only
    // touch the persisted shape (e.g. label normalization) keep their live resolution state.
    private void WriteChannelConfigToDisk()
    {
        var session = new ConfigEditorSession(_paths);
        session.Apply(_mapper.BuildContribution(
            Step,
            _knownProviders,
            _channelAudiences,
            _context.SelectedPosture ?? DeploymentPosture.Personal));
        session.Save();
    }

    // The probe succeeded but some channel names/ids did not resolve. We saved the
    // whole adapter anyway (token + resolved channels + unresolved names kept as-is)
    // and flag the unresolved entries non-blockingly so the operator can fix or
    // remove them. An unresolved name persisted into AllowedChannelIds is an inert
    // allow-list entry — it matches no real channel ID, so it grants nothing.
    private static ConfigStatusMessage BuildSaveStatus(string successMessage, IReadOnlyList<string> unresolved)
        => unresolved.Count == 0
            ? new ConfigStatusMessage(successMessage, ConfigStatusTone.Success)
            : new ConfigStatusMessage(
                $"Saved. Could not resolve: {string.Join(", ", unresolved.Select(static name => $"#{name}"))} — flagged below; fix or remove them.",
                ConfigStatusTone.Warning);

    internal async Task<bool> SaveFromInputAsync(CancellationToken ct = default)
        => await ConfigAutosave.RunAsync(
            token => SaveAsync("Channels saved.", probeChannelAccess: true, token),
            Status,
            "Channel settings save failed",
            RequestRedraw,
            ct);

    internal bool TryOpenSelectedAdapterManagement()
    {
        if (!Step.IsInPickerMode || !Step.IsAdapterRowSelected)
            return false;

        var type = Step.SelectedAdapterType;
        if (!Step.IsAdapterKnown(type))
            return false;

        OpenAdapterManagement(type);
        return true;
    }

    internal bool TryToggleSelectedAdapterFromPicker()
    {
        if (!Step.IsInPickerMode || !Step.IsAdapterRowSelected)
            return false;

        var type = Step.SelectedAdapterType;
        var selectedIndex = GetAdapterIndex(type);
        var wasEnabled = Step.IsAdapterEnabled(type);
        _activeAdapterType = type;

        Step.ToggleAdapter(selectedIndex);
        if (Step.IsInSubFlow)
            return true;

        UpdateAdapterPickerSummary(type);
        AutosaveCompletedAction($"{GetAdapterDisplayName(type)} {(!wasEnabled ? "enabled" : "disabled")} and saved.");
        NotifyContentChanged();
        return true;
    }

    internal void OpenAdapterManagement(ChannelType type)
    {
        _activeAdapterType = type;
        _managementMenuIndex = 0;
        Screen.Value = ChannelsConfigScreen.AdapterMenu;
        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    private void OpenChannelPermissionsAfterInitialSetup(ChannelType type)
    {
        _activeAdapterType = type;
        _channelRowIndex = 0;
        UpdateAdapterPickerSummary(type);
        Screen.Value = ChannelsConfigScreen.ChannelPermissions;
        Status.Value = new ConfigStatusMessage(
            $"Set {GetAdapterDisplayName(type)} channel audiences. Completed actions save automatically.",
            ConfigStatusTone.Neutral);
        StartChannelLabelResolution(type);
    }

    internal async Task RefreshChannelLabelsAsync(ChannelType type, CancellationToken ct = default)
    {
        if (!Step.IsAdapterEnabled(type))
            return;

        var channelIds = GetChannelIds(type);
        if (channelIds.Count == 0)
            return;

        try
        {
            switch (type)
            {
                case ChannelType.Slack:
                    await RefreshSlackChannelLabelsAsync(channelIds, ct);
                    break;
                case ChannelType.Discord:
                    await RefreshDiscordChannelLabelsAsync(channelIds, ct);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Background label refresh was superseded (a newer resolution started) or
            // the user navigated away — abandon it quietly. Cancellation is not a
            // lookup failure, so it must not fall through to the warning status below.
        }
        catch (Exception ex)
        {
            Status.Value = new ConfigStatusMessage(
                $"{GetAdapterDisplayName(type)} channel label lookup failed: {ex.Message}",
                ConfigStatusTone.Warning);
        }
    }

    internal IReadOnlyList<ChannelsManagementMenuItem> GetManagementMenuItems()
    {
        var enabled = Step.IsAdapterEnabled(_activeAdapterType);
        return
        [
            new ChannelsManagementMenuItem(ChannelsManagementAction.ManageChannels, "Manage channels and permissions", "Edit allowed channels and audience levels."),
            new ChannelsManagementMenuItem(ChannelsManagementAction.AddChannel, $"Add a {ActiveAdapterName} channel", "Add channel ingress without touching credentials."),
            new ChannelsManagementMenuItem(ChannelsManagementAction.ManageUsers, "Manage allowed users", "Restrict messages to specific user IDs."),
            new ChannelsManagementMenuItem(ChannelsManagementAction.DirectMessages, "Direct messages", "Enable or disable DM ingress and audience."),
            new ChannelsManagementMenuItem(ChannelsManagementAction.RotateCredentials, "Rotate credentials", "Replace tokens only when explicitly entered."),
            new ChannelsManagementMenuItem(ChannelsManagementAction.ToggleEnabled, enabled ? $"Disable {ActiveAdapterName}" : $"Enable {ActiveAdapterName}", "Preserve saved setup while changing runtime state."),
            new ChannelsManagementMenuItem(ChannelsManagementAction.ResetConnection, $"Reset {ActiveAdapterName} connection", "Remove saved config and credentials.")
        ];
    }

    internal void MoveManagementMenu(int delta)
    {
        _managementMenuIndex = Clamp(_managementMenuIndex + delta, GetManagementMenuItems().Count);
        NotifyContentChanged();
    }

    internal void ActivateManagementMenuItem()
    {
        var item = GetManagementMenuItems()[_managementMenuIndex];
        switch (item.Action)
        {
            case ChannelsManagementAction.ManageChannels:
                _channelRowIndex = 0;
                Screen.Value = ChannelsConfigScreen.ChannelPermissions;
                StartChannelLabelResolution(_activeAdapterType);
                break;
            case ChannelsManagementAction.AddChannel:
                BeginAddChannel();
                break;
            case ChannelsManagementAction.ManageUsers:
                BeginAllowedUsers();
                break;
            case ChannelsManagementAction.DirectMessages:
                BeginDirectMessages();
                break;
            case ChannelsManagementAction.RotateCredentials:
                BeginRotateCredentials();
                break;
            case ChannelsManagementAction.ToggleEnabled:
                SetActiveAdapterEnabled(!Step.IsAdapterEnabled(_activeAdapterType));
                Screen.Value = ChannelsConfigScreen.Picker;
                break;
            case ChannelsManagementAction.ResetConnection:
                _resetConfirmIndex = 0;
                Screen.Value = ChannelsConfigScreen.ResetConfirm;
                break;
        }

        NotifyContentChanged();
    }

    internal string GetActiveAdapterSummary()
    {
        var channelCount = GetChannelIds(_activeAdapterType).Count;
        var userCount = GetAllowedUserIds(_activeAdapterType).Count;
        var credentials = GetCredentialSummary(_activeAdapterType);
        var dm = GetAllowDirectMessages(_activeAdapterType) ? "enabled" : "disabled";
        var enabled = Step.IsAdapterEnabled(_activeAdapterType) ? "enabled" : "disabled";
        return $"{enabled} · {credentials} · {Pluralize(channelCount, "channel", "channels")} · {Pluralize(userCount, "user", "users")} · DMs {dm}";
    }

    internal IReadOnlyList<ChannelPermissionRow> GetChannelRows(bool includeAddAction = true)
    {
        var rows = new List<ChannelPermissionRow>();
        var unresolved = GetActiveAdapterUnresolved();
        foreach (var channelId in GetChannelIds(_activeAdapterType))
        {
            rows.Add(new ChannelPermissionRow(
                channelId,
                FormatChannelLabel(_activeAdapterType, channelId),
                GetChannelAudience(_activeAdapterType, channelId, DefaultChannelAudience()),
                IsDirectMessage: false,
                IsAddAction: false,
                IsDoneAction: false,
                IsUnresolved: unresolved.Contains(channelId)));
        }

        if (GetAllowDirectMessages(_activeAdapterType))
        {
            rows.Add(new ChannelPermissionRow(
                "dm",
                "Direct messages",
                GetChannelAudience(_activeAdapterType, "dm", DefaultDirectMessageAudience()),
                IsDirectMessage: true,
                IsAddAction: false,
                IsDoneAction: false));
        }

        if (includeAddAction)
        {
            rows.Add(new ChannelPermissionRow(
                string.Empty,
                "+ Add channel",
                DefaultChannelAudience(),
                IsDirectMessage: false,
                IsAddAction: true,
                IsDoneAction: false));
            rows.Add(new ChannelPermissionRow(
                string.Empty,
                "Done adding channels",
                DefaultChannelAudience(),
                IsDirectMessage: false,
                IsAddAction: false,
                IsDoneAction: true));
        }

        if (_channelRowIndex >= rows.Count)
            _channelRowIndex = Math.Max(rows.Count - 1, 0);

        return rows;
    }

    internal void MoveChannelRow(int delta)
    {
        _channelRowIndex = Clamp(_channelRowIndex + delta, GetChannelRows().Count);
        NotifyContentChanged();
    }

    internal void OpenSelectedChannelAudience()
    {
        var rows = GetChannelRows();
        if (rows.Count == 0)
            return;

        var row = rows[_channelRowIndex];
        if (row.IsAddAction)
        {
            BeginAddChannel();
            return;
        }

        if (row.IsDoneAction)
        {
            FinishChannelPermissions();
            return;
        }

        _editingAudienceId = row.Id;
        _editingAudienceLabel = row.DisplayName;
        _editingAudienceIsDm = row.IsDirectMessage;
        _audienceSelectionIndex = AudienceIndex(row.Audience);
        Screen.Value = ChannelsConfigScreen.EditAudience;
        NotifyContentChanged();
    }

    internal void ChangeSelectedChannelAudience(int delta)
    {
        var rows = GetChannelRows();
        if (rows.Count == 0)
            return;

        var row = rows[_channelRowIndex];
        if (row.IsAction)
            return;

        var currentIndex = AudienceIndex(row.Audience);
        var next = AudienceOptions[Wrap(currentIndex + delta, AudienceOptions.Count)];
        SetChannelAudience(_activeAdapterType, row.Id, next);
        // Autosave like every other ChannelPermissions mutation (RemoveSelectedChannel,
        // ApplyAddChannel): this ←/→ toggle sets a security-relevant ACL trust tier, and without an
        // immediate save an Esc would silently discard it (the next load resets from disk).
        AutosaveCompletedAction($"{row.DisplayName} audience set to {next} and saved.");
    }

    internal void RemoveSelectedChannel()
    {
        var rows = GetChannelRows();
        if (rows.Count == 0)
            return;

        var row = rows[_channelRowIndex];
        if (row.IsAction || row.IsDirectMessage)
            return;

        var remaining = GetChannelIds(_activeAdapterType)
            .Where(id => !string.Equals(id, row.Id, StringComparison.Ordinal))
            .ToArray();
        SetChannelIds(_activeAdapterType, remaining);
        if (_channelAudiences.TryGetValue(_activeAdapterType, out var audiences))
            audiences.Remove(row.Id);

        UpdateAdapterPickerSummary(_activeAdapterType);
        _channelRowIndex = Clamp(_channelRowIndex, GetChannelRows().Count);
        AutosaveCompletedAction($"Removed {row.DisplayName} and saved.");
        NotifyContentChanged();
    }

    internal void BeginAddChannel()
    {
        AddChannelInput = null;
        _audienceSelectionIndex = AudienceIndex(DefaultChannelAudience());
        Screen.Value = ChannelsConfigScreen.AddChannel;
        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void ApplyAddChannel()
        => ApplyAddChannelAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Resolves the typed channel against the active adapter (does it exist? can
    /// the bot see it?) BEFORE adding it. On a resolve failure the channel is NOT
    /// added and the operator stays on the add screen with an error. On success
    /// the resolved channel ID is added at the system-default audience, its row is
    /// focused, and the change is autosaved. The operator tunes the audience
    /// afterward with ←/→ on the channel list — there is no audience picker during
    /// add (matches the design prototype's resolve-before-add flow).
    /// </summary>
    internal async Task ApplyAddChannelAsync(CancellationToken ct = default)
    {
        var rawInput = NormalizeChannelId(AddChannelInput);
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            Status.Value = new ConfigStatusMessage("Channel ID is required.", ConfigStatusTone.Error);
            NotifyContentChanged();
            return;
        }

        var existing = GetChannelIds(_activeAdapterType);

        Status.Value = new ConfigStatusMessage($"Resolving {rawInput} on {ActiveAdapterName}...", ConfigStatusTone.Neutral);
        RequestRedraw();

        ChannelResolveOutcome resolved;
        try
        {
            resolved = await ResolveSingleChannelAsync(_activeAdapterType, rawInput, ct);
        }
        catch (Exception ex)
        {
            Status.Value = new ConfigStatusMessage($"{ActiveAdapterName} channel lookup failed: {ex.Message}", ConfigStatusTone.Error);
            NotifyContentChanged();
            return;
        }

        if (!resolved.Success)
        {
            Status.Value = new ConfigStatusMessage(resolved.ErrorMessage!, ConfigStatusTone.Error);
            NotifyContentChanged();
            return;
        }

        var channelId = resolved.ChannelId!;
        if (existing.Contains(channelId, StringComparer.Ordinal))
        {
            Status.Value = new ConfigStatusMessage($"{channelId} is already configured.", ConfigStatusTone.Error);
            NotifyContentChanged();
            return;
        }

        SetChannelIds(_activeAdapterType, [.. existing, channelId]);
        SetChannelAudience(_activeAdapterType, channelId, DefaultChannelAudience());
        UpdateAdapterPickerSummary(_activeAdapterType);
        _channelRowIndex = GetChannelRows()
            .Select((row, index) => (row, index))
            .Single(entry => string.Equals(entry.row.Id, channelId, StringComparison.Ordinal))
            .index;
        Screen.Value = ChannelsConfigScreen.ChannelPermissions;
        AutosaveCompletedAction($"Added {channelId} at the {DefaultChannelAudience()} default and saved.");
        NotifyContentChanged();
    }

    /// <summary>
    /// Resolves a single typed channel name/ID against the live adapter. Slack
    /// channel IDs and Discord/Mattermost IDs are still probed for existence so a
    /// non-resolving channel errors instead of being saved.
    /// </summary>
    private async Task<ChannelResolveOutcome> ResolveSingleChannelAsync(ChannelType type, string input, CancellationToken ct)
    {
        switch (type)
        {
            case ChannelType.Slack:
            {
                // An entered Slack channel ID needs no name lookup — add it directly.
                if (IsSlackChannelId(input))
                    return ChannelResolveOutcome.Ok(input);

                var slack = Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
                var botToken = GetEffectiveSecret("Slack.BotToken", slack.BotToken, slack.HasPersistedBotToken);
                if (string.IsNullOrWhiteSpace(botToken))
                    return ChannelResolveOutcome.Fail(ChannelsEditorValidationMessages.SlackBotTokenRequired);

                var result = await _slackProbe.ResolveChannelNamesAsync(botToken, [input], ct);
                slack.LastChannelResolution = result; // feeds the channel-row display label.
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                    return ChannelResolveOutcome.Fail($"Slack channel lookup failed: {result.ErrorMessage}");
                if (result.Unresolved.Count > 0 || !result.Success)
                    return ChannelResolveOutcome.Fail($"Slack channel not found: #{input}");

                // Name resolved to an ID, or the probe accepted it without enriching.
                return ChannelResolveOutcome.Ok(result.Resolved.Count > 0 ? result.Resolved[0].Id : input);
            }

            case ChannelType.Discord:
            {
                var discord = Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord);
                var botToken = GetEffectiveSecret("Discord.BotToken", discord.BotToken, discord.HasPersistedBotToken);
                if (string.IsNullOrWhiteSpace(botToken))
                    return ChannelResolveOutcome.Fail(ChannelsEditorValidationMessages.DiscordBotTokenRequired);

                var result = await _discordProbe.ResolveChannelIdsAsync(botToken, [input], ct);
                discord.LastChannelResolution = result; // feeds the channel-row display label.
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                    return ChannelResolveOutcome.Fail($"Discord channel lookup failed: {result.ErrorMessage}");
                if (result.Unresolved.Count > 0 || !result.Success)
                    return ChannelResolveOutcome.Fail($"Discord channel ID not found: {input}");

                return ChannelResolveOutcome.Ok(result.Resolved.Count > 0 ? result.Resolved[0].ChannelId : input);
            }

            case ChannelType.Mattermost:
            {
                var mattermost = Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost);
                var serverUrl = Normalize(mattermost.ServerUrl);
                if (string.IsNullOrWhiteSpace(serverUrl))
                    return ChannelResolveOutcome.Fail(ChannelsEditorValidationMessages.MattermostServerUrlRequired);

                var botToken = GetEffectiveSecret("Mattermost.BotToken", mattermost.BotToken, mattermost.HasPersistedBotToken);
                if (string.IsNullOrWhiteSpace(botToken))
                    return ChannelResolveOutcome.Fail(ChannelsEditorValidationMessages.MattermostBotTokenRequired);

                var result = await _mattermostProbe.ResolveChannelIdsAsync(serverUrl, botToken, [input], ct);
                if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
                    return ChannelResolveOutcome.Fail($"Mattermost channel lookup failed: {result.ErrorMessage}");
                if (result.Unresolved.Count > 0 || !result.Success)
                    return ChannelResolveOutcome.Fail($"Mattermost channel ID not found: {input}");

                return ChannelResolveOutcome.Ok(result.Resolved.Count > 0 ? result.Resolved[0].ChannelId : input);
            }

            default:
                return ChannelResolveOutcome.Fail("Unsupported adapter.");
        }
    }

    private readonly record struct ChannelResolveOutcome(bool Success, string? ChannelId, string? ErrorMessage)
    {
        internal static ChannelResolveOutcome Ok(string channelId) => new(true, channelId, null);
        internal static ChannelResolveOutcome Fail(string error) => new(false, null, error);
    }

    internal void FinishChannelPermissions()
    {
        Screen.Value = ChannelsConfigScreen.AdapterMenu;
        Status.Value = new ConfigStatusMessage("Done adding channels. Completed changes are already saved.", ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal string? EditingAudienceLabel => _editingAudienceLabel;
    internal string? EditingAudienceId => _editingAudienceId;
    internal bool EditingAudienceIsDm => _editingAudienceIsDm;

    internal void MoveAudienceSelection(int delta)
    {
        _audienceSelectionIndex = Wrap(_audienceSelectionIndex + delta, AudienceOptions.Count);
        NotifyContentChanged();
    }

    internal void ApplyAudienceSelection()
    {
        if (string.IsNullOrWhiteSpace(_editingAudienceId))
            return;

        SetChannelAudience(_activeAdapterType, _editingAudienceId, AudienceOptions[_audienceSelectionIndex]);
        Screen.Value = ChannelsConfigScreen.ChannelPermissions;
        AutosaveCompletedAction($"Updated {_editingAudienceLabel} audience and saved.");
        NotifyContentChanged();
    }

    internal void BeginAllowedUsers()
    {
        AllowedUsersInput = ChannelCsv.JoinOrNull(GetAllowedUserIds(_activeAdapterType));
        Screen.Value = ChannelsConfigScreen.AllowedUsers;
        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void ApplyAllowedUsers()
    {
        var userIds = ChannelCsv.ParseCsv(AllowedUsersInput, trimHash: false);
        SetAllowedUserIds(_activeAdapterType, userIds);
        UpdateAdapterPickerSummary(_activeAdapterType);
        Screen.Value = ChannelsConfigScreen.AdapterMenu;
        AutosaveCompletedAction("Allowed users saved.");
        NotifyContentChanged();
    }

    internal void BeginDirectMessages()
    {
        DirectMessagesEnabled = GetAllowDirectMessages(_activeAdapterType);
        _directMessagesRowIndex = 0;
        _audienceSelectionIndex = AudienceIndex(GetChannelAudience(_activeAdapterType, "dm", DefaultDirectMessageAudience()));
        Screen.Value = ChannelsConfigScreen.DirectMessages;
        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void MoveDirectMessagesRow(int delta)
    {
        _directMessagesRowIndex = Clamp(_directMessagesRowIndex + delta, 2);
        NotifyContentChanged();
    }

    internal void ToggleDirectMessages()
    {
        DirectMessagesEnabled = !DirectMessagesEnabled;
        NotifyContentChanged();
    }

    internal void ChangeDirectMessageAudience(int delta)
    {
        _audienceSelectionIndex = Wrap(_audienceSelectionIndex + delta, AudienceOptions.Count);
        NotifyContentChanged();
    }

    internal void ApplyDirectMessages()
    {
        SetAllowDirectMessages(_activeAdapterType, DirectMessagesEnabled);
        SetChannelAudience(_activeAdapterType, "dm", AudienceOptions[_audienceSelectionIndex]);
        UpdateAdapterPickerSummary(_activeAdapterType);
        Screen.Value = ChannelsConfigScreen.AdapterMenu;
        AutosaveCompletedAction("Direct message settings saved.");
        NotifyContentChanged();
    }

    internal void BeginRotateCredentials()
    {
        BotTokenInput = null;
        AppTokenInput = null;
        ServerUrlInput = GetServerUrl(_activeAdapterType);
        CallbackUrlInput = GetCallbackUrl(_activeAdapterType);
        CredentialFieldIndex = 0;
        Screen.Value = ChannelsConfigScreen.RotateCredentials;
        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal IReadOnlyList<CredentialFieldSpec> GetCredentialFields()
    {
        return _activeAdapterType switch
        {
            ChannelType.Slack =>
            [
                new CredentialFieldSpec("bot", "Bot token", IsSecret: true, "xoxb-...", GetCredentialPresenceText("bot")),
                new CredentialFieldSpec("app", "App token", IsSecret: true, "xapp-...", GetCredentialPresenceText("app"))
            ],
            ChannelType.Discord =>
            [
                new CredentialFieldSpec("bot", "Bot token", IsSecret: true, "Discord bot token", GetCredentialPresenceText("bot"))
            ],
            ChannelType.Mattermost =>
            [
                new CredentialFieldSpec("server", "Server URL", IsSecret: false, "https://mattermost.example.com", null),
                new CredentialFieldSpec("bot", "Bot token", IsSecret: true, "Mattermost bot token", GetCredentialPresenceText("bot")),
                new CredentialFieldSpec("callback", "Callback URL", IsSecret: false, "https://netclaw.example.com/api/mattermost/actions", "Optional interactive button callback URL.")
            ],
            _ => []
        };
    }

    internal string? GetCredentialDraftValue(string key) => key switch
    {
        "bot" => BotTokenInput,
        "app" => AppTokenInput,
        "server" => ServerUrlInput,
        "callback" => CallbackUrlInput,
        _ => null
    };

    internal void StageCredentialDraftValue(string key, string? value)
    {
        switch (key)
        {
            case "bot":
                BotTokenInput = value;
                break;
            case "app":
                AppTokenInput = value;
                break;
            case "server":
                ServerUrlInput = value;
                break;
            case "callback":
                CallbackUrlInput = value;
                break;
        }
    }

    internal void MoveCredentialField(int delta)
    {
        CredentialFieldIndex = Clamp(CredentialFieldIndex + delta, GetCredentialFields().Count);
        NotifyContentChanged();
    }

    internal void ApplyCredentials()
    {
        var issue = ValidateCredentialDrafts();
        if (issue is not null)
        {
            Status.Value = new ConfigStatusMessage(issue.Message, ConfigStatusTone.Error);
            NotifyContentChanged();
            return;
        }

        switch (_activeAdapterType)
        {
            case ChannelType.Slack:
                var slack = Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
                slack.BotToken = Normalize(BotTokenInput);
                slack.AppToken = Normalize(AppTokenInput);
                break;
            case ChannelType.Discord:
                Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord).BotToken = Normalize(BotTokenInput);
                break;
            case ChannelType.Mattermost:
                var mattermost = Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost);
                mattermost.ServerUrl = Normalize(ServerUrlInput);
                mattermost.BotToken = Normalize(BotTokenInput);
                mattermost.CallbackUrl = Normalize(CallbackUrlInput);
                break;
        }

        Screen.Value = ChannelsConfigScreen.AdapterMenu;
        AutosaveCompletedAction("Credential changes saved.");
        NotifyContentChanged();
    }

    private ChannelsEditorValidationResult ValidateCurrentStep()
        => _validator.Validate(ChannelsEditorModel.FromStep(Step));

    // Carries the genuinely blocking validation issues (probe failure / !Success)
    // separately from the non-blocking unresolved channel names that we persist and
    // flag rather than reject.
    private readonly record struct ChannelAccessValidation(
        ChannelsEditorValidationResult Result,
        IReadOnlyList<string> Unresolved);

    private async Task<ChannelAccessValidation> ValidateChannelAccessAsync(CancellationToken ct)
    {
        var issues = new List<ChannelsEditorValidationIssue>();
        var unresolved = new List<string>();

        var slack = await ValidateSlackChannelsAsync(ct);
        ApplyChannelAccessOutcome(slack, issues, unresolved);

        var discord = await ValidateDiscordChannelsAsync(ct);
        ApplyChannelAccessOutcome(discord, issues, unresolved);

        var mattermost = await ValidateMattermostChannelsAsync(ct);
        ApplyChannelAccessOutcome(mattermost, issues, unresolved);

        var result = issues.Count == 0
            ? ChannelsEditorValidationResult.Empty
            : new ChannelsEditorValidationResult(issues);
        return new ChannelAccessValidation(result, unresolved);
    }

    private static void ApplyChannelAccessOutcome(
        ChannelAccessOutcome outcome,
        List<ChannelsEditorValidationIssue> issues,
        List<string> unresolved)
    {
        if (outcome.BlockingIssue is not null)
            issues.Add(outcome.BlockingIssue);

        unresolved.AddRange(outcome.Unresolved);
    }

    // Result of probing one adapter's channels: a blocking issue only when the probe
    // itself failed, plus the names/ids that the probe could not resolve (non-blocking).
    private readonly record struct ChannelAccessOutcome(
        ChannelsEditorValidationIssue? BlockingIssue,
        IReadOnlyList<string> Unresolved)
    {
        internal static ChannelAccessOutcome None { get; } = new(null, []);
        internal static ChannelAccessOutcome Blocked(ChannelsEditorValidationIssue issue) => new(issue, []);
        internal static ChannelAccessOutcome Flagged(IReadOnlyList<string> unresolved) => new(null, unresolved);
    }

    private async Task<ChannelAccessOutcome> ValidateSlackChannelsAsync(CancellationToken ct)
    {
        if (!Step.IsAdapterEnabled(ChannelType.Slack))
            return ChannelAccessOutcome.None;

        var slack = Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        var configuredChannels = ChannelCsv.ParseCsv(slack.ChannelNamesInput, trimHash: true);
        var namesToResolve = configuredChannels
            .Where(static channel => !IsSlackChannelId(channel))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (namesToResolve.Length == 0)
            return ChannelAccessOutcome.None;

        var botToken = GetEffectiveSecret("Slack.BotToken", slack.BotToken, slack.HasPersistedBotToken);
        if (string.IsNullOrWhiteSpace(botToken))
            return ChannelAccessOutcome.Blocked(Error(ChannelsEditorFieldPaths.SlackBotToken, ChannelsEditorValidationMessages.SlackBotTokenRequired));

        var result = await _slackProbe.ResolveChannelNamesAsync(botToken, namesToResolve, ct);
        slack.LastChannelResolution = result;

        // The probe itself failed — we cannot validate at all, so block the save. Only a
        // genuine failure (auth/scope/network/timeout) sets ErrorMessage. NOTE: do NOT also
        // block on !result.Success: the probe sets Success = "did EVERY name resolve?", so it
        // is false whenever any name is merely not found — which must stay non-blocking, or a
        // single unverifiable channel drops the whole adapter (token + valid channels) again.
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            return ChannelAccessOutcome.Blocked(Error(ChannelsEditorFieldPaths.SlackAllowedChannelIds, $"Slack channel lookup failed: {result.ErrorMessage}"));

        // Probe reachable. Map resolved names to IDs and keep unresolved names as-is so the
        // whole adapter still persists; the unresolved names are flagged, not blocked.
        var resolvedByName = result.Resolved.ToDictionary(
            static channel => channel.Name,
            static channel => channel.Id,
            StringComparer.OrdinalIgnoreCase);
        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        var resolvedChannels = new List<string>();

        foreach (var channel in configuredChannels)
        {
            if (IsSlackChannelId(channel))
            {
                resolvedChannels.Add(channel);
                continue;
            }

            if (resolvedByName.TryGetValue(channel, out var channelId))
            {
                resolvedChannels.Add(channelId);
                remap[channel] = channelId;
                continue;
            }

            // Unresolved name: keep it verbatim in the allow-list (inert until the
            // channel exists) so a single bad name never drops the whole adapter.
            resolvedChannels.Add(channel);
        }

        SetChannelIds(ChannelType.Slack, [.. resolvedChannels.Distinct(StringComparer.Ordinal)]);
        RemapChannelAudiences(ChannelType.Slack, remap);
        UpdateAdapterPickerSummary(ChannelType.Slack);
        return ChannelAccessOutcome.Flagged(result.Unresolved);
    }

    private async Task<ChannelAccessOutcome> ValidateDiscordChannelsAsync(CancellationToken ct)
    {
        if (!Step.IsAdapterEnabled(ChannelType.Discord))
            return ChannelAccessOutcome.None;

        var discord = Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord);
        var channelIds = ChannelCsv.ParseCsv(discord.ChannelIdsInput, trimHash: true);
        if (channelIds.Count == 0)
            return ChannelAccessOutcome.None;

        var botToken = GetEffectiveSecret("Discord.BotToken", discord.BotToken, discord.HasPersistedBotToken);
        if (string.IsNullOrWhiteSpace(botToken))
            return ChannelAccessOutcome.Blocked(Error(ChannelsEditorFieldPaths.DiscordBotToken, ChannelsEditorValidationMessages.DiscordBotTokenRequired));

        var result = await _discordProbe.ResolveChannelIdsAsync(botToken, channelIds, ct);
        discord.LastChannelResolution = result;

        // Only a genuine probe failure (ErrorMessage) blocks. result.Success is false whenever
        // any id is unresolved (Success = "did every id resolve?"), which must stay non-blocking.
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            return ChannelAccessOutcome.Blocked(Error(ChannelsEditorFieldPaths.DiscordAllowedChannelIds, $"Discord channel lookup failed: {result.ErrorMessage}"));

        // Discord allow-list already stores raw IDs, so unresolved IDs persist as-is;
        // they are flagged but not blocked.
        return ChannelAccessOutcome.Flagged(result.Unresolved);
    }

    private async Task<ChannelAccessOutcome> ValidateMattermostChannelsAsync(CancellationToken ct)
    {
        if (!Step.IsAdapterEnabled(ChannelType.Mattermost))
            return ChannelAccessOutcome.None;

        var mattermost = Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost);
        var channelIds = ChannelCsv.ParseCsv(mattermost.ChannelIdsInput, trimHash: true);
        if (channelIds.Count == 0)
            return ChannelAccessOutcome.None;

        var serverUrl = Normalize(mattermost.ServerUrl);
        if (string.IsNullOrWhiteSpace(serverUrl))
            return ChannelAccessOutcome.Blocked(Error(ChannelsEditorFieldPaths.MattermostServerUrl, ChannelsEditorValidationMessages.MattermostServerUrlRequired));

        var botToken = GetEffectiveSecret("Mattermost.BotToken", mattermost.BotToken, mattermost.HasPersistedBotToken);
        if (string.IsNullOrWhiteSpace(botToken))
            return ChannelAccessOutcome.Blocked(Error(ChannelsEditorFieldPaths.MattermostBotToken, ChannelsEditorValidationMessages.MattermostBotTokenRequired));

        var result = await _mattermostProbe.ResolveChannelIdsAsync(serverUrl, botToken, channelIds, ct);
        mattermost.LastChannelResolution = result;

        // Only a genuine probe failure (ErrorMessage) blocks. result.Success is false whenever
        // any id is unresolved (Success = "did every id resolve?"), which must stay non-blocking.
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            return ChannelAccessOutcome.Blocked(Error(ChannelsEditorFieldPaths.MattermostAllowedChannelIds, $"Mattermost channel lookup failed: {result.ErrorMessage}"));

        // Mattermost allow-list stores raw IDs, so unresolved IDs persist as-is and are
        // flagged but not blocked.
        return ChannelAccessOutcome.Flagged(result.Unresolved);
    }

    private async Task RefreshSlackChannelLabelsAsync(IReadOnlyList<string> channelIds, CancellationToken ct)
    {
        var slack = Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        var botToken = GetEffectiveSecret("Slack.BotToken", slack.BotToken, slack.HasPersistedBotToken);
        if (string.IsNullOrWhiteSpace(botToken))
            return;

        var result = await _slackProbe.ResolveChannelNamesAsync(botToken, channelIds, ct);
        if (ct.IsCancellationRequested)
            return;

        slack.LastChannelResolution = result;
        ApplyChannelLabelResolutionStatus(ChannelType.Slack, result.ErrorMessage, result.Unresolved);

        // A channel saved as a literal NAME (because it didn't resolve when first saved) stays
        // inert in the runtime ACL — SlackAclPolicy matches AllowedChannelIds against the Slack
        // channel ID, not the name. Once the bot can see the channel, rewrite the stored name to
        // its canonical ID and persist so the ACL actually matches and the row renders #name.
        var normalized = NormalizeSlackChannelNamesToIds(channelIds, result);
        if (normalized > 0 && string.IsNullOrWhiteSpace(result.ErrorMessage) && result.Unresolved.Count == 0)
            Status.Value = new ConfigStatusMessage(
                $"Updated {Pluralize(normalized, "channel", "channels")} to canonical IDs and saved.",
                ConfigStatusTone.Neutral);

        NotifyContentChanged();
    }

    /// <summary>
    /// Rewrites Slack allow-list entries stored as channel NAMES that now resolve to a canonical
    /// channel ID, then persists. Names only enter the allow-list when a channel could not be
    /// resolved at save time (the "save all, flag invalid" path); once the bot can see the channel
    /// the stored name must become its ID or the runtime ACL never matches it. Returns the count
    /// normalized (0 means nothing changed, so nothing is written).
    /// </summary>
    private int NormalizeSlackChannelNamesToIds(IReadOnlyList<string> storedChannels, SlackChannelResolutionResult result)
    {
        var resolvedByName = result.Resolved.ToDictionary(
            static channel => channel.Name,
            static channel => channel.Id,
            StringComparer.OrdinalIgnoreCase);

        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        var normalized = new List<string>(storedChannels.Count);
        foreach (var channel in storedChannels)
        {
            if (!IsSlackChannelId(channel)
                && resolvedByName.TryGetValue(channel, out var channelId)
                && !string.Equals(channel, channelId, StringComparison.Ordinal))
            {
                normalized.Add(channelId);
                remap[channel] = channelId;
            }
            else
            {
                normalized.Add(channel);
            }
        }

        if (remap.Count == 0)
            return 0;

        SetChannelIds(ChannelType.Slack, [.. normalized.Distinct(StringComparer.Ordinal)]);
        RemapChannelAudiences(ChannelType.Slack, remap);
        WriteChannelConfigToDisk();
        IsSaved.Value = true;
        return remap.Count;
    }

    private async Task RefreshDiscordChannelLabelsAsync(IReadOnlyList<string> channelIds, CancellationToken ct)
    {
        var discord = Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord);
        var botToken = GetEffectiveSecret("Discord.BotToken", discord.BotToken, discord.HasPersistedBotToken);
        if (string.IsNullOrWhiteSpace(botToken))
            return;

        var result = await _discordProbe.ResolveChannelIdsAsync(botToken, channelIds, ct);
        if (ct.IsCancellationRequested)
            return;

        discord.LastChannelResolution = result;
        ApplyChannelLabelResolutionStatus(ChannelType.Discord, result.ErrorMessage, result.Unresolved);
        NotifyContentChanged();
    }

    private void ApplyChannelLabelResolutionStatus(ChannelType type, string? errorMessage, IReadOnlyList<string> unresolved)
    {
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            Status.Value = new ConfigStatusMessage(
                $"{GetAdapterDisplayName(type)} channel label lookup failed: {errorMessage}",
                ConfigStatusTone.Warning);
            return;
        }

        if (unresolved.Count > 0)
        {
            Status.Value = new ConfigStatusMessage(
                $"{GetAdapterDisplayName(type)} channel labels not found: {string.Join(", ", unresolved)}",
                ConfigStatusTone.Warning);
        }
    }

    private static ChannelsEditorValidationIssue Error(string fieldId, string message)
        => new(fieldId, message, ConfigValidationSeverity.Error);

    private string? GetEffectiveSecret(string path, string? draftValue, bool hasPersistedSecret)
    {
        var normalized = Normalize(draftValue);
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        if (!hasPersistedSecret)
            return null;

        return Normalize(ConfigFileHelper.ReadDecryptedSecret(_paths, path));
    }

    private void RemapChannelAudiences(ChannelType type, IReadOnlyDictionary<string, string> remap)
    {
        if (remap.Count == 0 || !_channelAudiences.TryGetValue(type, out var audiences))
            return;

        foreach (var (oldId, newId) in remap)
        {
            if (!audiences.TryGetValue(oldId, out var audience))
                continue;

            audiences.Remove(oldId);
            audiences.TryAdd(newId, audience);
        }
    }

    private ChannelsEditorValidationIssue? ValidateCredentialDrafts()
    {
        var candidate = ChannelsEditorModel.FromStep(Step);
        ApplyCredentialDrafts(candidate);
        var validation = _validator.Validate(candidate);
        var activeFieldPaths = GetCredentialFieldPaths(_activeAdapterType);
        return validation.Issues.FirstOrDefault(issue => issue.FieldId is null || activeFieldPaths.Contains(issue.FieldId));
    }

    private void ApplyCredentialDrafts(ChannelsEditorModel model)
    {
        switch (_activeAdapterType)
        {
            case ChannelType.Slack:
                model.Slack.Enabled = true;
                model.Slack.BotTokenDraft = Normalize(BotTokenInput);
                model.Slack.AppTokenDraft = Normalize(AppTokenInput);
                break;
            case ChannelType.Discord:
                model.Discord.Enabled = true;
                model.Discord.BotTokenDraft = Normalize(BotTokenInput);
                break;
            case ChannelType.Mattermost:
                model.Mattermost.Enabled = true;
                model.Mattermost.ServerUrl = Normalize(ServerUrlInput);
                model.Mattermost.BotTokenDraft = Normalize(BotTokenInput);
                model.Mattermost.CallbackUrl = Normalize(CallbackUrlInput);
                break;
        }
    }

    private static IReadOnlySet<string> GetCredentialFieldPaths(ChannelType type)
        => type switch
        {
            ChannelType.Slack => new HashSet<string>(StringComparer.Ordinal)
            {
                ChannelsEditorFieldPaths.SlackBotToken,
                ChannelsEditorFieldPaths.SlackAppToken,
            },
            ChannelType.Discord => new HashSet<string>(StringComparer.Ordinal)
            {
                ChannelsEditorFieldPaths.DiscordBotToken,
            },
            ChannelType.Mattermost => new HashSet<string>(StringComparer.Ordinal)
            {
                ChannelsEditorFieldPaths.MattermostServerUrl,
                ChannelsEditorFieldPaths.MattermostBotToken,
                ChannelsEditorFieldPaths.MattermostCallbackUrl,
            },
            _ => new HashSet<string>(StringComparer.Ordinal),
        };

    private static ConfigStatusMessage BuildValidationErrorStatus(
        ChannelsEditorValidationResult validation,
        string fallbackMessage)
    {
        var issue = validation.Issues.FirstOrDefault();
        return issue is null
            ? new ConfigStatusMessage(fallbackMessage, ConfigStatusTone.Error)
            : new ConfigStatusMessage(issue.Message, ConfigStatusTone.Error);
    }

    internal void MoveResetConfirmation(int delta)
    {
        _resetConfirmIndex = Clamp(_resetConfirmIndex + delta, 2);
        NotifyContentChanged();
    }

    internal void ApplyResetConfirmation()
    {
        if (_resetConfirmIndex == 0)
        {
            Screen.Value = ChannelsConfigScreen.AdapterMenu;
            NotifyContentChanged();
            return;
        }

        var resetType = _activeAdapterType;
        var resetName = ActiveAdapterName;
        var session = new ConfigEditorSession(_paths);
        session.Apply(_mapper.BuildResetContribution(resetType));
        session.Save();

        var savedDraft = _mapper.Load(_paths);
        _knownProviders.Clear();
        foreach (var provider in savedDraft.KnownProviders)
            _knownProviders.Add(provider);

        LoadAudienceDrafts(savedDraft);
        Step.OnEnter(_context, NavigationDirection.Forward);
        _mapper.ApplyToStep(Step, savedDraft);
        _activeAdapterType = resetType;
        Screen.Value = ChannelsConfigScreen.Picker;
        Status.Value = new ConfigStatusMessage($"{resetName} reset saved.", ConfigStatusTone.Success);
        IsSaved.Value = true;
        NotifyContentChanged();
    }

    public void RequestQuit()
    {
        ShutdownRequestedForTest = true;
        Shutdown();
    }

    public override void Dispose()
    {
        _labelResolutionCts?.Cancel();
        _labelResolutionCts?.Dispose();
        IsSaved.Dispose();
        Screen.Dispose();
        Status.Dispose();
        Step.Dispose();
        _context.Dispose();
        base.Dispose();
    }

    private void GoBackWithinManagement()
    {
        Screen.Value = Screen.Value switch
        {
            ChannelsConfigScreen.AdapterMenu => ChannelsConfigScreen.Picker,
            ChannelsConfigScreen.ChannelPermissions => ChannelsConfigScreen.AdapterMenu,
            ChannelsConfigScreen.EditAudience => ChannelsConfigScreen.ChannelPermissions,
            ChannelsConfigScreen.AddChannel => ChannelsConfigScreen.ChannelPermissions,
            ChannelsConfigScreen.AllowedUsers => ChannelsConfigScreen.AdapterMenu,
            ChannelsConfigScreen.DirectMessages => ChannelsConfigScreen.AdapterMenu,
            ChannelsConfigScreen.RotateCredentials => ChannelsConfigScreen.AdapterMenu,
            ChannelsConfigScreen.ResetConfirm => ChannelsConfigScreen.AdapterMenu,
            _ => ChannelsConfigScreen.Picker
        };

        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    private void SetActiveAdapterEnabled(bool enabled)
    {
        var selectedIndex = GetAdapterIndex(_activeAdapterType);

        if (Step.IsAdapterEnabled(_activeAdapterType) != enabled)
            Step.ToggleAdapter(selectedIndex);

        UpdateAdapterPickerSummary(_activeAdapterType);

        AutosaveCompletedAction($"{ActiveAdapterName} {(enabled ? "enabled" : "disabled")} and saved.");
    }

    private bool AutosaveCompletedAction(string successMessage)
        => ConfigAutosave.Run(
            // Autosave persists synchronously without the blocking network channel-access probe
            // (validation runs in the background); the remaining await is the fast label-refresh
            // cancellation, not a network round-trip.
            () => SaveAsync(successMessage, probeChannelAccess: false).GetAwaiter().GetResult(),
            Status,
            "Channel settings save failed",
            RequestRedraw);

    private int GetAdapterIndex(ChannelType type)
        => Step.Adapters
            .Select((entry, index) => (entry.Type, index))
            .Single(entry => entry.Type == type)
            .index;

    private void UpdateAdapterPickerSummary(ChannelType type)
    {
        if (!Step.IsAdapterEnabled(type))
        {
            Step.SetAdapterSummary(type, "disabled, saved setup");
            return;
        }

        var channelCount = GetChannelIds(type).Count;
        var userCount = GetAllowedUserIds(type).Count;
        var parts = new List<string>
        {
            channelCount > 0
                ? Pluralize(channelCount, "channel", "channels")
                : GetAllowDirectMessages(type) ? "DMs only" : "no channels"
        };

        if (userCount > 0)
            parts.Add(Pluralize(userCount, "user", "users"));

        Step.SetAdapterSummary(type, string.Join(", ", parts));
    }

    private void LoadAudienceDrafts(ChannelsConfigDraft draft)
    {
        _channelAudiences.Clear();
        AddAudienceDraft(ChannelType.Slack, draft.Slack.ChannelAudiences);
        AddAudienceDraft(ChannelType.Discord, draft.Discord.ChannelAudiences);
        AddAudienceDraft(ChannelType.Mattermost, draft.Mattermost.ChannelAudiences);
    }

    private void AddAudienceDraft(ChannelType type, IReadOnlyDictionary<string, TrustAudience> audiences)
    {
        if (audiences.Count == 0)
            return;

        _channelAudiences[type] = new Dictionary<string, TrustAudience>(audiences, StringComparer.Ordinal);
    }

    private TrustAudience GetChannelAudience(ChannelType type, string channelId, TrustAudience defaultAudience)
        => _channelAudiences.TryGetValue(type, out var audiences) && audiences.TryGetValue(channelId, out var audience)
            ? audience
            : defaultAudience;

    private void SetChannelAudience(ChannelType type, string channelId, TrustAudience audience)
    {
        if (!_channelAudiences.TryGetValue(type, out var audiences))
        {
            audiences = new Dictionary<string, TrustAudience>(StringComparer.Ordinal);
            _channelAudiences[type] = audiences;
        }

        audiences[channelId] = audience;
    }

    private TrustAudience DefaultChannelAudience()
        => ChannelAudienceDefaults.ForChannel(_context.SelectedPosture ?? DeploymentPosture.Personal);

    private TrustAudience DefaultDirectMessageAudience()
        => ChannelAudienceDefaults.ForDirectMessage(
            _context.SelectedPosture ?? DeploymentPosture.Personal,
            GetAllowedUserIds(_activeAdapterType).Count);

    private IReadOnlyList<string> GetChannelIds(ChannelType type) => type switch
    {
        ChannelType.Slack => ChannelCsv.ParseCsv(Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack).ChannelNamesInput, trimHash: true),
        ChannelType.Discord => ChannelCsv.ParseCsv(Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord).ChannelIdsInput, trimHash: true),
        ChannelType.Mattermost => ChannelCsv.ParseCsv(Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost).ChannelIdsInput, trimHash: true),
        _ => []
    };

    private void SetChannelIds(ChannelType type, IReadOnlyList<string> channelIds)
    {
        var value = ChannelCsv.JoinOrNull(channelIds);
        switch (type)
        {
            case ChannelType.Slack:
                Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack).ChannelNamesInput = value;
                break;
            case ChannelType.Discord:
                Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord).ChannelIdsInput = value;
                break;
            case ChannelType.Mattermost:
                Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost).ChannelIdsInput = value;
                break;
        }
    }

    private IReadOnlyList<string> GetAllowedUserIds(ChannelType type) => type switch
    {
        ChannelType.Slack => ChannelCsv.ParseCsv(Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack).AllowedUserIdsInput, trimHash: false),
        ChannelType.Discord => ChannelCsv.ParseCsv(Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord).AllowedUserIdsInput, trimHash: false),
        ChannelType.Mattermost => ChannelCsv.ParseCsv(Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost).AllowedUserIdsInput, trimHash: false),
        _ => []
    };

    private void SetAllowedUserIds(ChannelType type, IReadOnlyList<string> userIds)
    {
        var value = ChannelCsv.JoinOrNull(userIds);
        switch (type)
        {
            case ChannelType.Slack:
                var slack = Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
                slack.RestrictToSpecificUsers = userIds.Count > 0;
                slack.AllowedUserIdsInput = value;
                break;
            case ChannelType.Discord:
                var discord = Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord);
                discord.RestrictToSpecificUsers = userIds.Count > 0;
                discord.AllowedUserIdsInput = value;
                break;
            case ChannelType.Mattermost:
                var mattermost = Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost);
                mattermost.RestrictToSpecificUsers = userIds.Count > 0;
                mattermost.AllowedUserIdsInput = value;
                break;
        }
    }

    private bool GetAllowDirectMessages(ChannelType type) => type switch
    {
        ChannelType.Slack => Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack).AllowDirectMessages,
        ChannelType.Discord => Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord).AllowDirectMessages,
        ChannelType.Mattermost => Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost).AllowDirectMessages,
        _ => false
    };

    private void SetAllowDirectMessages(ChannelType type, bool enabled)
    {
        switch (type)
        {
            case ChannelType.Slack:
                Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack).AllowDirectMessages = enabled;
                break;
            case ChannelType.Discord:
                Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord).AllowDirectMessages = enabled;
                break;
            case ChannelType.Mattermost:
                Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost).AllowDirectMessages = enabled;
                break;
        }
    }

    private string? GetServerUrl(ChannelType type)
        => type == ChannelType.Mattermost
            ? Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost).ServerUrl
            : null;

    private string? GetCallbackUrl(ChannelType type)
        => type == ChannelType.Mattermost
            ? Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost).CallbackUrl
            : null;

    private string? GetCredentialPresenceText(string key)
    {
        return _activeAdapterType switch
        {
            ChannelType.Slack when key == "bot" && Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack).HasPersistedBotToken =>
                "configured - leave blank to keep",
            ChannelType.Slack when key == "app" && Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack).HasPersistedAppToken =>
                "configured - leave blank to keep",
            ChannelType.Discord when key == "bot" && Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord).HasPersistedBotToken =>
                "configured - leave blank to keep",
            ChannelType.Mattermost when key == "bot" && Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost).HasPersistedBotToken =>
                "configured - leave blank to keep",
            _ => null
        };
    }

    private string GetCredentialSummary(ChannelType type)
    {
        return type switch
        {
            ChannelType.Slack =>
                (Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack).HasPersistedBotToken ? "bot token configured" : "bot token missing")
                + " · "
                + (Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack).HasPersistedAppToken ? "app token configured" : "app token missing"),
            ChannelType.Discord => Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord).HasPersistedBotToken
                ? "bot token configured"
                : "bot token missing",
            ChannelType.Mattermost => Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost).HasPersistedBotToken
                ? "bot token configured"
                : "bot token missing",
            _ => "credentials unknown"
        };
    }

    private static string GetAdapterDisplayName(ChannelType type) => type switch
    {
        ChannelType.Slack => "Slack",
        ChannelType.Discord => "Discord",
        ChannelType.Mattermost => "Mattermost",
        _ => type.ToString()
    };

    // Channel names/ids the active adapter's most recent probe could not resolve.
    // Used to flag the matching channel rows; comparison is case-insensitive because
    // resolution is name-based for Slack and the operator's casing may not match.
    private IReadOnlySet<string> GetActiveAdapterUnresolved()
    {
        var unresolved = _activeAdapterType switch
        {
            ChannelType.Slack => Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack).LastChannelResolution?.Unresolved,
            ChannelType.Discord => Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord).LastChannelResolution?.Unresolved,
            ChannelType.Mattermost => Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost).LastChannelResolution?.Unresolved,
            _ => null
        };

        return unresolved is null or { Count: 0 }
            ? EmptyUnresolved
            : new HashSet<string>(unresolved, StringComparer.OrdinalIgnoreCase);
    }

    private static readonly IReadOnlySet<string> EmptyUnresolved =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private string FormatChannelLabel(ChannelType type, string channelId)
        => type switch
        {
            ChannelType.Slack => FormatSlackChannelLabel(channelId),
            ChannelType.Discord => FormatDiscordChannelLabel(channelId),
            ChannelType.Mattermost => channelId,
            _ => channelId
        };

    private string FormatSlackChannelLabel(string channelId)
    {
        var slack = Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
        var resolved = slack.LastChannelResolution?.Resolved.FirstOrDefault(channel =>
            string.Equals(channel.Id, channelId, StringComparison.Ordinal));
        return resolved is null ? channelId : $"#{resolved.Name}";
    }

    private string FormatDiscordChannelLabel(string channelId)
    {
        var discord = Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord);
        var resolved = discord.LastChannelResolution?.Resolved.FirstOrDefault(channel =>
            string.Equals(channel.ChannelId, channelId, StringComparison.Ordinal));
        return resolved?.ToDisplayName() ?? channelId;
    }

    private static int AudienceIndex(TrustAudience audience)
    {
        for (var i = 0; i < AudienceOptions.Count; i++)
        {
            if (AudienceOptions[i] == audience)
                return i;
        }

        return 0;
    }


    private static string? NormalizeChannelId(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimStart('#');

    private static bool IsSlackChannelId(string value)
        => value.Length > 1
           && value[0] is 'C' or 'G'
           && value.Skip(1).All(static ch => char.IsUpper(ch) || char.IsDigit(ch));

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int Clamp(int index, int count)
        => count == 0 ? 0 : Math.Clamp(index, 0, count - 1);

    private static int Wrap(int index, int count)
        => count == 0 ? 0 : (index % count + count) % count;

    private static string Pluralize(int count, string singular, string plural)
        => count == 1 ? $"1 {singular}" : $"{count} {plural}";

    private void ReturnToDashboard()
    {
        if (TryGoBack())
            return;

        RequestQuit();
    }

    private bool TryGoBack()
    {
        if (_navigation is null)
            return false;

        try
        {
            return _navigation.TryGoBack();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void NotifyContentChanged()
    {
        OnStepContentChanged?.Invoke();
        RequestRedraw();
    }

    private void StartChannelLabelResolution(ChannelType type)
    {
        if (type is not (ChannelType.Slack or ChannelType.Discord))
            return;

        _labelResolutionCts?.Cancel();
        _labelResolutionCts?.Dispose();
        _labelResolutionCts = new CancellationTokenSource();
        _labelRefreshTask = RefreshChannelLabelsAsync(type, _labelResolutionCts.Token);
    }

    // Exposes the in-flight background label refresh for tests asserting save/dispose serialization.
    internal Task? PendingLabelRefresh => _labelRefreshTask;

    // Stop the in-flight background label refresh (if any) and wait for it to unwind before the
    // caller validates, persists, or resets channel state. Without this, a probe that resumes after
    // a save could clobber the just-reloaded view-model state or write a stale snapshot over the
    // save — the two HIGH races in the deep review (background normalizer vs SaveAsync).
    private async Task CancelAndAwaitLabelRefreshAsync()
    {
        _labelResolutionCts?.Cancel();
        var inFlight = _labelRefreshTask;
        if (inFlight is null)
            return;

        // RefreshChannelLabelsAsync swallows its own OperationCanceledException, so awaiting the
        // tracked task observes completion without throwing; the catch is defensive only.
        try
        {
            await inFlight;
        }
        catch (OperationCanceledException)
        {
        }

        _labelRefreshTask = null;
    }

    private static DeploymentPosture LoadDeploymentPosture(NetclawPaths paths)
    {
        var config = ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);
        if (!ConfigFileHelper.TryGetPathValue(config, "Security.DeploymentPosture", out var value))
            return DeploymentPosture.Personal;

        if (Enum.TryParse<DeploymentPosture>(value?.ToString(), ignoreCase: true, out var posture))
            return posture;

        throw new InvalidOperationException($"Configuration value 'Security.DeploymentPosture' is not a valid deployment posture: {value}.");
    }
}

internal enum ChannelsConfigScreen
{
    Picker,
    AdapterMenu,
    ChannelPermissions,
    EditAudience,
    AddChannel,
    AllowedUsers,
    DirectMessages,
    RotateCredentials,
    ResetConfirm
}

internal enum ChannelsManagementAction
{
    ManageChannels,
    AddChannel,
    ManageUsers,
    DirectMessages,
    RotateCredentials,
    ToggleEnabled,
    ResetConnection
}

internal sealed record ChannelsManagementMenuItem(
    ChannelsManagementAction Action,
    string Label,
    string Description);

internal sealed record ChannelPermissionRow(
    string Id,
    string DisplayName,
    TrustAudience Audience,
    bool IsDirectMessage,
    bool IsAddAction,
    bool IsDoneAction,
    bool IsUnresolved = false)
{
    internal bool IsAction => IsAddAction || IsDoneAction;
}

internal sealed record CredentialFieldSpec(
    string Key,
    string Label,
    bool IsSecret,
    string Placeholder,
    string? Hint);

internal sealed record ChannelPersistenceSpec(
    string ConfigSection,
    IReadOnlyList<string> SecretPaths);

internal sealed class ChannelsConfigPersistenceMapper
{
    private static readonly IReadOnlyDictionary<ChannelType, ChannelPersistenceSpec> ChannelSpecs =
        new Dictionary<ChannelType, ChannelPersistenceSpec>
        {
            [ChannelType.Slack] = new ChannelPersistenceSpec("Slack", ["Slack.BotToken", "Slack.AppToken"]),
            [ChannelType.Discord] = new ChannelPersistenceSpec("Discord", ["Discord.BotToken"]),
            [ChannelType.Mattermost] = new ChannelPersistenceSpec("Mattermost", ["Mattermost.BotToken"])
        };

    internal ChannelsConfigDraft Load(NetclawPaths paths)
    {
        var config = ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);
        var secrets = ConfigFileHelper.LoadJsonDict(paths.SecretsPath);
        var draft = new ChannelsConfigDraft
        {
            Slack = LoadSlack(paths, config, secrets),
            Discord = LoadDiscord(paths, config, secrets),
            Mattermost = LoadMattermost(paths, config, secrets)
        };

        AddKnownProvider(draft.KnownProviders, ChannelType.Slack, draft.Slack.IsKnown);
        AddKnownProvider(draft.KnownProviders, ChannelType.Discord, draft.Discord.IsKnown);
        AddKnownProvider(draft.KnownProviders, ChannelType.Mattermost, draft.Mattermost.IsKnown);
        return draft;
    }

    internal void ApplyToStep(ChannelPickerStepViewModel step, ChannelsConfigDraft draft)
    {
        step.LoadAdapterState(
            ChannelType.Slack,
            draft.Slack.Enabled,
            BuildSummary(draft.Slack),
            vm => ApplySlack((SlackStepViewModel)vm, draft.Slack),
            draft.Slack.IsKnown);

        step.LoadAdapterState(
            ChannelType.Discord,
            draft.Discord.Enabled,
            BuildSummary(draft.Discord),
            vm => ApplyDiscord((DiscordStepViewModel)vm, draft.Discord),
            draft.Discord.IsKnown);

        step.LoadAdapterState(
            ChannelType.Mattermost,
            draft.Mattermost.Enabled,
            BuildSummary(draft.Mattermost),
            vm => ApplyMattermost((MattermostStepViewModel)vm, draft.Mattermost),
            draft.Mattermost.IsKnown);
    }

    internal SectionContribution BuildContribution(
        ChannelPickerStepViewModel step,
        IReadOnlySet<ChannelType> knownProviders,
        IReadOnlyDictionary<ChannelType, Dictionary<string, TrustAudience>> channelAudiences,
        DeploymentPosture posture)
    {
        var fields = new List<SectionFieldAction>();
        var secrets = new List<SectionSecretAction>();

        // The picker (Step.IsAdapterEnabled) is the single source of truth for
        // "is this adapter enabled?" — the same source dynamic validation uses. The
        // sub-VM's own *Enabled flag is a parallel copy; gating the contribution on it
        // instead let a validated+probed adapter persist nothing (Enabled=false, no
        // channels) while Save() still reported success. Read the canonical flag here
        // so a save can never half-write an adapter the editor treats as enabled.
        AddSlackContribution(
            fields,
            secrets,
            step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack),
            step.IsAdapterEnabled(ChannelType.Slack),
            knownProviders.Contains(ChannelType.Slack),
            channelAudiences,
            posture);
        AddDiscordContribution(
            fields,
            secrets,
            step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord),
            step.IsAdapterEnabled(ChannelType.Discord),
            knownProviders.Contains(ChannelType.Discord),
            channelAudiences,
            posture);
        AddMattermostContribution(
            fields,
            secrets,
            step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost),
            step.IsAdapterEnabled(ChannelType.Mattermost),
            knownProviders.Contains(ChannelType.Mattermost),
            channelAudiences,
            posture);

        return new SectionContribution(fields, secrets);
    }

    internal SectionContribution BuildResetContribution(ChannelType type)
    {
        var fields = new List<SectionFieldAction>();
        var secrets = new List<SectionSecretAction>();
        AddResetActions(fields, secrets, type);

        return new SectionContribution(fields, secrets);
    }

    private static SlackChannelDraft LoadSlack(
        NetclawPaths paths,
        Dictionary<string, object> config,
        Dictionary<string, object> secrets)
    {
        var hasBotToken = HasSecret(paths, secrets, "Slack.BotToken");
        var hasAppToken = HasSecret(paths, secrets, "Slack.AppToken");
        var sectionPresent = SectionPresent(config, "Slack");
        var channels = ReadConfiguredChannels(config, "Slack");
        var users = GetStringArray(config, "Slack.AllowedUserIds");
        return new SlackChannelDraft
        {
            IsKnown = sectionPresent || hasBotToken || hasAppToken,
            Enabled = sectionPresent && GetBool(config, "Slack.Enabled", defaultValue: false),
            HasPersistedBotToken = hasBotToken,
            HasPersistedAppToken = hasAppToken,
            ChannelIds = channels,
            AllowDirectMessages = GetBool(config, "Slack.AllowDirectMessages", defaultValue: false),
            AllowedUserIds = users,
            ChannelAudiences = GetChannelAudiences(config, "Slack.ChannelAudiences")
        };
    }

    private static DiscordChannelDraft LoadDiscord(
        NetclawPaths paths,
        Dictionary<string, object> config,
        Dictionary<string, object> secrets)
    {
        var hasBotToken = HasSecret(paths, secrets, "Discord.BotToken");
        var sectionPresent = SectionPresent(config, "Discord");
        var channels = ReadConfiguredChannels(config, "Discord");
        var users = GetStringArray(config, "Discord.AllowedUserIds");
        return new DiscordChannelDraft
        {
            IsKnown = sectionPresent || hasBotToken,
            Enabled = sectionPresent && GetBool(config, "Discord.Enabled", defaultValue: false),
            HasPersistedBotToken = hasBotToken,
            ChannelIds = channels,
            AllowDirectMessages = GetBool(config, "Discord.AllowDirectMessages", defaultValue: false),
            AllowedUserIds = users,
            ChannelAudiences = GetChannelAudiences(config, "Discord.ChannelAudiences")
        };
    }

    private static MattermostChannelDraft LoadMattermost(
        NetclawPaths paths,
        Dictionary<string, object> config,
        Dictionary<string, object> secrets)
    {
        var hasBotToken = HasSecret(paths, secrets, "Mattermost.BotToken");
        var sectionPresent = SectionPresent(config, "Mattermost");
        var channels = ReadConfiguredChannels(config, "Mattermost");
        var users = GetStringArray(config, "Mattermost.AllowedUserIds");
        return new MattermostChannelDraft
        {
            IsKnown = sectionPresent || hasBotToken,
            Enabled = sectionPresent && GetBool(config, "Mattermost.Enabled", defaultValue: false),
            HasPersistedBotToken = hasBotToken,
            ServerUrl = GetString(config, "Mattermost.ServerUrl"),
            CallbackUrl = GetString(config, "Mattermost.CallbackUrl"),
            ChannelIds = channels,
            AllowDirectMessages = GetBool(config, "Mattermost.AllowDirectMessages", defaultValue: false),
            AllowedUserIds = users,
            ChannelAudiences = GetChannelAudiences(config, "Mattermost.ChannelAudiences")
        };
    }

    private static void ApplySlack(SlackStepViewModel vm, SlackChannelDraft draft)
    {
        vm.SlackEnabled = draft.Enabled;
        vm.BotToken = null;
        vm.AppToken = null;
        vm.HasPersistedBotToken = draft.HasPersistedBotToken;
        vm.HasPersistedAppToken = draft.HasPersistedAppToken;
        vm.ChannelNamesInput = ChannelCsv.JoinOrNull(draft.ChannelIds);
        vm.AllowDirectMessages = draft.AllowDirectMessages;
        vm.RestrictToSpecificUsers = draft.AllowedUserIds.Count > 0;
        vm.AllowedUserIdsInput = ChannelCsv.JoinOrNull(draft.AllowedUserIds);
    }

    private static void ApplyDiscord(DiscordStepViewModel vm, DiscordChannelDraft draft)
    {
        vm.DiscordEnabled = draft.Enabled;
        vm.BotToken = null;
        vm.HasPersistedBotToken = draft.HasPersistedBotToken;
        vm.ChannelIdsInput = ChannelCsv.JoinOrNull(draft.ChannelIds);
        vm.AllowDirectMessages = draft.AllowDirectMessages;
        vm.RestrictToSpecificUsers = draft.AllowedUserIds.Count > 0;
        vm.AllowedUserIdsInput = ChannelCsv.JoinOrNull(draft.AllowedUserIds);
    }

    private static void ApplyMattermost(MattermostStepViewModel vm, MattermostChannelDraft draft)
    {
        vm.MattermostEnabled = draft.Enabled;
        vm.ServerUrl = draft.ServerUrl;
        vm.BotToken = null;
        vm.HasPersistedBotToken = draft.HasPersistedBotToken;
        vm.ChannelIdsInput = ChannelCsv.JoinOrNull(draft.ChannelIds);
        vm.AllowDirectMessages = draft.AllowDirectMessages;
        vm.RestrictToSpecificUsers = draft.AllowedUserIds.Count > 0;
        vm.AllowedUserIdsInput = ChannelCsv.JoinOrNull(draft.AllowedUserIds);
        vm.CallbackUrl = draft.CallbackUrl;
    }

    private static void AddSlackContribution(
        List<SectionFieldAction> fields,
        List<SectionSecretAction> secrets,
        SlackStepViewModel vm,
        bool enabled,
        bool knownProvider,
        IReadOnlyDictionary<ChannelType, Dictionary<string, TrustAudience>> channelAudiences,
        DeploymentPosture posture)
    {
        if (!enabled)
        {
            if (knownProvider)
                fields.Add(new SectionFieldAction("Slack.Enabled", SectionFieldActionKind.Set, false));
            AddSecretPreserveOrSet(secrets, "Slack.BotToken", vm.BotToken, vm.HasPersistedBotToken);
            AddSecretPreserveOrSet(secrets, "Slack.AppToken", vm.AppToken, vm.HasPersistedAppToken);
            return;
        }

        var channelIds = ChannelCsv.ParseCsv(vm.ChannelNamesInput, trimHash: true);
        var userIds = vm.RestrictToSpecificUsers ? ChannelCsv.ParseCsv(vm.AllowedUserIdsInput, trimHash: false) : [];

        fields.Add(new SectionFieldAction("Slack.Enabled", SectionFieldActionKind.Set, true));
        fields.Add(new SectionFieldAction("Slack.SocketMode", SectionFieldActionKind.Set, true));
        fields.Add(new SectionFieldAction("Slack.AllowDirectMessages", SectionFieldActionKind.Set, vm.AllowDirectMessages));
        SetArrayOrDelete(fields, "Slack.AllowedChannelIds", channelIds);
        SetStringOrDelete(fields, "Slack.DefaultChannelId", channelIds.FirstOrDefault());
        fields.Add(new SectionFieldAction("Slack.DefaultChannelName", SectionFieldActionKind.Delete));
        SetArrayOrDelete(fields, "Slack.AllowedUserIds", userIds);
        SetDictionaryOrDelete(fields, "Slack.ChannelAudiences", BuildAudienceMap(ChannelType.Slack, channelIds, userIds, vm.AllowDirectMessages, channelAudiences, posture));
        AddSecretPreserveOrSet(secrets, "Slack.BotToken", vm.BotToken, vm.HasPersistedBotToken);
        AddSecretPreserveOrSet(secrets, "Slack.AppToken", vm.AppToken, vm.HasPersistedAppToken);
    }

    private static void AddDiscordContribution(
        List<SectionFieldAction> fields,
        List<SectionSecretAction> secrets,
        DiscordStepViewModel vm,
        bool enabled,
        bool knownProvider,
        IReadOnlyDictionary<ChannelType, Dictionary<string, TrustAudience>> channelAudiences,
        DeploymentPosture posture)
    {
        if (!enabled)
        {
            if (knownProvider)
                fields.Add(new SectionFieldAction("Discord.Enabled", SectionFieldActionKind.Set, false));
            AddSecretPreserveOrSet(secrets, "Discord.BotToken", vm.BotToken, vm.HasPersistedBotToken);
            return;
        }

        var channelIds = ChannelCsv.ParseCsv(vm.ChannelIdsInput, trimHash: true);
        var userIds = vm.RestrictToSpecificUsers ? ChannelCsv.ParseCsv(vm.AllowedUserIdsInput, trimHash: false) : [];

        fields.Add(new SectionFieldAction("Discord.Enabled", SectionFieldActionKind.Set, true));
        fields.Add(new SectionFieldAction("Discord.AllowDirectMessages", SectionFieldActionKind.Set, vm.AllowDirectMessages));
        SetArrayOrDelete(fields, "Discord.AllowedChannelIds", channelIds);
        SetStringOrDelete(fields, "Discord.DefaultChannelId", channelIds.FirstOrDefault());
        SetArrayOrDelete(fields, "Discord.AllowedUserIds", userIds);
        SetDictionaryOrDelete(fields, "Discord.ChannelAudiences", BuildAudienceMap(ChannelType.Discord, channelIds, userIds, vm.AllowDirectMessages, channelAudiences, posture));
        AddSecretPreserveOrSet(secrets, "Discord.BotToken", vm.BotToken, vm.HasPersistedBotToken);
    }

    private static void AddMattermostContribution(
        List<SectionFieldAction> fields,
        List<SectionSecretAction> secrets,
        MattermostStepViewModel vm,
        bool enabled,
        bool knownProvider,
        IReadOnlyDictionary<ChannelType, Dictionary<string, TrustAudience>> channelAudiences,
        DeploymentPosture posture)
    {
        if (!enabled)
        {
            if (knownProvider)
                fields.Add(new SectionFieldAction("Mattermost.Enabled", SectionFieldActionKind.Set, false));
            AddSecretPreserveOrSet(secrets, "Mattermost.BotToken", vm.BotToken, vm.HasPersistedBotToken);
            return;
        }

        var channelIds = ChannelCsv.ParseCsv(vm.ChannelIdsInput, trimHash: true);
        var userIds = vm.RestrictToSpecificUsers ? ChannelCsv.ParseCsv(vm.AllowedUserIdsInput, trimHash: false) : [];

        fields.Add(new SectionFieldAction("Mattermost.Enabled", SectionFieldActionKind.Set, true));
        fields.Add(new SectionFieldAction("Mattermost.AllowDirectMessages", SectionFieldActionKind.Set, vm.AllowDirectMessages));
        SetStringOrDelete(fields, "Mattermost.ServerUrl", Normalize(vm.ServerUrl));
        SetStringOrDelete(fields, "Mattermost.CallbackUrl", Normalize(vm.CallbackUrl));
        SetArrayOrDelete(fields, "Mattermost.AllowedChannelIds", channelIds);
        SetStringOrDelete(fields, "Mattermost.DefaultChannelId", channelIds.FirstOrDefault());
        SetArrayOrDelete(fields, "Mattermost.AllowedUserIds", userIds);
        SetDictionaryOrDelete(fields, "Mattermost.ChannelAudiences", BuildAudienceMap(ChannelType.Mattermost, channelIds, userIds, vm.AllowDirectMessages, channelAudiences, posture));
        AddSecretPreserveOrSet(secrets, "Mattermost.BotToken", vm.BotToken, vm.HasPersistedBotToken);
    }

    private static void AddSecretPreserveOrSet(
        List<SectionSecretAction> secrets,
        string path,
        string? draftValue,
        bool hasPersistedSecret)
    {
        var normalized = Normalize(draftValue);
        if (!string.IsNullOrWhiteSpace(normalized))
            secrets.Add(new SectionSecretAction(path, SectionSecretActionKind.Set, new SensitiveString(normalized)));
        else if (hasPersistedSecret)
            secrets.Add(new SectionSecretAction(path, SectionSecretActionKind.Preserve));
    }

    private static void AddResetActions(
        List<SectionFieldAction> fields,
        List<SectionSecretAction> secrets,
        ChannelType type)
    {
        if (!ChannelSpecs.TryGetValue(type, out var spec))
            return;

        fields.Add(new SectionFieldAction(spec.ConfigSection, SectionFieldActionKind.Delete));
        foreach (var secretPath in spec.SecretPaths)
            secrets.Add(new SectionSecretAction(secretPath, SectionSecretActionKind.Delete));
    }

    private static void SetArrayOrDelete(List<SectionFieldAction> fields, string path, IReadOnlyList<string> values)
    {
        fields.Add(values.Count > 0
            ? new SectionFieldAction(path, SectionFieldActionKind.Set, values.ToArray())
            : new SectionFieldAction(path, SectionFieldActionKind.Delete));
    }

    private static void SetDictionaryOrDelete(List<SectionFieldAction> fields, string path, IReadOnlyDictionary<string, string> values)
    {
        fields.Add(values.Count > 0
            ? new SectionFieldAction(path, SectionFieldActionKind.Set, new Dictionary<string, string>(values, StringComparer.Ordinal))
            : new SectionFieldAction(path, SectionFieldActionKind.Delete));
    }

    private static void SetStringOrDelete(List<SectionFieldAction> fields, string path, string? value)
    {
        var normalized = Normalize(value);
        fields.Add(!string.IsNullOrWhiteSpace(normalized)
            ? new SectionFieldAction(path, SectionFieldActionKind.Set, normalized)
            : new SectionFieldAction(path, SectionFieldActionKind.Delete));
    }

    private static bool SectionPresent(Dictionary<string, object> config, string sectionName)
    {
        if (!ConfigFileHelper.TryGetPathValue(config, sectionName, out var value) || value is null)
            return false;

        if (value is Dictionary<string, object>)
            return true;

        throw new InvalidOperationException($"Configuration section '{sectionName}' must be an object.");
    }

    private static Dictionary<string, string> BuildAudienceMap(
        ChannelType type,
        IReadOnlyList<string> channelIds,
        IReadOnlyList<string> userIds,
        bool allowDirectMessages,
        IReadOnlyDictionary<ChannelType, Dictionary<string, TrustAudience>> channelAudiences,
        DeploymentPosture posture)
    {
        channelAudiences.TryGetValue(type, out var explicitAudiences);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var channelId in channelIds)
        {
            var audience = explicitAudiences is not null && explicitAudiences.TryGetValue(channelId, out var explicitAudience)
                ? explicitAudience
                : ChannelAudienceDefaults.ForChannel(posture);
            map[channelId] = audience.ToWireValue();
        }

        if (explicitAudiences is not null && explicitAudiences.TryGetValue("dm", out var explicitDmAudience))
        {
            map["dm"] = explicitDmAudience.ToWireValue();
        }
        else if (allowDirectMessages)
        {
            map["dm"] = ChannelAudienceDefaults.ForDirectMessage(posture, userIds.Count).ToWireValue();
        }

        return map;
    }

    private static bool GetBool(Dictionary<string, object> config, string path, bool defaultValue)
    {
        if (!ConfigFileHelper.TryGetPathValue(config, path, out var value) || value is null)
            return defaultValue;

        return value is bool boolValue
            ? boolValue
            : throw new InvalidOperationException($"Configuration value '{path}' must be a boolean.");
    }

    private static string? GetString(Dictionary<string, object> config, string path)
    {
        if (!ConfigFileHelper.TryGetPathValue(config, path, out var value) || value is null)
            return null;

        return value is string stringValue
            ? stringValue
            : throw new InvalidOperationException($"Configuration value '{path}' must be a string.");
    }

    private static IReadOnlyList<string> ReadConfiguredChannels(Dictionary<string, object> config, string sectionName)
    {
        var channels = new List<string>();
        channels.AddRange(GetStringArray(config, $"{sectionName}.AllowedChannelIds"));

        var defaultChannelId = GetString(config, $"{sectionName}.DefaultChannelId");
        if (!string.IsNullOrWhiteSpace(defaultChannelId))
            channels.Add(defaultChannelId);

        if (string.Equals(sectionName, "Slack", StringComparison.Ordinal))
        {
            var defaultChannelName = GetString(config, "Slack.DefaultChannelName");
            if (!string.IsNullOrWhiteSpace(defaultChannelName))
                channels.Add(defaultChannelName.StartsWith('#') ? defaultChannelName : $"#{defaultChannelName}");
        }

        return [.. channels
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)];
    }

    private static IReadOnlyList<string> GetStringArray(Dictionary<string, object> config, string path)
    {
        if (!ConfigFileHelper.TryGetPathValue(config, path, out var value) || value is null)
            return [];

        if (value is object[] objectValues)
        {
            return [.. objectValues
                .Select(static item => item switch
                {
                    string stringValue => stringValue,
                    JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()!,
                    _ => throw new InvalidOperationException("Channel list values must be strings.")
                })
                .Where(static item => !string.IsNullOrWhiteSpace(item))];
        }

        if (value is string[] stringValues)
            return [.. stringValues.Where(static item => !string.IsNullOrWhiteSpace(item))];

        throw new InvalidOperationException($"Configuration value '{path}' must be an array of strings.");
    }

    private static Dictionary<string, TrustAudience> GetChannelAudiences(Dictionary<string, object> config, string path)
    {
        if (!ConfigFileHelper.TryGetPathValue(config, path, out var value) || value is null)
            return [];

        if (value is not Dictionary<string, object> values)
            throw new InvalidOperationException($"Configuration value '{path}' must be an object.");

        var audiences = new Dictionary<string, TrustAudience>(StringComparer.Ordinal);
        foreach (var (channelId, rawAudience) in values)
        {
            var wire = rawAudience switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => throw new InvalidOperationException($"Channel audience '{path}.{channelId}' must be a string.")
            };

            if (!SecurityPolicyDefaults.TryParseAudience(wire, out var audience))
                throw new InvalidOperationException($"Channel audience '{path}.{channelId}' is not valid: {wire}.");

            audiences[channelId] = audience;
        }

        return audiences;
    }

    private static bool HasSecret(NetclawPaths paths, Dictionary<string, object> secrets, string path)
    {
        if (!ConfigFileHelper.TryGetPathValue(secrets, path, out var value))
            return false;

        return !string.IsNullOrWhiteSpace(ConfigFileHelper.DecryptIfEncrypted(paths, value?.ToString()));
    }

    private static string? BuildSummary(ChannelProviderDraft draft)
    {
        if (!draft.IsKnown)
            return null;

        if (!draft.Enabled)
            return "disabled, saved setup";

        var channelCount = draft.ChannelIds.Count;
        var userCount = draft.AllowedUserIds.Count;
        var parts = new List<string>
        {
            channelCount > 0
                ? Pluralize(channelCount, "channel", "channels")
                : draft.AllowDirectMessages ? "DMs only" : "no channels"
        };

        if (userCount > 0)
            parts.Add(Pluralize(userCount, "user", "users"));

        return string.Join(", ", parts);
    }

    private static void AddKnownProvider(HashSet<ChannelType> knownProviders, ChannelType type, bool isKnown)
    {
        if (isKnown)
            knownProviders.Add(type);
    }


    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Pluralize(int count, string singular, string plural)
        => count == 1 ? $"1 {singular}" : $"{count} {plural}";
}

internal sealed class ChannelsConfigDraft
{
    public required SlackChannelDraft Slack { get; init; }
    public required DiscordChannelDraft Discord { get; init; }
    public required MattermostChannelDraft Mattermost { get; init; }
    public HashSet<ChannelType> KnownProviders { get; } = [];
}

internal abstract class ChannelProviderDraft
{
    public bool IsKnown { get; init; }
    public bool Enabled { get; init; }
    public IReadOnlyList<string> ChannelIds { get; init; } = [];
    public bool AllowDirectMessages { get; init; }
    public IReadOnlyList<string> AllowedUserIds { get; init; } = [];
    public IReadOnlyDictionary<string, TrustAudience> ChannelAudiences { get; init; } = new Dictionary<string, TrustAudience>(StringComparer.Ordinal);
}

internal sealed class SlackChannelDraft : ChannelProviderDraft
{
    public bool HasPersistedBotToken { get; init; }
    public bool HasPersistedAppToken { get; init; }
}

internal sealed class DiscordChannelDraft : ChannelProviderDraft
{
    public bool HasPersistedBotToken { get; init; }
}

internal sealed class MattermostChannelDraft : ChannelProviderDraft
{
    public string? ServerUrl { get; init; }
    public bool HasPersistedBotToken { get; init; }
    public string? CallbackUrl { get; init; }
}

/// <summary>
/// Shared parsing for the comma-separated channel/user lists in the Channels editor. One copy
/// feeds the display/read path and one feeds the persistence-mapper write path — keeping them
/// here guarantees a channel list is canonicalized identically in both directions.
/// </summary>
internal static class ChannelCsv
{
    internal static List<string> ParseCsv(string? input, bool trimHash)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];

        return [.. input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => trimHash ? value.Trim().TrimStart('#') : value.Trim())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)];
    }

    internal static string? JoinOrNull(IReadOnlyList<string> values)
        => values.Count == 0 ? null : string.Join(", ", values);
}
