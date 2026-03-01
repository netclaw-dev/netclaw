using Netclaw.Configuration;
using Netclaw.Configuration.Providers;
using R3;
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
    private static readonly string[] SpinnerFrames = ["\u280b", "\u2819", "\u2838", "\u2834", "\u2826", "\u2807"];

    private SelectionListNode<string>? _providerList;
    private SelectionListNode<string>? _authList;
    private TextInputNode? _apiKeyInput;
    private TextInputNode? _endpointInput;
    private SelectionListNode<string>? _confirmList;

    private IFocusable? _lastFocusedList;
    private TextInputNode? _lastFocusedInput;
    private DynamicLayoutNode? _contentNode;
    private readonly CompositeDisposable _stepSubs = new();

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
            .WithChild(BuildContent())
            .WithChild(Layouts.Empty().Fill())
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
                ProviderManagerState.AddSelectAuth => BuildAddAuthView(),
                ProviderManagerState.AddCredentials => BuildCredentialsView(),
                ProviderManagerState.AddValidating => BuildValidatingView(),
                ProviderManagerState.AddComplete => BuildAddCompleteView(),
                ProviderManagerState.Details => BuildDetailsView(),
                ProviderManagerState.FixCredentials => BuildFixCredentialsView(),
                ProviderManagerState.RemoveConfirm => BuildRemoveConfirmView(),
                _ => Layouts.Empty()
            };
        });

        ViewModel.StateVersion
            .Subscribe(_ => _contentNode.Invalidate())
            .DisposeWith(Subscriptions);

        return _contentNode;
    }

    private LayoutNode BuildStatusBar()
    {
        return ViewModel.StatusMessage
            .Select(msg => (ILayoutNode)(string.IsNullOrWhiteSpace(msg)
                ? Layouts.Empty()
                : new TextNode($"  {msg}").WithForeground(Color.Green)))
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
                    ProviderManagerState.Details =>
                        " [K] Update key  [R] Remove  [V] Re-validate  [Esc] Back  [Ctrl+Q] Quit",
                    ProviderManagerState.RemoveConfirm =>
                        " [Enter] Confirm  [Esc] Cancel  [Ctrl+Q] Quit",
                    ProviderManagerState.AddComplete =>
                        " [Enter] Save  [Esc] Cancel  [Ctrl+Q] Quit",
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
        var elapsed = ViewModel.EagerProbeElapsedSeconds.Value;
        var frame = SpinnerFrames[elapsed % SpinnerFrames.Length];

        var children = Layouts.Vertical();
        children.WithChild(new TextNode($"  {frame} Checking configured providers...")
            .WithForeground(Color.Yellow));
        children.WithChild(new TextNode("").Height(1));

        foreach (var item in ViewModel.DisplayProviders)
        {
            if (!item.IsConfigured) continue;

            var (statusChar, color) = item.Health switch
            {
                ProviderHealthStatus.Healthy => ("\u2713", Color.Green),
                ProviderHealthStatus.Unhealthy => ("\u26a0", Color.Red),
                _ => (SpinnerFrames[elapsed % SpinnerFrames.Length], Color.Yellow)
            };

            children.WithChild(new TextNode($"  {statusChar} {item.ProviderType}")
                .WithForeground(color));
        }

        return children;
    }

    private ILayoutNode BuildProviderListView()
    {
        var items = ViewModel.DisplayProviders
            .Select(p =>
            {
                var statusChar = p switch
                {
                    { IsConfigured: true, Health: ProviderHealthStatus.Healthy } => "\u2713",
                    { IsConfigured: true, Health: ProviderHealthStatus.Unhealthy } => "\u26a0",
                    { IsConfigured: true, Health: ProviderHealthStatus.Probing } => "\u2026",
                    _ => " "
                };

                return $"{statusChar} {p.ProviderType,-16} {p.DisplayAuth,-12} {p.DisplayEndpoint}";
            })
            .ToList();

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
                    var idx = items.IndexOf(selected[0]);
                    if (idx >= 0)
                    {
                        ViewModel.SelectedProviderIndex = idx;
                        ViewModel.ActivateSelectedProvider();
                    }
                }
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode($"  {"",2}{"Type",-16} {"Auth",-12} Endpoint")
                .WithForeground(Color.White).Bold())
            .WithChild(_providerList);
    }

    private ILayoutNode BuildAddAuthView()
    {
        var providerType = ViewModel.NewProviderType ?? "unknown";
        var descriptor = ViewModel.Registry.Get(providerType);
        var supportedMethods = descriptor.SupportedAuthMethods
            .Where(m => m != AuthMethod.None)
            .Select(m => m switch
            {
                AuthMethod.ApiKey => "API Key",
                AuthMethod.OAuthDevice => "OAuth Device Flow (coming soon)",
                _ => m.ToString()
            })
            .ToList();

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
                    var method = selected[0].StartsWith("API", StringComparison.Ordinal)
                        ? AuthMethod.ApiKey
                        : AuthMethod.OAuthDevice;
                    ViewModel.SelectAuthMethod(method);
                }
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode($"  Authentication for {providerType}:")
                .WithForeground(Color.White))
            .WithChild(_authList);
    }

    private ILayoutNode BuildCredentialsView()
    {
        var children = Layouts.Vertical();
        var providerType = ViewModel.NewProviderType ?? "unknown";
        var descriptor = ViewModel.Registry.Get(providerType);

        children.WithChild(new TextNode($"  Provider: {providerType} (name: {ViewModel.NewProviderName})")
            .WithForeground(Color.White));

        if (descriptor.CredentialMode == CredentialInputMode.ApiKey)
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

            if (descriptor.ApiKeyGuidanceUrl is { } guidanceUrl)
            {
                children.WithChild(new TextNode("").Height(1));
                children.WithChild(new TextNode($"  Get your API key at {guidanceUrl}")
                    .WithForeground(Color.Gray));
            }
        }
        else if (descriptor.CredentialMode == CredentialInputMode.EndpointOnly)
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

    private ILayoutNode BuildValidatingView()
    {
        var elapsed = ViewModel.ProbeElapsedSeconds.Value;
        var frame = SpinnerFrames[elapsed % SpinnerFrames.Length];
        var result = ViewModel.ProbeResult.Value;

        if (ViewModel.IsProbing.Value)
        {
            return Layouts.Vertical()
                .WithChild(new TextNode($"  {frame} Validating connection... ({elapsed}s)")
                    .WithForeground(Color.Yellow));
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
                .WithChild(new TextNode("  Press [Enter] to save, [Esc] to cancel.")
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
            .WithChild(new TextNode($"  \u2714 Provider '{ViewModel.NewProviderName}' ready to save")
                .WithForeground(Color.Green))
            .WithChild(new TextNode($"    Type: {ViewModel.NewProviderType}")
                .WithForeground(Color.White))
            .WithChild(new TextNode($"    Auth: {ViewModel.NewAuthMethod}")
                .WithForeground(Color.White))
            .WithChild(new TextNode($"    Models: {result?.Models.Count ?? 0} discovered")
                .WithForeground(Color.White))
            .WithChild(new TextNode("").Height(1))
            .WithChild(new TextNode("  Press [Enter] to save, [Esc] to cancel.")
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
            .WithChild(new TextNode($"    Type:     {item.ProviderType}").WithForeground(Color.White))
            .WithChild(new TextNode($"    Auth:     {item.DisplayAuth}").WithForeground(Color.White))
            .WithChild(new TextNode($"    Endpoint: {item.DisplayEndpoint}").WithForeground(Color.White))
            .WithChild(new TextNode($"    Status:   {healthStr}").WithForeground(healthColor))
            .WithChild(new TextNode($"    Models:   {modelCount} discovered").WithForeground(Color.White));
    }

    private ILayoutNode BuildFixCredentialsView()
    {
        var item = ViewModel.DetailProvider;
        if (item is null)
            return Layouts.Empty();

        var descriptor = ViewModel.Registry.Get(item.ProviderType);
        var children = Layouts.Vertical();

        children.WithChild(new TextNode($"  Fix credentials for: {item.ConfiguredName} ({item.ProviderType})")
            .WithForeground(Color.White).Bold());

        if (item.ProbeResult is { Success: false, ErrorMessage: not null })
        {
            children.WithChild(new TextNode("").Height(1));
            children.WithChild(new TextNode($"  Error: {item.ProbeResult.ErrorMessage}")
                .WithForeground(Color.Red));
        }

        if (descriptor.CredentialMode == CredentialInputMode.EndpointOnly)
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
        else if (descriptor.SupportedAuthMethods.Contains(AuthMethod.ApiKey))
        {
            children.WithChild(new TextNode("").Height(1));
            children.WithChild(new TextNode("  New API Key:").WithForeground(Color.White));

            _apiKeyInput = new TextInputNode()
                .AsPassword()
                .WithPlaceholder($"Enter new {item.ProviderType} API key...");
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

            if (descriptor.ApiKeyGuidanceUrl is { } guidanceUrl)
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

        // Details state shortcuts
        if (state == ProviderManagerState.Details)
        {
            switch (keyInfo.Key)
            {
                case ConsoleKey.K:
                    if (ViewModel.DetailProvider is not null)
                        ViewModel.StartFixCredentials(ViewModel.DetailProvider);
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
