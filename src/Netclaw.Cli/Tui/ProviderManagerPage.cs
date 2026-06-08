// -----------------------------------------------------------------------
// <copyright file="ProviderManagerPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Providers;
using Netclaw.Providers.OAuth;
using R3;
using Termina.Clipboard;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Termina page for the <c>netclaw provider</c> interactive TUI.
/// Shows all known provider types as a dashboard and provides
/// context-sensitive actions based on provider state.
/// </summary>
public sealed class ProviderManagerPage : ReactivePage<ProviderManagerViewModel>
{
    private readonly IClipboardService? _clipboardService;

    public ProviderManagerPage(IClipboardService? clipboardService = null)
    {
        _clipboardService = clipboardService;
    }

    private SelectionListNode<string>? _providerList;
    private SelectionListNode<string>? _authList;
    private TextInputNode? _apiKeyInput;
    private TextInputNode? _endpointInput;
    private TextInputNode? _nameInput;
    private TextInputNode? _renameInput;
    private SelectionListNode<string>? _confirmList;

    private IFocusable? _lastFocusedList;
    private TextInputNode? _lastFocusedInput;
    private DynamicLayoutNode? _contentNode;
    private readonly CompositeDisposable _stepSubs = [];

    protected override void OnBound()
    {
        base.OnBound();

        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
    {
        return Layouts.Vertical()
            .WithChild(
                new PanelNode()
                    .WithTitle("Provider Manager")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Cyan)
                    .WithContent(BuildInnerLayout())
                    .Fill());
    }

    private ILayoutNode BuildInnerLayout()
    {
        return Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(BuildContent().Fill())
            .WithChild(BuildStatusBar())
            .WithChild(BuildKeyBindings());
    }

    private LayoutNode BuildContent()
    {
        _contentNode = new DynamicLayoutNode(() =>
        {
            _lastFocusedList = null;
            _lastFocusedInput = null;
            _stepSubs.Clear();

            return ViewModel.CurrentState.Value switch
            {
                ProviderManagerState.Loading => BuildLoadingView(),
                ProviderManagerState.List => BuildProviderListView(),
                ProviderManagerState.AddSelectType => BuildAddSelectTypeView(),
                ProviderManagerState.AddName => BuildAddNameView(),
                ProviderManagerState.AddSelectAuth => BuildAddAuthView(),
                ProviderManagerState.AddCredentials => BuildCredentialsView(),
                ProviderManagerState.AddOAuthDeviceFlow => BuildOAuthDeviceFlowView(),
                ProviderManagerState.AddBrowserOAuthFlow => BuildBrowserOAuthFlowView(),
                ProviderManagerState.AddValidating => BuildValidatingView(),
                ProviderManagerState.AddComplete => BuildAddCompleteView(),
                ProviderManagerState.Details => BuildDetailsView(),
                ProviderManagerState.RenameProvider => BuildRenameView(),
                ProviderManagerState.FixCredentials => BuildFixCredentialsView(),
                ProviderManagerState.RemoveConfirm => BuildRemoveConfirmView(),
                _ => Layouts.Empty()
            };
        });

        ViewModel.StateVersion
            .Subscribe(_ => _contentNode.Invalidate())
            .DisposeWith(Subscriptions);

        // Spinners during loading/validation/OAuth self-animate via SpinnerNode
        // (see SpinnerViews) and propagate their own redraws up the layout tree —
        // no per-surface tick subscription required.

        return _contentNode;
    }

    private LayoutNode BuildStatusBar()
    {
        // Combine StatusMessage (success/green) with ErrorMessage (error/red).
        // ErrorMessage wins when both are set so the user sees the latest
        // validation feedback immediately.
        return ViewModel.ErrorMessage
            .CombineLatest(ViewModel.StatusMessage, (err, status) => (err, status))
            .Select(t => (ILayoutNode)(!string.IsNullOrWhiteSpace(t.err)
                ? new TextNode($"  {t.err}").WithForeground(Color.Red)
                : !string.IsNullOrWhiteSpace(t.status)
                    ? new TextNode($"  {t.status}").WithForeground(Color.Green)
                    : Layouts.Empty()))
            .AsLayout()
            .Height(1);
    }

    private LayoutNode BuildKeyBindings()
    {
        return ViewModel.CurrentState
            .Select(state =>
            {
                var text = state switch
                {
                    ProviderManagerState.Loading =>
                        " Checking providers...  [Ctrl+Q] Quit",
                    ProviderManagerState.List =>
                        " [\u2191/\u2193] Navigate  [Enter] Select  [Esc] Quit  [Ctrl+Q] Quit",
                    ProviderManagerState.AddSelectType =>
                        " [\u2191/\u2193] Navigate  [Enter] Select  [Esc] Back  [Ctrl+Q] Quit",
                    ProviderManagerState.AddName =>
                        " [Enter] Continue  [Esc] Cancel  [Ctrl+Q] Quit",
                    ProviderManagerState.Details =>
                        " [K] Update key  [N] Rename  [R] Remove  [V] Re-validate  [Esc] Back  [Ctrl+Q] Quit",
                    ProviderManagerState.RenameProvider =>
                        " [Enter] Confirm rename  [Esc] Cancel  [Ctrl+Q] Quit",
                    ProviderManagerState.RemoveConfirm =>
                        " [Enter] Confirm  [Esc] Cancel  [Ctrl+Q] Quit",
                    ProviderManagerState.AddComplete =>
                        " [Enter] Continue  [Esc] Back  [Ctrl+Q] Quit",
                    ProviderManagerState.AddOAuthDeviceFlow =>
                        " [Esc] Cancel  [Ctrl+Q] Quit",
                    _ =>
                        " [\u2191/\u2193] Navigate  [Enter] Next  [Esc] Back  [Ctrl+Q] Quit"
                };
                return (ILayoutNode)new TextNode(text).WithForeground(Color.BrightBlack);
            })
            .AsLayout()
            .Height(1);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Content views
    // ═══════════════════════════════════════════════════════════════════

    private ILayoutNode BuildLoadingView()
    {
        var children = Layouts.Vertical();
        children.WithChild(SpinnerViews.Labeled("Checking configured providers...", Color.Yellow));
        children.WithChild(new TextNode("").Height(1));

        foreach (var item in ViewModel.DisplayProviders)
        {
            if (!item.IsConfigured) continue;

            var label = item.ConfiguredName is not null
                ? $"{item.ConfiguredName} ({item.DisplayName})"
                : item.DisplayName;

            // Still-probing providers get a live spinner; completed ones a glyph.
            children.WithChild(item.Health switch
            {
                ProviderHealthStatus.Healthy => (ILayoutNode)new TextNode($"  \u2713 {label}").WithForeground(Color.Green),
                ProviderHealthStatus.Unhealthy => new TextNode($"  \u26a0 {label}").WithForeground(Color.Red),
                _ => SpinnerViews.Labeled(label, Color.Yellow)
            });
        }

        return children;
    }

    private const string AddNewProviderSentinel = "  + Add new provider...";

    private ILayoutNode BuildProviderListView()
    {
        var items = ViewModel.DisplayProviders
            .Select(p =>
            {
                if (p.IsConfigured)
                {
                    var statusChar = p.Health switch
                    {
                        ProviderHealthStatus.Healthy => "\u2713",
                        ProviderHealthStatus.Unhealthy => "\u26a0",
                        ProviderHealthStatus.Probing => "\u2026",
                        _ => " "
                    };

                    var nameLabel = $"{p.ConfiguredName} ({p.DisplayName})";
                    return $"{statusChar} {nameLabel,-36} {p.DisplayAuth,-12} {p.DisplayEndpoint}";
                }

                return $"  {p.DisplayName,-36} {"(not configured)",-12}";
            })
            .ToList();

        items.Add(AddNewProviderSentinel);

        _providerList = Layouts.SelectionList(items)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _providerList.OnFocused();
        _lastFocusedList = _providerList;

        _providerList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    if (selected[0] == AddNewProviderSentinel)
                    {
                        ViewModel.StartAddNewProvider();
                    }
                    else
                    {
                        var idx = items.IndexOf(selected[0]);
                        if (idx >= 0)
                        {
                            ViewModel.SelectedProviderIndex = idx;
                            ViewModel.ActivateSelectedProvider();
                        }
                    }
                }
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode($"  {"",2}{"Provider",-36} {"Auth",-12} Endpoint")
                .WithForeground(Color.White).Bold())
            .WithChild(_providerList.WithFillHeight());
    }

    private ILayoutNode BuildAddSelectTypeView()
    {
        var registry = ViewModel.Registry;
        var displayToTypeKey = registry.KnownTypeKeys
            .ToDictionary(k => registry.Get(k).DisplayName, k => k);

        _providerList = Layouts.SelectionList(displayToTypeKey.Keys.ToList())
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _providerList.OnFocused();
        _lastFocusedList = _providerList;

        _providerList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0 && displayToTypeKey.TryGetValue(selected[0], out var typeKey))
                {
                    ViewModel.StartAddForType(typeKey);
                }
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Select provider type to add:").WithForeground(Color.White))
            .WithChild(_providerList);
    }

    private ILayoutNode BuildAddNameView()
    {
        var providerType = ViewModel.NewProviderType ?? "unknown";
        var descriptor = ViewModel.Registry.Get(providerType);

        var children = Layouts.Vertical();
        children.WithChild(new TextNode("  Name your provider").WithForeground(Color.White).Bold());
        children.WithChild(new TextNode("").Height(1));
        children.WithChild(new TextNode($"  Type: {descriptor.DisplayName}").WithForeground(Color.White));
        children.WithChild(new TextNode("").Height(1));

        _nameInput = new TextInputNode().WithPlaceholder($"my-{providerType}");
        _nameInput.Text = ViewModel.NewProviderName ?? string.Empty;
        // Termina's Text setter leaves the cursor at position 0. Synthesize
        // End so the user can immediately edit the suffix instead of having
        // their first keystroke insert before the pre-filled name.
        _nameInput.HandleInput(new ConsoleKeyInfo('\0', ConsoleKey.End, shift: false, alt: false, control: false));
        _nameInput.OnFocused();
        _lastFocusedInput = _nameInput;

        _nameInput.Submitted
            .Subscribe(text =>
            {
                if (ViewModel.TrySetNewProviderName(text, out var error))
                {
                    ViewModel.ErrorMessage.Value = "";
                    ViewModel.AdvanceAfterName();
                }
                else
                {
                    ViewModel.ErrorMessage.Value = error;
                    ViewModel.RequestRedraw();
                }
            })
            .DisposeWith(_stepSubs);

        children.WithChild(new PanelNode()
            .WithTitle("Name")
            .WithBorder(BorderStyle.Rounded)
            .WithBorderColor(Color.Gray)
            .WithContent(_nameInput)
            .Height(3));

        children.WithChild(new TextNode("").Height(1));
        children.WithChild(new TextNode("  This is how the provider appears in `netclaw provider list`")
            .WithForeground(Color.Gray));
        children.WithChild(new TextNode("  and how model roles reference it. Press [Enter] to continue.")
            .WithForeground(Color.Gray));

        return children;
    }

    private ILayoutNode BuildAddAuthView()
    {
        var providerType = ViewModel.NewProviderType ?? "unknown";
        var descriptor = ViewModel.Registry.Get(providerType);
        var supportedMethods = OAuthFlowViews.BuildAuthMethodLabels(descriptor.Auth);

        _authList = Layouts.SelectionList(supportedMethods)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _authList.OnFocused();
        _lastFocusedList = _authList;

        _authList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    ViewModel.SelectAuthMethod(OAuthFlowViews.ParseAuthMethodLabel(selected[0], descriptor.Auth));
                }
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode($"  Authentication for {descriptor.DisplayName}:")
                .WithForeground(Color.White))
            .WithChild(_authList);
    }

    private ILayoutNode BuildCredentialsView()
    {
        var children = Layouts.Vertical();
        var providerType = ViewModel.NewProviderType ?? "unknown";
        var descriptor = ViewModel.Registry.Get(providerType);

        children.WithChild(new TextNode($"  Provider: {descriptor.DisplayName} (name: {ViewModel.NewProviderName})")
            .WithForeground(Color.White));

        if (descriptor.Auth is ApiKeyAuth or MultiAuth)
        {
            children.WithChild(new TextNode("").Height(1));
            children.WithChild(new TextNode("  API Key:").WithForeground(Color.White));

            _apiKeyInput = new TextInputNode()
                .AsPassword()
                .WithPlaceholder($"Enter {providerType} API key...");
            _apiKeyInput.OnFocused();
            _lastFocusedInput = _apiKeyInput;

            _apiKeyInput.Submitted
                .Subscribe(text =>
                {
                    ViewModel.NewApiKey = text;
                    ViewModel.SubmitCredentials();
                })
                .DisposeWith(_stepSubs);

            children.WithChild(new PanelNode()
                .WithTitle("API Key")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_apiKeyInput)
                .Height(3));

            if (descriptor.Auth.GetApiKeyGuidanceUrl() is { } guidanceUrl)
            {
                children.WithChild(new TextNode("").Height(1));
                children.WithChild(new TextNode($"  Get your API key at {guidanceUrl}")
                    .WithForeground(Color.Gray));
            }
        }
        else if (descriptor.Auth is EndpointOnlyAuth)
        {
            children.WithChild(new TextNode("").Height(1));
            children.WithChild(new TextNode($"  Endpoint (default: {descriptor.DefaultEndpoint}):")
                .WithForeground(Color.White));

            _endpointInput = new TextInputNode()
                .WithPlaceholder(descriptor.DefaultEndpoint);
            _endpointInput.OnFocused();
            _lastFocusedInput = _endpointInput;

            _endpointInput.Submitted
                .Subscribe(text =>
                {
                    ViewModel.NewEndpoint = string.IsNullOrWhiteSpace(text) ? null : text;
                    ViewModel.SubmitCredentials();
                })
                .DisposeWith(_stepSubs);

            children.WithChild(new PanelNode()
                .WithTitle("Endpoint")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_endpointInput)
                .Height(3));

            children.WithChild(new TextNode("").Height(1));
            children.WithChild(new TextNode($"  {descriptor.DisplayName} runs locally. No authentication required.")
                .WithForeground(Color.Gray));
        }

        return children;
    }

    private ILayoutNode BuildOAuthDeviceFlowView()
    {
        var children = Layouts.Vertical();
        var providerType = ViewModel.NewProviderType ?? "unknown";
        var flowState = ViewModel.OAuth.FlowState.Value;

        children.WithChild(new TextNode($"  OAuth Device Flow for {ViewModel.Registry.Get(providerType).DisplayName}")
            .WithForeground(Color.White).Bold());
        children.WithChild(new TextNode("").Height(1));

        switch (flowState)
        {
            case DeviceFlowState.NotStarted:
                children.WithChild(new TextNode("  Starting device authorization...")
                    .WithForeground(Color.Yellow));
                break;

            case DeviceFlowState.WaitingForUser:
            case DeviceFlowState.Polling:
            {
                // Prefer verification_uri_complete (RFC 8628 §3.3.1, with user
                // code embedded) so [O] opens a one-click-complete URL.
                var displayUri = ViewModel.OAuth.VerificationUriComplete ?? ViewModel.OAuth.VerificationUri;
                if (displayUri is not null)
                {
                    children.WithChild(new TextNode($"  Visit: {displayUri}")
                        .WithForeground(Color.Cyan));
                    children.WithChild(new TextNode("").Height(1));
                }

                if (ViewModel.OAuth.UserCode is not null)
                {
                    children.WithChild(new TextNode($"  Enter code: {ViewModel.OAuth.UserCode}")
                        .WithForeground(Color.White).Bold());
                    children.WithChild(new TextNode("").Height(1));
                }

                var hints = new List<string>();
                if (displayUri is not null && BrowserDetection.CanOpenBrowser())
                {
                    hints.Add("[O] open in browser");
                }
                if (ViewModel.OAuth.UserCode is not null && _clipboardService is not null)
                {
                    hints.Add("[C] copy code");
                }
                if (hints.Count > 0)
                {
                    children.WithChild(new TextNode($"  {string.Join("    ", hints)}")
                        .WithForeground(Color.BrightBlack));
                    children.WithChild(new TextNode("").Height(1));
                }

                children.WithChild(SpinnerViews.Labeled("Waiting for authorization...", Color.Yellow));
                break;
            }

            case DeviceFlowState.Succeeded:
                children.WithChild(new TextNode("  \u2714 Authorization successful!")
                    .WithForeground(Color.Green));
                break;

            case DeviceFlowState.Denied:
            case DeviceFlowState.Expired:
            case DeviceFlowState.Error:
                children.WithChild(new TextNode($"  \u2718 {ViewModel.OAuth.ErrorMessage ?? "Authorization failed."}")
                    .WithForeground(Color.Red));
                children.WithChild(new TextNode("").Height(1));
                children.WithChild(new TextNode("  Press [Esc] to go back and try again.")
                    .WithForeground(Color.Gray));
                break;

            case DeviceFlowState.Cancelled:
                children.WithChild(new TextNode("  Authorization cancelled.")
                    .WithForeground(Color.Yellow));
                break;
        }

        return children;
    }

    private ILayoutNode BuildBrowserOAuthFlowView()
    {
        var oauthProviderType = ViewModel.NewProviderType ?? "unknown";
        var result = OAuthFlowViews.BuildBrowserOAuthFlow(
            ViewModel.Registry.Get(oauthProviderType).DisplayName,
            ViewModel.OAuth.FlowState.Value,
            ViewModel.OAuth.BrowserOpenFailed,
            ViewModel.OAuth.VerificationUri,
            ViewModel.ProbeElapsedSeconds,
            ViewModel.OAuth.ErrorMessage,
            _clipboardService,
            ref _redirectUrlInput,
            text => _ = ViewModel.SubmitRedirectUrlAsync(text));

        // Route keyboard input to the redirect URL paste box
        if (_redirectUrlInput is not null)
        {
            _lastFocusedInput = _redirectUrlInput;
            _redirectUrlInput.OnFocused();
        }

        return result;
    }

    private TextInputNode? _redirectUrlInput;

    private ILayoutNode BuildValidatingView()
    {
        var result = ViewModel.ProbeResult.Value;

        if (ViewModel.IsProbing.Value)
        {
            return Layouts.Vertical()
                .WithChild(SpinnerViews.WithElapsed(
                    "Validating connection...", Color.Yellow, ViewModel.ProbeElapsedSeconds));
        }

        if (result is { Success: true })
        {
            if (ViewModel.IsFixFlow)
            {
                return Layouts.Vertical()
                    .WithChild(new TextNode($"  \u2714 Connection restored! ({result.Models.Count} models found)")
                        .WithForeground(Color.Green));
            }

            return Layouts.Vertical()
                .WithChild(new TextNode($"  \u2714 Connection successful! ({result.Models.Count} models found)")
                    .WithForeground(Color.Green))
                .WithChild(new TextNode("  Provider saved. Press [Enter] to continue.")
                    .WithForeground(Color.Gray));
        }

        return Layouts.Vertical()
            .WithChild(new TextNode($"  \u2718 Validation failed: {result?.ErrorMessage ?? "unknown error"}")
                .WithForeground(Color.Red))
            .WithChild(new TextNode("  Press [Enter] to retry, [Esc] to go back.")
                .WithForeground(Color.Gray));
    }

    private ILayoutNode BuildAddCompleteView()
    {
        var result = ViewModel.ProbeResult.Value;
        return Layouts.Vertical()
            .WithChild(new TextNode($"  \u2714 Provider '{ViewModel.NewProviderName}' added")
                .WithForeground(Color.Green))
            .WithChild(new TextNode($"    Type: {ViewModel.Registry.Get(ViewModel.NewProviderType ?? "unknown").DisplayName}")
                .WithForeground(Color.White))
            .WithChild(new TextNode($"    Auth: {ViewModel.NewAuthMethod}")
                .WithForeground(Color.White))
            .WithChild(new TextNode($"    Models: {result?.Models.Count ?? 0} discovered")
                .WithForeground(Color.White))
            .WithChild(new TextNode("").Height(1))
            .WithChild(new TextNode("  Press [Enter] to return to the provider list.")
                .WithForeground(Color.Gray));
    }

    private ILayoutNode BuildDetailsView()
    {
        var item = ViewModel.DetailProvider;
        if (item is null)
            return Layouts.Empty();

        var healthStr = item.Health switch
        {
            ProviderHealthStatus.Healthy => "\u2713 Healthy",
            ProviderHealthStatus.Unhealthy => "\u26a0 Unhealthy",
            ProviderHealthStatus.Probing => "\u2026 Checking...",
            _ => "Unknown"
        };

        var healthColor = item.Health switch
        {
            ProviderHealthStatus.Healthy => Color.Green,
            ProviderHealthStatus.Unhealthy => Color.Red,
            _ => Color.Yellow
        };

        var modelCount = item.ProbeResult?.Models.Count ?? 0;

        return Layouts.Vertical()
            .WithChild(new TextNode($"  Provider: {item.ConfiguredName}").WithForeground(Color.White).Bold())
            .WithChild(new TextNode("").Height(1))
            .WithChild(new TextNode($"    Type:     {item.DisplayName}").WithForeground(Color.White))
            .WithChild(new TextNode($"    Auth:     {item.DisplayAuth}").WithForeground(Color.White))
            .WithChild(new TextNode($"    Endpoint: {item.DisplayEndpoint}").WithForeground(Color.White))
            .WithChild(new TextNode($"    Status:   {healthStr}").WithForeground(healthColor))
            .WithChild(new TextNode($"    Models:   {modelCount} discovered").WithForeground(Color.White));
    }

    private ILayoutNode BuildRenameView()
    {
        var item = ViewModel.DetailProvider;
        if (item is null)
            return Layouts.Empty();

        var children = Layouts.Vertical();
        children.WithChild(new TextNode($"  Rename '{item.ConfiguredName}' ({item.DisplayName})")
            .WithForeground(Color.White).Bold());
        children.WithChild(new TextNode("").Height(1));

        _renameInput = new TextInputNode().WithPlaceholder(item.ConfiguredName ?? "");
        _renameInput.Text = ViewModel.RenameNewName ?? item.ConfiguredName ?? string.Empty;
        // Termina's Text setter leaves the cursor at position 0. Synthesize
        // End so the user can immediately edit the suffix instead of having
        // their first keystroke insert before the pre-filled name.
        _renameInput.HandleInput(new ConsoleKeyInfo('\0', ConsoleKey.End, shift: false, alt: false, control: false));
        _renameInput.OnFocused();
        _lastFocusedInput = _renameInput;

        _renameInput.Submitted
            .Subscribe(text => ViewModel.ConfirmRename(text))
            .DisposeWith(_stepSubs);

        children.WithChild(new PanelNode()
            .WithTitle("New name")
            .WithBorder(BorderStyle.Rounded)
            .WithBorderColor(Color.Gray)
            .WithContent(_renameInput)
            .Height(3));

        children.WithChild(new TextNode("").Height(1));
        children.WithChild(new TextNode("  Renames the provider and cascades the change to any model")
            .WithForeground(Color.Gray));
        children.WithChild(new TextNode("  role(s) that reference it. Restart the daemon for changes to take effect.")
            .WithForeground(Color.Gray));

        return children;
    }

    private ILayoutNode BuildFixCredentialsView()
    {
        var item = ViewModel.DetailProvider;
        if (item is null)
            return Layouts.Empty();

        var descriptor = ViewModel.Registry.Get(item.ProviderType);
        var children = Layouts.Vertical();

        children.WithChild(new TextNode($"  Fix credentials for: {item.ConfiguredName} ({item.DisplayName})")
            .WithForeground(Color.White).Bold());

        if (item.ProbeResult is { Success: false, ErrorMessage: not null })
        {
            children.WithChild(new TextNode("").Height(1));
            children.WithChild(new TextNode($"  Error: {item.ProbeResult.ErrorMessage}")
                .WithForeground(Color.Red));
        }

        if (item.Entry?.AuthMethod is AuthMethod.OAuthPkce or AuthMethod.OAuthDevice
            || (item.Entry is null && descriptor.Auth is OAuthAuth))
        {
            // OAuth provider: route to re-authentication flow
            children.WithChild(new TextNode("").Height(1));
            children.WithChild(new TextNode("  This provider uses OAuth authentication.")
                .WithForeground(Color.White));
            children.WithChild(new TextNode("  Press [Enter] to re-authenticate.")
                .WithForeground(Color.Gray));

            // Wire Enter key to start OAuth re-auth via a confirmation list
            var reAuthItems = new List<string> { "Re-authenticate" };
            var reAuthList = Layouts.SelectionList(reAuthItems)
                .WithMode(SelectionMode.Single)
                .WithHighlightColors(Color.Black, Color.Cyan);

            reAuthList.OnFocused();
            _lastFocusedList = reAuthList;

            reAuthList.SelectionConfirmed
                .Subscribe(_ => ViewModel.StartOAuthReAuth())
                .DisposeWith(_stepSubs);

            children.WithChild(new TextNode("").Height(1));
            children.WithChild(reAuthList);
        }
        else if (descriptor.Auth is EndpointOnlyAuth)
        {
            children.WithChild(new TextNode("").Height(1));
            children.WithChild(new TextNode("  Endpoint:").WithForeground(Color.White));

            _endpointInput = new TextInputNode()
                .WithPlaceholder(item.Entry?.Endpoint ?? descriptor.DefaultEndpoint);
            _endpointInput.OnFocused();
            _lastFocusedInput = _endpointInput;

            _endpointInput.Submitted
                .Subscribe(text =>
                {
                    ViewModel.FixEndpoint = string.IsNullOrWhiteSpace(text)
                        ? item.Entry?.Endpoint
                        : text;
                    ViewModel.SubmitFixCredentials();
                })
                .DisposeWith(_stepSubs);

            children.WithChild(new PanelNode()
                .WithTitle("Endpoint")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_endpointInput)
                .Height(3));
        }
        else
        {
            // API key path (ApiKeyAuth or MultiAuth with API key auth method)
            children.WithChild(new TextNode("").Height(1));
            children.WithChild(new TextNode("  New API Key:").WithForeground(Color.White));

            _apiKeyInput = new TextInputNode()
                .AsPassword()
                .WithPlaceholder($"Enter new {item.DisplayName} API key...");
            _apiKeyInput.OnFocused();
            _lastFocusedInput = _apiKeyInput;

            _apiKeyInput.Submitted
                .Subscribe(text =>
                {
                    ViewModel.FixApiKey = text;
                    ViewModel.SubmitFixCredentials();
                })
                .DisposeWith(_stepSubs);

            children.WithChild(new PanelNode()
                .WithTitle("API Key")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_apiKeyInput)
                .Height(3));

            if (descriptor.Auth.GetApiKeyGuidanceUrl() is { } guidanceUrl)
            {
                children.WithChild(new TextNode("").Height(1));
                children.WithChild(new TextNode($"  Get your API key at {guidanceUrl}")
                    .WithForeground(Color.Gray));
            }
        }

        return children;
    }

    private ILayoutNode BuildRemoveConfirmView()
    {
        if (ViewModel.RemoveBlockingRoles.Count > 0)
        {
            return Layouts.Vertical()
                .WithChild(new TextNode($"  Cannot remove '{ViewModel.RemoveProviderName}'")
                    .WithForeground(Color.Red))
                .WithChild(new TextNode($"  Referenced by model role(s): {string.Join(", ", ViewModel.RemoveBlockingRoles)}")
                    .WithForeground(Color.Red))
                .WithChild(new TextNode("").Height(1))
                .WithChild(new TextNode("  Reassign these roles first with `netclaw model set`.")
                    .WithForeground(Color.Gray))
                .WithChild(new TextNode("  Press [Esc] to go back.")
                    .WithForeground(Color.Gray));
        }

        var items = new List<string> { "Yes, remove", "No, cancel" };
        _confirmList = Layouts.SelectionList(items)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Red);

        _confirmList.OnFocused();
        _lastFocusedList = _confirmList;

        _confirmList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0 && selected[0].StartsWith("Yes", StringComparison.Ordinal))
                    ViewModel.ConfirmRemove();
                else
                    ViewModel.GoBackToList();
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode($"  Remove provider '{ViewModel.RemoveProviderName}'?")
                .WithForeground(Color.Yellow))
            .WithChild(_confirmList);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Input handling
    // ═══════════════════════════════════════════════════════════════════

    private void HandleKeyPress(KeyPressed key)
    {
        var keyInfo = key.KeyInfo;
        var state = ViewModel.CurrentState.Value;

        if (keyInfo.Key == ConsoleKey.Escape)
        {
            ViewModel.GoBack();
            return;
        }

        // Browser OAuth: "C" to copy URL to clipboard
        if (state == ProviderManagerState.AddBrowserOAuthFlow
            && keyInfo.Key == ConsoleKey.C
            && ViewModel.OAuth.BrowserOpenFailed
            && ViewModel.OAuth.VerificationUri is not null)
        {
            if (OAuthFlowViews.TryCopyToClipboard(_clipboardService, ViewModel.OAuth.VerificationUri))
                ViewModel.StatusMessage.Value = "\u2714 URL copied to clipboard";
            return;
        }

        // Device OAuth: "C" to copy user code to clipboard
        if (state == ProviderManagerState.AddOAuthDeviceFlow
            && keyInfo.Key == ConsoleKey.C
            && ViewModel.OAuth.UserCode is not null)
        {
            if (OAuthFlowViews.TryCopyToClipboard(_clipboardService, ViewModel.OAuth.UserCode))
            {
                ViewModel.StatusMessage.Value = "\u2714 Code copied to clipboard";
            }
            return;
        }

        // Device OAuth: "O" to open the verification URL in the default browser
        if (state == ProviderManagerState.AddOAuthDeviceFlow
            && keyInfo.Key == ConsoleKey.O
            && (ViewModel.OAuth.VerificationUriComplete ?? ViewModel.OAuth.VerificationUri) is not null)
        {
            var url = ViewModel.OAuth.VerificationUriComplete ?? ViewModel.OAuth.VerificationUri;
            ViewModel.StatusMessage.Value = OAuthFlowViews.TryOpenInBrowser(url)
                ? "\u2714 Opening browser..."
                : "\u2718 Could not open browser.";
            return;
        }

        // Details state shortcuts
        if (state == ProviderManagerState.Details)
        {
            switch (keyInfo.Key)
            {
                case ConsoleKey.K:
                    if (ViewModel.DetailProvider is not null)
                        ViewModel.StartFixCredentials(ViewModel.DetailProvider);
                    return;
                case ConsoleKey.N:
                    ViewModel.StartRename();
                    return;
                case ConsoleKey.R:
                    ViewModel.StartRemove();
                    return;
                case ConsoleKey.V:
                    ViewModel.RevalidateDetailProvider();
                    return;
            }
        }

        // List state: Enter is handled by SelectionConfirmed subscription,
        // arrow keys are routed through RouteInputToActiveComponent

        // Enter in validating state
        if (state == ProviderManagerState.AddValidating && keyInfo.Key == ConsoleKey.Enter)
        {
            var result = ViewModel.ProbeResult.Value;
            if (result is { Success: true } && !ViewModel.IsFixFlow)
                ViewModel.ConfirmAdd();
            else if (result is not null)
                ViewModel.StartProbe(); // retry
            return;
        }

        // Enter in complete state
        if (state == ProviderManagerState.AddComplete && keyInfo.Key == ConsoleKey.Enter)
        {
            ViewModel.ConfirmAdd();
            return;
        }

        // Route to focused component
        RouteInputToActiveComponent(keyInfo);
    }

    private void RouteInputToActiveComponent(ConsoleKeyInfo keyInfo)
    {
        if (_lastFocusedInput is not null)
        {
            _lastFocusedInput.HandleInput(keyInfo);
            ViewModel.RequestRedraw();
            return;
        }

        if (_lastFocusedList is not null)
        {
            ((SelectionListNode<string>)_lastFocusedList).HandleInput(keyInfo);
            ViewModel.RequestRedraw();
        }
    }
}
