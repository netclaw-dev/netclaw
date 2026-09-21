// -----------------------------------------------------------------------
// <copyright file="ChannelsConfigViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Netclaw.Actors.Channels;
using Netclaw.Channels.Slack;
using Netclaw.Channels.Teams;
using Netclaw.Channels.Teams.Graph;
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
    private readonly TimeProvider _timeProvider;
    private readonly TuiNavigation? _navigation;
    private readonly Func<TeamsChannelOptions, ITeamsDirectory>? _teamsDirectoryFactory;
    private readonly Func<string, string?> _environmentVariableReader;
    private readonly ChannelsConfigPersistenceMapper _mapper = new();
    private readonly ChannelsEditorValidationAdapter _validator = new();
    private readonly WizardContext _context;
    private readonly HashSet<ChannelType> _knownProviders;
    private readonly Dictionary<ChannelType, Dictionary<string, TrustAudience>> _channelAudiences = [];
    private readonly Dictionary<ChannelType, Dictionary<string, bool>> _channelMentionRequired = [];
    private readonly Dictionary<string, TeamsDirectoryTeam> _teamsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TeamsDirectoryChannel> _teamsChannelsByIdentity = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _teamsChannelTeamIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TeamsDirectoryUser> _teamsUsersById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TeamsDirectoryGroup> _teamsGroupsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TeamsDirectoryGroupChat> _teamsGroupChatsById = new(StringComparer.Ordinal);
    private ChannelType _activeAdapterType = ChannelType.Slack;
    private int _managementMenuIndex;
    private int _channelRowIndex;
    private int _audienceSelectionIndex;
    private int _directMessagesRowIndex;
    private int _resetConfirmIndex;
    private CancellationTokenSource? _labelResolutionCts;
    private Task? _labelRefreshTask;
    private TeamsDirectorySearchController? _teamsDirectorySearch;
    private ITeamsDirectory? _teamsDirectory;
    private IDisposable? _teamsDirectoryLifetime;
    private bool? _teamsDirectoryLabelsAvailable;
    private IReadOnlyList<TeamsDirectoryTeam> _teamSearchResults = [];
    private IReadOnlyList<TeamsDirectoryChannel> _channelSearchResults = [];
    private IReadOnlyList<TeamsDirectoryUser> _userSearchResults = [];
    private IReadOnlyList<TeamsDirectoryGroup> _groupSearchResults = [];
    private TeamsDirectoryTeam? _selectedTeam;
    private int _directoryResultIndex;
    private TeamsChannelAccessOverride? _editingChannelAccess;
    private int _channelAccessRowIndex;
    private int _teamsDestinationAddIndex;
    private bool _isGroupChatDiscovery;
    private bool _hasSearchedGroupChats;
    private IReadOnlyList<TeamsDirectoryGroupChat> _groupChatSearchResults = [];
    private string? _groupChatContinuation;
    private Task? _groupChatSearchTask;
    private CancellationTokenSource? _groupChatSearchCts;
    private long _groupChatSearchGeneration;
    private bool _isGroupChatSearchRunning;
    private TeamsDirectoryGroupChatSearchPage? _groupChatSearchProgress;
    internal const int MaximumGroupChatSearchBatches = 20;
    internal const int MaximumRetainedGroupChatMatches = 1000;
    private static readonly TimeSpan GroupChatSearchRunLimit = TimeSpan.FromMinutes(2);
    private string? _groupChatSearchInput;
    private string? _groupChatDetailsId;
    private int _teamsPrincipalManagementIndex;
    private int _teamsPrincipalFilterIndex;
    private int _teamsPrincipalRemovalIndex;
    private TeamsPrincipalRow? _pendingPrincipalRemoval;
    private TeamsChannelPrincipalRemoval? _pendingChannelPrincipalRemoval;
    private ChannelPermissionRow? _pendingTeamsDestinationRemoval;
    private int _teamsDestinationRemovalIndex;
    private ChannelsConfigScreen? _teamsPrincipalSearchReturnScreen;

    // Cancels every input-triggered config write (and its channel-access probe) when the editor is
    // torn down. Fire-and-forget writes resume on thread-pool continuations (the loop has no
    // SynchronizationContext), so without a lifetime token a write started just before disposal would
    // run its probe to completion and then mutate already-disposed reactive state. Threaded into the
    // FromInput entry points; cancelled and drained in Dispose.
    private readonly CancellationTokenSource _lifetimeCts = new();

    public ChannelsConfigViewModel(
        NetclawPaths paths,
        ISlackProbe slackProbe,
        IDiscordProbe discordProbe,
        IMattermostProbe mattermostProbe,
        TimeProvider timeProvider,
        TuiNavigation? navigation = null,
        Func<TeamsChannelOptions, ITeamsDirectory>? teamsDirectoryFactory = null,
        Func<string, string?>? environmentVariableReader = null)
    {
        _paths = paths;
        _slackProbe = slackProbe;
        _discordProbe = discordProbe;
        _mattermostProbe = mattermostProbe;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _navigation = navigation;
        _teamsDirectoryFactory = teamsDirectoryFactory;
        _environmentVariableReader = environmentVariableReader ?? Environment.GetEnvironmentVariable;
        Status = new ReactiveProperty<ConfigStatusMessage>(new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral));
        Step = new ChannelPickerStepViewModel(slackProbe, discordProbe, mattermostProbe)
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
    internal bool IsCredentialSaveInProgress { get; private set; }
    public Action? OnStepContentChanged { get; set; }

    internal bool ShutdownRequestedForTest { get; private set; }

    internal ChannelType ActiveAdapterType => _activeAdapterType;
    internal string ActiveAdapterName => GetAdapterDisplayName(_activeAdapterType);

    // Matches the first-connect sub-flow's guidance per adapter. Discord/Mattermost display names rarely
    // resolve from a name alone (server+channel ambiguity), so steer the operator to channel IDs there.
    internal string AddChannelPlaceholder => _activeAdapterType switch
    {
        ChannelType.Slack => "channel names or IDs, comma-separated",
        ChannelType.Teams => "Advanced: canonical Teams channel IDs, comma-separated",
        _ => "channel IDs, comma-separated"
    };
    internal int ManagementMenuIndex => _managementMenuIndex;
    internal int ChannelRowIndex => _channelRowIndex;
    internal int AudienceSelectionIndex => _audienceSelectionIndex;
    internal int DirectMessagesRowIndex => _directMessagesRowIndex;
    internal int ResetConfirmIndex => _resetConfirmIndex;
    internal string? AddChannelInput { get; set; }
    internal string? AllowedUsersInput { get; set; }
    internal string? AllowedGroupsInput { get; set; }
    internal string? AllowedGroupChatsInput { get; set; }
    internal bool GroupChatsEnabled { get; set; }
    internal bool IsGroupChatSaveInProgress { get; private set; }
    internal bool IsGroupChatIngressEnabled => Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).AllowGroupChats;
    internal bool AttachmentsEnabled { get; private set; }
    internal string? DirectorySearchInput { get; set; }
    internal bool DirectMessagesEnabled { get; set; }
    internal string? BotTokenInput { get; set; }
    internal string? AppTokenInput { get; set; }
    internal string? ServerUrlInput { get; set; }
    internal string? CallbackUrlInput { get; set; }
    internal string? TenantIdInput { get; set; }
    internal string? ClientIdInput { get; set; }
    internal string? BotIdInput { get; set; }
    internal string? ClientSecretInput { get; set; }
    internal int CredentialFieldIndex { get; set; }
    internal int DirectoryResultIndex => _directoryResultIndex;
    internal int ChannelAccessRowIndex => _channelAccessRowIndex;
    internal TeamsChannelAccessOverride? EditingChannelAccess => _editingChannelAccess;
    internal IReadOnlyList<TeamsDirectoryTeam> TeamSearchResults => _teamSearchResults;
    internal IReadOnlyList<TeamsDirectoryChannel> ChannelSearchResults => _channelSearchResults;
    internal IReadOnlyList<TeamsDirectoryUser> UserSearchResults => _userSearchResults;
    internal IReadOnlyList<TeamsDirectoryGroup> GroupSearchResults => _groupSearchResults;
    internal TeamsDirectoryTeam? SelectedTeam => _selectedTeam;
    internal int TeamsDestinationAddIndex => _teamsDestinationAddIndex;
    internal bool IsGroupChatDiscovery => _isGroupChatDiscovery;
    internal IReadOnlyList<TeamsDirectoryGroupChat> GroupChatSearchResults => _groupChatSearchResults;
    internal bool HasSearchedGroupChats => _hasSearchedGroupChats;
    internal string? GroupChatSearchInput
    {
        get => _groupChatSearchInput;
        set
        {
            if (string.Equals(_groupChatSearchInput, value, StringComparison.Ordinal))
                return;

            _groupChatSearchInput = value;
            CancelGroupChatSearch();
            _groupChatSearchResults = [];
            _groupChatContinuation = null;
            _groupChatSearchProgress = null;
            _hasSearchedGroupChats = false;
            _directoryResultIndex = 0;
            Status.Value = new ConfigStatusMessage("Press Enter to search this Group Chat name.", ConfigStatusTone.Neutral);
        }
    }
    internal IReadOnlyList<TeamsDirectoryGroupChat> FilteredGroupChatSearchResults => _groupChatSearchResults;
    internal int GroupChatSearchActionIndex => _groupChatSearchResults.Count;
    internal int GroupChatSearchAdvancedIndex => GroupChatSearchActionIndex + (_isGroupChatSearchRunning ? 2 : 1);
    internal bool IsGroupChatSearchRunning => _isGroupChatSearchRunning;
    internal string GroupChatSearchProgressText => _groupChatSearchProgress is { } progress
        ? $"{progress.UsersExamined} users; {progress.ChatsExamined} chats; {progress.RequestsMade} requests; {_groupChatSearchResults.Count} matches"
          + (progress.UnavailableUsers > 0 ? $"; {progress.UnavailableUsers} unavailable" : string.Empty)
        : string.Empty;
    internal bool HasGroupChatContinuation => !string.IsNullOrWhiteSpace(_groupChatContinuation);
    internal Task? PendingGroupChatSearch => _groupChatSearchTask;
    internal int TeamsPrincipalManagementIndex => _teamsPrincipalManagementIndex;
    internal int TeamsPrincipalFilterIndex => _teamsPrincipalFilterIndex;
    internal TeamsPrincipalRow? PendingPrincipalRemoval => _pendingPrincipalRemoval;
    internal int TeamsPrincipalRemovalIndex => _teamsPrincipalRemovalIndex;
    internal string TeamsPrincipalRemovalImpact
    {
        get
        {
            var pending = _pendingPrincipalRemoval;
            if (pending is null)
                return string.Empty;

            var users = GetAllowedUserIds(ChannelType.Teams)
                .Where(id => pending.Kind != TeamsPrincipalKind.User || !string.Equals(id, pending.Id, StringComparison.Ordinal));
            var groups = GetAllowedGroupIds(ChannelType.Teams)
                .Where(id => pending.Kind != TeamsPrincipalKind.Group || !string.Equals(id, pending.Id, StringComparison.Ordinal));
            if (users.Any() || groups.Any())
                return "Other global grants remain. Channel-specific grants can also authorize this person.";

            return "After activation, channels without exact grants accept any verified sender. Personal and Group Chat ingress deny until you add a global user or group.";
        }
    }

    internal TeamsChannelPrincipalRemoval? PendingChannelPrincipalRemoval => _pendingChannelPrincipalRemoval;
    internal int TeamsChannelPrincipalRemovalIndex => _teamsPrincipalRemovalIndex;
    internal string TeamsChannelPrincipalRemovalImpact
    {
        get
        {
            var pending = _pendingChannelPrincipalRemoval;
            var access = _editingChannelAccess;
            if (pending is null || access is null)
                return string.Empty;

            var hasOtherExactPrincipal = pending.Kind == TeamsPrincipalKind.User
                ? access.AllowedUserIds.Any(id => !string.Equals(id, pending.PrincipalId, StringComparison.Ordinal))
                  || access.AllowedGroupIds.Length > 0
                : access.AllowedUserIds.Length > 0
                  || access.AllowedGroupIds.Any(id => !string.Equals(id, pending.PrincipalId, StringComparison.Ordinal));
            if (hasOtherExactPrincipal)
                return "Other exact channel principals remain after this removal.";

            if (GetAllowedUserIds(ChannelType.Teams).Count > 0 || GetAllowedGroupIds(ChannelType.Teams).Count > 0)
                return "Global principal rules remain after this removal.";

            return "This is the final principal restriction. After activation, this channel accepts any verified Teams sender.";
        }
    }

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
        if (_activeAdapterType == ChannelType.Teams)
            StartChannelLabelResolution(ChannelType.Teams);
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
            _channelMentionRequired,
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

    // Single autosave wrapper for both the explicit save (probe on) and the completed-action autosaves
    // (probe off, via SaveCompletedAsync). ConfigAutosave.RunAsync catches any failure and surfaces it
    // as an Error status; this returns whether the save succeeded.
    private Task<bool> SaveViaAutosaveAsync(string successMessage, bool probeChannelAccess, CancellationToken ct)
        => ConfigAutosave.RunAsync(
            token => SaveAsync(successMessage, probeChannelAccess, token),
            Status,
            "Channel settings save failed",
            RequestRedraw,
            ct);

    internal Task<bool> SaveFromInputAsync(CancellationToken ct = default)
        => SaveViaAutosaveAsync("Channels saved.", probeChannelAccess: true, ct);

    // Input-triggered config writes (autosave, add-channel, reset) run async OFF the Termina loop so
    // they never freeze input/rendering on a probe or disk write. The sync .GetAwaiter().GetResult()
    // bridges they replaced were implicitly serialized by the single-threaded loop — exactly one ran
    // start-to-finish before the next input. Preserve that ordering explicitly by chaining each write
    // behind the previous one, so two rapid mutations can't race the disk write + state reload. In the
    // common case (no in-flight label refresh) the prior task is already complete, so the chain runs
    // synchronously inline and adds no overhead. Touched only on the loop thread (and the test thread,
    // also single-threaded), so the field assignment needs no synchronization. Exposed as
    // PendingConfigWrite so tests await completion deterministically instead of sleeping.
    private Task _pendingConfigWrite = Task.CompletedTask;

    internal Task PendingConfigWrite => _pendingConfigWrite;

    private Task EnqueueConfigWriteAsync(Func<Task> write)
    {
        var prior = _pendingConfigWrite;
        var next = ChainAsync();
        _pendingConfigWrite = next;
        return next;

        async Task ChainAsync()
        {
            // The prior write already surfaced its own failure status via ConfigAutosave; swallowing
            // here only prevents one failed write from cancelling the writes queued behind it.
            try { await prior; }
            catch (Exception priorFailure)
            {
                // Defensive: every write is exception-safe (autosave via ConfigAutosave, reset/add via
                // their own catches), so a faulted prior is not expected. Swallow it here only so one
                // failed write can't cancel the writes queued behind it; the prior already surfaced its
                // own status. Trace in case this path is ever hit.
                Debug.WriteLine($"ChannelsConfig: prior config write faulted (already surfaced): {priorFailure.Message}");
            }

            await write();
        }
    }

    // Dispatched fire-and-forget from the synchronous Termina key handler. The loop stays responsive
    // while the add resolves channels against the platform API; the write itself is serialized behind
    // any prior config write by EnqueueConfigWriteAsync.
    internal Task AddChannelFromInputAsync()
        => EnqueueConfigWriteAsync(() => ApplyAddChannelAsync(_lifetimeCts.Token));

    internal Task ResetConfirmationFromInputAsync()
        => EnqueueConfigWriteAsync(() => ApplyResetConfirmationAsync(_lifetimeCts.Token));

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
        ClearTeamsEditContext();
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

    // The inline, awaited label-resolution path used by ApplyAddChannelAsync (the add flow). It is part of
    // one linear awaited write that is serialized behind any other config write by EnqueueConfigWriteAsync;
    // the caller awaits it and then expects the canonical state settled, so the reconcile runs inline here
    // rather than deferring to a loop-thread drain. (The fire-and-forget BACKGROUND refresh launched by
    // StartChannelLabelResolution must NOT use this path — its continuation would reconcile off-loop,
    // concurrently with render and the edit handlers. That path publishes via RefreshChannelLabelsInBackground
    // and the loop thread drains it.)
    internal async Task RefreshChannelLabelsAsync(ChannelType type, CancellationToken ct = default)
    {
        var pending = await ProbeChannelLabelsAsync(type, ct);
        if (pending is null || ct.IsCancellationRequested)
            return;

        ApplyChannelResolution(pending);
        NotifyContentChanged();
    }

    // The fire-and-forget BACKGROUND refresh (StartChannelLabelResolution). It runs the pure async probe
    // off-loop, then marshals the reconcile back onto the Termina loop thread via InvokeAsync — it must NOT
    // reconcile, set LastChannelResolution, write config, or set IsSaved/Status off-loop, because render and
    // the edit handlers touch that same state on the loop thread (and audiences are security-relevant ACL
    // trust tiers). Tracked so the save/reset guard can cancel-and-await it.
    private async Task RefreshChannelLabelsInBackgroundAsync(ChannelType type, CancellationToken ct)
    {
        var pending = await ProbeChannelLabelsAsync(type, ct);
        if (pending is null || ct.IsCancellationRequested)
            return;

        // Marshal the reconcile onto the Termina render loop, but DO NOT await it. The tracked task must
        // complete as soon as the off-loop probe unwinds: CancelAndAwaitLabelRefreshAsync — which a save/reset
        // awaits at its top — would otherwise block on a loop turn, deferring the save's OWN disk write behind
        // this apply and dropping it on a fast quit. InvokeAsync queues the apply on the loop thread (Termina
        // installs no SynchronizationContext, so reconciling in this continuation would race render/input — the
        // #1426 race); the re-check makes a superseding refresh or a reset that has already cancelled this ct
        // skip the now-stale apply when the loop reaches it. Fire-and-forget is exception-safe here: the queued
        // work only completes-or-cancels (ApplyChannelResolution surfaces its own warnings on the loop — see
        // ReconcileResolvedChannels), so it never faults an unobserved task. In tests the unbound InvokeAsync
        // runs the apply inline — the post-frame state the loop reaches in production.
        _ = InvokeAsync(
            () =>
            {
                if (ct.IsCancellationRequested)
                    return;

                ApplyChannelResolution(pending);
                NotifyContentChanged();
            },
            ct);
    }

    // Shared probe step for both label-refresh paths: read the loop-thread channel snapshot synchronously
    // BEFORE the await, run the probe off-loop, and package the outcome as ONE immutable PendingChannelResolution
    // (or null when the adapter is disabled / has no channels / lacks credentials). Sets NO shared state.
    private async Task<PendingChannelResolution?> ProbeChannelLabelsAsync(ChannelType type, CancellationToken ct)
    {
        if (!Step.IsAdapterEnabled(type))
            return null;

        var channelIds = GetChannelIds(type);
        if (channelIds.Count == 0)
            return null;

        try
        {
            var probe = await ResolveChannelReferencesAsync(type, channelIds, ct);
            if (probe is null || ct.IsCancellationRequested)
                return null;

            return new PendingChannelResolution(
                type, channelIds, probe.Value.Result, probe.Value.Error, probe.Value.Resolved.ToArray());
        }
        catch (Exception ex)
        {
            // A superseded background refresh (a newer resolution started, or the user navigated away)
            // cancels via ct — not a lookup failure, so abandon it quietly. Any other failure becomes a
            // warning the loop thread surfaces (Status fan-out invalidates Termina nodes — never set off-loop).
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
                return null;

            return PendingChannelResolution.Failed(
                type, $"{GetAdapterDisplayName(type)} channel label lookup failed: {ex.Message}");
        }
    }

    // Applies one resolution outcome to shared view-model state. MUST run on the loop thread (the inline add
    // path awaits it; the background path marshals it there via InvokeAsync). Sets the adapter's
    // LastChannelResolution snapshot, then reconciles the stored allow-list to canonical ids — or surfaces a
    // lookup-failure warning.
    private void ApplyChannelResolution(PendingChannelResolution pending)
    {
        if (pending.LookupErrorStatus is { } lookupError)
        {
            Status.Value = new ConfigStatusMessage(lookupError, ConfigStatusTone.Warning);
            return;
        }

        SetLastChannelResolution(pending.Type, pending.Result);
        ReconcileResolvedChannels(pending.Type, pending.Stored, pending.Error, pending.Resolved);
    }

    // Carries one off-loop label-resolution outcome to the loop thread. Immutable: the only fields are the
    // probed type, the references that were probed, the raw resolution result (for LastChannelResolution),
    // and the (error, resolved-pair) reconcile inputs. A LookupErrorStatus instead means the probe threw.
    private sealed record PendingChannelResolution(
        ChannelType Type,
        IReadOnlyList<string> Stored,
        object? Result,
        string? Error,
        IReadOnlyList<(string Id, string Name)> Resolved,
        string? LookupErrorStatus = null)
    {
        internal static PendingChannelResolution Failed(ChannelType type, string status)
            => new(type, [], null, null, [], status);
    }

    // Applies the raw probe result onto the active adapter's LastChannelResolution (loop-thread only).
    // The probe returns the platform-specific result object; route it to the matching adapter VM.
    private void SetLastChannelResolution(ChannelType type, object? result)
    {
        switch (type)
        {
            case ChannelType.Slack when result is SlackChannelResolutionResult slackResult:
                Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack).LastChannelResolution = slackResult;
                break;
            case ChannelType.Discord when result is DiscordChannelResolutionResult discordResult:
                Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord).LastChannelResolution = discordResult;
                break;
            case ChannelType.Mattermost when result is MattermostChannelResolutionResult mattermostResult:
                Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost).LastChannelResolution = mattermostResult;
                break;
        }
    }

    internal IReadOnlyList<ChannelsManagementMenuItem> GetManagementMenuItems()
    {
        var enabled = Step.IsAdapterEnabled(_activeAdapterType);
        if (_activeAdapterType == ChannelType.Teams)
        {
            return
            [
                new ChannelsManagementMenuItem(ChannelsManagementAction.ManageChannels, "Manage channels and permissions", "Edit channels, Group Chats, audiences, and ingress."),
                new ChannelsManagementMenuItem(ChannelsManagementAction.AddChannel, "Add a channel or Group Chat", "Search and review a Teams destination."),
                new ChannelsManagementMenuItem(ChannelsManagementAction.ManageGroupChats, $"Group Chat ingress: {(IsGroupChatIngressEnabled ? "ON" : "OFF")}", "Enable or disable saved Group Chats."),
                new ChannelsManagementMenuItem(ChannelsManagementAction.AddPrincipals, "Add users or groups", "Search a friendly identity and save its Entra ID."),
                new ChannelsManagementMenuItem(ChannelsManagementAction.ManagePrincipals, "Manage users and groups", "Review and remove global Teams principals."),
                new ChannelsManagementMenuItem(ChannelsManagementAction.ManageAttachments, "Attachments", "Enable supported inbound Teams attachments."),
                new ChannelsManagementMenuItem(ChannelsManagementAction.DirectoryStatus, "Directory / Graph status", "Review safe directory capability and consent guidance."),
                new ChannelsManagementMenuItem(ChannelsManagementAction.DirectMessages, "Direct messages", "Enable or disable DM ingress and audience."),
                CreateCredentialsMenuItem(),
                new ChannelsManagementMenuItem(ChannelsManagementAction.ToggleEnabled, enabled ? "Disable Microsoft Teams" : "Enable Microsoft Teams", "Preserve saved setup while changing runtime state."),
                new ChannelsManagementMenuItem(ChannelsManagementAction.ResetConnection, "Reset Microsoft Teams connection", "Remove saved config and credentials."),
                new ChannelsManagementMenuItem(ChannelsManagementAction.Done, "Done", "Return to Channels.")
            ];
        }

        List<ChannelsManagementMenuItem> items =
        [
            new ChannelsManagementMenuItem(ChannelsManagementAction.ManageChannels, "Manage channels and permissions", "Edit allowed channels and audience levels."),
            new ChannelsManagementMenuItem(ChannelsManagementAction.AddChannel, $"Add a {ActiveAdapterName} channel", "Add channel ingress without touching credentials."),
            new ChannelsManagementMenuItem(ChannelsManagementAction.ManageUsers, "Manage allowed users", "Restrict messages to specific user IDs."),
        ];

        items.AddRange(
        [
            new ChannelsManagementMenuItem(ChannelsManagementAction.DirectMessages, "Direct messages", "Enable or disable DM ingress and audience."),
            CreateCredentialsMenuItem(),
            new ChannelsManagementMenuItem(ChannelsManagementAction.ToggleEnabled, enabled ? $"Disable {ActiveAdapterName}" : $"Enable {ActiveAdapterName}", "Preserve saved setup while changing runtime state."),
            new ChannelsManagementMenuItem(ChannelsManagementAction.ResetConnection, $"Reset {ActiveAdapterName} connection", "Remove saved config and credentials."),
            new ChannelsManagementMenuItem(ChannelsManagementAction.Done, "Done", "Return to Channels.")
        ]);
        return items;
    }

    internal void MoveManagementMenu(int delta)
    {
        _managementMenuIndex = Clamp(_managementMenuIndex + delta, GetManagementMenuItems().Count);
        NotifyContentChanged();
    }

    private ChannelsManagementMenuItem CreateCredentialsMenuItem()
    {
        if (_activeAdapterType != ChannelType.Teams)
            return new ChannelsManagementMenuItem(ChannelsManagementAction.RotateCredentials, "Rotate credentials", "Replace tokens only when explicitly entered.");

        return HasCompleteTeamsConnection()
            ? new ChannelsManagementMenuItem(ChannelsManagementAction.RotateCredentials, "Connection & credentials", "Review connection IDs or replace the client secret.")
            : new ChannelsManagementMenuItem(ChannelsManagementAction.RotateCredentials, "Configure Teams connection", "Set tenant, application, bot ID and client secret.");
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
            case ChannelsManagementAction.AddPrincipals:
                BeginTeamsPrincipalAdd();
                break;
            case ChannelsManagementAction.ManagePrincipals:
                BeginTeamsPrincipalManagement();
                break;
            case ChannelsManagementAction.ManageGroups:
                BeginAllowedGroups();
                break;
            case ChannelsManagementAction.ManageGroupChats:
                BeginGroupChats();
                break;
            case ChannelsManagementAction.ManageAttachments:
                BeginAttachments();
                break;
            case ChannelsManagementAction.DirectoryStatus:
                Screen.Value = ChannelsConfigScreen.DirectoryStatus;
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
            case ChannelsManagementAction.Done:
                // Discoverable equivalent of Esc ("[Esc] Channels") — return to the adapter picker.
                Screen.Value = ChannelsConfigScreen.Picker;
                break;
        }

        NotifyContentChanged();
    }

    internal string GetActiveAdapterSummary()
    {
        var channelCount = GetChannelIds(_activeAdapterType).Count;
        var userCount = GetAllowedUserIds(_activeAdapterType).Count;
        var groupCount = GetAllowedGroupIds(_activeAdapterType).Count;
        var groupChatCount = _activeAdapterType == ChannelType.Teams
            ? ChannelCsv.ParseCsv(Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).AllowedGroupChatIdsInput, trimHash: false).Count
            : 0;
        var credentials = GetCredentialSummary(_activeAdapterType);
        var dm = GetAllowDirectMessages(_activeAdapterType) ? "enabled" : "disabled";
        var enabled = Step.IsAdapterEnabled(_activeAdapterType) ? "enabled" : "disabled";
        var groups = _activeAdapterType == ChannelType.Teams
            ? $" · {Pluralize(groupCount, "group", "groups")} · {Pluralize(groupChatCount, "Group Chat", "Group Chats")}" : string.Empty;
        return $"{enabled} · {credentials} · {Pluralize(channelCount, "channel", "channels")} · {Pluralize(userCount, "user", "users")}{groups} · DMs {dm}";
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
                IsUnresolved: unresolved.Contains(channelId),
                MentionRequired: GetChannelMentionRequired(_activeAdapterType, channelId)));
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

        if (_activeAdapterType == ChannelType.Teams)
        {
            var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
            foreach (var chatId in ChannelCsv.ParseCsv(teams.AllowedGroupChatIdsInput, trimHash: false))
            {
                rows.Add(new ChannelPermissionRow(
                    chatId,
                    $"Group chat · {FormatTeamsGroupChatLabel(chatId)}",
                    TrustAudience.Team,
                    IsDirectMessage: false,
                    IsAddAction: false,
                    IsDoneAction: false,
                    IsGroupChat: true));
            }
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

    internal void ActivateSelectedChannelRow()
    {
        var rows = GetChannelRows();
        if (rows.Count == 0)
            return;

        var row = rows[_channelRowIndex];
        if (row.IsAddAction)
            BeginAddChannel();
        else if (row.IsDoneAction)
            FinishChannelPermissions();
        else if (row.IsGroupChat)
            BeginGroupChatDetails(row.Id);
        else if (_activeAdapterType == ChannelType.Teams && !row.IsDirectMessage)
            BeginTeamsChannelAccess(row.Id);

        // A channel or DM row has no detail page: audience is set inline with
        // left/right and the thread mention rule with Space.
    }

    internal void ToggleSelectedChannelMentionRequired()
    {
        var rows = GetChannelRows();
        if (rows.Count == 0)
            return;

        var row = rows[_channelRowIndex];
        // The thread mention rule applies only to real channels. A DM is
        // one-to-one, and the add/done rows are actions.
        if (row.IsAction || row.IsDirectMessage || row.IsGroupChat)
            return;

        if (_activeAdapterType == ChannelType.Teams)
        {
            var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
            var nextMentionOnly = !teams.MentionOnly;
            teams.MentionOnly = nextMentionOnly;
            AutosaveCompletedAction($"Microsoft Teams require @mentions in all selected channels {(nextMentionOnly ? "on" : "off")} and saved.");
            return;
        }

        var next = !GetChannelMentionRequired(_activeAdapterType, row.Id);
        SetChannelMentionRequired(_activeAdapterType, row.Id, next);
        // Autosave like the audience toggle: without an immediate save an Esc
        // would silently discard it (the next load resets from disk).
        AutosaveCompletedAction($"{row.DisplayName} require @mention {(next ? "on" : "off")} and saved.");
    }

    internal void ChangeSelectedChannelAudience(int delta)
    {
        var rows = GetChannelRows();
        if (rows.Count == 0)
            return;

        var row = rows[_channelRowIndex];
        if (row.IsAction || row.IsGroupChat)
            return;

        var currentIndex = AudienceIndex(row.Audience);
        var next = AudienceOptions[Wrap(currentIndex + delta, AudienceOptions.Count)];
        if (_activeAdapterType == ChannelType.Teams
            && !TrySetTeamsChannelAudience(row.Id, next, teamId: null))
        {
            Status.Value = new ConfigStatusMessage(
                "This Teams channel needs one exact Team ID before its audience can change. Use the Team search path or configure a structured override.",
                ConfigStatusTone.Error);
            NotifyContentChanged();
            return;
        }

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

        if (row.IsGroupChat)
        {
            var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
            teams.AllowedGroupChatIdsInput = ChannelCsv.JoinOrNull(
                ChannelCsv.ParseCsv(teams.AllowedGroupChatIdsInput, trimHash: false)
                    .Where(id => !string.Equals(id, row.Id, StringComparison.Ordinal))
                    .ToArray());
            UpdateAdapterPickerSummary(ChannelType.Teams);
            _channelRowIndex = Clamp(_channelRowIndex, GetChannelRows().Count);
            AutosaveCompletedAction($"Removed Group Chat {AbbreviateIdentifier(row.Id)} and saved.");
            NotifyContentChanged();
            return;
        }

        var remaining = GetChannelIds(_activeAdapterType)
            .Where(id => !string.Equals(id, row.Id, StringComparison.Ordinal))
            .ToArray();
        SetChannelIds(_activeAdapterType, remaining);
        if (_channelAudiences.TryGetValue(_activeAdapterType, out var audiences))
            audiences.Remove(row.Id);
        if (_channelMentionRequired.TryGetValue(_activeAdapterType, out var mentionRequired))
            mentionRequired.Remove(row.Id);
        if (_activeAdapterType == ChannelType.Teams)
        {
            var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
            teams.ChannelAudienceOverrides =
            [
                .. teams.ChannelAudienceOverrides.Where(audienceOverride =>
                    !string.Equals(audienceOverride.ChannelId, row.Id, StringComparison.Ordinal))
            ];
            teams.ChannelAccessOverrides =
            [
                .. teams.ChannelAccessOverrides.Where(accessOverride =>
                    !string.Equals(accessOverride.ChannelId, row.Id, StringComparison.Ordinal))
            ];
        }

        UpdateAdapterPickerSummary(_activeAdapterType);
        _channelRowIndex = Clamp(_channelRowIndex, GetChannelRows().Count);
        AutosaveCompletedAction($"Removed {row.DisplayName} and saved.");
        NotifyContentChanged();
    }

    internal void BeginTeamsDestinationRemoval()
    {
        var rows = GetChannelRows();
        if (_activeAdapterType != ChannelType.Teams || rows.Count == 0)
            return;

        var row = rows[_channelRowIndex];
        if (row.IsAction || row.IsDirectMessage)
            return;

        _pendingTeamsDestinationRemoval = row;
        _teamsDestinationRemovalIndex = 0;
        Screen.Value = ChannelsConfigScreen.TeamsDestinationRemovalConfirm;
        Status.Value = new ConfigStatusMessage(
            "Confirm removal. The configured destination is denied after configuration activation.",
            ConfigStatusTone.Warning);
        NotifyContentChanged();
    }

    internal ChannelPermissionRow? PendingTeamsDestinationRemoval => _pendingTeamsDestinationRemoval;
    internal int TeamsDestinationRemovalIndex => _teamsDestinationRemovalIndex;

    internal void MoveTeamsDestinationRemoval(int delta)
    {
        _teamsDestinationRemovalIndex = Clamp(_teamsDestinationRemovalIndex + delta, 2);
        NotifyContentChanged();
    }

    internal void ConfirmTeamsDestinationRemoval(bool remove)
    {
        var pending = _pendingTeamsDestinationRemoval;
        _pendingTeamsDestinationRemoval = null;
        Screen.Value = ChannelsConfigScreen.ChannelPermissions;
        if (remove && pending is not null)
            RemoveSelectedChannel();
        else
            NotifyContentChanged();
    }

    internal void BeginAddChannel()
    {
        if (_activeAdapterType == ChannelType.Teams)
        {
            _teamsDestinationAddIndex = 0;
            Screen.Value = ChannelsConfigScreen.TeamsDestinationAdd;
            Status.Value = new ConfigStatusMessage("Select a Teams channel or Group Chat.", ConfigStatusTone.Neutral);
            NotifyContentChanged();
            return;
        }

        AddChannelInput = null;
        _audienceSelectionIndex = AudienceIndex(DefaultChannelAudience());
        Screen.Value = ChannelsConfigScreen.AddChannel;
        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void MoveTeamsDestinationAdd(int delta)
    {
        _teamsDestinationAddIndex = Clamp(_teamsDestinationAddIndex + delta, 2);
        NotifyContentChanged();
    }

    internal void ActivateTeamsDestinationAdd()
    {
        if (_teamsDestinationAddIndex == 0)
            BeginTeamsTeamSearch();
        else
            BeginGroupChatDiscovery();
    }

    internal void BeginGroupChatDiscovery()
    {
        ClearTeamsEditContext();
        _teamsDirectorySearch?.Invalidate();
        _isGroupChatDiscovery = true;
        _groupChatSearchResults = [];
        _groupChatContinuation = null;
        _groupChatSearchInput = null;
        _directoryResultIndex = 0;
        Screen.Value = ChannelsConfigScreen.TeamsGroupChatSearch;
        Status.Value = new ConfigStatusMessage("Enter all or part of the Group Chat name, then press Enter to search.", ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void BeginTeamsTeamSearch()
    {
        _teamsDirectorySearch?.Invalidate();
        DirectorySearchInput = null;
        _teamSearchResults = [];
        _channelSearchResults = [];
        _selectedTeam = null;
        _directoryResultIndex = 0;
        Screen.Value = ChannelsConfigScreen.TeamsTeamSearch;
        Status.Value = new ConfigStatusMessage("Search Teams by name. Use the advanced entry action when directory search is unavailable.", ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void BeginManualTeamsChannelEntry()
    {
        AddChannelInput = null;
        _audienceSelectionIndex = AudienceIndex(DefaultChannelAudience());
        Screen.Value = ChannelsConfigScreen.AddChannel;
        Status.Value = new ConfigStatusMessage("Advanced path: enter canonical Teams channel IDs. Existing IDs remain valid without directory access.", ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void ResetTeamsTeamSearchResults()
    {
        _teamsDirectorySearch?.Invalidate();
        _teamSearchResults = [];
        _directoryResultIndex = 0;
    }

    internal Task SearchTeamsFromInputAsync()
        => SearchTeamsAsync(_lifetimeCts.Token);

    private async Task SearchTeamsAsync(CancellationToken cancellationToken)
    {
        var search = TryGetTeamsDirectorySearch();
        if (search is null)
            return;

        Status.Value = new ConfigStatusMessage("Searching Microsoft Teams...", ConfigStatusTone.Neutral);
        NotifyContentChanged();
        var query = DirectorySearchInput ?? string.Empty;
        var response = await search.SearchTeamsAsync(query, cancellationToken).ConfigureAwait(false);
        if (!response.IsCurrent || cancellationToken.IsCancellationRequested)
            return;

        _ = InvokeAsync(() => ApplyTeamSearchResponse(query, response), cancellationToken);
    }

    internal Task SelectTeamAndSearchChannelsAsync()
    {
        if (_teamSearchResults.Count == 0)
            return Task.CompletedTask;

        _selectedTeam = _teamSearchResults[_directoryResultIndex];
        _channelSearchResults = [];
        _directoryResultIndex = 0;
        Screen.Value = ChannelsConfigScreen.TeamsChannelSearch;
        NotifyContentChanged();
        return SearchChannelsForSelectedTeamAsync(_lifetimeCts.Token);
    }

    private async Task SearchChannelsForSelectedTeamAsync(CancellationToken cancellationToken)
    {
        var team = _selectedTeam;
        var search = TryGetTeamsDirectorySearch();
        if (team is null || search is null)
            return;

        Status.Value = new ConfigStatusMessage("Loading Team channels...", ConfigStatusTone.Neutral);
        NotifyContentChanged();
        var response = await search.GetChannelsAsync(team.Id, cancellationToken).ConfigureAwait(false);
        if (!response.IsCurrent || cancellationToken.IsCancellationRequested)
            return;

        _ = InvokeAsync(() => ApplyChannelSearchResponse(team.Id, response), cancellationToken);
    }

    internal void MoveDirectoryResult(int delta)
    {
        var count = Screen.Value switch
        {
            ChannelsConfigScreen.TeamsTeamSearch => _teamSearchResults.Count + 1,
            ChannelsConfigScreen.TeamsChannelSearch => _channelSearchResults.Count + 1,
            ChannelsConfigScreen.TeamsUserSearch => _userSearchResults.Count + 1,
            ChannelsConfigScreen.TeamsGroupSearch => _groupSearchResults.Count + 1,
            ChannelsConfigScreen.TeamsGroupChatSearch => GetGroupChatSearchResultCount(),
            _ => 0
        };
        _directoryResultIndex = Clamp(_directoryResultIndex + delta, count);
        NotifyContentChanged();
    }

    internal void SaveSelectedTeamsChannel()
    {
        if (IsAdvancedTeamsDirectoryActionSelected())
        {
            BeginManualTeamsChannelEntry();
            return;
        }

        if (_selectedTeam is null || _channelSearchResults.Count == 0)
            return;

        var channel = _channelSearchResults[_directoryResultIndex];
        AddDiscoveredTeamsChannel(_selectedTeam, channel);
    }

    internal void AddDiscoveredTeamsChannel(TeamsDirectoryTeam team, TeamsDirectoryChannel channel)
    {
        ArgumentNullException.ThrowIfNull(team);
        ArgumentNullException.ThrowIfNull(channel);
        if (!string.Equals(team.Id, channel.TeamId, StringComparison.Ordinal))
        {
            Status.Value = new ConfigStatusMessage("The selected Teams channel does not belong to the selected Team.", ConfigStatusTone.Error);
            NotifyContentChanged();
            return;
        }

        var currentTeams = GetTeamIds();
        var currentChannels = GetChannelIds(ChannelType.Teams);
        if (currentChannels.Contains(channel.Id, StringComparer.Ordinal))
        {
            Status.Value = new ConfigStatusMessage("That Teams channel is already configured.", ConfigStatusTone.Neutral);
            NotifyContentChanged();
            return;
        }

        SetTeamIds([.. currentTeams, team.Id]);
        SetChannelIds(ChannelType.Teams, [.. currentChannels, channel.Id]);
        var audience = DefaultChannelAudience();
        SetChannelAudience(ChannelType.Teams, channel.Id, audience);
        TrySetTeamsChannelAudience(channel.Id, audience, team.Id);
        _teamsById[team.Id] = team;
        _teamsChannelsByIdentity[TeamsChannelIdentity(team.Id, channel.Id)] = channel;
        _channelRowIndex = Math.Max(GetChannelRows().Count - 3, 0);
        Screen.Value = ChannelsConfigScreen.ChannelPermissions;
        UpdateAdapterPickerSummary(ChannelType.Teams);
        AutosaveCompletedAction($"Added {FormatTeamsChannelLabel(team, channel)} and saved.");
        NotifyContentChanged();
    }

    private TeamsDirectorySearchController? TryGetTeamsDirectorySearch()
    {
        if (_teamsDirectorySearch is not null)
            return _teamsDirectorySearch;

        var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
        var tenantId = GetEffectiveTeamsSetting("TenantId", teams.TenantId);
        var clientId = GetEffectiveTeamsSetting("ClientId", teams.ClientId);
        var secret = GetEffectiveTeamsClientSecret(teams);
        if (string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(secret))
        {
            Status.Value = new ConfigStatusMessage("Graph credentials are incomplete. Use the advanced canonical-ID path or configure Teams credentials.", ConfigStatusTone.Error);
            NotifyContentChanged();
            return null;
        }

        var options = new TeamsChannelOptions
        {
            TenantId = tenantId,
            ClientId = clientId,
            ClientSecret = new SensitiveString(secret)
        };
        var directory = _teamsDirectoryFactory?.Invoke(options)
                        ?? TeamsGraphDirectoryClient.Create(options, _timeProvider);
        _teamsDirectoryLifetime = directory as IDisposable;
        _teamsDirectory = directory;
        _teamsDirectorySearch = new TeamsDirectorySearchController(directory, _timeProvider);
        return _teamsDirectorySearch;
    }

    private void InvalidateTeamsDirectory()
    {
        _teamsDirectorySearch?.Dispose();
        _teamsDirectoryLifetime?.Dispose();
        _teamsDirectorySearch = null;
        _teamsDirectoryLifetime = null;
        _teamsDirectory = null;
        _teamsDirectoryLabelsAvailable = null;
        _teamsById.Clear();
        _teamsChannelsByIdentity.Clear();
        _teamsChannelTeamIds.Clear();
    }

    private static string DirectoryFailureMessage(string? reasonCode) => reasonCode switch
    {
        "teams_directory_authentication_failed" => "Microsoft Graph authentication failed. Check the tenant ID, application ID, and client secret.",
        "teams_directory_permission_denied" => "Microsoft Graph permission was denied. Grant the required application permissions and tenant admin consent.",
        "teams_directory_timeout" => "Microsoft Graph timed out. Check network access and try again.",
        "teams_directory_network_unavailable" => "Microsoft Graph is unavailable. Check network access and try again.",
        "teams_directory_request_failed" => "Microsoft Graph could not complete the request. Existing IDs remain unchanged.",
        "teams_directory_query_too_short" => "Enter at least two characters to search the directory.",
        "teams_directory_invalid_continuation" => "This search has expired. Select Search again to restart.",
        "teams_directory_search_limit_reached" => "This search reached its result limit. Enter a more specific Group Chat name.",
        "teams_directory_pagination_stalled" => "Microsoft Graph repeated a page. The search stopped. Retry the search or use a canonical chat ID.",
        "teams_directory_throttled" => "Microsoft Graph is busy. Retry this search after a short delay.",
        _ => "Microsoft Graph is unavailable. Existing IDs remain unchanged. Use the advanced canonical-ID path if needed."
    };

    /// <summary>
    /// Appends the typed channel reference(s) to the active adapter, then canonicalizes them through the
    /// SAME path the first-connect flow uses (<see cref="ReconcileResolvedChannels"/>): an id-shaped
    /// reference is kept as the stable ACL key (even when the bot can't enumerate it — e.g. a Discord
    /// channel id), a resolvable display name becomes its id, and anything that maps to no channel id is
    /// dropped and flagged. Accepts a comma-separated list via the one shared <c>ChannelCsv</c> parser,
    /// same as onboarding. Audiences are tuned afterward with ←/→ on the channel list.
    /// </summary>
    internal async Task ApplyAddChannelAsync(CancellationToken ct = default)
    {
        var references = ChannelCsv.ParseCsv(AddChannelInput, trimHash: true);
        if (references.Count == 0)
        {
            Status.Value = new ConfigStatusMessage("At least one channel is required.", ConfigStatusTone.Error);
            NotifyContentChanged();
            return;
        }

        var existing = GetChannelIds(_activeAdapterType);
        var existingSet = new HashSet<string>(existing, StringComparer.Ordinal);
        var fresh = references.Where(reference => existingSet.Add(reference)).ToList();
        if (fresh.Count == 0)
        {
            Status.Value = new ConfigStatusMessage("Those channels are already configured.", ConfigStatusTone.Neutral);
            NotifyContentChanged();
            return;
        }

        if (_activeAdapterType == ChannelType.Teams && GetTeamIds().Count != 1)
        {
            Status.Value = new ConfigStatusMessage(
                "Advanced Teams channel entry needs exactly one configured Team ID. Use Team search to save an exact Team/channel binding.",
                ConfigStatusTone.Error);
            NotifyContentChanged();
            return;
        }

        // Append the typed references with the default audience and move to the permissions list, exactly
        // like completing the first-connect sub-flow. Settle ALL on-loop UI state — the screen and the
        // focus on the newly added row — synchronously, BEFORE the async persist. This runs fire-and-forget
        // from the key handler, so a subsequent keypress must navigate from a deterministic position; the
        // async tail below only updates status + labels (via RequestRedraw), never navigation or focus. The
        // row index is computed from the in-memory append, which the save's reload preserves.
        SetChannelIds(_activeAdapterType, [.. existing, .. fresh]);
        foreach (var reference in fresh)
        {
            var defaultAudience = DefaultChannelAudience();
            SetChannelAudience(_activeAdapterType, reference, defaultAudience);
            // Broad audiences (Team/Public) default to requiring an explicit mention in a thread;
            // Personal channels do not. Seeded here so a channel added at a broad tier starts guarded.
            SetChannelMentionRequired(
                _activeAdapterType,
                reference,
                defaultAudience is TrustAudience.Team or TrustAudience.Public);
        }
        Screen.Value = ChannelsConfigScreen.ChannelPermissions;

        var lastRow = GetChannelRows()
            .Select((row, index) => (row, index))
            .LastOrDefault(entry => !entry.row.IsDirectMessage && !entry.row.IsAction);
        if (lastRow.row is not null)
            _channelRowIndex = lastRow.index;
        NotifyContentChanged();

        // Persist the appended list, then canonicalize through the shared reconcile — the front door. It
        // resolves display names to ids, keeps id-shaped references the bot can't enumerate, drops the
        // unmappable ones, and sets the final status. There is no bespoke single-channel resolver.
        // Already inside an enqueued config write (AddChannelFromInputAsync), so persist via the
        // awaitable autosave directly rather than re-enqueueing — re-enqueue would deadlock the add
        // behind itself. The bool gates the follow-up label refresh.
        if (await SaveCompletedAsync(fresh.Count == 1
                ? $"Added {fresh[0]} at the {DefaultChannelAudience()} default and saved."
                : $"Added {Pluralize(fresh.Count, "channel", "channels")} and saved.", ct))
            await RefreshChannelLabelsAsync(_activeAdapterType, ct);
    }

    internal void FinishChannelPermissions()
    {
        Screen.Value = ChannelsConfigScreen.AdapterMenu;
        Status.Value = new ConfigStatusMessage("Done adding channels. Completed changes are already saved.", ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void BeginAllowedUsers()
    {
        if (_activeAdapterType == ChannelType.Teams)
        {
            BeginTeamsUserSearch();
            return;
        }

        AllowedUsersInput = ChannelCsv.JoinOrNull(GetAllowedUserIds(_activeAdapterType));
        Screen.Value = ChannelsConfigScreen.AllowedUsers;
        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void ApplyAllowedUsers()
    {
        var userIds = ChannelCsv.ParseCsv(AllowedUsersInput, trimHash: false);
        if (_activeAdapterType == ChannelType.Teams
            && !TryNormalizeEntraObjectIds(userIds, out userIds))
        {
            Status.Value = new ConfigStatusMessage("Each Teams user ID must be a canonical Entra object ID.", ConfigStatusTone.Error);
            NotifyContentChanged();
            return;
        }

        if (_editingChannelAccess is not null)
        {
            ReplaceEditingChannelAccess(new TeamsChannelAccessOverride
            {
                TeamId = _editingChannelAccess.TeamId,
                ChannelId = _editingChannelAccess.ChannelId,
                AllowedUserIds = [.. userIds],
                AllowedGroupIds = _editingChannelAccess.AllowedGroupIds
            });
            Screen.Value = ChannelsConfigScreen.TeamsChannelAccess;
            AutosaveCompletedAction("Channel-specific allowed users saved.");
            NotifyContentChanged();
            return;
        }

        SetAllowedUserIds(_activeAdapterType, userIds);
        UpdateAdapterPickerSummary(_activeAdapterType);
        _teamsPrincipalSearchReturnScreen = null;
        Screen.Value = ChannelsConfigScreen.AdapterMenu;
        AutosaveCompletedAction("Allowed users saved.");
        NotifyContentChanged();
    }

    internal void BeginAllowedGroups()
    {
        if (_activeAdapterType == ChannelType.Teams)
        {
            BeginTeamsGroupSearch();
            return;
        }

        AllowedGroupsInput = ChannelCsv.JoinOrNull(GetAllowedGroupIds(_activeAdapterType));
        Screen.Value = ChannelsConfigScreen.AllowedGroups;
        NotifyContentChanged();
    }

    internal void BeginTeamsPrincipalAdd()
    {
        ClearTeamsEditContext();
        _teamsPrincipalManagementIndex = 0;
        Screen.Value = ChannelsConfigScreen.TeamsPrincipalAdd;
        Status.Value = new ConfigStatusMessage("Select a user or group to add to the global Teams access list.", ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void MoveTeamsPrincipalAdd(int delta)
    {
        _teamsPrincipalManagementIndex = Clamp(_teamsPrincipalManagementIndex + delta, 2);
        NotifyContentChanged();
    }

    internal void ActivateTeamsPrincipalAdd()
    {
        _teamsPrincipalSearchReturnScreen = ChannelsConfigScreen.TeamsPrincipalAdd;
        if (_teamsPrincipalManagementIndex == 0)
            BeginTeamsUserSearch();
        else
            BeginTeamsGroupSearch();
    }

    internal IReadOnlyList<TeamsPrincipalRow> GetTeamsPrincipalRows()
    {
        var rows = new List<TeamsPrincipalRow>();
        if (_teamsPrincipalFilterIndex is 0 or 1)
        {
            rows.AddRange(GetAllowedUserIds(ChannelType.Teams).Select(id => new TeamsPrincipalRow(
                id,
                TeamsPrincipalKind.User,
                $"User · {FormatTeamsUserLabel(id)}",
                "Global Teams access")));
        }

        if (_teamsPrincipalFilterIndex is 0 or 2)
        {
            rows.AddRange(GetAllowedGroupIds(ChannelType.Teams).Select(id => new TeamsPrincipalRow(
                id,
                TeamsPrincipalKind.Group,
                $"Group · {FormatTeamsGroupLabel(id)}",
                "Global Teams access")));
        }

        return rows;
    }

    internal string TeamsPrincipalFilterLabel => _teamsPrincipalFilterIndex switch
    {
        1 => "Users",
        2 => "Groups",
        _ => "All"
    };

    internal void MoveTeamsPrincipalManagement(int delta)
    {
        var count = GetTeamsPrincipalRows().Count + 2;
        _teamsPrincipalManagementIndex = Clamp(_teamsPrincipalManagementIndex + delta, count);
        NotifyContentChanged();
    }

    internal void ChangeTeamsPrincipalFilter(int delta)
    {
        _teamsPrincipalFilterIndex = Wrap(_teamsPrincipalFilterIndex + delta, 3);
        _teamsPrincipalManagementIndex = 0;
        NotifyContentChanged();
    }

    internal void BeginTeamsPrincipalManagement()
    {
        ClearTeamsEditContext();
        _teamsPrincipalManagementIndex = 0;
        _teamsPrincipalFilterIndex = 0;
        Screen.Value = ChannelsConfigScreen.TeamsPrincipalManagement;
        Status.Value = new ConfigStatusMessage("Saved identities remain removable when directory labels are unavailable.", ConfigStatusTone.Neutral);
        StartChannelLabelResolution(ChannelType.Teams);
        NotifyContentChanged();
    }

    internal void ActivateTeamsPrincipalManagement()
    {
        var rows = GetTeamsPrincipalRows();
        if (_teamsPrincipalManagementIndex == rows.Count)
        {
            BeginTeamsPrincipalAdd();
            return;
        }

        if (_teamsPrincipalManagementIndex == rows.Count + 1)
        {
            Screen.Value = ChannelsConfigScreen.AdapterMenu;
            NotifyContentChanged();
            return;
        }

        if (_teamsPrincipalManagementIndex >= rows.Count)
            return;

        _pendingPrincipalRemoval = rows[_teamsPrincipalManagementIndex];
        _teamsPrincipalRemovalIndex = 0;
        Screen.Value = ChannelsConfigScreen.TeamsPrincipalRemovalConfirm;
        Status.Value = new ConfigStatusMessage("Confirm removal. Other global or channel-specific grants can still authorize this person.", ConfigStatusTone.Warning);
        NotifyContentChanged();
    }

    internal void ConfirmTeamsPrincipalRemoval(bool remove)
    {
        var pending = _pendingPrincipalRemoval;
        _pendingPrincipalRemoval = null;
        if (!remove || pending is null)
        {
            Screen.Value = ChannelsConfigScreen.TeamsPrincipalManagement;
            NotifyContentChanged();
            return;
        }

        if (pending.Kind == TeamsPrincipalKind.User)
            SetAllowedUserIds(ChannelType.Teams, GetAllowedUserIds(ChannelType.Teams).Where(id => !string.Equals(id, pending.Id, StringComparison.Ordinal)).ToArray());
        else
            SetAllowedGroupIds(ChannelType.Teams, GetAllowedGroupIds(ChannelType.Teams).Where(id => !string.Equals(id, pending.Id, StringComparison.Ordinal)).ToArray());

        UpdateAdapterPickerSummary(ChannelType.Teams);
        Screen.Value = ChannelsConfigScreen.TeamsPrincipalManagement;
        AutosaveCompletedAction($"Removed {pending.Kind.ToString().ToLowerInvariant()} {AbbreviateIdentifier(pending.Id)} from global Teams access and saved.");
        NotifyContentChanged();
    }

    internal void MoveTeamsPrincipalRemoval(int delta)
    {
        _teamsPrincipalRemovalIndex = Clamp(_teamsPrincipalRemovalIndex + delta, 2);
        NotifyContentChanged();
    }

    internal void ApplyAllowedGroups()
    {
        var groupIds = ChannelCsv.ParseCsv(AllowedGroupsInput, trimHash: false);
        if (_activeAdapterType == ChannelType.Teams
            && !TryNormalizeEntraObjectIds(groupIds, out groupIds))
        {
            Status.Value = new ConfigStatusMessage("Each Teams group ID must be a canonical Entra object ID.", ConfigStatusTone.Error);
            NotifyContentChanged();
            return;
        }

        if (_editingChannelAccess is not null)
        {
            ReplaceEditingChannelAccess(new TeamsChannelAccessOverride
            {
                TeamId = _editingChannelAccess.TeamId,
                ChannelId = _editingChannelAccess.ChannelId,
                AllowedUserIds = _editingChannelAccess.AllowedUserIds,
                AllowedGroupIds = [.. groupIds]
            });
            Screen.Value = ChannelsConfigScreen.TeamsChannelAccess;
            AutosaveCompletedAction("Channel-specific allowed groups saved.");
            NotifyContentChanged();
            return;
        }

        SetAllowedGroupIds(_activeAdapterType, groupIds);
        UpdateAdapterPickerSummary(_activeAdapterType);
        _teamsPrincipalSearchReturnScreen = null;
        Screen.Value = ChannelsConfigScreen.AdapterMenu;
        AutosaveCompletedAction("Allowed group settings saved.");
        NotifyContentChanged();
    }

    internal void BeginGroupChats()
    {
        EndGroupChatDiscovery();
        _groupChatDetailsId = null;
        var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
        GroupChatsEnabled = teams.AllowGroupChats;
        AllowedGroupChatsInput = teams.AllowedGroupChatIdsInput;
        Screen.Value = ChannelsConfigScreen.GroupChats;
        Status.Value = new ConfigStatusMessage(
            "Space changes ingress for all allowed Group Chats. Enter saves. Esc cancels.",
            ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    private void BeginGroupChatDetails(string chatId)
    {
        var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
        GroupChatsEnabled = teams.AllowGroupChats;
        AllowedGroupChatsInput = chatId;
        _groupChatDetailsId = chatId;
        Screen.Value = ChannelsConfigScreen.GroupChats;
        Status.Value = new ConfigStatusMessage(
            "This Group Chat uses Team audience and global principal rules. Remove it with Delete from the destination list.",
            ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void ToggleGroupChats()
    {
        if (IsGroupChatSaveInProgress)
            return;

        GroupChatsEnabled = !GroupChatsEnabled;
        NotifyContentChanged();
    }

    internal void ApplyGroupChats()
    {
        if (IsGroupChatSaveInProgress)
            return;

        var groupChatIds = ChannelCsv.ParseCsv(AllowedGroupChatsInput, trimHash: false);
        if (groupChatIds.Any(id => !TeamsSessionIdentifierCodec.IsCanonicalGroupChatConversationId(id)))
        {
            Status.Value = new ConfigStatusMessage(
                "Each Group Chat ID must use the canonical 19:…@thread.v2 format.",
                ConfigStatusTone.Error);
            NotifyContentChanged();
            return;
        }

        var enabled = GroupChatsEnabled;
        var detailsId = _groupChatDetailsId;
        IsGroupChatSaveInProgress = true;
        Status.Value = new ConfigStatusMessage("Saving Microsoft Teams Group Chat settings...", ConfigStatusTone.Neutral);
        var ct = _lifetimeCts.Token;
        _ = EnqueueConfigWriteAsync(() => CompleteGroupChatSaveAsync(groupChatIds, enabled, detailsId, ct));
        NotifyContentChanged();
    }

    private async Task CompleteGroupChatSaveAsync(
        IReadOnlyList<string> groupChatIds,
        bool enabled,
        string? detailsId,
        CancellationToken ct)
    {
        var previousEnabled = false;
        string? previousIds = null;
        try
        {
            // A preceding write can reload Step. Apply the captured draft only after that write completes.
            await InvokeAsync(() =>
            {
                var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
                previousEnabled = teams.AllowGroupChats;
                previousIds = teams.AllowedGroupChatIdsInput;
                teams.AllowGroupChats = enabled;
                if (detailsId is not null)
                {
                    var retained = ChannelCsv.ParseCsv(previousIds, trimHash: false)
                        .Where(id => !string.Equals(id, detailsId, StringComparison.Ordinal));
                    teams.AllowedGroupChatIdsInput = ChannelCsv.JoinOrNull([.. retained, .. groupChatIds]);
                }
                else
                {
                    teams.AllowedGroupChatIdsInput = ChannelCsv.JoinOrNull(groupChatIds);
                }
            }, ct);

            ct.ThrowIfCancellationRequested();
            var saved = await SaveCompletedAsync("Microsoft Teams Group Chat settings saved.", ct);
            await InvokeAsync(() =>
            {
                IsGroupChatSaveInProgress = false;
                if (!saved)
                {
                    var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
                    teams.AllowGroupChats = previousEnabled;
                    teams.AllowedGroupChatIdsInput = previousIds;
                    NotifyContentChanged();
                    return;
                }

                _groupChatDetailsId = null;
                EndGroupChatDiscovery();
                UpdateAdapterPickerSummary(ChannelType.Teams);
                Screen.Value = ChannelsConfigScreen.AdapterMenu;
                NotifyContentChanged();
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Debug.WriteLine("ChannelsConfig: Group Chat save cancelled during disposal.");
        }
    }

    internal void BeginAttachments()
    {
        var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
        AttachmentsEnabled = teams.AllowAttachments;
        Screen.Value = ChannelsConfigScreen.Attachments;
        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void ToggleAttachments()
    {
        AttachmentsEnabled = !AttachmentsEnabled;
        Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).AllowAttachments = AttachmentsEnabled;
        UpdateAdapterPickerSummary(ChannelType.Teams);
        AutosaveCompletedAction($"Microsoft Teams attachments {(AttachmentsEnabled ? "enabled" : "disabled")} and saved.");
        NotifyContentChanged();
    }

    private void BeginTeamsChannelAccess(string channelId)
    {
        _teamsPrincipalSearchReturnScreen = null;
        var matches = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).ChannelAccessOverrides
            .Where(accessOverride => string.Equals(accessOverride.ChannelId, channelId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length > 1)
        {
            Status.Value = new ConfigStatusMessage("Channel-specific access is ambiguous. Use the Team search path or correct the structured overrides in configuration.", ConfigStatusTone.Error);
            NotifyContentChanged();
            return;
        }

        var existing = matches.SingleOrDefault();
        var resolvedTeamId = existing?.TeamId;
        if (resolvedTeamId is null && !TryResolveTeamsTeamId(channelId, out resolvedTeamId))
        {
            Status.Value = new ConfigStatusMessage("Channel-specific access needs one canonical Team ID. Use the Team search path or add an exact structured override in configuration.", ConfigStatusTone.Error);
            NotifyContentChanged();
            return;
        }

        _editingChannelAccess = existing ?? new TeamsChannelAccessOverride
        {
            TeamId = resolvedTeamId,
            ChannelId = channelId
        };
        _channelAccessRowIndex = 0;
        Screen.Value = ChannelsConfigScreen.TeamsChannelAccess;
        NotifyContentChanged();
    }

    internal void MoveChannelAccessRow(int delta)
    {
        _channelAccessRowIndex = Clamp(_channelAccessRowIndex + delta, GetTeamsChannelAccessRows().Count);
        NotifyContentChanged();
    }

    internal IReadOnlyList<TeamsChannelAccessRow> GetTeamsChannelAccessRows()
    {
        if (_editingChannelAccess is null)
            return [];

        return
        [
            new TeamsChannelAccessRow("Add allowed user", TeamsChannelAccessRowKind.AddUser, null),
            new TeamsChannelAccessRow("Add allowed group", TeamsChannelAccessRowKind.AddGroup, null),
            .. _editingChannelAccess.AllowedUserIds.Select(id => new TeamsChannelAccessRow(
                $"Remove user · {FormatTeamsUserLabel(id)}", TeamsChannelAccessRowKind.RemoveUser, id)),
            .. _editingChannelAccess.AllowedGroupIds.Select(id => new TeamsChannelAccessRow(
                $"Remove group · {FormatTeamsGroupLabel(id)}", TeamsChannelAccessRowKind.RemoveGroup, id)),
            new TeamsChannelAccessRow("Done", TeamsChannelAccessRowKind.Done, null)
        ];
    }

    internal void ActivateChannelAccessRow()
    {
        if (_editingChannelAccess is null)
            return;

        var rows = GetTeamsChannelAccessRows();
        if (_channelAccessRowIndex >= rows.Count)
            return;

        var row = rows[_channelAccessRowIndex];
        switch (row.Kind)
        {
            case TeamsChannelAccessRowKind.AddUser:
                BeginTeamsUserSearch();
                break;
            case TeamsChannelAccessRowKind.AddGroup:
                BeginTeamsGroupSearch();
                break;
            case TeamsChannelAccessRowKind.RemoveUser:
            case TeamsChannelAccessRowKind.RemoveGroup:
                BeginTeamsChannelPrincipalRemoval(row);
                break;
            case TeamsChannelAccessRowKind.Done:
                _editingChannelAccess = null;
                Screen.Value = ChannelsConfigScreen.ChannelPermissions;
                break;
        }

        NotifyContentChanged();
    }

    private void BeginTeamsChannelPrincipalRemoval(TeamsChannelAccessRow row)
    {
        if (_editingChannelAccess is null || row.Id is null)
            return;

        _pendingChannelPrincipalRemoval = new TeamsChannelPrincipalRemoval(
            _editingChannelAccess.TeamId,
            _editingChannelAccess.ChannelId,
            row.Id,
            row.Kind == TeamsChannelAccessRowKind.RemoveUser ? TeamsPrincipalKind.User : TeamsPrincipalKind.Group,
            row.Label);
        _teamsPrincipalRemovalIndex = 0;
        Screen.Value = ChannelsConfigScreen.TeamsChannelPrincipalRemovalConfirm;
        Status.Value = new ConfigStatusMessage(
            "Confirm removal. This exact channel grant can change the sender access rule.",
            ConfigStatusTone.Warning);
    }

    internal void MoveTeamsChannelPrincipalRemoval(int delta)
    {
        _teamsPrincipalRemovalIndex = Clamp(_teamsPrincipalRemovalIndex + delta, 2);
        NotifyContentChanged();
    }

    internal void ConfirmTeamsChannelPrincipalRemoval(bool remove)
    {
        var pending = _pendingChannelPrincipalRemoval;
        _pendingChannelPrincipalRemoval = null;
        if (!remove || pending is null || _editingChannelAccess is null)
        {
            Screen.Value = ChannelsConfigScreen.TeamsChannelAccess;
            NotifyContentChanged();
            return;
        }

        var replacement = pending.Kind == TeamsPrincipalKind.User
            ? new TeamsChannelAccessOverride
            {
                TeamId = _editingChannelAccess.TeamId,
                ChannelId = _editingChannelAccess.ChannelId,
                AllowedUserIds = [.. _editingChannelAccess.AllowedUserIds.Where(id => !string.Equals(id, pending.PrincipalId, StringComparison.Ordinal))],
                AllowedGroupIds = _editingChannelAccess.AllowedGroupIds
            }
            : new TeamsChannelAccessOverride
            {
                TeamId = _editingChannelAccess.TeamId,
                ChannelId = _editingChannelAccess.ChannelId,
                AllowedUserIds = _editingChannelAccess.AllowedUserIds,
                AllowedGroupIds = [.. _editingChannelAccess.AllowedGroupIds.Where(id => !string.Equals(id, pending.PrincipalId, StringComparison.Ordinal))]
            };
        ReplaceEditingChannelAccess(replacement);
        _channelAccessRowIndex = Clamp(_channelAccessRowIndex, GetTeamsChannelAccessRows().Count);
        Screen.Value = ChannelsConfigScreen.TeamsChannelAccess;
        AutosaveCompletedAction($"Removed {pending.PrincipalId} from channel-specific Teams access and saved.");
        NotifyContentChanged();
    }

    private void ReplaceEditingChannelAccess(TeamsChannelAccessOverride replacement)
    {
        var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
        teams.ChannelAccessOverrides =
        [
            .. teams.ChannelAccessOverrides.Where(accessOverride =>
                !string.Equals(accessOverride.TeamId, replacement.TeamId, StringComparison.Ordinal)
                || !string.Equals(accessOverride.ChannelId, replacement.ChannelId, StringComparison.Ordinal)),
            replacement
        ];
        _editingChannelAccess = replacement;
    }

    internal void BeginTeamsUserSearch()
    {
        _teamsDirectorySearch?.Invalidate();
        DirectorySearchInput = null;
        _userSearchResults = [];
        _directoryResultIndex = 0;
        Screen.Value = ChannelsConfigScreen.TeamsUserSearch;
        Status.Value = new ConfigStatusMessage(
            "Search users by name, UPN, or mail. Select the advanced entry action for canonical IDs.",
            ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void BeginTeamsGroupSearch()
    {
        _teamsDirectorySearch?.Invalidate();
        DirectorySearchInput = null;
        _groupSearchResults = [];
        _directoryResultIndex = 0;
        Screen.Value = ChannelsConfigScreen.TeamsGroupSearch;
        Status.Value = new ConfigStatusMessage("Search Microsoft 365 or security groups. Select the advanced entry action for canonical IDs.", ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void BeginManualTeamsUserEntry()
    {
        AllowedUsersInput = ChannelCsv.JoinOrNull(_editingChannelAccess?.AllowedUserIds ?? GetAllowedUserIds(ChannelType.Teams));
        Screen.Value = ChannelsConfigScreen.AllowedUsers;
        Status.Value = new ConfigStatusMessage("Advanced path: enter canonical Entra user IDs.", ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void BeginAdvancedTeamsUserEntry()
    {
        BeginManualTeamsUserEntry();
    }

    internal void BeginManualTeamsGroupEntry()
    {
        AllowedGroupsInput = ChannelCsv.JoinOrNull(_editingChannelAccess?.AllowedGroupIds ?? GetAllowedGroupIds(ChannelType.Teams));
        Screen.Value = ChannelsConfigScreen.AllowedGroups;
        Status.Value = new ConfigStatusMessage("Advanced path: enter canonical Entra group IDs.", ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void ResetTeamsPrincipalSearchResults()
    {
        _teamsDirectorySearch?.Invalidate();
        _userSearchResults = [];
        _groupSearchResults = [];
        _directoryResultIndex = 0;
    }

    internal void StageTeamsDirectorySearchInput(string? value)
    {
        if (string.Equals(DirectorySearchInput, value, StringComparison.Ordinal))
            return;

        DirectorySearchInput = value;
        _teamsDirectorySearch?.Invalidate();
        _directoryResultIndex = 0;

        switch (Screen.Value)
        {
            case ChannelsConfigScreen.TeamsTeamSearch:
                _teamSearchResults = [];
                break;
            case ChannelsConfigScreen.TeamsUserSearch:
                _userSearchResults = [];
                break;
            case ChannelsConfigScreen.TeamsGroupSearch:
                _groupSearchResults = [];
                break;
        }
    }

    internal Task SearchUsersFromInputAsync()
        => SearchUsersAsync(_lifetimeCts.Token);

    private async Task SearchUsersAsync(CancellationToken cancellationToken)
    {
        var search = TryGetTeamsDirectorySearch();
        if (search is null)
            return;

        Status.Value = new ConfigStatusMessage("Searching Entra users...", ConfigStatusTone.Neutral);
        NotifyContentChanged();
        var query = DirectorySearchInput ?? string.Empty;
        var response = await search.SearchUsersAsync(query, cancellationToken).ConfigureAwait(false);
        if (!response.IsCurrent || cancellationToken.IsCancellationRequested)
            return;

        _ = InvokeAsync(() => ApplyUserSearchResponse(query, response), cancellationToken);
    }

    internal Task SearchGroupsFromInputAsync()
        => SearchGroupsAsync(_lifetimeCts.Token);

    private async Task SearchGroupsAsync(CancellationToken cancellationToken)
    {
        var search = TryGetTeamsDirectorySearch();
        if (search is null)
            return;

        Status.Value = new ConfigStatusMessage("Searching Entra groups...", ConfigStatusTone.Neutral);
        NotifyContentChanged();
        var query = DirectorySearchInput ?? string.Empty;
        var response = await search.SearchGroupsAsync(query, cancellationToken).ConfigureAwait(false);
        if (!response.IsCurrent || cancellationToken.IsCancellationRequested)
            return;

        _ = InvokeAsync(() => ApplyGroupSearchResponse(query, response), cancellationToken);
    }

    private void ApplyTeamSearchResponse(
        string query,
        TeamsDirectorySearchResponse<IReadOnlyList<TeamsDirectoryTeam>> response)
    {
        if (Screen.Value != ChannelsConfigScreen.TeamsTeamSearch
            || !string.Equals(DirectorySearchInput, query, StringComparison.Ordinal)
            || !response.IsCurrent
            || _teamsDirectorySearch?.IsCurrent(response.Generation) != true)
            return;

        if (!response.Result.IsAvailable || response.Result.Value is null)
        {
            Status.Value = new ConfigStatusMessage(DirectoryFailureMessage(response.Result.ReasonCode), ConfigStatusTone.Error);
            NotifyContentChanged();
            return;
        }

        _teamSearchResults = response.Result.Value;
        _directoryResultIndex = 0;
        Status.Value = new ConfigStatusMessage(
            _teamSearchResults.Count == 0 ? "No Teams matched that search." : "Select a Team, then press Enter.",
            ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    private void ApplyChannelSearchResponse(
        string teamId,
        TeamsDirectorySearchResponse<IReadOnlyList<TeamsDirectoryChannel>> response)
    {
        if (Screen.Value != ChannelsConfigScreen.TeamsChannelSearch
            || !string.Equals(_selectedTeam?.Id, teamId, StringComparison.Ordinal)
            || !response.IsCurrent
            || _teamsDirectorySearch?.IsCurrent(response.Generation) != true)
            return;

        if (!response.Result.IsAvailable || response.Result.Value is null)
        {
            Status.Value = new ConfigStatusMessage(DirectoryFailureMessage(response.Result.ReasonCode), ConfigStatusTone.Error);
            NotifyContentChanged();
            return;
        }

        _channelSearchResults = response.Result.Value;
        _directoryResultIndex = 0;
        Status.Value = new ConfigStatusMessage(
            _channelSearchResults.Count == 0 ? "No channels are available in this Team." : "Select a channel, then press Enter to save it.",
            ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    private void ApplyUserSearchResponse(
        string query,
        TeamsDirectorySearchResponse<IReadOnlyList<TeamsDirectoryUser>> response)
    {
        if (Screen.Value != ChannelsConfigScreen.TeamsUserSearch
            || !string.Equals(DirectorySearchInput, query, StringComparison.Ordinal)
            || !response.IsCurrent
            || _teamsDirectorySearch?.IsCurrent(response.Generation) != true)
            return;

        if (!response.Result.IsAvailable || response.Result.Value is null)
        {
            Status.Value = new ConfigStatusMessage(DirectoryFailureMessage(response.Result.ReasonCode), ConfigStatusTone.Error);
            NotifyContentChanged();
            return;
        }

        _userSearchResults = response.Result.Value;
        _directoryResultIndex = 0;
        Status.Value = new ConfigStatusMessage(
            _userSearchResults.Count == 0 ? "No users matched that search." : "Select a user, then press Enter to add it.",
            ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    private void ApplyGroupSearchResponse(
        string query,
        TeamsDirectorySearchResponse<IReadOnlyList<TeamsDirectoryGroup>> response)
    {
        if (Screen.Value != ChannelsConfigScreen.TeamsGroupSearch
            || !string.Equals(DirectorySearchInput, query, StringComparison.Ordinal)
            || !response.IsCurrent
            || _teamsDirectorySearch?.IsCurrent(response.Generation) != true)
            return;

        if (!response.Result.IsAvailable || response.Result.Value is null)
        {
            Status.Value = new ConfigStatusMessage(DirectoryFailureMessage(response.Result.ReasonCode), ConfigStatusTone.Error);
            NotifyContentChanged();
            return;
        }

        _groupSearchResults = response.Result.Value;
        _directoryResultIndex = 0;
        Status.Value = new ConfigStatusMessage(
            _groupSearchResults.Count == 0 ? "No groups matched that search." : "Select a group, then press Enter to add it.",
            ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void AddSelectedTeamsUser()
    {
        if (IsAdvancedTeamsDirectoryActionSelected())
        {
            BeginManualTeamsUserEntry();
            return;
        }

        if (_userSearchResults.Count == 0)
            return;

        AddDiscoveredTeamsUser(_userSearchResults[_directoryResultIndex]);
    }

    internal Task SearchGroupChatsFromInputAsync()
    {
        if (_isGroupChatSearchRunning)
            return _groupChatSearchTask ?? Task.CompletedTask;

        var search = TryGetTeamsDirectorySearch();
        if (search is null)
            return Task.CompletedTask;

        CancelGroupChatSearch();
        if (!HasGroupChatContinuation)
        {
            _groupChatSearchResults = [];
            _groupChatSearchProgress = null;
            _hasSearchedGroupChats = false;
            _directoryResultIndex = 0;
        }

        _groupChatSearchCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _isGroupChatSearchRunning = true;
        Status.Value = new ConfigStatusMessage("Searching Group Chat names... Press Ctrl+S to stop.", ConfigStatusTone.Neutral);
        NotifyContentChanged();
        _groupChatSearchTask = SearchGroupChatsAsync(
            search, _groupChatSearchInput?.Trim() ?? string.Empty, _groupChatContinuation,
            _groupChatSearchGeneration, _groupChatSearchCts.Token);
        return _groupChatSearchTask;
    }

    private async Task SearchGroupChatsAsync(
        TeamsDirectorySearchController search,
        string query,
        string? continuation,
        long generation,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(GroupChatSearchRunLimit, _timeProvider);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var visitedContinuations = new HashSet<string>(StringComparer.Ordinal);
        if (continuation is not null)
            visitedContinuations.Add(continuation);

        try
        {
            for (var batch = 0; batch < MaximumGroupChatSearchBatches; batch++)
            {
                // Start each request on the loop. The controller shares its generation with other directory screens.
                ValueTask<TeamsDirectorySearchResponse<TeamsDirectoryGroupChatSearchPage>> request = default;
                await InvokeAsync(() =>
                {
                    if (IsCurrentGroupChatSearch(query, generation))
                        request = search.SearchGroupChatsAsync(query, continuation, run.Token);
                }, run.Token);
                run.Token.ThrowIfCancellationRequested();
                var response = await request.ConfigureAwait(false);
                run.Token.ThrowIfCancellationRequested();
                var next = continuation;
                var keepSearching = false;
                await InvokeAsync(() =>
                {
                    if (!IsCurrentGroupChatSearch(query, generation))
                        return;

                    keepSearching = ApplyGroupChatSearchResponse(query, continuation, response);
                    next = _groupChatContinuation;
                    if (keepSearching && !visitedContinuations.Add(next!))
                    {
                        FinishGroupChatSearchRun();
                        _groupChatContinuation = null;
                        Status.Value = new ConfigStatusMessage(
                            "The search repeated its continuation. Retry the search or use a canonical chat ID.", ConfigStatusTone.Error);
                        keepSearching = false;
                        NotifyContentChanged();
                    }
                }, run.Token);
                if (!keepSearching)
                    return;

                continuation = next;
            }

            await InvokeAsync(() => PauseGroupChatSearch(query, generation, "Search paused at its work limit."), cancellationToken);
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await InvokeAsync(() => PauseGroupChatSearch(query, generation, "Search paused after two minutes."), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The input or screen changed while the timeout update waited for the loop.
                    return;
                }
            }
        }
    }

    private bool IsCurrentGroupChatSearch(string query, long generation)
        => generation == _groupChatSearchGeneration
           && _isGroupChatSearchRunning
           && Screen.Value == ChannelsConfigScreen.TeamsGroupChatSearch
           && string.Equals(_groupChatSearchInput?.Trim() ?? string.Empty, query, StringComparison.Ordinal);

    private bool ApplyGroupChatSearchResponse(
        string query,
        string? continuation,
        TeamsDirectorySearchResponse<TeamsDirectoryGroupChatSearchPage> response)
    {
        if (Screen.Value != ChannelsConfigScreen.TeamsGroupChatSearch
            || !string.Equals(_groupChatSearchInput?.Trim() ?? string.Empty, query, StringComparison.Ordinal)
            || !string.Equals(_groupChatContinuation, continuation, StringComparison.Ordinal)
            || !response.IsCurrent
            || _teamsDirectorySearch?.IsCurrent(response.Generation) != true)
            return false;

        if (!response.Result.IsAvailable || response.Result.Value is null)
        {
            FinishGroupChatSearchRun();
            if (response.Result.ReasonCode is "teams_directory_invalid_continuation"
                or "teams_directory_pagination_stalled" or "teams_directory_search_limit_reached")
                _groupChatContinuation = null;
            Status.Value = new ConfigStatusMessage(
                DirectoryFailureMessage(response.Result.ReasonCode) + GroupChatSearchCoverage(), ConfigStatusTone.Error);
            NotifyContentChanged();
            return false;
        }

        var previousCount = _groupChatSearchResults.Count;
        var selectedStopAction = _directoryResultIndex == previousCount + 1;
        var selectedAdvancedAction = _directoryResultIndex == GroupChatSearchAdvancedIndex;
        var selectedSearchAction = previousCount > 0 && _directoryResultIndex == previousCount;
        var matches = _groupChatSearchResults.Concat(response.Result.Value.Chats)
            .DistinctBy(static chat => chat.Id, StringComparer.Ordinal).ToArray();
        if (_groupChatSearchProgress is { RequestsMade: > 0 } previous
            && response.Result.Value.Continuation is not null
            && response.Result.Value.RequestsMade <= previous.RequestsMade
            && response.Result.Value.ChatsExamined <= previous.ChatsExamined
            && response.Result.Value.UsersExamined <= previous.UsersExamined
            && matches.Length == previousCount)
        {
            FinishGroupChatSearchRun();
            _groupChatContinuation = null;
            Status.Value = new ConfigStatusMessage(
                "The search made no progress. Retry the search or use a canonical chat ID.", ConfigStatusTone.Error);
            NotifyContentChanged();
            return false;
        }
        if (matches.Length > MaximumRetainedGroupChatMatches)
        {
            FinishGroupChatSearchRun();
            _groupChatContinuation = null;
            Status.Value = new ConfigStatusMessage(
                "The search reached 1,000 matches. Enter a more specific Group Chat name.", ConfigStatusTone.Warning);
            NotifyContentChanged();
            return false;
        }

        _groupChatSearchResults = matches;
        _groupChatContinuation = response.Result.Value.Continuation;
        _groupChatSearchProgress = response.Result.Value;
        _hasSearchedGroupChats = true;
        _isGroupChatSearchRunning = HasGroupChatContinuation;
        if (selectedAdvancedAction)
            _directoryResultIndex = GroupChatSearchAdvancedIndex;
        else if (selectedStopAction)
            _directoryResultIndex = GroupChatSearchActionIndex + (_isGroupChatSearchRunning ? 1 : 0);
        else if (selectedSearchAction)
            _directoryResultIndex = GroupChatSearchActionIndex;

        var unavailable = response.Result.Value.UnavailableUsers;
        var state = _isGroupChatSearchRunning
            ? "Search continues automatically. Select a match or press Ctrl+S to stop."
            : matches.Length > 0
                ? "Search complete. Select a Group Chat to review."
                : unavailable > 0
                    ? "No matches in the available chats."
                    : "Search complete. No Group Chats matched that name.";
        Status.Value = new ConfigStatusMessage(state + GroupChatSearchCoverage(),
            unavailable > 0 ? ConfigStatusTone.Warning : ConfigStatusTone.Neutral);
        NotifyContentChanged();
        return _isGroupChatSearchRunning;
    }

    private string GroupChatSearchCoverage()
    {
        var progress = _groupChatSearchProgress;
        if (progress is null)
            return string.Empty;

        var coverage = $" {GroupChatSearchProgressText}.";
        if (progress.UnavailableUsers > 0)
            coverage += $" Chats for {progress.UnavailableUsers} users were unavailable; coverage is incomplete.";
        return coverage;
    }

    private void PauseGroupChatSearch(string query, long generation, string message)
    {
        if (!IsCurrentGroupChatSearch(query, generation))
            return;

        FinishGroupChatSearchRun();
        Status.Value = new ConfigStatusMessage(
            message + (HasGroupChatContinuation ? " Select Resume search to continue." : " Press Enter to retry.")
            + GroupChatSearchCoverage(), ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void StopGroupChatSearch()
    {
        if (!_isGroupChatSearchRunning)
            return;

        FinishGroupChatSearchRun();
        CancelGroupChatSearch();
        Status.Value = new ConfigStatusMessage(
            "Search stopped." + (HasGroupChatContinuation ? " Select Resume search to continue." : " Press Enter to retry.")
            + GroupChatSearchCoverage(), ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    private void FinishGroupChatSearchRun()
    {
        var selectedAdvancedAction = _directoryResultIndex == GroupChatSearchAdvancedIndex;
        _isGroupChatSearchRunning = false;
        _directoryResultIndex = selectedAdvancedAction
            ? GroupChatSearchAdvancedIndex
            : Math.Min(_directoryResultIndex, GroupChatSearchActionIndex);
    }

    private void CancelGroupChatSearch()
    {
        _groupChatSearchCts?.Cancel();
        _groupChatSearchCts?.Dispose();
        _groupChatSearchCts = null;
        _groupChatSearchGeneration++;
        _isGroupChatSearchRunning = false;
        _teamsDirectorySearch?.Invalidate();
    }

    internal void BeginManualGroupChatEntry()
    {
        CancelGroupChatSearch();
        var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
        GroupChatsEnabled = teams.AllowGroupChats;
        AllowedGroupChatsInput = teams.AllowedGroupChatIdsInput;
        _groupChatDetailsId = null;
        Screen.Value = ChannelsConfigScreen.GroupChats;
        Status.Value = new ConfigStatusMessage(
            "Advanced path: enter a canonical Group Chat ID. Syntax does not verify chat type or app installation.",
            ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void SelectGroupChatForReview()
    {
        var chats = FilteredGroupChatSearchResults;
        if (_directoryResultIndex >= chats.Count)
            return;

        var chat = chats[_directoryResultIndex];
        CancelGroupChatSearch();
        var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
        var existing = ChannelCsv.ParseCsv(teams.AllowedGroupChatIdsInput, trimHash: false);
        if (!existing.Contains(chat.Id, StringComparer.Ordinal))
            AllowedGroupChatsInput = ChannelCsv.JoinOrNull([.. existing, chat.Id]);
        else
            AllowedGroupChatsInput = teams.AllowedGroupChatIdsInput;

        GroupChatsEnabled = teams.AllowGroupChats;
        _groupChatDetailsId = null;
        Screen.Value = ChannelsConfigScreen.GroupChats;
        Status.Value = new ConfigStatusMessage("Review the canonical Group Chat ID and ingress switch before you apply the change.", ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    internal void AddDiscoveredTeamsUser(TeamsDirectoryUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (_editingChannelAccess is not null)
        {
            if (_editingChannelAccess.AllowedUserIds.Contains(user.Id, StringComparer.Ordinal))
            {
                Status.Value = new ConfigStatusMessage("That user already has channel access.", ConfigStatusTone.Neutral);
                NotifyContentChanged();
                return;
            }

            ReplaceEditingChannelAccess(new TeamsChannelAccessOverride
            {
                TeamId = _editingChannelAccess.TeamId,
                ChannelId = _editingChannelAccess.ChannelId,
                AllowedUserIds = [.. _editingChannelAccess.AllowedUserIds, user.Id],
                AllowedGroupIds = _editingChannelAccess.AllowedGroupIds
            });
            Screen.Value = ChannelsConfigScreen.TeamsChannelAccess;
            AutosaveCompletedAction($"Added {FormatTeamsUserLabel(user)} to the channel and saved.");
            NotifyContentChanged();
            return;
        }

        var users = GetAllowedUserIds(ChannelType.Teams);
        if (users.Contains(user.Id, StringComparer.Ordinal))
        {
            Status.Value = new ConfigStatusMessage("That user is already allowed.", ConfigStatusTone.Neutral);
            NotifyContentChanged();
            return;
        }

        SetAllowedUserIds(ChannelType.Teams, [.. users, user.Id]);
        UpdateAdapterPickerSummary(ChannelType.Teams);
        _teamsPrincipalSearchReturnScreen = null;
        Screen.Value = ChannelsConfigScreen.AdapterMenu;
        AutosaveCompletedAction($"Added {FormatTeamsUserLabel(user)} and saved.");
        NotifyContentChanged();
    }

    internal void AddSelectedTeamsGroup()
    {
        if (IsAdvancedTeamsDirectoryActionSelected())
        {
            BeginManualTeamsGroupEntry();
            return;
        }

        if (_groupSearchResults.Count == 0)
            return;

        AddDiscoveredTeamsGroup(_groupSearchResults[_directoryResultIndex]);
    }

    internal void AddDiscoveredTeamsGroup(TeamsDirectoryGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (_editingChannelAccess is not null)
        {
            if (_editingChannelAccess.AllowedGroupIds.Contains(group.Id, StringComparer.Ordinal))
            {
                Status.Value = new ConfigStatusMessage("That group already has channel access.", ConfigStatusTone.Neutral);
                NotifyContentChanged();
                return;
            }

            ReplaceEditingChannelAccess(new TeamsChannelAccessOverride
            {
                TeamId = _editingChannelAccess.TeamId,
                ChannelId = _editingChannelAccess.ChannelId,
                AllowedUserIds = _editingChannelAccess.AllowedUserIds,
                AllowedGroupIds = [.. _editingChannelAccess.AllowedGroupIds, group.Id]
            });
            Screen.Value = ChannelsConfigScreen.TeamsChannelAccess;
            AutosaveCompletedAction($"Added {FormatTeamsGroupLabel(group)} to the channel and saved.");
            NotifyContentChanged();
            return;
        }

        var groups = GetAllowedGroupIds(ChannelType.Teams);
        if (groups.Contains(group.Id, StringComparer.Ordinal))
        {
            Status.Value = new ConfigStatusMessage("That group is already allowed.", ConfigStatusTone.Neutral);
            NotifyContentChanged();
            return;
        }

        SetAllowedGroupIds(ChannelType.Teams, [.. groups, group.Id]);
        UpdateAdapterPickerSummary(ChannelType.Teams);
        _teamsPrincipalSearchReturnScreen = null;
        Screen.Value = ChannelsConfigScreen.AdapterMenu;
        AutosaveCompletedAction($"Added {FormatTeamsGroupLabel(group)} and saved.");
        NotifyContentChanged();
    }

    internal IReadOnlyList<string> GetDirectoryStatusLines()
    {
        var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
        var tenantId = GetEffectiveTeamsSetting("TenantId", teams.TenantId);
        var clientId = GetEffectiveTeamsSetting("ClientId", teams.ClientId);
        var botId = GetEffectiveTeamsSetting("BotId", teams.BotId);
        var hasClientSecret = !string.IsNullOrWhiteSpace(GetEffectiveTeamsClientSecret(teams));
        var teamsConnectionConfigured = !string.IsNullOrWhiteSpace(tenantId)
                                       && !string.IsNullOrWhiteSpace(clientId)
                                       && !string.IsNullOrWhiteSpace(botId)
                                       && hasClientSecret;
        var graphCredentialsConfigured = !string.IsNullOrWhiteSpace(tenantId)
                                        && !string.IsNullOrWhiteSpace(clientId)
                                        && hasClientSecret;
        var groupsConfigured = GetAllowedGroupIds(ChannelType.Teams).Count > 0;
        return
        [
            teamsConnectionConfigured ? "Teams connection: configured" : "Teams connection: incomplete",
            graphCredentialsConfigured ? "Graph app credentials: configured; access not yet verified" : "Graph app credentials: incomplete",
            "Required Graph application permissions: Team.ReadBasic.All, Channel.ReadBasic.All",
            "Required Graph application permissions: User.Read.All, GroupMember.Read.All",
            "Optional Graph application permission: Chat.ReadBasic.All for Group Chat labels and discovery",
            "Admin consent: required for Graph application permissions",
            _teamsDirectoryLabelsAvailable switch
            {
                true => "Directory labels: available",
                false => "Directory labels: unavailable",
                _ => "Directory labels: not yet tested"
            },
            groupsConfigured ? "Group authorization: configured (fails closed when Graph is unavailable)" : "Group authorization: not configured"
        ];
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
        TenantIdInput = _activeAdapterType == ChannelType.Teams ? Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).TenantId : null;
        ClientIdInput = _activeAdapterType == ChannelType.Teams ? Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).ClientId : null;
        BotIdInput = _activeAdapterType == ChannelType.Teams ? Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).BotId : null;
        ClientSecretInput = null;
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
            ChannelType.Teams =>
            [
                new CredentialFieldSpec("tenant", "Entra tenant ID", IsSecret: false, "tenant GUID", null),
                new CredentialFieldSpec("client", "Application / client ID", IsSecret: false, "application GUID", null),
                new CredentialFieldSpec("botid", "Teams bot ID", IsSecret: false, "bot registration ID", null),
                new CredentialFieldSpec("secret", "Client secret", IsSecret: true, "client secret", GetCredentialPresenceText("secret"))
            ],
            _ => []
        };
    }

    internal string GetCredentialsScreenTitle()
        => _activeAdapterType == ChannelType.Teams
            ? "Microsoft Teams > Connection & credentials"
            : $"{ActiveAdapterName} > Credentials";

    internal string? GetCredentialDraftValue(string key) => key switch
    {
        "bot" => BotTokenInput,
        "app" => AppTokenInput,
        "server" => ServerUrlInput,
        "callback" => CallbackUrlInput,
        "tenant" => TenantIdInput,
        "client" => ClientIdInput,
        "botid" => BotIdInput,
        "secret" => ClientSecretInput,
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
            case "tenant":
                TenantIdInput = value;
                break;
            case "client":
                ClientIdInput = value;
                break;
            case "botid":
                BotIdInput = value;
                break;
            case "secret":
                ClientSecretInput = value;
                break;
        }
    }

    internal void MoveCredentialField(int delta)
    {
        CredentialFieldIndex = Wrap(CredentialFieldIndex + delta, GetCredentialFields().Count);
        NotifyContentChanged();
    }

    internal Task ApplyCredentialsAsync()
    {
        var issue = ValidateCredentialDrafts();
        if (issue is not null)
        {
            FocusCredentialField(issue.FieldId);
            Status.Value = new ConfigStatusMessage(issue.Message, ConfigStatusTone.Error);
            NotifyContentChanged();
            return Task.CompletedTask;
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
            case ChannelType.Teams:
                var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
                teams.TenantId = Normalize(TenantIdInput);
                teams.ClientId = Normalize(ClientIdInput);
                teams.BotId = Normalize(BotIdInput);
                teams.ClientSecret = Normalize(ClientSecretInput);
                break;
        }

        IsCredentialSaveInProgress = true;
        Status.Value = new ConfigStatusMessage($"Saving {ActiveAdapterName} connection...", ConfigStatusTone.Neutral);
        NotifyContentChanged();
        return EnqueueConfigWriteAsync(CompleteCredentialSaveAsync);
    }

    private async Task CompleteCredentialSaveAsync()
    {
        // Yield before the disk write. The key handler keeps the Termina loop free while the
        // existing serialized write path persists and reloads the candidate configuration.
        await Task.Yield();

        var savedAdapter = _activeAdapterType;
        var successMessage = savedAdapter == ChannelType.Teams
            ? "Microsoft Teams connection saved."
            : "Credential changes saved.";
        var saved = await SaveCompletedAsync(successMessage, _lifetimeCts.Token);

        await InvokeAsync(() => FinishCredentialSave(savedAdapter, saved), _lifetimeCts.Token);
    }

    private void FinishCredentialSave(ChannelType savedAdapter, bool saved)
    {
        IsCredentialSaveInProgress = false;
        if (!saved)
        {
            NotifyContentChanged();
            return;
        }

        if (savedAdapter == ChannelType.Teams)
        {
            var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
            if (!teams.HasPersistedClientSecret)
            {
                Status.Value = new ConfigStatusMessage("Microsoft Teams client secret did not persist. Enter the secret and apply again.", ConfigStatusTone.Error);
                NotifyContentChanged();
                return;
            }

            InvalidateTeamsDirectory();
            StartChannelLabelResolution(ChannelType.Teams);
        }

        UpdateAdapterPickerSummary(savedAdapter);
        Screen.Value = ChannelsConfigScreen.AdapterMenu;
        Status.Value = new ConfigStatusMessage(
            savedAdapter == ChannelType.Teams ? "Microsoft Teams connection saved." : "Credential changes saved.",
            ConfigStatusTone.Success);
        NotifyContentChanged();
    }

    private void FocusCredentialField(string? fieldId)
    {
        var key = fieldId switch
        {
            ChannelsEditorFieldPaths.SlackBotToken or ChannelsEditorFieldPaths.DiscordBotToken or ChannelsEditorFieldPaths.MattermostBotToken => "bot",
            ChannelsEditorFieldPaths.SlackAppToken => "app",
            ChannelsEditorFieldPaths.MattermostServerUrl => "server",
            ChannelsEditorFieldPaths.MattermostCallbackUrl => "callback",
            ChannelsEditorFieldPaths.TeamsTenantId => "tenant",
            ChannelsEditorFieldPaths.TeamsClientId => "client",
            ChannelsEditorFieldPaths.TeamsBotId => "botid",
            ChannelsEditorFieldPaths.TeamsClientSecret => "secret",
            _ => null
        };
        if (key is null)
            return;

        var fields = GetCredentialFields();
        var index = fields
            .Select((field, index) => (field, index))
            .FirstOrDefault(entry => string.Equals(entry.field.Key, key, StringComparison.Ordinal))
            .index;
        CredentialFieldIndex = index;
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
    // The operator chose fail-loud (no inert allow-list entries): a channel that cannot be resolved
    // to an id the runtime ACL will match blocks the save with this message until it is fixed/removed.
    private static string BuildUnresolvedChannelMessage(IReadOnlyList<string> unresolved)
        => $"Could not resolve {string.Join(", ", unresolved.Select(static channel => $"#{channel}"))} to a channel the bot can see — fix or remove before saving (an unmatchable channel grants nothing).";

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

        // Probe reachable. Map resolved names to IDs.
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
            }

            // Unresolved names are intentionally NOT added — see the fail-loud block below.
        }

        // Fail loud: a name that does not resolve to a real channel id is an inert allow-list entry
        // that the runtime ACL (SlackAclPolicy, ordinal id match) can never match, so it would
        // silently grant nothing. Block the save and make the operator fix or remove it rather than
        // persisting a dead entry. Do not mutate the draft on a blocked save.
        if (result.Unresolved.Count > 0)
            return ChannelAccessOutcome.Blocked(Error(ChannelsEditorFieldPaths.SlackAllowedChannelIds, BuildUnresolvedChannelMessage(result.Unresolved)));

        SetChannelIds(ChannelType.Slack, [.. resolvedChannels.Distinct(StringComparer.Ordinal)]);
        RemapChannelAudiences(ChannelType.Slack, remap);
        RemapChannelMentionRequired(ChannelType.Slack, remap);
        UpdateAdapterPickerSummary(ChannelType.Slack);
        return ChannelAccessOutcome.None;
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

        // Fail loud: a reference that does not resolve to a real channel the bot can see is an inert
        // allow-list entry the runtime ACL can never match, so block the save rather than persist a
        // dead entry.
        if (result.Unresolved.Count > 0)
            return ChannelAccessOutcome.Blocked(Error(ChannelsEditorFieldPaths.DiscordAllowedChannelIds, BuildUnresolvedChannelMessage(result.Unresolved)));

        // All references resolved: map any names to their channel ids and persist the ids (the
        // runtime ACL matches ids, not names). Mirrors the Slack validator.
        SetResolvedChannels(ChannelType.Discord, channelIds, result.Resolved.Select(c => (c.ChannelId, c.ChannelName)));
        return ChannelAccessOutcome.None;
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

        // Fail loud: a reference that does not resolve to a real channel the bot can see is an inert
        // allow-list entry the runtime ACL can never match, so block the save rather than persist a
        // dead entry.
        if (result.Unresolved.Count > 0)
            return ChannelAccessOutcome.Blocked(Error(ChannelsEditorFieldPaths.MattermostAllowedChannelIds, BuildUnresolvedChannelMessage(result.Unresolved)));

        // All references resolved: map any names to their channel ids and persist the ids (the
        // runtime ACL matches ids, not names). Mirrors the Slack validator.
        SetResolvedChannels(ChannelType.Mattermost, channelIds, result.Resolved.Select(c => (c.ChannelId, c.ChannelName)));
        return ChannelAccessOutcome.None;
    }

    // Shared name→id remap for Discord/Mattermost: each configured reference is either already a
    // resolved channel id (kept as-is) or a name that resolves to one (replaced by the id and remapped
    // in ChannelAudiences). Unresolved references are blocked before this runs, so every reference maps.
    private void SetResolvedChannels(
        ChannelType type, IReadOnlyList<string> references, IEnumerable<(string Id, string Name)> resolved)
    {
        var byId = new HashSet<string>(StringComparer.Ordinal);
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in resolved)
        {
            byId.Add(id);
            byName.TryAdd(name, id);
        }

        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        var resolvedChannels = new List<string>();
        foreach (var reference in references)
        {
            if (byId.Contains(reference))
            {
                resolvedChannels.Add(reference);
            }
            else if (byName.TryGetValue(reference, out var id))
            {
                resolvedChannels.Add(id);
                remap[reference] = id;
            }
        }

        SetChannelIds(type, [.. resolvedChannels.Distinct(StringComparer.Ordinal)]);
        RemapChannelAudiences(type, remap);
        RemapChannelMentionRequired(type, remap);
        UpdateAdapterPickerSummary(type);
    }

    // The single place that knows each transport's probe shape: probe the live adapter for the given
    // references and return the raw result plus (probe-error, resolved id/name pairs) — or null when the
    // adapter's credentials aren't available. Reads the credential snapshot synchronously before the await
    // (loop-thread state), then awaits the probe off-loop. Crucially it does NOT write LastChannelResolution
    // here: it returns the raw Result, and the loop-thread apply sets it (SetLastChannelResolution) — the inline
    // add path inline, the background path via InvokeAsync. Downstream (ReconcileResolvedChannels) is transport-agnostic.
    private async Task<(object Result, string? Error, IEnumerable<(string Id, string Name)> Resolved)?> ResolveChannelReferencesAsync(
        ChannelType type, IReadOnlyList<string> channelIds, CancellationToken ct)
    {
        switch (type)
        {
            case ChannelType.Slack:
            {
                var slack = Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack);
                var botToken = GetEffectiveSecret("Slack.BotToken", slack.BotToken, slack.HasPersistedBotToken);
                if (string.IsNullOrWhiteSpace(botToken))
                    return null;

                var result = await _slackProbe.ResolveChannelNamesAsync(botToken, channelIds, ct);
                return (result, result.ErrorMessage, result.Resolved.Select(static c => (c.Id, c.Name)));
            }

            case ChannelType.Discord:
            {
                var discord = Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord);
                var botToken = GetEffectiveSecret("Discord.BotToken", discord.BotToken, discord.HasPersistedBotToken);
                if (string.IsNullOrWhiteSpace(botToken))
                    return null;

                var result = await _discordProbe.ResolveChannelIdsAsync(botToken, channelIds, ct);
                return (result, result.ErrorMessage, result.Resolved.Select(static c => (c.ChannelId, c.ChannelName)));
            }

            case ChannelType.Mattermost:
            {
                var mattermost = Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost);
                var serverUrl = Normalize(mattermost.ServerUrl);
                var botToken = GetEffectiveSecret("Mattermost.BotToken", mattermost.BotToken, mattermost.HasPersistedBotToken);
                if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(botToken))
                    return null;

                var result = await _mattermostProbe.ResolveChannelIdsAsync(serverUrl, botToken, channelIds, ct);
                return (result, result.ErrorMessage, result.Resolved.Select(static c => (c.ChannelId, c.ChannelName)));
            }

            default:
                return null;
        }
    }

    // Canonicalize the persisted allow-list against a completed channel resolution. The stored ACL key
    // is the platform's IMMUTABLE channel id (what the runtime matches); a human display name is mutable
    // and resolved dynamically for rendering only — it is NEVER the stored key. So, per reference:
    //   - already a channel id  -> keep it (it IS the stable key; never dropped, even when the bot can't
    //     currently fetch its display label — a transient display miss must not delete a real ACL entry)
    //   - a display name that maps to an id -> store the id (and remap its audience key)
    //   - a display name with NO id mapping -> drop it: we do not persist a display name we can't map to
    //     a real channel id (it would be an inert allow-list entry that silently grants nothing). Fail loud.
    // A probe failure (auth/scope/network) produces no id mapping for the typed display names, so by the
    // same rule they are not persisted — only id-shaped references survive it — and the underlying reason
    // is surfaced. This mutates shared view-model/status state and persists, so it MUST run on the loop
    // thread (the inline add path awaits it; the background path marshals it there via InvokeAsync).
    // See .claude/skills/termina-tui-patterns.md ("Marshal the apply onto the loop") for why off-loop is unsafe.
    private void ReconcileResolvedChannels(
        ChannelType type,
        IReadOnlyList<string> stored,
        string? errorMessage,
        IEnumerable<(string Id, string Name)> resolved)
    {
        var byId = new HashSet<string>(StringComparer.Ordinal);
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in resolved)
        {
            byId.Add(id);
            byName.TryAdd(name, id);
        }

        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        var canonical = new List<string>(stored.Count);
        var dropped = new List<string>();
        foreach (var reference in stored)
        {
            if (byId.Contains(reference))
                canonical.Add(reference);                 // probe confirmed it as a channel id — keep
            else if (byName.TryGetValue(reference, out var id))
            {
                canonical.Add(id);                        // display name → its channel id
                remap[reference] = id;
            }
            else if (IsChannelId(type, reference))
                canonical.Add(reference);                 // id-shaped but the bot can't enumerate it now —
                                                          // keep: a real id is the stable key, never dropped
            else
                dropped.Add(reference);                   // a display name with no id mapping — fail loud
        }

        canonical = [.. canonical.Distinct(StringComparer.Ordinal)];
        var changed = !stored.SequenceEqual(canonical, StringComparer.Ordinal);
        if (changed)
        {
            SetChannelIds(type, canonical);
            RemapChannelAudiences(type, remap);
            RemapChannelMentionRequired(type, remap);
            UpdateAdapterPickerSummary(type);
            WriteChannelConfigToDisk();
            IsSaved.Value = true;
        }

        // Fail loud only when something is actually lost. A drop is the meaningful failure: name the
        // dropped channels and the reason — the probe error if there was one, else the usual checklist.
        // A probe error that dropped NOTHING (every reference is an id-shaped key the bot just couldn't
        // enrich with a display label) is benign: leave the prior status (e.g. the add's "Added …")
        // intact rather than masking a successful add with a lookup failure.
        if (dropped.Count > 0)
        {
            var reason = string.IsNullOrWhiteSpace(errorMessage)
                ? "check the name, that the bot is invited, and that it has channel-read scope"
                : errorMessage;
            Status.Value = new ConfigStatusMessage(
                $"Dropped {string.Join(", ", dropped.Select(static c => $"#{c}"))} — {reason}",
                ConfigStatusTone.Warning);
        }
        else if (changed)
        {
            Status.Value = new ConfigStatusMessage(
                $"Resolved {GetAdapterDisplayName(type)} channels to canonical IDs and saved.",
                ConfigStatusTone.Neutral);
        }
    }

    // Whether a typed reference is already the platform's canonical channel id (the stable ACL key) as
    // opposed to a human display name that must be resolved to one. Mirrors each platform's id format:
    // Slack C…/G…; Discord numeric snowflake; Mattermost 26-char base-32.
    private static bool IsChannelId(ChannelType type, string reference) => type switch
    {
        ChannelType.Slack => IsSlackChannelId(reference),
        ChannelType.Discord => IsDiscordChannelId(reference),
        ChannelType.Mattermost => IsMattermostChannelId(reference),
        _ => false
    };

    private static bool IsDiscordChannelId(string value)
        => value.Length is >= 17 and <= 20 && value.All(char.IsAsciiDigit);

    private static bool IsMattermostChannelId(string value)
        => value.Length == 26 && value.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c));

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

    // The mention rule is seeded on the typed reference (which may be a display name), so it must
    // move to the canonical channel id when the name resolves — otherwise BuildMentionRequiredMap
    // drops the rule because the name is no longer in the channel id list.
    private void RemapChannelMentionRequired(ChannelType type, IReadOnlyDictionary<string, string> remap)
    {
        if (remap.Count == 0 || !_channelMentionRequired.TryGetValue(type, out var mentionRequired))
            return;

        foreach (var (oldId, newId) in remap)
        {
            if (!mentionRequired.TryGetValue(oldId, out var required))
                continue;

            mentionRequired.Remove(oldId);
            mentionRequired.TryAdd(newId, required);
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
            case ChannelType.Teams:
                model.Teams.Enabled = true;
                model.Teams.TenantId = Normalize(TenantIdInput);
                model.Teams.ClientId = Normalize(ClientIdInput);
                model.Teams.BotId = Normalize(BotIdInput);
                model.Teams.ClientSecretDraft = Normalize(ClientSecretInput);
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
            ChannelType.Teams => new HashSet<string>(StringComparer.Ordinal)
            {
                ChannelsEditorFieldPaths.TeamsTenantId,
                ChannelsEditorFieldPaths.TeamsClientId,
                ChannelsEditorFieldPaths.TeamsBotId,
                ChannelsEditorFieldPaths.TeamsClientSecret,
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

    internal async Task ApplyResetConfirmationAsync(CancellationToken ct = default)
    {
        if (_resetConfirmIndex == 0)
        {
            Screen.Value = ChannelsConfigScreen.AdapterMenu;
            NotifyContentChanged();
            return;
        }

        // Cancel and await any in-flight Slack label refresh before persisting the reset and
        // rebuilding view-model state. A live background normalizer would otherwise write a stale
        // snapshot over the reset's config file, or clobber the just-reloaded view-model state — the
        // same race SaveAsync guards at its top. This path bypasses SaveAsync, so it needs the same
        // guard. Awaited (not blocked via .GetResult()) so it never freezes the Termina loop and
        // never deadlocks under a host that installs a SynchronizationContext (e.g. the xunit v3 test
        // runner). Dispatched fire-and-forget via ResetConfirmationFromInputAsync.
        // Cancelling the refresh also neuters its marshaled apply: the background path queues the reconcile on
        // the loop via InvokeAsync(..., ct), and that queued work re-checks ct and skips once cancellation
        // trips. So even though the apply is fire-and-forget (not awaited), no stale reconcile can re-add the
        // deleted channels on a later frame after this reset deletes the section.
        await CancelAndAwaitLabelRefreshAsync();

        var resetType = _activeAdapterType;
        var resetName = ActiveAdapterName;
        try
        {
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
            if (resetType == ChannelType.Teams)
                InvalidateTeamsDirectory();
            Screen.Value = ChannelsConfigScreen.Picker;
            Status.Value = new ConfigStatusMessage($"{resetName} reset saved.", ConfigStatusTone.Success);
            IsSaved.Value = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A disk-full / permission-denied write, or a malformed existing netclaw.json (the reload
            // deserializes it), must surface to the operator — not escape into the Termina event loop.
            // Stay on the confirmation screen so the reset can be retried. Mirrors the autosave paths,
            // which catch the same way inside ConfigAutosave.RunAsync; this reset path bypasses
            // SaveAsync, so it carries its own equivalent guard.
            Status.Value = new ConfigStatusMessage($"Could not save reset: {ex.Message}", ConfigStatusTone.Error);
        }
        catch (Exception ex)
        {
            // Fail LOUD on any other error (e.g. a type-malformed but JSON-valid config surfacing as
            // InvalidOperationException from the reload's deserialization). This runs as a fire-and-forget
            // chained write whose ChainAsync swallows a faulted prior task, so an unsurfaced throw here
            // would vanish with no operator feedback — exactly the silent fallback to avoid. Surface it
            // and stay on the confirmation screen for retry; catching here also keeps the chained task
            // from faulting, so subsequent writes are unaffected.
            Status.Value = new ConfigStatusMessage($"Could not reset {resetName}: {ex.Message}", ConfigStatusTone.Error);
        }

        NotifyContentChanged();
    }

    public void RequestQuit()
    {
        ShutdownRequestedForTest = true;
        Shutdown();
    }

    public override void Dispose()
    {
        IsGroupChatSaveInProgress = false;
        // Cancel any in-flight config write / label refresh, then DRAIN them before disposing the
        // reactive state they publish to. A fire-and-forget write resumes on a thread-pool continuation
        // (the loop has no SynchronizationContext), so without this a write could mutate a disposed
        // ReactiveProperty / Step after teardown. Cancellation makes the in-flight probe abort promptly;
        // the bounded Wait is a last-resort backstop so Dispose can never block the loop indefinitely on
        // a wedged probe (it returns false on timeout rather than throwing or hanging).
        _lifetimeCts.Cancel();
        CancelGroupChatSearch();
        _labelResolutionCts?.Cancel();
        try
        {
            Task.WhenAll(_pendingConfigWrite, _labelRefreshTask ?? Task.CompletedTask)
                .Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception drainFailure)
        {
            // A faulted/cancelled in-flight write has already surfaced its own status (autosave/reset
            // both catch and report); trace at debug level and let teardown complete regardless.
            Debug.WriteLine($"ChannelsConfig: in-flight write drain on dispose faulted: {drainFailure.Message}");
        }

        _labelResolutionCts?.Dispose();
        InvalidateTeamsDirectory();
        _lifetimeCts.Dispose();
        IsSaved.Dispose();
        Screen.Dispose();
        Status.Dispose();
        Step.Dispose();
        _context.Dispose();
        base.Dispose();
    }

    private void GoBackWithinManagement()
    {
        if (Screen.Value == ChannelsConfigScreen.TeamsChannelAccess)
            _editingChannelAccess = null;

        if (Screen.Value == ChannelsConfigScreen.GroupChats && _isGroupChatDiscovery)
            EndGroupChatDiscovery();

        if (Screen.Value == ChannelsConfigScreen.TeamsGroupChatSearch)
            EndGroupChatDiscovery();

        if (Screen.Value == ChannelsConfigScreen.TeamsPrincipalRemovalConfirm)
            _pendingPrincipalRemoval = null;

        if (Screen.Value == ChannelsConfigScreen.TeamsChannelPrincipalRemovalConfirm)
            _pendingChannelPrincipalRemoval = null;

        if (Screen.Value == ChannelsConfigScreen.TeamsDestinationRemovalConfirm)
            _pendingTeamsDestinationRemoval = null;

        if (Screen.Value is ChannelsConfigScreen.TeamsTeamSearch
            or ChannelsConfigScreen.TeamsChannelSearch
            or ChannelsConfigScreen.TeamsUserSearch
            or ChannelsConfigScreen.TeamsGroupSearch
            or ChannelsConfigScreen.TeamsGroupChatSearch)
        {
            _teamsDirectorySearch?.Invalidate();
        }

        if (Screen.Value is ChannelsConfigScreen.AllowedUsers or ChannelsConfigScreen.AllowedGroups)
            _teamsPrincipalSearchReturnScreen = null;

        Screen.Value = Screen.Value switch
        {
            ChannelsConfigScreen.AdapterMenu => ChannelsConfigScreen.Picker,
            ChannelsConfigScreen.TeamsDestinationAdd => ChannelsConfigScreen.AdapterMenu,
            ChannelsConfigScreen.TeamsPrincipalAdd => ChannelsConfigScreen.AdapterMenu,
            ChannelsConfigScreen.TeamsPrincipalManagement => ChannelsConfigScreen.AdapterMenu,
            ChannelsConfigScreen.TeamsPrincipalRemovalConfirm => ChannelsConfigScreen.TeamsPrincipalManagement,
            ChannelsConfigScreen.TeamsChannelPrincipalRemovalConfirm => ChannelsConfigScreen.TeamsChannelAccess,
            ChannelsConfigScreen.TeamsDestinationRemovalConfirm => ChannelsConfigScreen.ChannelPermissions,
            ChannelsConfigScreen.ChannelPermissions => ChannelsConfigScreen.AdapterMenu,
            ChannelsConfigScreen.AddChannel => ChannelsConfigScreen.ChannelPermissions,
            ChannelsConfigScreen.TeamsTeamSearch => ChannelsConfigScreen.ChannelPermissions,
            ChannelsConfigScreen.TeamsChannelSearch => ChannelsConfigScreen.TeamsTeamSearch,
            ChannelsConfigScreen.TeamsUserSearch when _editingChannelAccess is null && _teamsPrincipalSearchReturnScreen is { } returnScreen => ClearTeamsPrincipalSearchReturnScreen(returnScreen),
            ChannelsConfigScreen.TeamsGroupSearch when _editingChannelAccess is null && _teamsPrincipalSearchReturnScreen is { } returnScreen => ClearTeamsPrincipalSearchReturnScreen(returnScreen),
            ChannelsConfigScreen.TeamsUserSearch => _editingChannelAccess is null ? ChannelsConfigScreen.AdapterMenu : ChannelsConfigScreen.TeamsChannelAccess,
            ChannelsConfigScreen.TeamsGroupSearch => _editingChannelAccess is null ? ChannelsConfigScreen.AdapterMenu : ChannelsConfigScreen.TeamsChannelAccess,
            ChannelsConfigScreen.TeamsGroupChatSearch => ChannelsConfigScreen.TeamsDestinationAdd,
            ChannelsConfigScreen.TeamsChannelAccess => ChannelsConfigScreen.ChannelPermissions,
            ChannelsConfigScreen.AllowedUsers => _editingChannelAccess is null ? ChannelsConfigScreen.AdapterMenu : ChannelsConfigScreen.TeamsChannelAccess,
            ChannelsConfigScreen.AllowedGroups => _editingChannelAccess is null ? ChannelsConfigScreen.AdapterMenu : ChannelsConfigScreen.TeamsChannelAccess,
            ChannelsConfigScreen.GroupChats => ChannelsConfigScreen.AdapterMenu,
            ChannelsConfigScreen.Attachments => ChannelsConfigScreen.AdapterMenu,
            ChannelsConfigScreen.DirectoryStatus => ChannelsConfigScreen.AdapterMenu,
            ChannelsConfigScreen.DirectMessages => ChannelsConfigScreen.AdapterMenu,
            ChannelsConfigScreen.RotateCredentials => ChannelsConfigScreen.AdapterMenu,
            ChannelsConfigScreen.ResetConfirm => ChannelsConfigScreen.AdapterMenu,
            _ => ChannelsConfigScreen.Picker
        };

        Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
        NotifyContentChanged();
    }

    private void ClearTeamsEditContext()
    {
        _editingChannelAccess = null;
        _teamsPrincipalSearchReturnScreen = null;
        EndGroupChatDiscovery();
    }

    private void EndGroupChatDiscovery()
    {
        CancelGroupChatSearch();
        _groupChatSearchProgress = null;
        _isGroupChatDiscovery = false;
        _hasSearchedGroupChats = false;
        _groupChatSearchResults = [];
        _groupChatContinuation = null;
        _groupChatSearchInput = null;
    }

    private ChannelsConfigScreen ClearTeamsPrincipalSearchReturnScreen(ChannelsConfigScreen returnScreen)
    {
        _teamsPrincipalSearchReturnScreen = null;
        return returnScreen;
    }

    private void SetActiveAdapterEnabled(bool enabled)
    {
        var selectedIndex = GetAdapterIndex(_activeAdapterType);

        if (Step.IsAdapterEnabled(_activeAdapterType) != enabled)
            Step.ToggleAdapter(selectedIndex);

        UpdateAdapterPickerSummary(_activeAdapterType);

        AutosaveCompletedAction($"{ActiveAdapterName} {(enabled ? "enabled" : "disabled")} and saved.");
    }

    // Fire-and-forget autosave from a synchronous Termina key handler: the calling handler already
    // mutated VM state on the loop thread; this enqueues only the persist (disk write + reload) so it
    // is serialized behind any in-flight write and never blocks the loop. Autosave skips the blocking
    // channel-access probe (the background label refresh re-validates instead).
    private void AutosaveCompletedAction(string successMessage)
        => _ = EnqueueConfigWriteAsync(() => SaveCompletedAsync(successMessage, _lifetimeCts.Token));

    // Awaitable autosave for callers already running inside an enqueued write (ApplyAddChannelAsync),
    // where re-enqueueing would deadlock the op behind itself. Returns whether the save succeeded.
    private Task<bool> SaveCompletedAsync(string successMessage, CancellationToken ct = default)
        => SaveViaAutosaveAsync(successMessage, probeChannelAccess: false, ct);

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
        _channelMentionRequired.Clear();
        AddAudienceDraft(ChannelType.Slack, draft.Slack.ChannelAudiences);
        AddAudienceDraft(ChannelType.Discord, draft.Discord.ChannelAudiences);
        AddAudienceDraft(ChannelType.Mattermost, draft.Mattermost.ChannelAudiences);
        AddTeamsAudienceDraft(draft.Teams.ChannelAudienceOverrides);
        AddMentionRequiredDraft(ChannelType.Slack, draft.Slack.MentionRequiredInThreadByChannel);
        AddMentionRequiredDraft(ChannelType.Discord, draft.Discord.MentionRequiredInThreadByChannel);
        AddMentionRequiredDraft(ChannelType.Mattermost, draft.Mattermost.MentionRequiredInThreadByChannel);
    }

    private void AddAudienceDraft(ChannelType type, IReadOnlyDictionary<string, TrustAudience> audiences)
    {
        if (audiences.Count == 0)
            return;

        _channelAudiences[type] = new Dictionary<string, TrustAudience>(audiences, StringComparer.Ordinal);
    }

    private void AddTeamsAudienceDraft(IReadOnlyList<TeamsChannelAudienceOverride> overrides)
    {
        var exactOverrides = overrides
            .Where(static audienceOverride => !string.IsNullOrWhiteSpace(audienceOverride.ChannelId)
                                             && SecurityPolicyDefaults.TryParseAudience(audienceOverride.Audience, out _))
            .GroupBy(static audienceOverride => audienceOverride.ChannelId!, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .ToDictionary(
                static group => group.Key,
                static group =>
                {
                    SecurityPolicyDefaults.TryParseAudience(group.Single().Audience, out var audience);
                    return audience;
                },
                StringComparer.Ordinal);
        AddAudienceDraft(ChannelType.Teams, exactOverrides);
    }

    private void AddMentionRequiredDraft(ChannelType type, IReadOnlyDictionary<string, bool> mentionRequired)
    {
        if (mentionRequired.Count == 0)
            return;

        _channelMentionRequired[type] = new Dictionary<string, bool>(mentionRequired, StringComparer.Ordinal);
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
        if (type == ChannelType.Teams)
            TrySetTeamsChannelAudience(channelId, audience, teamId: null);
    }

    private bool TrySetTeamsChannelAudience(string channelId, TrustAudience audience, string? teamId)
    {
        var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
        var resolvedTeamId = teamId;
        if (string.IsNullOrWhiteSpace(resolvedTeamId))
        {
            var candidates = teams.ChannelAudienceOverrides
                .Where(audienceOverride => string.Equals(audienceOverride.ChannelId, channelId, StringComparison.Ordinal))
                .Select(static audienceOverride => audienceOverride.TeamId)
                .Concat(teams.ChannelAccessOverrides
                    .Where(accessOverride => string.Equals(accessOverride.ChannelId, channelId, StringComparison.Ordinal))
                    .Select(static accessOverride => accessOverride.TeamId))
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length == 1)
                resolvedTeamId = candidates[0];
            else
            {
                var configuredTeams = GetTeamIds();
                if (configuredTeams.Count == 1)
                    resolvedTeamId = configuredTeams[0];
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedTeamId))
            return false;

        teams.ChannelAudienceOverrides =
        [
            .. teams.ChannelAudienceOverrides.Where(audienceOverride =>
                !string.Equals(audienceOverride.TeamId, resolvedTeamId, StringComparison.Ordinal)
                || !string.Equals(audienceOverride.ChannelId, channelId, StringComparison.Ordinal)),
            new TeamsChannelAudienceOverride
            {
                TeamId = resolvedTeamId,
                ChannelId = channelId,
                Audience = audience.ToWireValue()
            }
        ];
        return true;
    }

    private bool GetChannelMentionRequired(ChannelType type, string channelId)
        => type == ChannelType.Teams
            ? Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).MentionOnly
            : _channelMentionRequired.TryGetValue(type, out var mentionRequired)
              && mentionRequired.TryGetValue(channelId, out var required)
              && required;

    private void SetChannelMentionRequired(ChannelType type, string channelId, bool value)
    {
        if (!_channelMentionRequired.TryGetValue(type, out var mentionRequired))
        {
            mentionRequired = new Dictionary<string, bool>(StringComparer.Ordinal);
            _channelMentionRequired[type] = mentionRequired;
        }

        mentionRequired[channelId] = value;
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
        ChannelType.Teams => ChannelCsv.ParseCsv(Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).ChannelIdsInput, trimHash: false),
        _ => []
    };

    private IReadOnlyList<string> GetTeamIds()
        => ChannelCsv.ParseCsv(
            Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).TeamIdsInput,
            trimHash: false);

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
            case ChannelType.Teams:
                Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).ChannelIdsInput = value;
                break;
        }
    }

    private void SetTeamIds(IReadOnlyList<string> teamIds)
        => Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).TeamIdsInput =
            ChannelCsv.JoinOrNull(teamIds.Distinct(StringComparer.Ordinal).ToArray());

    private IReadOnlyList<string> GetAllowedUserIds(ChannelType type) => type switch
    {
        ChannelType.Slack => ChannelCsv.ParseCsv(Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack).AllowedUserIdsInput, trimHash: false),
        ChannelType.Discord => ChannelCsv.ParseCsv(Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord).AllowedUserIdsInput, trimHash: false),
        ChannelType.Mattermost => ChannelCsv.ParseCsv(Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost).AllowedUserIdsInput, trimHash: false),
        ChannelType.Teams => ChannelCsv.ParseCsv(Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).AllowedUserIdsInput, trimHash: false),
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
            case ChannelType.Teams:
                Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).AllowedUserIdsInput = value;
                break;
        }
    }

    private IReadOnlyList<string> GetAllowedGroupIds(ChannelType type)
        => type == ChannelType.Teams
            ? ChannelCsv.ParseCsv(Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).AllowedGroupIdsInput, trimHash: false)
            : [];

    private void SetAllowedGroupIds(ChannelType type, IReadOnlyList<string> groupIds)
    {
        if (type == ChannelType.Teams)
            Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).AllowedGroupIdsInput = ChannelCsv.JoinOrNull(groupIds);
    }

    private bool GetAllowDirectMessages(ChannelType type) => type switch
    {
        ChannelType.Slack => Step.GetAdapterViewModel<SlackStepViewModel>(ChannelType.Slack).AllowDirectMessages,
        ChannelType.Discord => Step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord).AllowDirectMessages,
        ChannelType.Mattermost => Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost).AllowDirectMessages,
        ChannelType.Teams => Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).AllowDirectMessages,
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
            case ChannelType.Teams:
                Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).AllowDirectMessages = enabled;
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
            ChannelType.Teams when key == "secret" && Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams).HasPersistedClientSecret =>
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
            ChannelType.Teams => HasCompleteTeamsConnection()
                ? "connection configured"
                : "connection incomplete",
            _ => "credentials unknown"
        };
    }

    private bool HasCompleteTeamsConnection()
    {
        var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
        return !string.IsNullOrWhiteSpace(GetEffectiveTeamsSetting("TenantId", teams.TenantId))
               && !string.IsNullOrWhiteSpace(GetEffectiveTeamsSetting("ClientId", teams.ClientId))
               && !string.IsNullOrWhiteSpace(GetEffectiveTeamsSetting("BotId", teams.BotId))
               && !string.IsNullOrWhiteSpace(GetEffectiveTeamsClientSecret(teams));
    }

    // The daemon treats NETCLAW_* variables as the highest-priority source. The
    // TUI's live Graph lookup must use that same connection, otherwise a stale
    // secrets.json value can make discovery fail while the deployed connector works.
    private string? GetEffectiveTeamsSetting(string name, string? persistedValue)
    {
        var environmentValue = Normalize(_environmentVariableReader($"NETCLAW_Teams__{name}"));
        return string.IsNullOrWhiteSpace(environmentValue) ? Normalize(persistedValue) : environmentValue;
    }

    private string? GetEffectiveTeamsClientSecret(TeamsStepViewModel teams)
        => GetEffectiveTeamsSetting(
            "ClientSecret",
            GetEffectiveSecret("Teams.ClientSecret", teams.ClientSecret, teams.HasPersistedClientSecret));

    private static string GetAdapterDisplayName(ChannelType type) => type switch
    {
        ChannelType.Slack => "Slack",
        ChannelType.Discord => "Discord",
        ChannelType.Mattermost => "Mattermost",
        ChannelType.Teams => "Microsoft Teams",
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
            ChannelType.Mattermost => FormatMattermostChannelLabel(channelId),
            ChannelType.Teams => FormatTeamsChannelLabel(channelId),
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

    private string FormatMattermostChannelLabel(string channelId)
    {
        var mattermost = Step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost);
        var resolved = mattermost.LastChannelResolution?.Resolved.FirstOrDefault(channel =>
            string.Equals(channel.ChannelId, channelId, StringComparison.Ordinal));
        if (resolved is null)
            return channelId;

        // Mattermost exposes a human display name and a url slug; prefer the display name, fall back to
        // the #slug, and only show the opaque id when neither resolved.
        return !string.IsNullOrWhiteSpace(resolved.DisplayName)
            ? resolved.DisplayName
            : $"#{resolved.ChannelName}";
    }

    private static string FormatTeamsChannelLabel(TeamsDirectoryTeam team, TeamsDirectoryChannel channel)
    {
        var teamLabel = string.IsNullOrWhiteSpace(team.DisplayName) ? "Microsoft Teams" : team.DisplayName;
        var channelLabel = string.IsNullOrWhiteSpace(channel.DisplayName) ? "Channel" : channel.DisplayName;
        return $"{teamLabel} / {channelLabel}";
    }

    private string FormatTeamsChannelLabel(string channelId)
    {
        if (!TryResolveTeamsTeamId(channelId, out var teamId))
            return $"Teams channel {AbbreviateIdentifier(channelId)}";

        _teamsById.TryGetValue(teamId, out var team);
        _teamsChannelsByIdentity.TryGetValue(TeamsChannelIdentity(teamId, channelId), out var channel);
        if (team is null && channel is null)
            return $"Teams channel {AbbreviateIdentifier(channelId)}";

        var teamLabel = string.IsNullOrWhiteSpace(team?.DisplayName) ? "Microsoft Teams" : team.DisplayName;
        var channelLabel = string.IsNullOrWhiteSpace(channel?.DisplayName)
            ? AbbreviateIdentifier(channelId)
            : channel.DisplayName;
        return $"{teamLabel} / {channelLabel}";
    }

    private bool TryResolveTeamsTeamId(string channelId, out string teamId)
    {
        if (_teamsChannelTeamIds.TryGetValue(channelId, out var cachedTeamId)
            && !string.IsNullOrWhiteSpace(cachedTeamId))
        {
            teamId = cachedTeamId;
            return true;
        }

        var teams = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
        var candidates = teams.ChannelAudienceOverrides
            .Where(audienceOverride => string.Equals(audienceOverride.ChannelId, channelId, StringComparison.Ordinal))
            .Select(static audienceOverride => audienceOverride.TeamId)
            .Concat(teams.ChannelAccessOverrides
                .Where(accessOverride => string.Equals(accessOverride.ChannelId, channelId, StringComparison.Ordinal))
                .Select(static accessOverride => accessOverride.TeamId))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 1)
        {
            teamId = candidates[0];
            return true;
        }

        var configuredTeams = GetTeamIds();
        if (configuredTeams.Count == 1)
        {
            teamId = configuredTeams[0];
            return true;
        }

        teamId = string.Empty;
        return false;
    }

    private static string TeamsChannelIdentity(string teamId, string channelId) => teamId + "\n" + channelId;

    private static bool TryNormalizeEntraObjectIds(IReadOnlyList<string> ids, out List<string> normalized)
    {
        normalized = [];
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!Guid.TryParse(id, out var parsed))
                return false;

            var canonical = parsed.ToString("D");
            if (unique.Add(canonical))
                normalized.Add(canonical);
        }

        return true;
    }

    private int GetGroupChatSearchResultCount()
        => GroupChatSearchAdvancedIndex + 1;

    internal bool IsAdvancedTeamsDirectoryActionSelected() => Screen.Value switch
    {
        ChannelsConfigScreen.TeamsTeamSearch => _directoryResultIndex == _teamSearchResults.Count,
        ChannelsConfigScreen.TeamsChannelSearch => _directoryResultIndex == _channelSearchResults.Count,
        ChannelsConfigScreen.TeamsUserSearch => _directoryResultIndex == _userSearchResults.Count,
        ChannelsConfigScreen.TeamsGroupSearch => _directoryResultIndex == _groupSearchResults.Count,
        ChannelsConfigScreen.TeamsGroupChatSearch => _directoryResultIndex == GroupChatSearchAdvancedIndex,
        _ => false
    };

    private static string AbbreviateIdentifier(string value)
    {
        const int visibleLength = 8;
        if (string.IsNullOrWhiteSpace(value))
            return "unavailable";

        var trimmed = value.Trim();
        return trimmed.Length <= visibleLength * 2
            ? trimmed
            : $"{trimmed[..visibleLength]}…{trimmed[^visibleLength..]}";
    }

    internal static string GetGroupChatDisplaySuffix(string chatId)
    {
        var separator = chatId.IndexOf('@', StringComparison.Ordinal);
        var opaqueId = separator < 0 ? chatId : chatId[..separator];
        return opaqueId.Length <= 8 ? opaqueId : opaqueId[^8..];
    }

    private static string FormatTeamsUserLabel(TeamsDirectoryUser user)
    {
        var principal = user.UserPrincipalName ?? user.Mail;
        return !string.IsNullOrWhiteSpace(user.DisplayName) && !string.IsNullOrWhiteSpace(principal)
            ? $"{user.DisplayName} <{principal}>"
            : principal ?? user.DisplayName ?? "selected user";
    }

    private string FormatTeamsUserLabel(string userId)
        => _teamsUsersById.TryGetValue(userId, out var user)
            ? FormatTeamsUserLabel(user)
            : AbbreviateIdentifier(userId);

    private static string FormatTeamsGroupLabel(TeamsDirectoryGroup group)
    {
        var label = group.DisplayName ?? group.Mail ?? "selected group";
        return $"{label} ({group.Kind})";
    }

    private string FormatTeamsGroupLabel(string groupId)
        => _teamsGroupsById.TryGetValue(groupId, out var group)
            ? FormatTeamsGroupLabel(group)
            : AbbreviateIdentifier(groupId);

    private string FormatTeamsGroupChatLabel(string chatId)
    {
        if (!_teamsGroupChatsById.TryGetValue(chatId, out var chat))
            return GetGroupChatDisplaySuffix(chatId);

        var display = !string.IsNullOrWhiteSpace(chat.Topic)
            ? chat.Topic
            : chat.ParticipantPreview.Count > 0
                ? string.Join(", ", chat.ParticipantPreview)
                : "Group Chat";
        return $"{display} · {GetGroupChatDisplaySuffix(chatId)}";
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
        if (type == ChannelType.Teams)
        {
            if (TryGetTeamsDirectorySearch() is null || _teamsDirectory is null)
                return;

            _labelResolutionCts?.Cancel();
            _labelResolutionCts?.Dispose();
            _labelResolutionCts = new CancellationTokenSource();
            _labelRefreshTask = RefreshTeamsChannelLabelsInBackgroundAsync(_teamsDirectory, _labelResolutionCts.Token);
            return;
        }

        if (type is not (ChannelType.Slack or ChannelType.Discord or ChannelType.Mattermost))
            return;

        _labelResolutionCts?.Cancel();
        _labelResolutionCts?.Dispose();
        _labelResolutionCts = new CancellationTokenSource();
        // Fire-and-forget: probe off-loop, then marshal the reconcile onto the loop via InvokeAsync. This
        // path must NOT reconcile in its continuation (that is the render/edit race this fix closes).
        _labelRefreshTask = RefreshChannelLabelsInBackgroundAsync(type, _labelResolutionCts.Token);
    }

    private async Task RefreshTeamsChannelLabelsInBackgroundAsync(ITeamsDirectory directory, CancellationToken ct)
    {
        if (!Step.IsAdapterEnabled(ChannelType.Teams))
            return;

        var channelIds = GetChannelIds(ChannelType.Teams);
        var mappings = channelIds
            .Select(channelId => TryResolveTeamsTeamId(channelId, out var teamId)
                ? new TeamsSavedChannel(teamId, channelId)
                : default(TeamsSavedChannel?))
            .OfType<TeamsSavedChannel>()
            .Distinct()
            .ToArray();
        var unmappedChannelIds = channelIds
            .Except(mappings.Select(static mapping => mapping.ChannelId), StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var configuredTeamIds = GetTeamIds();
        var teamsDraft = Step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams);
        var allowedUsers = GetAllowedUserIds(ChannelType.Teams)
            .Concat(teamsDraft.ChannelAccessOverrides.SelectMany(static access => access.AllowedUserIds))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var allowedGroups = GetAllowedGroupIds(ChannelType.Teams)
            .Concat(teamsDraft.ChannelAccessOverrides.SelectMany(static access => access.AllowedGroupIds))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var groupChatIds = ChannelCsv.ParseCsv(teamsDraft.AllowedGroupChatIdsInput,
            trimHash: false);
        if (mappings.Length == 0
            && (unmappedChannelIds.Count == 0 || configuredTeamIds.Count == 0)
            && allowedUsers.Length == 0
            && allowedGroups.Length == 0
            && groupChatIds.Count == 0)
            return;

        var teams = new ConcurrentDictionary<string, TeamsDirectoryTeam>(StringComparer.Ordinal);
        var channels = new ConcurrentDictionary<string, TeamsDirectoryChannel>(StringComparer.Ordinal);
        var users = new ConcurrentDictionary<string, TeamsDirectoryUser>(StringComparer.Ordinal);
        var groups = new ConcurrentDictionary<string, TeamsDirectoryGroup>(StringComparer.Ordinal);
        var groupChats = new ConcurrentDictionary<string, TeamsDirectoryGroupChat>(StringComparer.Ordinal);
        var legacyCandidates = new ConcurrentDictionary<string, ConcurrentDictionary<string, TeamsDirectoryChannel>>(StringComparer.Ordinal);
        var directoryRequestCount = 0;
        var availableRequestCount = 0;
        try
        {
            if (unmappedChannelIds.Count > 0 && configuredTeamIds.Count > 0)
            {
                await Parallel.ForEachAsync(
                    configuredTeamIds,
                    new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
                    async (teamId, token) =>
                    {
                        Interlocked.Increment(ref directoryRequestCount);
                        var result = await directory.GetChannelsAsync(teamId, TeamsGraphSearchLimits.MaximumResults, token).ConfigureAwait(false);
                        if (!result.IsAvailable || result.Value is null)
                            return;

                        Interlocked.Increment(ref availableRequestCount);

                        foreach (var channel in result.Value.Where(channel => unmappedChannelIds.Contains(channel.Id)))
                        {
                            var candidates = legacyCandidates.GetOrAdd(
                                channel.Id,
                                static _ => new ConcurrentDictionary<string, TeamsDirectoryChannel>(StringComparer.Ordinal));
                            candidates[channel.TeamId] = channel;
                        }
                    }).ConfigureAwait(false);
            }

            var legacyMappings = legacyCandidates
                .Where(static candidate => candidate.Value.Count == 1)
                .Select(static candidate => new TeamsSavedChannel(candidate.Value.Single().Key, candidate.Key))
                .ToArray();
            foreach (var mapping in legacyMappings)
            {
                var channel = legacyCandidates[mapping.ChannelId][mapping.TeamId];
                channels[TeamsChannelIdentity(mapping.TeamId, mapping.ChannelId)] = channel;
            }

            var resolvedMappings = mappings.Concat(legacyMappings).Distinct().ToArray();
            await Parallel.ForEachAsync(
                resolvedMappings.Select(static mapping => mapping.TeamId).Distinct(StringComparer.Ordinal),
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
                async (teamId, token) =>
                {
                    Interlocked.Increment(ref directoryRequestCount);
                    var result = await directory.GetTeamAsync(teamId, token).ConfigureAwait(false);
                    if (result.IsAvailable && result.Value is not null)
                    {
                        Interlocked.Increment(ref availableRequestCount);
                        teams[result.Value.Id] = result.Value;
                    }
                }).ConfigureAwait(false);

            await Parallel.ForEachAsync(
                mappings,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
                async (mapping, token) =>
                {
                    Interlocked.Increment(ref directoryRequestCount);
                    var result = await directory.GetChannelAsync(mapping.TeamId, mapping.ChannelId, token).ConfigureAwait(false);
                    if (result.IsAvailable && result.Value is not null)
                    {
                        Interlocked.Increment(ref availableRequestCount);
                        channels[TeamsChannelIdentity(result.Value.TeamId, result.Value.Id)] = result.Value;
                    }
                }).ConfigureAwait(false);

            await Parallel.ForEachAsync(
                allowedUsers,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
                async (userId, token) =>
                {
                    Interlocked.Increment(ref directoryRequestCount);
                    var result = await directory.GetUserAsync(userId, token).ConfigureAwait(false);
                    if (result.IsAvailable && result.Value is not null)
                    {
                        Interlocked.Increment(ref availableRequestCount);
                        users[result.Value.Id] = result.Value;
                    }
                }).ConfigureAwait(false);

            await Parallel.ForEachAsync(
                allowedGroups,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
                async (groupId, token) =>
                {
                    Interlocked.Increment(ref directoryRequestCount);
                    var result = await directory.GetGroupAsync(groupId, token).ConfigureAwait(false);
                    if (result.IsAvailable && result.Value is not null)
                    {
                        Interlocked.Increment(ref availableRequestCount);
                        groups[result.Value.Id] = result.Value;
                    }
                }).ConfigureAwait(false);

            await Parallel.ForEachAsync(
                groupChatIds,
                new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
                async (chatId, token) =>
                {
                    Interlocked.Increment(ref directoryRequestCount);
                    var result = await directory.GetGroupChatAsync(chatId, token).ConfigureAwait(false);
                    if (result.IsAvailable && result.Value is not null)
                    {
                        Interlocked.Increment(ref availableRequestCount);
                        groupChats[result.Value.Id] = result.Value;
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }

        if (ct.IsCancellationRequested || directoryRequestCount == 0)
            return;

        _ = InvokeAsync(
            () =>
            {
                if (ct.IsCancellationRequested)
                    return;

                _teamsDirectoryLabelsAvailable = availableRequestCount > 0;
                foreach (var (teamId, team) in teams)
                    _teamsById[teamId] = team;
                foreach (var (identity, channel) in channels)
                    _teamsChannelsByIdentity[identity] = channel;
                foreach (var (userId, user) in users)
                    _teamsUsersById[userId] = user;
                foreach (var (groupId, group) in groups)
                    _teamsGroupsById[groupId] = group;
                foreach (var (chatId, groupChat) in groupChats)
                    _teamsGroupChatsById[chatId] = groupChat;
                foreach (var candidate in legacyCandidates.Where(static candidate => candidate.Value.Count == 1))
                    _teamsChannelTeamIds[candidate.Key] = candidate.Value.Single().Key;
                NotifyContentChanged();
            },
            ct);
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

        // The tracked task completes as soon as the off-loop PROBE unwinds — the apply is marshaled onto the
        // loop fire-and-forget, not awaited here, precisely so this wait can't block on a loop turn and defer
        // the caller's own disk write. ProbeChannelLabelsAsync catches its own exceptions (returning a
        // Failed/null result), so awaiting completes without throwing. Cancelling above guarantees any apply the
        // refresh already queued re-checks ct and skips, so once this returns no resumed probe or stale apply
        // can clobber the just-reloaded state or write off-loop.
        await inFlight;

        _labelRefreshTask = null;
    }

    private readonly record struct TeamsSavedChannel(string TeamId, string ChannelId);

    // Fail CLOSED to Public on a corrupt posture via the shared reader rather than throwing into this
    // view-model's constructor. The Security editor (the posture's owner) surfaces the corruption;
    // Channels only consumes posture for channel/DM audience ACL defaults, where Public is the safe
    // restrictive default. Throwing here previously made the entire Channels page inaccessible on a
    // value the Security page reads without crashing.
    private static DeploymentPosture LoadDeploymentPosture(NetclawPaths paths)
    {
        DeploymentPostureReader.TryRead(ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath), out var posture, out _);
        return posture;
    }
}

internal enum ChannelsConfigScreen
{
    Picker,
    AdapterMenu,
    ChannelPermissions,
    AddChannel,
    TeamsDestinationAdd,
    TeamsPrincipalAdd,
    TeamsPrincipalManagement,
    TeamsPrincipalRemovalConfirm,
    TeamsChannelPrincipalRemovalConfirm,
    TeamsDestinationRemovalConfirm,
    TeamsTeamSearch,
    TeamsChannelSearch,
    TeamsUserSearch,
    TeamsGroupSearch,
    TeamsGroupChatSearch,
    TeamsChannelAccess,
    AllowedUsers,
    AllowedGroups,
    GroupChats,
    Attachments,
    DirectoryStatus,
    DirectMessages,
    RotateCredentials,
    ResetConfirm
}

internal enum ChannelsManagementAction
{
    ManageChannels,
    AddChannel,
    ManageUsers,
    AddPrincipals,
    ManagePrincipals,
    ManageGroups,
    ManageGroupChats,
    ManageAttachments,
    DirectoryStatus,
    DirectMessages,
    RotateCredentials,
    ToggleEnabled,
    ResetConnection,
    Done
}

internal sealed record ChannelsManagementMenuItem(
    ChannelsManagementAction Action,
    string Label,
    string Description);

internal enum TeamsPrincipalKind
{
    User,
    Group
}

internal sealed record TeamsPrincipalRow(
    string Id,
    TeamsPrincipalKind Kind,
    string Label,
    string Scope);

internal sealed record TeamsChannelPrincipalRemoval(
    string TeamId,
    string ChannelId,
    string PrincipalId,
    TeamsPrincipalKind Kind,
    string Label);

internal sealed record ChannelPermissionRow(
    string Id,
    string DisplayName,
    TrustAudience Audience,
    bool IsDirectMessage,
    bool IsAddAction,
    bool IsDoneAction,
    bool IsUnresolved = false,
    bool MentionRequired = false,
    bool IsGroupChat = false)
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
            [ChannelType.Mattermost] = new ChannelPersistenceSpec("Mattermost", ["Mattermost.BotToken"]),
            [ChannelType.Teams] = new ChannelPersistenceSpec("Teams", ["Teams.ClientSecret"])
        };

    internal ChannelsConfigDraft Load(NetclawPaths paths)
    {
        var config = ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);
        var secrets = ConfigFileHelper.LoadJsonDict(paths.SecretsPath);
        var draft = new ChannelsConfigDraft
        {
            Slack = LoadSlack(paths, config, secrets),
            Discord = LoadDiscord(paths, config, secrets),
            Mattermost = LoadMattermost(paths, config, secrets),
            Teams = LoadTeams(paths, config, secrets)
        };

        AddKnownProvider(draft.KnownProviders, ChannelType.Slack, draft.Slack.IsKnown);
        AddKnownProvider(draft.KnownProviders, ChannelType.Discord, draft.Discord.IsKnown);
        AddKnownProvider(draft.KnownProviders, ChannelType.Mattermost, draft.Mattermost.IsKnown);
        AddKnownProvider(draft.KnownProviders, ChannelType.Teams, draft.Teams.IsKnown);
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

        step.LoadAdapterState(
            ChannelType.Teams,
            draft.Teams.Enabled,
            BuildSummary(draft.Teams),
            vm => ApplyTeams((TeamsStepViewModel)vm, draft.Teams),
            draft.Teams.IsKnown);
    }

    internal SectionContribution BuildContribution(
        ChannelPickerStepViewModel step,
        IReadOnlySet<ChannelType> knownProviders,
        IReadOnlyDictionary<ChannelType, Dictionary<string, TrustAudience>> channelAudiences,
        IReadOnlyDictionary<ChannelType, Dictionary<string, bool>> channelMentionRequired,
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
            channelMentionRequired,
            posture);
        AddDiscordContribution(
            fields,
            secrets,
            step.GetAdapterViewModel<DiscordStepViewModel>(ChannelType.Discord),
            step.IsAdapterEnabled(ChannelType.Discord),
            knownProviders.Contains(ChannelType.Discord),
            channelAudiences,
            channelMentionRequired,
            posture);
        AddMattermostContribution(
            fields,
            secrets,
            step.GetAdapterViewModel<MattermostStepViewModel>(ChannelType.Mattermost),
            step.IsAdapterEnabled(ChannelType.Mattermost),
            knownProviders.Contains(ChannelType.Mattermost),
            channelAudiences,
            channelMentionRequired,
            posture);
        AddTeamsContribution(
            fields,
            secrets,
            step.GetAdapterViewModel<TeamsStepViewModel>(ChannelType.Teams),
            step.IsAdapterEnabled(ChannelType.Teams),
            knownProviders.Contains(ChannelType.Teams));

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
            ChannelAudiences = GetChannelAudiences(config, "Slack.ChannelAudiences"),
            MentionRequiredInThreadByChannel = GetChannelMentionRequired(config, "Slack.MentionRequiredInThreadByChannel")
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
            ChannelAudiences = GetChannelAudiences(config, "Discord.ChannelAudiences"),
            MentionRequiredInThreadByChannel = GetChannelMentionRequired(config, "Discord.MentionRequiredInThreadByChannel")
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
            ChannelAudiences = GetChannelAudiences(config, "Mattermost.ChannelAudiences"),
            MentionRequiredInThreadByChannel = GetChannelMentionRequired(config, "Mattermost.MentionRequiredInThreadByChannel")
        };
    }

    private static TeamsChannelDraft LoadTeams(
        NetclawPaths paths,
        Dictionary<string, object> config,
        Dictionary<string, object> secrets)
    {
        var hasClientSecret = HasSecret(paths, secrets, "Teams.ClientSecret");
        var sectionPresent = SectionPresent(config, "Teams");
        return new TeamsChannelDraft
        {
            IsKnown = sectionPresent || hasClientSecret,
            Enabled = sectionPresent && GetBool(config, "Teams.Enabled", defaultValue: false),
            HasPersistedClientSecret = hasClientSecret,
            TenantId = GetString(config, "Teams.TenantId"),
            ClientId = GetString(config, "Teams.ClientId"),
            BotId = GetString(config, "Teams.BotId"),
            TeamIds = GetStringArray(config, "Teams.AllowedTeamIds"),
            ChannelIds = GetStringArray(config, "Teams.AllowedChannelIds"),
            AllowDirectMessages = GetBool(config, "Teams.AllowDirectMessages", defaultValue: false),
            AllowGroupChats = GetBool(config, "Teams.AllowGroupChats", defaultValue: false),
            AllowAttachments = GetBool(config, "Teams.AllowAttachments", defaultValue: false),
            AllowedGroupChatIds = GetStringArray(config, "Teams.AllowedGroupChatIds"),
            MentionOnly = GetBool(config, "Teams.MentionOnly", defaultValue: true),
            AllowedUserIds = GetStringArray(config, "Teams.AllowedUserIds"),
            AllowedGroupIds = GetStringArray(config, "Teams.AllowedGroupIds"),
            ChannelAudienceOverrides = GetTeamsChannelAudienceOverrides(config),
            ChannelAccessOverrides = GetTeamsChannelAccessOverrides(config)
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

    private static void ApplyTeams(TeamsStepViewModel vm, TeamsChannelDraft draft)
    {
        vm.TeamsEnabled = draft.Enabled;
        vm.TenantId = draft.TenantId;
        vm.ClientId = draft.ClientId;
        vm.BotId = draft.BotId;
        vm.ClientSecret = null;
        vm.HasPersistedClientSecret = draft.HasPersistedClientSecret;
        vm.TeamIdsInput = ChannelCsv.JoinOrNull(draft.TeamIds);
        vm.ChannelIdsInput = ChannelCsv.JoinOrNull(draft.ChannelIds);
        vm.AllowDirectMessages = draft.AllowDirectMessages;
        vm.AllowGroupChats = draft.AllowGroupChats;
        vm.AllowAttachments = draft.AllowAttachments;
        vm.AllowedGroupChatIdsInput = ChannelCsv.JoinOrNull(draft.AllowedGroupChatIds);
        vm.MentionOnly = draft.MentionOnly;
        vm.AllowedUserIdsInput = ChannelCsv.JoinOrNull(draft.AllowedUserIds);
        vm.AllowedGroupIdsInput = ChannelCsv.JoinOrNull(draft.AllowedGroupIds);
        vm.ChannelAudienceOverrides = [.. draft.ChannelAudienceOverrides];
        vm.ChannelAccessOverrides = [.. draft.ChannelAccessOverrides];
    }

    private static void AddSlackContribution(
        List<SectionFieldAction> fields,
        List<SectionSecretAction> secrets,
        SlackStepViewModel vm,
        bool enabled,
        bool knownProvider,
        IReadOnlyDictionary<ChannelType, Dictionary<string, TrustAudience>> channelAudiences,
        IReadOnlyDictionary<ChannelType, Dictionary<string, bool>> channelMentionRequired,
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
        SetBoolDictionaryOrDelete(fields, "Slack.MentionRequiredInThreadByChannel", BuildMentionRequiredMap(ChannelType.Slack, channelIds, channelMentionRequired));
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
        IReadOnlyDictionary<ChannelType, Dictionary<string, bool>> channelMentionRequired,
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
        SetBoolDictionaryOrDelete(fields, "Discord.MentionRequiredInThreadByChannel", BuildMentionRequiredMap(ChannelType.Discord, channelIds, channelMentionRequired));
        AddSecretPreserveOrSet(secrets, "Discord.BotToken", vm.BotToken, vm.HasPersistedBotToken);
    }

    private static void AddMattermostContribution(
        List<SectionFieldAction> fields,
        List<SectionSecretAction> secrets,
        MattermostStepViewModel vm,
        bool enabled,
        bool knownProvider,
        IReadOnlyDictionary<ChannelType, Dictionary<string, TrustAudience>> channelAudiences,
        IReadOnlyDictionary<ChannelType, Dictionary<string, bool>> channelMentionRequired,
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
        SetBoolDictionaryOrDelete(fields, "Mattermost.MentionRequiredInThreadByChannel", BuildMentionRequiredMap(ChannelType.Mattermost, channelIds, channelMentionRequired));
        AddSecretPreserveOrSet(secrets, "Mattermost.BotToken", vm.BotToken, vm.HasPersistedBotToken);
    }

    private static void AddTeamsContribution(
        List<SectionFieldAction> fields,
        List<SectionSecretAction> secrets,
        TeamsStepViewModel vm,
        bool enabled,
        bool knownProvider)
    {
        if (!enabled)
        {
            if (knownProvider)
                fields.Add(new SectionFieldAction("Teams.Enabled", SectionFieldActionKind.Set, false));
            SetArrayOrDelete(fields, "Teams.AllowedTeamIds", ChannelCsv.ParseCsv(vm.TeamIdsInput, trimHash: false));
            SetArrayOrDelete(fields, "Teams.AllowedChannelIds", ChannelCsv.ParseCsv(vm.ChannelIdsInput, trimHash: false));
            SetArrayOrDelete(fields, "Teams.AllowedGroupChatIds", ChannelCsv.ParseCsv(vm.AllowedGroupChatIdsInput, trimHash: false));
            SetArrayOrDelete(fields, "Teams.AllowedUserIds", ChannelCsv.ParseCsv(vm.AllowedUserIdsInput, trimHash: false));
            SetArrayOrDelete(fields, "Teams.AllowedGroupIds", ChannelCsv.ParseCsv(vm.AllowedGroupIdsInput, trimHash: false));
            SetTeamsChannelAudienceOverridesOrDelete(fields, vm.ChannelAudienceOverrides);
            SetTeamsChannelAccessOverridesOrDelete(fields, vm.ChannelAccessOverrides);
            AddSecretPreserveOrSet(secrets, "Teams.ClientSecret", vm.ClientSecret, vm.HasPersistedClientSecret);
            return;
        }

        fields.Add(new SectionFieldAction("Teams.Enabled", SectionFieldActionKind.Set, true));
        fields.Add(new SectionFieldAction("Teams.MentionOnly", SectionFieldActionKind.Set, vm.MentionOnly));
        fields.Add(new SectionFieldAction("Teams.AllowDirectMessages", SectionFieldActionKind.Set, vm.AllowDirectMessages));
        fields.Add(new SectionFieldAction("Teams.AllowGroupChats", SectionFieldActionKind.Set, vm.AllowGroupChats));
        fields.Add(new SectionFieldAction("Teams.AllowAttachments", SectionFieldActionKind.Set, vm.AllowAttachments));
        SetStringOrDelete(fields, "Teams.TenantId", vm.TenantId);
        SetStringOrDelete(fields, "Teams.ClientId", vm.ClientId);
        SetStringOrDelete(fields, "Teams.BotId", vm.BotId);
        SetArrayOrDelete(fields, "Teams.AllowedTeamIds", ChannelCsv.ParseCsv(vm.TeamIdsInput, trimHash: false));
        SetArrayOrDelete(fields, "Teams.AllowedChannelIds", ChannelCsv.ParseCsv(vm.ChannelIdsInput, trimHash: false));
        SetArrayOrDelete(fields, "Teams.AllowedGroupChatIds", ChannelCsv.ParseCsv(vm.AllowedGroupChatIdsInput, trimHash: false));
        SetArrayOrDelete(fields, "Teams.AllowedUserIds", ChannelCsv.ParseCsv(vm.AllowedUserIdsInput, trimHash: false));
        SetArrayOrDelete(fields, "Teams.AllowedGroupIds", ChannelCsv.ParseCsv(vm.AllowedGroupIdsInput, trimHash: false));
        SetTeamsChannelAudienceOverridesOrDelete(fields, vm.ChannelAudienceOverrides);
        SetTeamsChannelAccessOverridesOrDelete(fields, vm.ChannelAccessOverrides);
        AddSecretPreserveOrSet(secrets, "Teams.ClientSecret", vm.ClientSecret, vm.HasPersistedClientSecret);
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

    private static void SetTeamsChannelAccessOverridesOrDelete(
        List<SectionFieldAction> fields,
        IReadOnlyList<TeamsChannelAccessOverride> overrides)
    {
        var valid = overrides
            .Where(static accessOverride => !string.IsNullOrWhiteSpace(accessOverride.TeamId)
                                            && !string.IsNullOrWhiteSpace(accessOverride.ChannelId))
            .ToArray();
        fields.Add(valid.Length > 0
            ? new SectionFieldAction("Teams.ChannelAccessOverrides", SectionFieldActionKind.Set, valid)
            : new SectionFieldAction("Teams.ChannelAccessOverrides", SectionFieldActionKind.Delete));
    }

    private static void SetTeamsChannelAudienceOverridesOrDelete(
        List<SectionFieldAction> fields,
        IReadOnlyList<TeamsChannelAudienceOverride> overrides)
    {
        var valid = overrides
            .Where(static audienceOverride => !string.IsNullOrWhiteSpace(audienceOverride.TeamId)
                                             && SecurityPolicyDefaults.TryParseAudience(audienceOverride.Audience, out _))
            .ToArray();
        fields.Add(valid.Length > 0
            ? new SectionFieldAction("Teams.ChannelAudienceOverrides", SectionFieldActionKind.Set, valid)
            : new SectionFieldAction("Teams.ChannelAudienceOverrides", SectionFieldActionKind.Delete));
    }

    private static void SetDictionaryOrDelete(List<SectionFieldAction> fields, string path, IReadOnlyDictionary<string, string> values)
    {
        fields.Add(values.Count > 0
            ? new SectionFieldAction(path, SectionFieldActionKind.Set, new Dictionary<string, string>(values, StringComparer.Ordinal))
            : new SectionFieldAction(path, SectionFieldActionKind.Delete));
    }

    private static void SetBoolDictionaryOrDelete(List<SectionFieldAction> fields, string path, IReadOnlyDictionary<string, bool> values)
    {
        fields.Add(values.Count > 0
            ? new SectionFieldAction(path, SectionFieldActionKind.Set, new Dictionary<string, bool>(values, StringComparer.Ordinal))
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

    // Persist only the channels whose mention rule is true — false is the runtime default, so
    // an absent key means "no mention required". This keeps the config minimal and stops a stale
    // rule for a removed channel from persisting (the map is built from the live channel id list).
    private static Dictionary<string, bool> BuildMentionRequiredMap(
        ChannelType type,
        IReadOnlyList<string> channelIds,
        IReadOnlyDictionary<ChannelType, Dictionary<string, bool>> channelMentionRequired)
    {
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (!channelMentionRequired.TryGetValue(type, out var explicitMentionRequired))
            return map;

        foreach (var channelId in channelIds)
        {
            if (explicitMentionRequired.TryGetValue(channelId, out var required) && required)
                map[channelId] = true;
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

    private static IReadOnlyList<TeamsChannelAccessOverride> GetTeamsChannelAccessOverrides(Dictionary<string, object> config)
    {
        if (!ConfigFileHelper.TryGetPathValue(config, "Teams.ChannelAccessOverrides", out var value) || value is null)
            return [];

        if (value is not object[] values)
            throw new InvalidOperationException("Configuration value 'Teams.ChannelAccessOverrides' must be an array.");

        var overrides = new List<TeamsChannelAccessOverride>(values.Length);
        foreach (var valueItem in values)
        {
            var accessOverride = valueItem switch
            {
                JsonElement element => JsonSerializer.Deserialize<TeamsChannelAccessOverride>(element.GetRawText()),
                Dictionary<string, object> item => JsonSerializer.Deserialize<TeamsChannelAccessOverride>(JsonSerializer.Serialize(item)),
                _ => null
            };
            if (accessOverride is null
                || string.IsNullOrWhiteSpace(accessOverride.TeamId)
                || string.IsNullOrWhiteSpace(accessOverride.ChannelId))
            {
                throw new InvalidOperationException("Teams channel access overrides require canonical TeamId and ChannelId values.");
            }

            overrides.Add(accessOverride);
        }

        return overrides;
    }

    private static IReadOnlyList<TeamsChannelAudienceOverride> GetTeamsChannelAudienceOverrides(Dictionary<string, object> config)
    {
        if (!ConfigFileHelper.TryGetPathValue(config, "Teams.ChannelAudienceOverrides", out var value) || value is null)
            return [];

        if (value is not object[] values)
            throw new InvalidOperationException("Configuration value 'Teams.ChannelAudienceOverrides' must be an array.");

        var overrides = new List<TeamsChannelAudienceOverride>(values.Length);
        foreach (var valueItem in values)
        {
            var audienceOverride = valueItem switch
            {
                JsonElement element => JsonSerializer.Deserialize<TeamsChannelAudienceOverride>(element.GetRawText()),
                Dictionary<string, object> item => JsonSerializer.Deserialize<TeamsChannelAudienceOverride>(JsonSerializer.Serialize(item)),
                _ => null
            };
            if (audienceOverride is null
                || string.IsNullOrWhiteSpace(audienceOverride.TeamId)
                || !SecurityPolicyDefaults.TryParseAudience(audienceOverride.Audience, out _))
            {
                throw new InvalidOperationException("Teams channel audience overrides require a canonical TeamId and a valid audience.");
            }

            overrides.Add(audienceOverride);
        }

        return overrides;
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

    private static Dictionary<string, bool> GetChannelMentionRequired(Dictionary<string, object> config, string path)
    {
        if (!ConfigFileHelper.TryGetPathValue(config, path, out var value) || value is null)
            return [];

        if (value is not Dictionary<string, object> values)
            throw new InvalidOperationException($"Configuration value '{path}' must be an object.");

        var mentionRequired = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (channelId, rawRequired) in values)
        {
            mentionRequired[channelId] = rawRequired switch
            {
                bool required => required,
                JsonElement { ValueKind: JsonValueKind.True } => true,
                JsonElement { ValueKind: JsonValueKind.False } => false,
                _ => throw new InvalidOperationException($"Channel mention rule '{path}.{channelId}' must be a boolean.")
            };
        }

        return mentionRequired;
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
    public required TeamsChannelDraft Teams { get; init; }
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
    public IReadOnlyDictionary<string, bool> MentionRequiredInThreadByChannel { get; init; } = new Dictionary<string, bool>(StringComparer.Ordinal);
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

internal sealed class TeamsChannelDraft : ChannelProviderDraft
{
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? BotId { get; init; }
    public bool HasPersistedClientSecret { get; init; }
    public bool MentionOnly { get; init; } = true;
    public IReadOnlyList<string> TeamIds { get; init; } = [];
    public bool AllowGroupChats { get; init; }
    public bool AllowAttachments { get; init; }
    public IReadOnlyList<string> AllowedGroupChatIds { get; init; } = [];
    public IReadOnlyList<string> AllowedGroupIds { get; init; } = [];
    public IReadOnlyList<TeamsChannelAudienceOverride> ChannelAudienceOverrides { get; init; } = [];
    public IReadOnlyList<TeamsChannelAccessOverride> ChannelAccessOverrides { get; init; } = [];
}

internal enum TeamsChannelAccessRowKind
{
    AddUser,
    AddGroup,
    RemoveUser,
    RemoveGroup,
    Done
}

internal sealed record TeamsChannelAccessRow(string Label, TeamsChannelAccessRowKind Kind, string? Id);

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
