using Netclaw.Configuration;
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
/// Provides browsing, adding, and removing provider configurations.
/// </summary>
public sealed class ProviderManagerPage : ReactivePage<ProviderManagerViewModel>
{
    private static readonly string[] SpinnerFrames = ["\u280b", "\u2819", "\u2838", "\u2834", "\u2826", "\u2807"];

    private SelectionListNode<string>? _providerList;
    private SelectionListNode<string>? _typeList;
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
                ProviderManagerState.List => BuildProviderListView(),
                ProviderManagerState.AddSelectType => BuildAddTypeView(),
                ProviderManagerState.AddSelectAuth => BuildAddAuthView(),
                ProviderManagerState.AddCredentials => BuildCredentialsView(),
                ProviderManagerState.AddValidating => BuildValidatingView(),
                ProviderManagerState.AddComplete => BuildAddCompleteView(),
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
                    ProviderManagerState.List =>
                        " [\u2191/\u2193] Navigate  [A] Add  [R] Remove  [Esc] Quit  [Ctrl+Q] Quit",
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

    private ILayoutNode BuildProviderListView()
    {
        if (ViewModel.Providers.Count == 0)
        {
            return Layouts.Vertical()
                .WithChild(new TextNode("  No providers configured.").WithForeground(Color.Gray))
                .WithChild(new TextNode("  Press [A] to add a provider.").WithForeground(Color.Gray));
        }

        var items = ViewModel.Providers
            .Select(p =>
            {
                var authStr = p.Entry.AuthMethod == AuthMethod.None ? "none" : p.Entry.AuthMethod.ToString();
                return $"{p.Name,-18} {p.Entry.Type,-10} {authStr,-10} {p.Entry.Endpoint}";
            })
            .ToList();

        _providerList = Layouts.SelectionList(items)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _providerList.OnFocused();
        _lastFocusedList = _providerList;

        return Layouts.Vertical()
            .WithChild(new TextNode($"  {"Name",-18} {"Type",-10} {"Auth",-10} Endpoint")
                .WithForeground(Color.White).Bold())
            .WithChild(_providerList);
    }

    private ILayoutNode BuildAddTypeView()
    {
        _typeList = Layouts.SelectionList(
                ProviderCapabilities.KnownProviderTypes.ToList())
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _typeList.OnFocused();
        _lastFocusedList = _typeList;

        _typeList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                    ViewModel.SelectProviderType(selected[0]);
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Select provider type:").WithForeground(Color.White))
            .WithChild(_typeList)
            .WithChild(BuildProviderTypeHelp());
    }

    private ILayoutNode BuildProviderTypeHelp()
    {
        return Layouts.Vertical()
            .WithChild(new TextNode("").Height(1))
            .WithChild(new TextNode("  Ollama     \u2014 local inference, no auth needed").WithForeground(Color.Gray))
            .WithChild(new TextNode("  OpenRouter \u2014 model marketplace with unified API").WithForeground(Color.Gray))
            .WithChild(new TextNode("  Anthropic  \u2014 Claude models (API key or OAuth)").WithForeground(Color.Gray))
            .WithChild(new TextNode("  OpenAI     \u2014 GPT models (API key or OAuth)").WithForeground(Color.Gray));
    }

    private ILayoutNode BuildAddAuthView()
    {
        var providerType = ViewModel.NewProviderType ?? "unknown";
        var supportedMethods = ProviderCapabilities.GetSupportedAuthMethods(providerType)
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

        children.WithChild(new TextNode($"  Provider: {ViewModel.NewProviderType} (name: {ViewModel.NewProviderName})")
            .WithForeground(Color.White));

        if (ViewModel.NewAuthMethod == AuthMethod.ApiKey)
        {
            children.WithChild(new TextNode("").Height(1));
            children.WithChild(new TextNode("  API Key:").WithForeground(Color.White));

            _apiKeyInput = new TextInputNode()
                .AsPassword()
                .WithPlaceholder($"Enter {ViewModel.NewProviderType} API key...");
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

            var guidance = ViewModel.NewProviderType switch
            {
                "openrouter" => "  Get your API key at https://openrouter.ai/keys",
                "anthropic" => "  Get your API key at https://console.anthropic.com/settings/keys",
                "openai" => "  Get your API key at https://platform.openai.com/api-keys",
                _ => null
            };

            if (guidance is not null)
            {
                children.WithChild(new TextNode("").Height(1));
                children.WithChild(new TextNode(guidance).WithForeground(Color.Gray));
            }
        }
        else if (ViewModel.NewProviderType == "ollama")
        {
            children.WithChild(new TextNode("").Height(1));
            children.WithChild(new TextNode("  Endpoint (default: http://localhost:11434):")
                .WithForeground(Color.White));

            var ollamaDefault = ProviderCapabilities.GetDefaultEndpoint("ollama");
            _endpointInput = new TextInputNode()
                .WithPlaceholder(ollamaDefault);
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
            children.WithChild(new TextNode("  Ollama runs locally. No authentication required.")
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

        // List-state shortcuts
        if (state == ProviderManagerState.List)
        {
            switch (keyInfo.Key)
            {
                case ConsoleKey.A:
                    ViewModel.StartAdd();
                    return;
                case ConsoleKey.R:
                    ViewModel.StartRemove();
                    return;
            }
        }

        // Enter in validating state
        if (state == ProviderManagerState.AddValidating && keyInfo.Key == ConsoleKey.Enter)
        {
            var result = ViewModel.ProbeResult.Value;
            if (result is { Success: true })
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
