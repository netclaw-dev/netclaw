// -----------------------------------------------------------------------
// <copyright file="ChannelsConfigPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Channels.Teams;
using Netclaw.Cli.Tui.Wizard;
using Netclaw.Cli.Tui.Wizard.Steps;
using Netclaw.Cli.Tui.Workflow;
using Netclaw.Configuration;
using R3;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Config;

public sealed class ChannelsConfigPage : ReactivePage<ChannelsConfigViewModel>
{
    private DynamicLayoutNode? _contentNode;
    private DynamicLayoutNode? _helpTextNode;
    private DynamicLayoutNode? _keyBindingsNode;
    private TextInputNode? _singleInput;
    private ChannelsConfigScreen? _singleInputScreen;
    private string? _singleInputKey;
    private readonly Dictionary<string, TextInputNode> _credentialInputs = [];
    private ChannelType? _credentialInputAdapter;
    private readonly CompositeDisposable _stepSubs = [];
    private bool _quitAfterCredentialSave;

    protected override void OnBound()
    {
        base.OnBound();

        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);

        ViewModel.Input.OfType<IInputEvent, PasteEvent>()
            .Subscribe(HandlePaste)
            .DisposeWith(Subscriptions);

        ViewModel.IsSaved.Subscribe(_ => InvalidateAll()).DisposeWith(Subscriptions);
        ViewModel.Screen.Subscribe(_ =>
        {
            ResetTextInputs();
            InvalidateAll();
        }).DisposeWith(Subscriptions);
        ViewModel.Status.Subscribe(_ =>
        {
            _contentNode?.Invalidate();
            if (_quitAfterCredentialSave && !ViewModel.IsCredentialSaveInProgress)
            {
                _quitAfterCredentialSave = false;
                ViewModel.RequestQuit();
            }
        }).DisposeWith(Subscriptions);
        ViewModel.OnStepContentChanged = () =>
        {
            _contentNode?.Invalidate();
            _helpTextNode?.Invalidate();
            _keyBindingsNode?.Invalidate();
        };
    }

    public override ILayoutNode BuildLayout()
        => NetclawTuiChrome.BuildPageFrame("Channels", BuildInnerLayout());

    private ILayoutNode BuildInnerLayout()
        => Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(BuildContent())
            .WithChild(BuildHelpText())
            .WithChild(Layouts.Empty().Fill())
            .WithChild(BuildStatusBar())
            .WithChild(BuildKeyBindings());

    private LayoutNode BuildContent()
    {
        _contentNode = new DynamicLayoutNode(() =>
        {
            if (ViewModel.Screen.Value != ChannelsConfigScreen.Picker)
            {
                _stepSubs.Clear();
                ViewModel.StepView.ClearFocusState();
                return ViewModel.Screen.Value switch
                {
                    ChannelsConfigScreen.AdapterMenu => BuildAdapterMenu(),
                    ChannelsConfigScreen.ChannelPermissions => BuildChannelPermissions(),
                    ChannelsConfigScreen.AddChannel => BuildAddChannel(),
                    ChannelsConfigScreen.TeamsDestinationAdd => BuildTeamsDestinationAdd(),
                    ChannelsConfigScreen.TeamsPrincipalAdd => BuildTeamsPrincipalAdd(),
                    ChannelsConfigScreen.TeamsPrincipalManagement => BuildTeamsPrincipalManagement(),
                    ChannelsConfigScreen.TeamsPrincipalRemovalConfirm => BuildTeamsPrincipalRemovalConfirm(),
                    ChannelsConfigScreen.TeamsChannelPrincipalRemovalConfirm => BuildTeamsChannelPrincipalRemovalConfirm(),
                    ChannelsConfigScreen.TeamsDestinationRemovalConfirm => BuildTeamsDestinationRemovalConfirm(),
                    ChannelsConfigScreen.TeamsTeamSearch => BuildTeamsTeamSearch(),
                    ChannelsConfigScreen.TeamsChannelSearch => BuildTeamsChannelSearch(),
                    ChannelsConfigScreen.TeamsUserSearch => BuildTeamsUserSearch(),
                    ChannelsConfigScreen.TeamsGroupSearch => BuildTeamsGroupSearch(),
                    ChannelsConfigScreen.TeamsGroupChatSearch => BuildTeamsGroupChatSearch(),
                    ChannelsConfigScreen.TeamsChannelAccess => BuildTeamsChannelAccess(),
                    ChannelsConfigScreen.AllowedUsers => BuildAllowedUsers(),
                    ChannelsConfigScreen.AllowedGroups => BuildAllowedGroups(),
                    ChannelsConfigScreen.GroupChats => BuildGroupChats(),
                    ChannelsConfigScreen.Attachments => BuildAttachments(),
                    ChannelsConfigScreen.DirectoryStatus => BuildDirectoryStatus(),
                    ChannelsConfigScreen.DirectMessages => BuildDirectMessages(),
                    ChannelsConfigScreen.RotateCredentials => BuildRotateCredentials(),
                    ChannelsConfigScreen.ResetConfirm => BuildResetConfirmation(),
                    _ => Layouts.Empty()
                };
            }

            if (!ViewModel.StepView.ManagesOwnFocusState)
            {
                _stepSubs.Clear();
                ViewModel.StepView.ClearFocusState();
            }

            return ViewModel.StepView.BuildContent(ViewModel.Step, CreateCallbacks());
        });

        return _contentNode;
    }

    private ILayoutNode BuildAdapterMenu()
    {
        var layout = Layouts.Vertical()
            .WithChild(Header($"  {ViewModel.ActiveAdapterName} is configured."))
            .WithChild(Hint($"  {ViewModel.GetActiveAdapterSummary()}"));

        if (ViewModel.ActiveAdapterType != ChannelType.Teams)
        {
            layout = layout
                .WithChild(Layouts.Empty().Height(1))
                .WithChild(new TextNode("  What would you like to do?").WithForeground(Color.White))
                .WithChild(Layouts.Empty().Height(1));
        }

        var items = ViewModel.GetManagementMenuItems();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var focused = i == ViewModel.ManagementMenuIndex;
            layout = layout.WithChild(Row(
                $"{FocusPrefix(focused)}{item.Label,-36} {item.Description}",
                focused));
        }

        return layout;
    }

    private ILayoutNode BuildChannelPermissions()
    {
        var layout = Layouts.Vertical()
            .WithChild(Header($"  {ViewModel.ActiveAdapterName} > Channels & Permissions"))
            .WithChild(Hint(ViewModel.ActiveAdapterType == ChannelType.Teams
                ? $"  Group Chat ingress: {(ViewModel.IsGroupChatIngressEnabled ? "ON" : "OFF")}. Enter on a Group Chat opens its settings."
                : "  Configure allowed channels, their audience, and thread behavior."))
            .WithChild(Layouts.Empty().Height(1));

        var rows = ViewModel.GetChannelRows();
        if (rows.All(static row => row.IsAction))
        {
            layout = layout.WithChild(Hint("  No allowed channels configured."));
        }

        var editableRows = rows.Where(static row => !row.IsAction).ToArray();
        var displayNameWidth = Math.Clamp(
            editableRows.Select(static row => row.DisplayName.Length).DefaultIfEmpty(16).Max(),
            16,
            56);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var focused = i == ViewModel.ChannelRowIndex;
            if (row.IsUnresolved)
            {
                // A channel the live probe could not resolve. It was still saved (inert
                // allow-list entry), but we mark it red with ✗ so the operator can fix or
                // remove it. "✗  " keeps the same 3-char width as FocusPrefix.
                var unresolvedLine = $"✗  {Column(row.DisplayName, displayNameWidth)} {AudienceCycle(row.Audience)}   {MentionField(row.MentionRequired)}";
                layout = layout.WithChild(ConfigSelectionRow.Create(unresolvedLine, focused, Color.Red));
                continue;
            }

            // Real channels show the audience cycler plus the arrow-free mention
            // field (Space toggles it). A DM row is one-to-one, so it shows audience
            // only; action rows are just their label.
            string line;
            if (row.IsAction)
                line = $"{FocusPrefix(focused)}{row.DisplayName}";
            else if (row.IsDirectMessage)
                line = $"{FocusPrefix(focused)}{Column(row.DisplayName, displayNameWidth)} {AudienceCycle(row.Audience)}";
            else if (row.IsGroupChat)
                line = $"{FocusPrefix(focused)}{Column(row.DisplayName, displayNameWidth)} [Team audience]   Group Chat ingress";
            else
                line = $"{FocusPrefix(focused)}{Column(row.DisplayName, displayNameWidth)} {AudienceCycle(row.Audience)}   {MentionField(row.MentionRequired)}";

            layout = layout.WithChild(Row(line, focused));
        }

        return layout
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(BuildSelectedChannelDescription(rows));
    }

    // Describes the selected row below the list. The removed detail leaf used to
    // show these details on its own screen; now they follow the cursor so the
    // list is the single per-channel editor.
    private ILayoutNode BuildSelectedChannelDescription(IReadOnlyList<ChannelPermissionRow> rows)
    {
        if (rows.Count == 0)
            return Hint("  Audience controls which tools and data this channel can use.");

        var row = rows[Math.Clamp(ViewModel.ChannelRowIndex, 0, rows.Count - 1)];
        if (row.IsAction)
            return Hint("  Audience controls which tools and data this channel can use.");

        if (row.IsGroupChat)
            return Hint("  Group Chats use Team audience and global principal rules. Enter edits ingress. Delete removes this chat.");

        var description = Layouts.Vertical()
            .WithChild(Hint($"  {AudienceLabel(row.Audience)} — {AudienceDescription(row.Audience)}"));

        if (!row.IsDirectMessage)
        {
            var isTeams = ViewModel.ActiveAdapterType == ChannelType.Teams;
            description = description.WithChild(Hint(row.MentionRequired
                ? isTeams
                    ? "  Require @mention: applies to every selected Teams channel; the bot stays quiet until @mentioned."
                    : "  Require @mention: bot stays quiet until @mentioned, then catches up on the thread."
                : isTeams
                    ? "  Require @mention off: applies to every selected Teams channel; the bot replies to every message."
                    : "  Require @mention off: bot replies to every message in the thread (default)."));
        }

        return description;
    }

    private ILayoutNode BuildAddChannel()
    {
        var input = EnsureSingleInput(ChannelsConfigScreen.AddChannel, "channel", ViewModel.AddChannelInput, ViewModel.AddChannelPlaceholder);
        input.OnFocused();

        // Resolve-before-add: no audience picker here. The channel is resolved
        // against the adapter, added at the deployment-posture default audience,
        // and tuned afterward with ←/→ on the channel list.
        return Layouts.Vertical()
            .WithChild(Header($"  {ViewModel.ActiveAdapterName} > Add Channel"))
            .WithChild(new TextNode("  Channel name or ID:").WithForeground(Color.White))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(input, "Channel"))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(Hint($"  Netclaw resolves the channel on {ViewModel.ActiveAdapterName} and adds it at the default audience."))
            .WithChild(Hint("  Change its audience afterward with ←/→ on the channel list."));
    }

    private ILayoutNode BuildTeamsDestinationAdd()
    {
        var options = new[] { "Channel", "Group Chat" };
        var layout = Layouts.Vertical()
            .WithChild(Header("  Microsoft Teams > Add a destination"))
            .WithChild(Hint("  Select a channel or a Group Chat. Both paths save canonical identities."))
            .WithChild(Layouts.Empty().Height(1));

        for (var i = 0; i < options.Length; i++)
        {
            var focused = ViewModel.TeamsDestinationAddIndex == i;
            layout = layout.WithChild(Row($"{FocusPrefix(focused)}{options[i]}", focused));
        }

        return layout;
    }

    private ILayoutNode BuildTeamsTeamSearch()
    {
        var input = EnsureSingleInput(ChannelsConfigScreen.TeamsTeamSearch, "teams-search", ViewModel.DirectorySearchInput, "Search Teams by name");
        input.OnFocused();
        var layout = Layouts.Vertical()
            .WithChild(Header("  Microsoft Teams > Add channel"))
            .WithChild(Hint("  Search by Team display name. Canonical IDs remain the saved authority."))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(input, "Team search"));

        foreach (var (team, index) in ViewModel.TeamSearchResults.Select((team, index) => (team, index)))
        {
            var label = string.IsNullOrWhiteSpace(team.DisplayName) ? "Microsoft Teams" : team.DisplayName;
            layout = layout.WithChild(Row($"{FocusPrefix(ViewModel.DirectoryResultIndex == index)}{label}", ViewModel.DirectoryResultIndex == index));
        }

        layout = layout.WithChild(Row(
            $"{FocusPrefix(ViewModel.IsAdvancedTeamsDirectoryActionSelected())}Advanced canonical-ID entry",
            ViewModel.IsAdvancedTeamsDirectoryActionSelected()));

        return layout.WithChild(Layouts.Empty().Height(1)).WithChild(Hint("  Advanced entry does not verify a channel exists."));
    }

    private ILayoutNode BuildTeamsChannelSearch()
    {
        var selectedTeam = ViewModel.SelectedTeam;
        var teamLabel = string.IsNullOrWhiteSpace(selectedTeam?.DisplayName)
            ? "Microsoft Teams"
            : selectedTeam.DisplayName;
        var layout = Layouts.Vertical()
            .WithChild(Header($"  Microsoft Teams > {teamLabel}"))
            .WithChild(Hint("  Select a channel. Netclaw saves canonical Team and channel IDs."));

        foreach (var (channel, index) in ViewModel.ChannelSearchResults.Select((channel, index) => (channel, index)))
        {
            var label = string.IsNullOrWhiteSpace(channel.DisplayName) ? "Channel" : channel.DisplayName;
            layout = layout.WithChild(Row($"{FocusPrefix(ViewModel.DirectoryResultIndex == index)}{teamLabel} / {label}", ViewModel.DirectoryResultIndex == index));
        }

        layout = layout.WithChild(Row(
            $"{FocusPrefix(ViewModel.IsAdvancedTeamsDirectoryActionSelected())}Advanced canonical-ID entry",
            ViewModel.IsAdvancedTeamsDirectoryActionSelected()));

        return layout.WithChild(Layouts.Empty().Height(1)).WithChild(Hint("  Use Advanced entry when directory search is unavailable."));
    }

    private ILayoutNode BuildTeamsUserSearch()
    {
        var input = EnsureSingleInput(ChannelsConfigScreen.TeamsUserSearch, "teams-user-search", ViewModel.DirectorySearchInput, "Search users by name, UPN, or mail");
        input.OnFocused();
        var layout = Layouts.Vertical()
            .WithChild(Header(ViewModel.EditingChannelAccess is null
                ? "  Microsoft Teams > Allowed users"
                : "  Microsoft Teams > Channel allowed users"))
            .WithChild(Hint("  Search identity metadata. Netclaw saves only the canonical Entra object ID."))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(input, "User search"));

        foreach (var (user, index) in ViewModel.UserSearchResults.Select((user, index) => (user, index)))
        {
            var label = FormatTeamsUser(user);
            layout = layout.WithChild(Row($"{FocusPrefix(ViewModel.DirectoryResultIndex == index)}{label}", ViewModel.DirectoryResultIndex == index));
        }

        layout = layout.WithChild(Row(
            $"{FocusPrefix(ViewModel.IsAdvancedTeamsDirectoryActionSelected())}Advanced canonical-ID entry",
            ViewModel.IsAdvancedTeamsDirectoryActionSelected()));

        return layout.WithChild(Layouts.Empty().Height(1)).WithChild(Hint("  Use Advanced entry to enter a canonical Entra user ID."));
    }

    private ILayoutNode BuildTeamsGroupSearch()
    {
        var input = EnsureSingleInput(ChannelsConfigScreen.TeamsGroupSearch, "teams-group-search", ViewModel.DirectorySearchInput, "Search groups by name");
        input.OnFocused();
        var layout = Layouts.Vertical()
            .WithChild(Header(ViewModel.EditingChannelAccess is null
                ? "  Microsoft Teams > Allowed groups"
                : "  Microsoft Teams > Channel allowed groups"))
            .WithChild(Hint("  Microsoft 365 and security groups may grant access after membership verification."))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(input, "Group search"));

        foreach (var (group, index) in ViewModel.GroupSearchResults.Select((group, index) => (group, index)))
        {
            var label = FormatTeamsGroup(group);
            layout = layout.WithChild(Row($"{FocusPrefix(ViewModel.DirectoryResultIndex == index)}{label}", ViewModel.DirectoryResultIndex == index));
        }

        layout = layout.WithChild(Row(
            $"{FocusPrefix(ViewModel.IsAdvancedTeamsDirectoryActionSelected())}Advanced canonical-ID entry",
            ViewModel.IsAdvancedTeamsDirectoryActionSelected()));

        return layout.WithChild(Layouts.Empty().Height(1)).WithChild(Hint("  Use Advanced entry to enter a canonical Entra group ID."));
    }

    private ILayoutNode BuildTeamsGroupChatSearch()
    {
        var input = EnsureSingleInput(
            ChannelsConfigScreen.TeamsGroupChatSearch,
            "group-chat-search",
            ViewModel.GroupChatSearchInput,
            "Enter all or part of the Group Chat name");
        input.OnFocused();
        var layout = Layouts.Vertical()
            .WithChild(Header("  Microsoft Teams > Find a Group Chat"))
            .WithChild(Hint("  Search all or part of a Group Chat name. The search continues automatically."))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(input, "Group Chat name"))
            .WithChild(Hint($"  {ViewModel.GroupChatSearchProgressText}"));

        var chats = ViewModel.FilteredGroupChatSearchResults;
        var rowCount = ViewModel.GroupChatSearchAdvancedIndex + 1;
        // Keep results and continuation actions visible in an 80x24 terminal.
        // These are display bounds only; selection retains its full-page index.
        const int visibleRows = 5;
        var firstRow = Math.Clamp(ViewModel.DirectoryResultIndex - visibleRows / 2, 0, Math.Max(0, rowCount - visibleRows));
        var endRow = Math.Min(firstRow + visibleRows, rowCount);
        for (var index = firstRow; index < endRow; index++)
        {
            string label;
            if (index < chats.Count)
            {
                var chat = chats[index];
                var title = string.IsNullOrWhiteSpace(chat.Topic)
                    ? chat.ParticipantPreview.Count > 0 ? string.Join(", ", chat.ParticipantPreview) : "Group Chat"
                    : chat.Topic;
                label = $"{title} · {ChannelsConfigViewModel.GetGroupChatDisplaySuffix(chat.Id)}";
            }
            else if (index == ViewModel.GroupChatSearchActionIndex)
                label = ViewModel.IsGroupChatSearchRunning
                    ? "Search in progress..."
                    : ViewModel.HasGroupChatContinuation ? "Resume search"
                    : ViewModel.HasSearchedGroupChats ? "Search again" : "Search by name";
            else if (index == ViewModel.GroupChatSearchActionIndex + 1 && ViewModel.IsGroupChatSearchRunning)
                label = "Stop search (Ctrl+S)";
            else
                label = "Advanced canonical-ID entry";

            var focused = ViewModel.DirectoryResultIndex == index;
            layout = layout.WithChild(Row($"{FocusPrefix(focused)}{label}", focused));
        }

        if (rowCount > visibleRows)
            layout = layout.WithChild(Hint($"  Rows {firstRow + 1}–{endRow} of {rowCount}. Use ↑/↓ to see more."));

        return layout.WithChild(Hint("  Install the Netclaw app in the selected chat before the live test."));
    }

    private ILayoutNode BuildTeamsPrincipalAdd()
    {
        var options = new[] { "User", "Group" };
        var layout = Layouts.Vertical()
            .WithChild(Header("  Microsoft Teams > Add users or groups"))
            .WithChild(Hint("  Search friendly identity data. Netclaw saves only canonical Entra object IDs."))
            .WithChild(Layouts.Empty().Height(1));

        for (var i = 0; i < options.Length; i++)
            layout = layout.WithChild(Row($"{FocusPrefix(ViewModel.TeamsPrincipalManagementIndex == i)}{options[i]}", ViewModel.TeamsPrincipalManagementIndex == i));

        return layout;
    }

    private ILayoutNode BuildTeamsPrincipalManagement()
    {
        var rows = ViewModel.GetTeamsPrincipalRows();
        var layout = Layouts.Vertical()
            .WithChild(Header("  Microsoft Teams > Manage users and groups"))
            .WithChild(Hint($"  Filter: [< {ViewModel.TeamsPrincipalFilterLabel} >]. Existing channel rules remain separate."))
            .WithChild(Layouts.Empty().Height(1));

        if (rows.Count == 0)
            layout = layout.WithChild(Hint("  No saved global principals match this filter."));

        foreach (var (row, index) in rows.Select((row, index) => (row, index)))
        {
            var focused = ViewModel.TeamsPrincipalManagementIndex == index;
            layout = layout.WithChild(Row($"{FocusPrefix(focused)}{row.Label} · {row.Scope}", focused));
        }

        var addIndex = rows.Count;
        var doneIndex = addIndex + 1;
        layout = layout.WithChild(Row($"{FocusPrefix(ViewModel.TeamsPrincipalManagementIndex == addIndex)}Add user or group", ViewModel.TeamsPrincipalManagementIndex == addIndex));
        return layout.WithChild(Row($"{FocusPrefix(ViewModel.TeamsPrincipalManagementIndex == doneIndex)}Done", ViewModel.TeamsPrincipalManagementIndex == doneIndex));
    }

    private ILayoutNode BuildTeamsPrincipalRemovalConfirm()
    {
        var principal = ViewModel.PendingPrincipalRemoval;
        if (principal is null)
            return Layouts.Empty();

        return Layouts.Vertical()
            .WithChild(Header("  Remove global Teams principal?"))
            .WithChild(Hint($"  {principal.Label}"))
            .WithChild(Hint($"  Canonical ID: {principal.Id}"))
            .WithChild(Hint($"  {ViewModel.TeamsPrincipalRemovalImpact}"))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(Row($"{FocusPrefix(ViewModel.TeamsPrincipalRemovalIndex == 0)}Cancel", ViewModel.TeamsPrincipalRemovalIndex == 0))
            .WithChild(Row($"{FocusPrefix(ViewModel.TeamsPrincipalRemovalIndex == 1)}Remove", ViewModel.TeamsPrincipalRemovalIndex == 1));
    }

    private ILayoutNode BuildTeamsChannelPrincipalRemovalConfirm()
    {
        var principal = ViewModel.PendingChannelPrincipalRemoval;
        if (principal is null)
            return Layouts.Empty();

        return Layouts.Vertical()
            .WithChild(Header("  Remove exact Teams channel principal?"))
            .WithChild(Hint($"  Channel: {principal.TeamId} / {principal.ChannelId}"))
            .WithChild(Hint($"  {principal.Label}"))
            .WithChild(Hint($"  Canonical ID: {principal.PrincipalId}"))
            .WithChild(Hint($"  {ViewModel.TeamsChannelPrincipalRemovalImpact}"))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(Row($"{FocusPrefix(ViewModel.TeamsChannelPrincipalRemovalIndex == 0)}Cancel", ViewModel.TeamsChannelPrincipalRemovalIndex == 0))
            .WithChild(Row($"{FocusPrefix(ViewModel.TeamsChannelPrincipalRemovalIndex == 1)}Remove", ViewModel.TeamsChannelPrincipalRemovalIndex == 1));
    }

    private ILayoutNode BuildTeamsDestinationRemovalConfirm()
    {
        var destination = ViewModel.PendingTeamsDestinationRemoval;
        if (destination is null)
            return Layouts.Empty();

        return Layouts.Vertical()
            .WithChild(Header("  Remove Teams destination?"))
            .WithChild(Hint($"  {destination.DisplayName}"))
            .WithChild(Hint($"  Canonical ID: {destination.Id}"))
            .WithChild(Hint("  This removal affects only this configured destination after configuration activation."))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(Row($"{FocusPrefix(ViewModel.TeamsDestinationRemovalIndex == 0)}Cancel", ViewModel.TeamsDestinationRemovalIndex == 0))
            .WithChild(Row($"{FocusPrefix(ViewModel.TeamsDestinationRemovalIndex == 1)}Remove", ViewModel.TeamsDestinationRemovalIndex == 1));
    }

    private ILayoutNode BuildTeamsChannelAccess()
    {
        var access = ViewModel.EditingChannelAccess;
        if (access is null)
            return Layouts.Empty();

        var layout = Layouts.Vertical()
            .WithChild(Header("  Microsoft Teams > Channel access"))
            .WithChild(Hint("  These restrictions union with global Teams users and groups."))
            .WithChild(Layouts.Empty().Height(1));
        foreach (var (row, index) in ViewModel.GetTeamsChannelAccessRows().Select((row, index) => (row, index)))
        {
            layout = layout.WithChild(Row(
                $"{FocusPrefix(ViewModel.ChannelAccessRowIndex == index)}{row.Label}",
                ViewModel.ChannelAccessRowIndex == index));
        }
        return layout;
    }

    private ILayoutNode BuildAllowedUsers()
    {
        var input = EnsureSingleInput(ChannelsConfigScreen.AllowedUsers, "users", ViewModel.AllowedUsersInput, "U123, U456");
        input.OnFocused();

        return Layouts.Vertical()
            .WithChild(Header($"  {ViewModel.ActiveAdapterName} > Allowed Users"))
            .WithChild(Hint("  Leave blank to allow anyone in allowed channels."))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(new TextNode("  User IDs:").WithForeground(Color.White))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(input, "User IDs"));
    }

    private ILayoutNode BuildAllowedGroups()
    {
        var input = EnsureSingleInput(ChannelsConfigScreen.AllowedGroups, "groups", ViewModel.AllowedGroupsInput, "group object IDs");
        input.OnFocused();

        return Layouts.Vertical()
            .WithChild(Header("  Microsoft Teams > Allowed Groups"))
            .WithChild(Hint("  Enter canonical Entra group object IDs. Membership is checked fail-closed."))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(new TextNode("  Group IDs:").WithForeground(Color.White))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(input, "Group IDs"));
    }

    private ILayoutNode BuildGroupChats()
    {
        var input = EnsureSingleInput(
            ChannelsConfigScreen.GroupChats,
            "group-chats",
            ViewModel.AllowedGroupChatsInput,
            "19:chat-id@thread.v2");
        input.OnFocused();

        return Layouts.Vertical()
            .WithChild(Header("  Microsoft Teams > Group Chats"))
            .WithChild(Hint("  The ingress switch applies to all allowed Group Chats."))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(Row(
                $"   [{Check(ViewModel.GroupChatsEnabled)}] Enable Group Chat ingress",
                focused: ViewModel.GroupChatsEnabled,
                enabled: ViewModel.GroupChatsEnabled))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(new TextNode("  Canonical Group Chat IDs:").WithForeground(Color.White))
            .WithChild(WizardStepHelpers.BuildTextInputPanel(input, "Group Chat IDs"))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(Hint("  Each Group Chat message needs a structured bot mention when mention-only is enabled."));
    }

    private ILayoutNode BuildAttachments() => Layouts.Vertical()
        .WithChild(Header("  Microsoft Teams > Attachments"))
        .WithChild(Hint("  Supports inline images and personal file-download cards. Channel and Group Chat files are deferred."))
        .WithChild(Layouts.Empty().Height(1))
        .WithChild(Row(
            $"   [{Check(ViewModel.AttachmentsEnabled)}] Allow supported Teams attachments",
            focused: ViewModel.AttachmentsEnabled,
            enabled: ViewModel.AttachmentsEnabled));

    private ILayoutNode BuildDirectoryStatus()
    {
        var layout = Layouts.Vertical()
            .WithChild(Header("  Microsoft Teams > Directory / Graph status"))
            .WithChild(Hint("  This view is non-secret and does not acquire a token on the terminal UI loop."))
            .WithChild(Layouts.Empty().Height(1));

        foreach (var line in ViewModel.GetDirectoryStatusLines())
            layout = layout.WithChild(new TextNode($"  {line}").WithForeground(Color.White));

        return layout;
    }

    private ILayoutNode BuildDirectMessages()
    {
        var layout = Layouts.Vertical()
            .WithChild(Header($"  {ViewModel.ActiveAdapterName} > Direct Messages"))
            .WithChild(Hint("  Enable DMs only for audiences you trust."))
            .WithChild(Layouts.Empty().Height(1));

        layout = layout.WithChild(Row(
            $"{FocusPrefix(ViewModel.DirectMessagesRowIndex == 0)}[{Check(ViewModel.DirectMessagesEnabled)}] Allow direct messages",
            ViewModel.DirectMessagesRowIndex == 0,
            ViewModel.DirectMessagesEnabled));

        var audience = ChannelsConfigViewModel.AudienceOptions[ViewModel.AudienceSelectionIndex];
        layout = layout.WithChild(Row(
            $"{FocusPrefix(ViewModel.DirectMessagesRowIndex == 1)}DM audience      [< {AudienceLabel(audience),-8} >]",
            ViewModel.DirectMessagesRowIndex == 1));

        return layout;
    }

    private ILayoutNode BuildRotateCredentials()
    {
        var fields = ViewModel.GetCredentialFields();
        var layout = Layouts.Vertical()
            .WithChild(Header($"  {ViewModel.GetCredentialsScreenTitle()}"))
            .WithChild(Hint("  Secret fields are blank by design. Leave blank to keep existing secrets."))
            .WithChild(Layouts.Empty().Height(1));

        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var input = EnsureCredentialInput(field);
            if (i == ViewModel.CredentialFieldIndex)
                Focus.SetFocus(input);

            layout = layout
                .WithChild(WizardStepHelpers.BuildTextInputPanel(input, field.Label));

            if (!string.IsNullOrWhiteSpace(field.Hint))
                layout = layout.WithChild(Hint($"  {field.Hint}"));
        }

        return layout;
    }

    private ILayoutNode BuildResetConfirmation()
    {
        var options = new[] { "Cancel", $"Yes, reset {ViewModel.ActiveAdapterName}" };
        var layout = Layouts.Vertical()
            .WithChild(Header($"  Reset {ViewModel.ActiveAdapterName} connection?"))
            .WithChild(Hint($"  This removes {ViewModel.ActiveAdapterName} credentials, allowed channels, allowed users,"))
            .WithChild(Hint("  DM settings, and channel permission mappings immediately."))
            .WithChild(Layouts.Empty().Height(1));

        for (var i = 0; i < options.Length; i++)
        {
            var focused = i == ViewModel.ResetConfirmIndex;
            layout = layout.WithChild(Row($"{FocusPrefix(focused)}{options[i]}", focused));
        }

        return layout;
    }

    private LayoutNode BuildHelpText()
    {
        _helpTextNode = new DynamicLayoutNode(() =>
        {
            if (ViewModel.Screen.Value != ChannelsConfigScreen.Picker)
            {
                var help = ViewModel.Screen.Value switch
                {
                    ChannelsConfigScreen.AdapterMenu => "  Manage this adapter without re-entering credentials.",
                    ChannelsConfigScreen.ChannelPermissions when ViewModel.ActiveAdapterType == ChannelType.Teams => "  Left/right sets audience. Space toggles @mention for every selected Teams channel. Enter on Done finishes. a adds, Delete removes.",
                    ChannelsConfigScreen.ChannelPermissions => "  Left/right sets audience. Space toggles Require @mention. Enter on Done finishes. a adds, Delete removes.",
                    ChannelsConfigScreen.AddChannel => "  Enter applies the channel draft. Esc cancels.",
                    ChannelsConfigScreen.TeamsTeamSearch => "  Enter searches, then selects the Team. Type ordinary text in the search field.",
                    ChannelsConfigScreen.TeamsChannelSearch => "  Enter saves the selected channel.",
                    ChannelsConfigScreen.TeamsUserSearch => "  Enter searches, then adds the selected user. Type ordinary text in the search field.",
                    ChannelsConfigScreen.TeamsGroupSearch => "  Enter searches, then adds the selected group. Type ordinary text in the search field.",
                    ChannelsConfigScreen.TeamsDestinationAdd => "  Select Channel or Group Chat. Esc returns to the menu.",
                    ChannelsConfigScreen.TeamsPrincipalAdd => "  Select a principal type, then search a friendly identity.",
                    ChannelsConfigScreen.TeamsPrincipalManagement => "  Left/right changes the filter. Enter opens Add, Done, or removal confirmation.",
                    ChannelsConfigScreen.TeamsPrincipalRemovalConfirm => "  Confirm removal. This only removes the selected global grant.",
                    ChannelsConfigScreen.TeamsChannelPrincipalRemovalConfirm => "  Confirm removal. This can change the exact channel sender rule.",
                    ChannelsConfigScreen.TeamsDestinationRemovalConfirm => "  Confirm removal. The destination becomes denied after configuration activation.",
                    ChannelsConfigScreen.TeamsGroupChatSearch => "  Enter searches or reviews a chat. Ctrl+S stops the search and keeps its matches.",
                    ChannelsConfigScreen.TeamsChannelAccess => "  Enter edits a principal list. Channel rules only restrict this exact Team and channel.",
                    ChannelsConfigScreen.AllowedUsers => "  Use comma-separated user IDs. Blank means unrestricted users in allowed channels.",
                    ChannelsConfigScreen.AllowedGroups => "  Use comma-separated canonical Entra group IDs. Blank removes group-derived access.",
                    ChannelsConfigScreen.GroupChats => "  Space toggles ingress for all allowed Group Chats. Enter saves. Esc cancels.",
                    ChannelsConfigScreen.Attachments => "  Space saves supported inbound Teams attachment access.",
                    ChannelsConfigScreen.DirectoryStatus => "  Run netclaw doctor for offline configuration diagnostics. Esc returns to the menu.",
                    ChannelsConfigScreen.DirectMessages => "  Space toggles DMs. Left/right changes the DM audience.",
                    ChannelsConfigScreen.RotateCredentials => "  Blank secret fields preserve existing secrets. Tab and Shift+Tab switch fields.",
                    ChannelsConfigScreen.ResetConfirm => "  Reset writes immediately when confirmed.",
                    _ => string.Empty
                };
                return (ILayoutNode)new TextNode(help).WithForeground(Color.Gray);
            }

            return (ILayoutNode)new TextNode(ViewModel.Step.GetHelpText()).WithForeground(Color.Gray);
        });

        return _helpTextNode.Height(2);
    }

    private LayoutNode BuildStatusBar()
        => ViewModel.Status
            .Select(status => (ILayoutNode)(string.IsNullOrWhiteSpace(status.Text)
                ? Layouts.Empty()
                : NetclawTuiChrome.BuildStatusLine(status.Text, ToColor(status.Tone))))
            .AsLayout()
            .Height(1);

    private LayoutNode BuildKeyBindings()
    {
        _keyBindingsNode = new DynamicLayoutNode(() =>
        {
            var text = ViewModel.Screen.Value switch
                {
                    ChannelsConfigScreen.AdapterMenu => " [↑/↓] Navigate  [Enter] Select  [Esc] Channels  [Ctrl+Q] Quit",
                    ChannelsConfigScreen.ChannelPermissions => " [↑/↓] Navigate  [←/→] Audience  [Space] @mention  [Enter] Done  [Del] Remove  [Esc] Menu",
                    ChannelsConfigScreen.AddChannel => " [Type] Channel  [Enter] Resolve & add  [Esc] Channels  [Ctrl+Q] Quit",
                    ChannelsConfigScreen.TeamsDestinationAdd => " [↑/↓] Navigate  [Enter] Select  [Esc] Menu",
                    ChannelsConfigScreen.TeamsPrincipalAdd => " [↑/↓] Navigate  [Enter] Select  [Esc] Menu",
                    ChannelsConfigScreen.TeamsPrincipalManagement => " [↑/↓] Select  [←/→] Filter  [Enter] Remove/open  [Esc] Menu",
                    ChannelsConfigScreen.TeamsPrincipalRemovalConfirm => " [↑/↓] Select  [Enter] Confirm  [Esc] Cancel",
                    ChannelsConfigScreen.TeamsChannelPrincipalRemovalConfirm => " [↑/↓] Select  [Enter] Confirm  [Esc] Cancel",
                    ChannelsConfigScreen.TeamsDestinationRemovalConfirm => " [↑/↓] Select  [Enter] Confirm  [Esc] Cancel",
                    ChannelsConfigScreen.TeamsTeamSearch => " [Type] Search  [Enter] Search/select  [↑/↓] Select  [Esc] Channels",
                    ChannelsConfigScreen.TeamsChannelSearch => " [↑/↓] Select  [Enter] Save channel  [Esc] Teams",
                    ChannelsConfigScreen.TeamsUserSearch => " [Type] Search  [Enter] Search/add  [↑/↓] Select  [Esc] Menu",
                    ChannelsConfigScreen.TeamsGroupSearch => " [Type] Search  [Enter] Search/add  [↑/↓] Select  [Esc] Menu",
                    ChannelsConfigScreen.TeamsGroupChatSearch => " [Type] Chat name  [↑/↓] Select  [Enter] Search/review  [Ctrl+S] Stop  [Esc] Back",
                    ChannelsConfigScreen.TeamsChannelAccess => " [↑/↓] Select  [Enter] Edit  [Esc] Channels",
                    ChannelsConfigScreen.AllowedUsers => " [Enter] Apply  [Esc] Menu  [Ctrl+Q] Quit",
                    ChannelsConfigScreen.AllowedGroups => " [Enter] Apply  [Esc] Menu  [Ctrl+Q] Quit",
                    ChannelsConfigScreen.GroupChats => " [Type] IDs  [Space] Toggle  [Enter] Apply  [Esc] Menu",
                    ChannelsConfigScreen.Attachments => " [Space] Toggle & Save  [Esc] Menu",
                    ChannelsConfigScreen.DirectoryStatus => " [Esc] Menu  [Ctrl+Q] Quit",
                    ChannelsConfigScreen.DirectMessages => " [↑/↓] Navigate  [Space] Toggle  [←/→] Audience  [Enter] Apply  [Esc] Menu",
                    ChannelsConfigScreen.RotateCredentials => " [Tab/Shift+Tab] Field  [Enter] Apply  [Esc] Menu  [Ctrl+Q] Quit",
                    ChannelsConfigScreen.ResetConfirm => " [↑/↓] Navigate  [Enter] Select  [Esc] Menu  [Ctrl+Q] Quit",
                    _ => ViewModel.Step.IsInSubFlow
                        ? " [Enter] Next  [Esc] Back  [Ctrl+Q] Quit"
                        : " [↑/↓] Navigate  [Space] Toggle/Save  [Enter] Open/Done  [Esc] Back  [Ctrl+Q] Quit"
                };

            return NetclawTuiChrome.BuildKeyHintLine(text);
        });

        return _keyBindingsNode.Height(1);
    }

    public override bool HandlePageInput(ConsoleKeyInfo keyInfo)
    {
        if (base.HandlePageInput(keyInfo))
            return true;

        return HandleKeyInfo(keyInfo);
    }

    private bool HandleKeyInfo(ConsoleKeyInfo keyInfo)
    {
        if (ViewModel.IsGroupChatSaveInProgress)
            return true;

        if (ViewModel.Screen.Value == ChannelsConfigScreen.RotateCredentials
            && ViewModel.IsCredentialSaveInProgress)
        {
            _quitAfterCredentialSave |= keyInfo.Key == ConsoleKey.Q
                                      && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control);
            return true;
        }

        if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestQuit();
            return true;
        }

        if (keyInfo.Key == ConsoleKey.Escape)
        {
            ViewModel.GoBack();
            return true;
        }

        if (ViewModel.Screen.Value != ChannelsConfigScreen.Picker)
        {
            HandleManagementKey(keyInfo);
            return true;
        }

        if (TryOpenConfiguredAdapter(keyInfo))
            return true;

        if (keyInfo.Key == ConsoleKey.Spacebar && ViewModel.TryToggleSelectedAdapterFromPicker())
        {
            ViewModel.RequestRedraw();
            return true;
        }

        if (ViewModel.StepView.HandleKeyPress(new KeyPressed(keyInfo)))
        {
            ViewModel.RequestRedraw();
            return true;
        }

        return false;
    }

    private void HandleKeyPress(KeyPressed key)
        => HandleKeyInfo(key.KeyInfo);

    private void HandlePaste(PasteEvent paste)
    {
        if (ViewModel.IsGroupChatSaveInProgress)
            return;

        if (ViewModel.Screen.Value is ChannelsConfigScreen.AddChannel or ChannelsConfigScreen.TeamsTeamSearch or ChannelsConfigScreen.TeamsUserSearch or ChannelsConfigScreen.TeamsGroupSearch or ChannelsConfigScreen.TeamsGroupChatSearch or ChannelsConfigScreen.AllowedUsers or ChannelsConfigScreen.AllowedGroups or ChannelsConfigScreen.GroupChats)
        {
            _singleInput?.HandlePaste(paste);
            StageSingleInput();
            ViewModel.RequestRedraw();
            return;
        }

        if (ViewModel.Screen.Value == ChannelsConfigScreen.RotateCredentials)
        {
            var fields = ViewModel.GetCredentialFields();
            if (fields.Count > 0)
            {
                var field = fields[ViewModel.CredentialFieldIndex];
                if (_credentialInputs.TryGetValue(field.Key, out var input))
                {
                    input.HandlePaste(paste);
                    ViewModel.StageCredentialDraftValue(field.Key, input.Text);
                    ViewModel.RequestRedraw();
                }
            }

            return;
        }

        ViewModel.StepView.HandlePaste(paste);
        ViewModel.RequestRedraw();
    }

    private bool TryOpenConfiguredAdapter(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key is not (ConsoleKey.Enter or ConsoleKey.E))
            return false;

        if (!ViewModel.TryOpenSelectedAdapterManagement())
            return false;

        ViewModel.RequestRedraw();
        return true;
    }

    private void HandleManagementKey(ConsoleKeyInfo keyInfo)
    {
        switch (ViewModel.Screen.Value)
        {
            case ChannelsConfigScreen.AdapterMenu:
                HandleAdapterMenuKey(keyInfo);
                break;
            case ChannelsConfigScreen.ChannelPermissions:
                HandleChannelPermissionsKey(keyInfo);
                break;
            case ChannelsConfigScreen.AddChannel:
                HandleAddChannelKey(keyInfo);
                break;
            case ChannelsConfigScreen.TeamsDestinationAdd:
                HandleTeamsDestinationAddKey(keyInfo);
                break;
            case ChannelsConfigScreen.TeamsPrincipalAdd:
                HandleTeamsPrincipalAddKey(keyInfo);
                break;
            case ChannelsConfigScreen.TeamsPrincipalManagement:
                HandleTeamsPrincipalManagementKey(keyInfo);
                break;
            case ChannelsConfigScreen.TeamsPrincipalRemovalConfirm:
                HandleTeamsPrincipalRemovalConfirmKey(keyInfo);
                break;
            case ChannelsConfigScreen.TeamsChannelPrincipalRemovalConfirm:
                HandleTeamsChannelPrincipalRemovalConfirmKey(keyInfo);
                break;
            case ChannelsConfigScreen.TeamsDestinationRemovalConfirm:
                HandleTeamsDestinationRemovalConfirmKey(keyInfo);
                break;
            case ChannelsConfigScreen.TeamsTeamSearch:
                HandleTeamsTeamSearchKey(keyInfo);
                break;
            case ChannelsConfigScreen.TeamsChannelSearch:
                HandleTeamsChannelSearchKey(keyInfo);
                break;
            case ChannelsConfigScreen.TeamsUserSearch:
                HandleTeamsUserSearchKey(keyInfo);
                break;
            case ChannelsConfigScreen.TeamsGroupSearch:
                HandleTeamsGroupSearchKey(keyInfo);
                break;
            case ChannelsConfigScreen.TeamsGroupChatSearch:
                HandleTeamsGroupChatSearchKey(keyInfo);
                break;
            case ChannelsConfigScreen.TeamsChannelAccess:
                HandleTeamsChannelAccessKey(keyInfo);
                break;
            case ChannelsConfigScreen.AllowedUsers:
                HandleAllowedUsersKey(keyInfo);
                break;
            case ChannelsConfigScreen.AllowedGroups:
                HandleAllowedGroupsKey(keyInfo);
                break;
            case ChannelsConfigScreen.GroupChats:
                HandleGroupChatsKey(keyInfo);
                break;
            case ChannelsConfigScreen.Attachments:
                HandleAttachmentsKey(keyInfo);
                break;
            case ChannelsConfigScreen.DirectoryStatus:
                break;
            case ChannelsConfigScreen.DirectMessages:
                HandleDirectMessagesKey(keyInfo);
                break;
            case ChannelsConfigScreen.RotateCredentials:
                HandleRotateCredentialsKey(keyInfo);
                break;
            case ChannelsConfigScreen.ResetConfirm:
                HandleResetConfirmKey(keyInfo);
                break;
        }

        _contentNode?.Invalidate();
        _helpTextNode?.Invalidate();
        _keyBindingsNode?.Invalidate();
        ViewModel.RequestRedraw();
    }

    private void HandleAdapterMenuKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveManagementMenu(-1);
                break;
            case ConsoleKey.DownArrow:
                ViewModel.MoveManagementMenu(1);
                break;
            case ConsoleKey.Enter:
                ViewModel.ActivateManagementMenuItem();
                break;
        }
    }

    private void HandleChannelPermissionsKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveChannelRow(-1);
                break;
            case ConsoleKey.DownArrow:
                ViewModel.MoveChannelRow(1);
                break;
            case ConsoleKey.LeftArrow:
                ViewModel.ChangeSelectedChannelAudience(-1);
                break;
            case ConsoleKey.RightArrow:
                ViewModel.ChangeSelectedChannelAudience(1);
                break;
            case ConsoleKey.Spacebar:
                ViewModel.ToggleSelectedChannelMentionRequired();
                break;
            case ConsoleKey.Enter:
                ViewModel.ActivateSelectedChannelRow();
                break;
            case ConsoleKey.A:
                ViewModel.BeginAddChannel();
                break;
            case ConsoleKey.Delete:
                if (ViewModel.ActiveAdapterType == ChannelType.Teams)
                    ViewModel.BeginTeamsDestinationRemoval();
                else
                    ViewModel.RemoveSelectedChannel();
                break;
        }
    }

    private void HandleAddChannelKey(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key == ConsoleKey.Enter)
        {
            StageSingleInput();
            // Fire-and-forget: the add resolves channels against the platform API, so it runs async
            // off the loop (ViewModel serializes the write). Blocking here would freeze the TUI.
            _ = ViewModel.AddChannelFromInputAsync();
            return;
        }

        _singleInput?.HandleInput(keyInfo);
        StageSingleInput();
    }

    private void HandleTeamsDestinationAddKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveTeamsDestinationAdd(-1);
                break;
            case ConsoleKey.DownArrow:
                ViewModel.MoveTeamsDestinationAdd(1);
                break;
            case ConsoleKey.Enter:
                ViewModel.ActivateTeamsDestinationAdd();
                break;
        }
    }

    private void HandleTeamsPrincipalAddKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveTeamsPrincipalAdd(-1);
                break;
            case ConsoleKey.DownArrow:
                ViewModel.MoveTeamsPrincipalAdd(1);
                break;
            case ConsoleKey.Enter:
                ViewModel.ActivateTeamsPrincipalAdd();
                break;
        }
    }

    private void HandleTeamsPrincipalManagementKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveTeamsPrincipalManagement(-1);
                break;
            case ConsoleKey.DownArrow:
                ViewModel.MoveTeamsPrincipalManagement(1);
                break;
            case ConsoleKey.LeftArrow:
                ViewModel.ChangeTeamsPrincipalFilter(-1);
                break;
            case ConsoleKey.RightArrow:
                ViewModel.ChangeTeamsPrincipalFilter(1);
                break;
            case ConsoleKey.Enter:
                ViewModel.ActivateTeamsPrincipalManagement();
                break;
        }
    }

    private void HandleTeamsPrincipalRemovalConfirmKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveTeamsPrincipalRemoval(-1);
                break;
            case ConsoleKey.DownArrow:
                ViewModel.MoveTeamsPrincipalRemoval(1);
                break;
            case ConsoleKey.Enter:
                ViewModel.ConfirmTeamsPrincipalRemoval(ViewModel.TeamsPrincipalRemovalIndex == 1);
                break;
        }
    }

    private void HandleTeamsChannelPrincipalRemovalConfirmKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveTeamsChannelPrincipalRemoval(-1);
                break;
            case ConsoleKey.DownArrow:
                ViewModel.MoveTeamsChannelPrincipalRemoval(1);
                break;
            case ConsoleKey.Enter:
                ViewModel.ConfirmTeamsChannelPrincipalRemoval(ViewModel.TeamsChannelPrincipalRemovalIndex == 1);
                break;
        }
    }

    private void HandleTeamsDestinationRemovalConfirmKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveTeamsDestinationRemoval(-1);
                break;
            case ConsoleKey.DownArrow:
                ViewModel.MoveTeamsDestinationRemoval(1);
                break;
            case ConsoleKey.Enter:
                ViewModel.ConfirmTeamsDestinationRemoval(ViewModel.TeamsDestinationRemovalIndex == 1);
                break;
        }
    }

    private void HandleTeamsTeamSearchKey(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key == ConsoleKey.UpArrow)
        {
            ViewModel.MoveDirectoryResult(-1);
            return;
        }

        if (keyInfo.Key == ConsoleKey.DownArrow)
        {
            ViewModel.MoveDirectoryResult(1);
            return;
        }

        if (keyInfo.Key == ConsoleKey.Enter)
        {
            StageSingleInput();
            if (ViewModel.IsAdvancedTeamsDirectoryActionSelected()
                && string.IsNullOrWhiteSpace(ViewModel.DirectorySearchInput))
                ViewModel.BeginManualTeamsChannelEntry();
            else if (ViewModel.TeamSearchResults.Count == 0)
                _ = ViewModel.SearchTeamsFromInputAsync();
            else if (ViewModel.IsAdvancedTeamsDirectoryActionSelected())
                ViewModel.BeginManualTeamsChannelEntry();
            else
                _ = ViewModel.SelectTeamAndSearchChannelsAsync();
            return;
        }

        ViewModel.ResetTeamsTeamSearchResults();
        _singleInput?.HandleInput(keyInfo);
        StageSingleInput();
    }

    private void HandleTeamsChannelSearchKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveDirectoryResult(-1);
                break;
            case ConsoleKey.DownArrow:
                ViewModel.MoveDirectoryResult(1);
                break;
            case ConsoleKey.Enter:
                ViewModel.SaveSelectedTeamsChannel();
                break;
        }
    }

    private void HandleTeamsUserSearchKey(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key == ConsoleKey.UpArrow)
        {
            ViewModel.MoveDirectoryResult(-1);
            return;
        }

        if (keyInfo.Key == ConsoleKey.DownArrow)
        {
            ViewModel.MoveDirectoryResult(1);
            return;
        }

        if (keyInfo.Key == ConsoleKey.Enter)
        {
            StageSingleInput();
            if (ViewModel.IsAdvancedTeamsDirectoryActionSelected()
                && string.IsNullOrWhiteSpace(ViewModel.DirectorySearchInput))
                ViewModel.BeginAdvancedTeamsUserEntry();
            else if (ViewModel.UserSearchResults.Count == 0)
                _ = ViewModel.SearchUsersFromInputAsync();
            else
                ViewModel.AddSelectedTeamsUser();
            return;
        }

        ViewModel.ResetTeamsPrincipalSearchResults();
        _singleInput?.HandleInput(keyInfo);
        StageSingleInput();
    }

    private void HandleTeamsGroupSearchKey(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key == ConsoleKey.UpArrow)
        {
            ViewModel.MoveDirectoryResult(-1);
            return;
        }

        if (keyInfo.Key == ConsoleKey.DownArrow)
        {
            ViewModel.MoveDirectoryResult(1);
            return;
        }

        if (keyInfo.Key == ConsoleKey.Enter)
        {
            StageSingleInput();
            if (ViewModel.IsAdvancedTeamsDirectoryActionSelected()
                && string.IsNullOrWhiteSpace(ViewModel.DirectorySearchInput))
                ViewModel.BeginManualTeamsGroupEntry();
            else if (ViewModel.GroupSearchResults.Count == 0)
                _ = ViewModel.SearchGroupsFromInputAsync();
            else
                ViewModel.AddSelectedTeamsGroup();
            return;
        }

        ViewModel.ResetTeamsPrincipalSearchResults();
        _singleInput?.HandleInput(keyInfo);
        StageSingleInput();
    }

    private void HandleTeamsGroupChatSearchKey(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key == ConsoleKey.S && (keyInfo.Modifiers & ConsoleModifiers.Control) != 0)
        {
            ViewModel.StopGroupChatSearch();
            return;
        }

        if (keyInfo.Key == ConsoleKey.UpArrow)
        {
            ViewModel.MoveDirectoryResult(-1);
            return;
        }

        if (keyInfo.Key == ConsoleKey.DownArrow)
        {
            ViewModel.MoveDirectoryResult(1);
            return;
        }

        if (keyInfo.Key != ConsoleKey.Enter)
        {
            _singleInput?.HandleInput(keyInfo);
            StageSingleInput();
            return;
        }

        if (ViewModel.DirectoryResultIndex < ViewModel.FilteredGroupChatSearchResults.Count)
            ViewModel.SelectGroupChatForReview();
        else if (ViewModel.DirectoryResultIndex == ViewModel.GroupChatSearchActionIndex)
            _ = ViewModel.SearchGroupChatsFromInputAsync();
        else if (ViewModel.DirectoryResultIndex == ViewModel.GroupChatSearchActionIndex + 1 && ViewModel.IsGroupChatSearchRunning)
            ViewModel.StopGroupChatSearch();
        else
            ViewModel.BeginManualGroupChatEntry();
    }

    private void HandleTeamsChannelAccessKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveChannelAccessRow(-1);
                break;
            case ConsoleKey.DownArrow:
                ViewModel.MoveChannelAccessRow(1);
                break;
            case ConsoleKey.Enter:
                ViewModel.ActivateChannelAccessRow();
                break;
        }
    }

    private void HandleAllowedUsersKey(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key == ConsoleKey.Enter)
        {
            StageSingleInput();
            ViewModel.ApplyAllowedUsers();
            return;
        }

        _singleInput?.HandleInput(keyInfo);
        StageSingleInput();
    }

    private void HandleAllowedGroupsKey(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key == ConsoleKey.Enter)
        {
            StageSingleInput();
            ViewModel.ApplyAllowedGroups();
            return;
        }

        _singleInput?.HandleInput(keyInfo);
        StageSingleInput();
    }

    private void HandleGroupChatsKey(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key == ConsoleKey.Spacebar)
        {
            ViewModel.ToggleGroupChats();
            return;
        }

        if (keyInfo.Key == ConsoleKey.Enter)
        {
            StageSingleInput();
            ViewModel.ApplyGroupChats();
            return;
        }

        _singleInput?.HandleInput(keyInfo);
        StageSingleInput();
    }

    private void HandleAttachmentsKey(ConsoleKeyInfo keyInfo)
    {
        if (keyInfo.Key == ConsoleKey.Spacebar)
            ViewModel.ToggleAttachments();
    }

    private void HandleDirectMessagesKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveDirectMessagesRow(-1);
                break;
            case ConsoleKey.DownArrow:
                ViewModel.MoveDirectMessagesRow(1);
                break;
            case ConsoleKey.Spacebar when ViewModel.DirectMessagesRowIndex == 0:
                ViewModel.ToggleDirectMessages();
                break;
            case ConsoleKey.LeftArrow when ViewModel.DirectMessagesRowIndex == 1:
                ViewModel.ChangeDirectMessageAudience(-1);
                break;
            case ConsoleKey.RightArrow when ViewModel.DirectMessagesRowIndex == 1:
                ViewModel.ChangeDirectMessageAudience(1);
                break;
            case ConsoleKey.Enter:
                ViewModel.ApplyDirectMessages();
                break;
        }
    }

    private void HandleRotateCredentialsKey(ConsoleKeyInfo keyInfo)
    {
        var fields = ViewModel.GetCredentialFields();
        if (fields.Count == 0)
            return;

        if (keyInfo.Key == ConsoleKey.Tab)
        {
            StageCredentialInput(fields[ViewModel.CredentialFieldIndex]);
            ViewModel.MoveCredentialField(keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1);
            return;
        }

        if (keyInfo.Key == ConsoleKey.Enter)
        {
            StageAllCredentialInputs();
            _ = ViewModel.ApplyCredentialsAsync();
            return;
        }

        var field = fields[ViewModel.CredentialFieldIndex];
        var input = EnsureCredentialInput(field);
        input.HandleInput(keyInfo);
        StageCredentialInput(field);
    }

    private void HandleResetConfirmKey(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                ViewModel.MoveResetConfirmation(-1);
                break;
            case ConsoleKey.DownArrow:
                ViewModel.MoveResetConfirmation(1);
                break;
            case ConsoleKey.Enter:
                // Fire-and-forget: the reset cancels-and-awaits any in-flight label refresh before
                // writing, so it runs async off the loop (ViewModel serializes the write).
                _ = ViewModel.ResetConfirmationFromInputAsync();
                break;
        }
    }

    private StepViewCallbacks CreateCallbacks()
        => new()
        {
            Subscriptions = _stepSubs,
            InvalidateContent = () => _contentNode?.Invalidate(),
            InvalidateHelp = () => _helpTextNode?.Invalidate(),
            AdvanceStep = ViewModel.GoNext,
            RequestRedraw = ViewModel.RequestRedraw,
            SetStatusMessage = message => ViewModel.Status.Value = new ConfigStatusMessage(message, ConfigStatusTone.Error),
        };

    private void InvalidateAll()
    {
        _contentNode?.Invalidate();
        _helpTextNode?.Invalidate();
        _keyBindingsNode?.Invalidate();
    }

    private TextInputNode EnsureSingleInput(
        ChannelsConfigScreen screen,
        string key,
        string? seed,
        string placeholder)
    {
        if (_singleInput is not null && _singleInputScreen == screen && string.Equals(_singleInputKey, key, StringComparison.Ordinal))
        {
            WizardStepHelpers.SyncInputToViewModel(_singleInput, StageSingleInput, CreateCallbacks());
            return _singleInput;
        }

        _singleInput = new TextInputNode().WithPlaceholder(placeholder);
        _singleInput.Text = seed ?? string.Empty;
        if (!string.IsNullOrEmpty(_singleInput.Text))
            _singleInput.HandleInput(new ConsoleKeyInfo('\0', ConsoleKey.End, shift: false, alt: false, control: false));
        _singleInputScreen = screen;
        _singleInputKey = key;
        WizardStepHelpers.SyncInputToViewModel(_singleInput, StageSingleInput, CreateCallbacks());
        return _singleInput;
    }

    private TextInputNode EnsureCredentialInput(CredentialFieldSpec field)
    {
        if (_credentialInputAdapter != ViewModel.ActiveAdapterType)
        {
            _credentialInputs.Clear();
            _credentialInputAdapter = ViewModel.ActiveAdapterType;
        }

        if (_credentialInputs.TryGetValue(field.Key, out var existing))
        {
            WizardStepHelpers.SyncInputToViewModel(existing, () => StageCredentialInput(field), CreateCallbacks());
            return existing;
        }

        var input = new TextInputNode().WithPlaceholder(field.Placeholder);
        if (field.IsSecret)
            input.AsPassword();

        input.Text = ViewModel.GetCredentialDraftValue(field.Key) ?? string.Empty;
        if (!string.IsNullOrEmpty(input.Text))
            input.HandleInput(new ConsoleKeyInfo('\0', ConsoleKey.End, shift: false, alt: false, control: false));

        _credentialInputs[field.Key] = input;
        WizardStepHelpers.SyncInputToViewModel(input, () => StageCredentialInput(field), CreateCallbacks());
        return input;
    }

    private void StageSingleInput()
    {
        if (_singleInputScreen == ChannelsConfigScreen.AddChannel)
            ViewModel.AddChannelInput = _singleInput?.Text;
        else if (_singleInputScreen == ChannelsConfigScreen.TeamsTeamSearch)
            ViewModel.StageTeamsDirectorySearchInput(_singleInput?.Text);
        else if (_singleInputScreen is ChannelsConfigScreen.TeamsUserSearch or ChannelsConfigScreen.TeamsGroupSearch)
            ViewModel.StageTeamsDirectorySearchInput(_singleInput?.Text);
        else if (_singleInputScreen == ChannelsConfigScreen.TeamsGroupChatSearch)
            ViewModel.GroupChatSearchInput = _singleInput?.Text;
        else if (_singleInputScreen == ChannelsConfigScreen.AllowedUsers)
            ViewModel.AllowedUsersInput = _singleInput?.Text;
        else if (_singleInputScreen == ChannelsConfigScreen.AllowedGroups)
            ViewModel.AllowedGroupsInput = _singleInput?.Text;
        else if (_singleInputScreen == ChannelsConfigScreen.GroupChats)
            ViewModel.AllowedGroupChatsInput = _singleInput?.Text;
    }

    private void StageCredentialInput(CredentialFieldSpec field)
    {
        if (_credentialInputs.TryGetValue(field.Key, out var input))
            ViewModel.StageCredentialDraftValue(field.Key, input.Text);
    }

    private void StageAllCredentialInputs()
    {
        foreach (var field in ViewModel.GetCredentialFields())
            StageCredentialInput(field);
    }

    private void ResetTextInputs()
    {
        _singleInput = null;
        _singleInputScreen = null;
        _singleInputKey = null;
        _credentialInputs.Clear();
        _credentialInputAdapter = null;
    }

    private static TextNode Header(string text) => new TextNode(text).WithForeground(Color.White).Bold();
    private static TextNode Hint(string text) => new TextNode(text).WithForeground(Color.BrightBlack);

    // Constant indent so non-selected rows keep the same content column the
    // focused full-width bar uses (the bar replaces the old ▶ marker).
    private static string FocusPrefix(bool focused) => "   ";
    private static string Check(bool enabled) => enabled ? "✓" : " ";

    private static ILayoutNode Row(string line, bool focused, bool enabled = true)
        => ConfigSelectionRow.Create(line, focused, enabled ? Color.White : Color.BrightBlack);

    private static string AudienceLabel(TrustAudience audience) => audience switch
    {
        TrustAudience.Personal => "Personal",
        TrustAudience.Team => "Team",
        TrustAudience.Public => "Public",
        _ => audience.ToString()
    };

    private static string AudienceDescription(TrustAudience audience) => audience switch
    {
        TrustAudience.Personal => "Private operator or owner-only context.",
        TrustAudience.Team => "Trusted internal channel.",
        TrustAudience.Public => "Untrusted or broad audience with strict controls.",
        _ => string.Empty
    };

    private static string AudienceCycle(TrustAudience audience) => $"[◀ {AudienceLabel(audience),-8} ▶]";

    private static string FormatTeamsUser(TeamsDirectoryUser user)
    {
        var principal = user.UserPrincipalName ?? user.Mail;
        return !string.IsNullOrWhiteSpace(user.DisplayName) && !string.IsNullOrWhiteSpace(principal)
            ? $"{user.DisplayName} <{principal}>"
            : principal ?? user.DisplayName ?? "User";
    }

    private static string FormatTeamsGroup(TeamsDirectoryGroup group)
    {
        var label = group.DisplayName ?? group.Mail ?? "Group";
        return $"{label} · {group.Kind}";
    }

    // Arrow-free so it reads as a Space toggle, not a ←/→ cycler like the audience field.
    private static string MentionField(bool required) => $"Require @mention: {(required ? "On" : "Off")}";

    private static string Column(string value, int width)
    {
        if (value.Length <= width)
            return value.PadRight(width);

        return width <= 3
            ? value[..width]
            : string.Concat(value.AsSpan(0, width - 3), "...");
    }

    private static Color ToColor(ConfigStatusTone tone) => tone switch
    {
        ConfigStatusTone.Success => Color.Green,
        ConfigStatusTone.Warning => Color.Yellow,
        ConfigStatusTone.Error => Color.Red,
        _ => Color.White,
    };

    public override void Dispose()
    {
        _stepSubs.Dispose();
        base.Dispose();
    }
}
