// -----------------------------------------------------------------------
// <copyright file="ChannelsConfigPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
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
        ViewModel.Status.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
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
                    ChannelsConfigScreen.AllowedUsers => BuildAllowedUsers(),
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
            .WithChild(Hint($"  {ViewModel.GetActiveAdapterSummary()}"))
            .WithChild(Layouts.Empty().Height(1))
            .WithChild(new TextNode("  What would you like to do?").WithForeground(Color.White))
            .WithChild(Layouts.Empty().Height(1));

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
            .WithChild(Hint("  Configure allowed channels, their audience, and thread behavior."))
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

        var description = Layouts.Vertical()
            .WithChild(Hint($"  {AudienceLabel(row.Audience)} — {AudienceDescription(row.Audience)}"));

        if (!row.IsDirectMessage)
            description = description.WithChild(Hint(row.MentionRequired
                ? "  Require @mention: bot stays quiet until @mentioned, then catches up on the thread."
                : "  Require @mention off: bot replies to every message in the thread (default)."));

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
            .WithChild(Header($"  {ViewModel.ActiveAdapterName} > Credentials"))
            .WithChild(Hint("  Secret fields are blank by design. Leave blank to keep existing secrets."))
            .WithChild(Layouts.Empty().Height(1));

        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var input = EnsureCredentialInput(field);
            if (i == ViewModel.CredentialFieldIndex)
                Focus.SetFocus(input);

            layout = layout
                .WithChild(new TextNode($"  {field.Label}:").WithForeground(i == ViewModel.CredentialFieldIndex ? Color.Cyan : Color.White))
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
                    ChannelsConfigScreen.ChannelPermissions => "  Left/right sets audience. Space toggles Require @mention. Enter on Done finishes. a adds, Delete removes.",
                    ChannelsConfigScreen.AddChannel => "  Enter applies the channel draft. Esc cancels.",
                    ChannelsConfigScreen.AllowedUsers => "  Use comma-separated user IDs. Blank means unrestricted users in allowed channels.",
                    ChannelsConfigScreen.DirectMessages => "  Space toggles DMs. Left/right changes the DM audience.",
                    ChannelsConfigScreen.RotateCredentials => "  Blank secret fields preserve existing secrets. Tab switches fields.",
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
                    ChannelsConfigScreen.AllowedUsers => " [Enter] Apply  [Esc] Menu  [Ctrl+Q] Quit",
                    ChannelsConfigScreen.DirectMessages => " [↑/↓] Navigate  [Space] Toggle  [←/→] Audience  [Enter] Apply  [Esc] Menu",
                    ChannelsConfigScreen.RotateCredentials => " [Tab] Field  [Enter] Apply  [Esc] Menu  [Ctrl+Q] Quit",
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
        if (ViewModel.Screen.Value is ChannelsConfigScreen.AddChannel or ChannelsConfigScreen.AllowedUsers)
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
            case ChannelsConfigScreen.AllowedUsers:
                HandleAllowedUsersKey(keyInfo);
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
            ViewModel.MoveCredentialField(1);
            return;
        }

        if (keyInfo.Key == ConsoleKey.Enter)
        {
            StageCredentialInput(fields[ViewModel.CredentialFieldIndex]);
            ViewModel.ApplyCredentials();
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
            return _singleInput;

        _singleInput = new TextInputNode().WithPlaceholder(placeholder);
        _singleInput.Text = seed ?? string.Empty;
        if (!string.IsNullOrEmpty(_singleInput.Text))
            _singleInput.HandleInput(new ConsoleKeyInfo('\0', ConsoleKey.End, shift: false, alt: false, control: false));
        _singleInputScreen = screen;
        _singleInputKey = key;
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
            return existing;

        var input = new TextInputNode().WithPlaceholder(field.Placeholder);
        if (field.IsSecret)
            input.AsPassword();

        input.Text = ViewModel.GetCredentialDraftValue(field.Key) ?? string.Empty;
        if (!string.IsNullOrEmpty(input.Text))
            input.HandleInput(new ConsoleKeyInfo('\0', ConsoleKey.End, shift: false, alt: false, control: false));

        _credentialInputs[field.Key] = input;
        return input;
    }

    private void StageSingleInput()
    {
        if (_singleInputScreen == ChannelsConfigScreen.AddChannel)
            ViewModel.AddChannelInput = _singleInput?.Text;
        else if (_singleInputScreen == ChannelsConfigScreen.AllowedUsers)
            ViewModel.AllowedUsersInput = _singleInput?.Text;
    }

    private void StageCredentialInput(CredentialFieldSpec field)
    {
        if (_credentialInputs.TryGetValue(field.Key, out var input))
            ViewModel.StageCredentialDraftValue(field.Key, input.Text);
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
