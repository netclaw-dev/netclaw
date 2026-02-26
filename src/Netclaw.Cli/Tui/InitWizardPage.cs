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
/// Termina page for the <c>netclaw init</c> onboarding wizard.
/// Layout: outer panel with step indicator, step-specific content, help text, key bindings.
/// </summary>
public sealed class InitWizardPage : ReactivePage<InitWizardViewModel>
{
    // Step 1: Provider selection list + auth input
    private SelectionListNode<string>? _providerList;
    private SelectionListNode<string>? _authMethodList;
    private TextInputNode? _apiKeyInput;
    private TextInputNode? _endpointInput;

    // Step 2: Slack tokens
    private TextInputNode? _slackBotTokenInput;
    private TextInputNode? _slackAppTokenInput;
    private SelectionListNode<string>? _slackEnabledList;

    // Step 3: ACL
    private TextInputNode? _ownerIdentityInput;

    // Step 4: MCP
    private SelectionListNode<string>? _mcpList;

    // Step 5: Exposure
    private SelectionListNode<string>? _exposureList;

    // Track sub-step for provider (selection → auth → key)
    private int _providerSubStep; // 0=select provider, 1=select auth, 2=enter key/endpoint
    private int _slackSubStep;    // 0=enable?, 1=bot token, 2=app token

    // Focus tracking for selection lists (mirrors _lastFocusedInput for text inputs)
    private IFocusable? _lastFocusedList;
    private TextInputNode? _lastFocusedInput;

    // Dynamic layout node for step content — invalidated on sub-step changes
    private DynamicLayoutNode? _stepContentNode;
    private DynamicLayoutNode? _helpTextNode;

    public InitWizardPage()
    {
        FocusPolicy = FocusPolicy.FirstFocusable;
    }

    protected override void OnBound()
    {
        base.OnBound();
        InitializeComponents();

        // Route keyboard input
        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
    {
        return Layouts.Vertical()
            // Outer panel
            .WithChild(
                new PanelNode()
                    .WithTitle("Netclaw Setup")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Cyan)
                    .WithContent(BuildInnerLayout())
                    .Fill());
    }

    private ILayoutNode BuildInnerLayout()
    {
        return Layouts.Vertical()
            .WithSpacing(1)
            // Step indicator + progress
            .WithChild(BuildStepIndicator())
            // Step content (dynamic — rebuilds on step and sub-step changes)
            .WithChild(BuildStepContent())
            // Help text (dynamic — rebuilds on step and sub-step changes)
            .WithChild(BuildHelpText())
            // Status message
            .WithChild(BuildStatusBar())
            // Key bindings
            .WithChild(BuildKeyBindings());
    }

    private LayoutNode BuildStepIndicator()
    {
        return ViewModel.CurrentStep
            .Select(step =>
            {
                var stepNum = (int)step;
                var filled = new string('\u25a0', stepNum);
                var empty = new string('\u25a1', InitWizardViewModel.TotalSteps - stepNum);
                var pct = stepNum * 100 / InitWizardViewModel.TotalSteps;
                var title = step switch
                {
                    WizardStep.Provider => "LLM Provider",
                    WizardStep.Slack => "Slack Configuration",
                    WizardStep.Acl => "Access Control",
                    WizardStep.Mcp => "MCP Servers",
                    WizardStep.Exposure => "Exposure Mode",
                    WizardStep.HealthCheck => "Health Check",
                    _ => ""
                };
                return (ILayoutNode)new TextNode(
                        $"  Step {stepNum} of {InitWizardViewModel.TotalSteps}: {title}        [{filled}{empty}] {pct}%")
                    .WithForeground(Color.White)
                    .Bold();
            })
            .AsLayout()
            .Height(1);
    }

    private LayoutNode BuildStepContent()
    {
        // DynamicLayoutNode re-evaluates the factory on every render cycle.
        // Step changes trigger RequestRedraw via the observable subscription in InitializeComponents.
        // Sub-step changes call Invalidate() directly.
        _stepContentNode = new DynamicLayoutNode(() => ViewModel.CurrentStep.Value switch
        {
            WizardStep.Provider => BuildProviderStep(),
            WizardStep.Slack => BuildSlackStep(),
            WizardStep.Acl => BuildAclStep(),
            WizardStep.Mcp => BuildMcpStep(),
            WizardStep.Exposure => BuildExposureStep(),
            WizardStep.HealthCheck => BuildHealthCheckStep(),
            _ => Layouts.Empty()
        });

        return _stepContentNode.Fill();
    }

    private LayoutNode BuildHelpText()
    {
        _helpTextNode = new DynamicLayoutNode(() =>
        {
            var step = ViewModel.CurrentStep.Value;
            var text = step switch
            {
                WizardStep.Provider when _providerSubStep == 0 =>
                    "  Select your LLM provider. Ollama runs locally (no auth required).",
                WizardStep.Provider when _providerSubStep == 1 =>
                    "  Choose how to authenticate with this provider.",
                WizardStep.Provider when _providerSubStep == 2 =>
                    "  Enter your API key. It will be stored in secrets.json.",
                WizardStep.Slack when _slackSubStep == 0 =>
                    "  Enable Slack to connect Netclaw as a Slack bot.",
                WizardStep.Slack =>
                    "  Socket Mode requires both tokens. See: https://api.slack.com/apis/socket-mode",
                WizardStep.Acl =>
                    "  Your Slack user ID (e.g., U01234ABCDE) for admin access.",
                WizardStep.Mcp =>
                    "  MCP servers provide external tools. Memorizer adds persistent memory.",
                WizardStep.Exposure =>
                    "  Local-only is recommended for homelab use.",
                WizardStep.HealthCheck =>
                    "  Validating your configuration...",
                _ => ""
            };
            return (ILayoutNode)new TextNode(text).WithForeground(Color.BrightBlack);
        });

        return _helpTextNode.Height(2);
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
        return Observable.CombineLatest(ViewModel.CurrentStep, ViewModel.IsComplete,
                (step, complete) =>
                {
                    if (complete)
                        return (ILayoutNode)new TextNode(
                            " [Enter] Exit  [Ctrl+Q] Quit").WithForeground(Color.BrightBlack);

                    var backLabel = step == WizardStep.Provider ? "Quit" : "Back";
                    return (ILayoutNode)new TextNode(
                        $" [Enter] Next  [Esc] {backLabel}  [Ctrl+Q] Quit").WithForeground(Color.BrightBlack);
                })
            .AsLayout()
            .Height(1);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Step-specific layouts
    // ═══════════════════════════════════════════════════════════════════

    private ILayoutNode BuildProviderStep()
    {
        return _providerSubStep switch
        {
            0 => BuildProviderSelectionSubStep(),
            1 => BuildAuthMethodSubStep(),
            2 => BuildCredentialInputSubStep(),
            _ => Layouts.Empty()
        };
    }

    private ILayoutNode BuildProviderSelectionSubStep()
    {
        _providerList = Layouts.SelectionList(
                ProviderCapabilities.KnownProviderTypes)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        // Auto-focus so the highlight bar is visible immediately
        _providerList.OnFocused();
        _lastFocusedList = _providerList;

        _providerList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    ViewModel.SelectedProviderType = selected[0];
                    var supportedAuth = ProviderCapabilities.GetSupportedAuthMethods(selected[0]);
                    if (supportedAuth is [AuthMethod.None])
                    {
                        // Ollama — no auth needed, skip to endpoint
                        ViewModel.SelectedAuthMethod = AuthMethod.None;
                        SetProviderSubStep(2);
                    }
                    else
                    {
                        SetProviderSubStep(1);
                    }
                }
            })
            .DisposeWith(Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Choose your LLM provider:").WithForeground(Color.White))
            .WithChild(_providerList);
    }

    private ILayoutNode BuildAuthMethodSubStep()
    {
        var providerType = ViewModel.SelectedProviderType ?? "unknown";
        var supportedMethods = ProviderCapabilities.GetSupportedAuthMethods(providerType)
            .Where(m => m != AuthMethod.None)
            .Select(m => m switch
            {
                AuthMethod.ApiKey => "API Key",
                AuthMethod.OAuthDevice => "OAuth Device Flow (coming soon)",
                _ => m.ToString()
            })
            .ToList();

        _authMethodList = Layouts.SelectionList(supportedMethods)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        // Auto-focus so the highlight bar is visible immediately
        _authMethodList.OnFocused();
        _lastFocusedList = _authMethodList;

        _authMethodList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    ViewModel.SelectedAuthMethod = selected[0].StartsWith("API", StringComparison.Ordinal)
                        ? AuthMethod.ApiKey
                        : AuthMethod.OAuthDevice;
                    SetProviderSubStep(2);
                }
            })
            .DisposeWith(Subscriptions);

        _authMethodList.Cancelled
            .Subscribe(_ =>
            {
                SetProviderSubStep(0);
            })
            .DisposeWith(Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode($"  Authentication for {providerType}:").WithForeground(Color.White))
            .WithChild(_authMethodList);
    }

    private ILayoutNode BuildCredentialInputSubStep()
    {
        var providerType = ViewModel.SelectedProviderType ?? "unknown";

        if (providerType == "ollama")
        {
            // Ollama needs endpoint only
            _endpointInput = new TextInputNode()
                .WithPlaceholder("http://localhost:11434");
            _endpointInput.Text = ViewModel.EndpointInput ?? "http://localhost:11434";

            // Auto-focus for cursor display
            _endpointInput.OnFocused();
            _lastFocusedInput = _endpointInput;

            _endpointInput.Submitted
                .Subscribe(text =>
                {
                    ViewModel.EndpointInput = string.IsNullOrWhiteSpace(text) ? "http://localhost:11434" : text;
                    ViewModel.GoNext();
                })
                .DisposeWith(Subscriptions);

            return Layouts.Vertical()
                .WithChild(new TextNode("  Ollama endpoint:").WithForeground(Color.White))
                .WithChild(new PanelNode()
                    .WithTitle("Endpoint")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Gray)
                    .WithContent(_endpointInput)
                    .Height(3));
        }

        // API key input for cloud providers
        _apiKeyInput = new TextInputNode()
            .AsPassword()
            .WithPlaceholder($"Enter {providerType} API key...");

        if (!string.IsNullOrWhiteSpace(ViewModel.ApiKeyInput))
            _apiKeyInput.Text = ViewModel.ApiKeyInput;

        // Auto-focus for cursor display
        _apiKeyInput.OnFocused();
        _lastFocusedInput = _apiKeyInput;

        _apiKeyInput.Submitted
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Subscribe(text =>
            {
                ViewModel.ApiKeyInput = text;
                ViewModel.GoNext();
            })
            .DisposeWith(Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode($"  {providerType} API key:").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("API Key")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_apiKeyInput)
                .Height(3));
    }

    private ILayoutNode BuildSlackStep()
    {
        return _slackSubStep switch
        {
            0 => BuildSlackEnableSubStep(),
            1 => BuildSlackBotTokenSubStep(),
            2 => BuildSlackAppTokenSubStep(),
            _ => Layouts.Empty()
        };
    }

    private ILayoutNode BuildSlackEnableSubStep()
    {
        _slackEnabledList = Layouts.SelectionList("Yes — configure Slack bot", "No — skip for now")
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        // Auto-focus so the highlight bar is visible immediately
        _slackEnabledList.OnFocused();
        _lastFocusedList = _slackEnabledList;

        _slackEnabledList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    ViewModel.SlackEnabled = selected[0].StartsWith("Yes", StringComparison.Ordinal);
                    if (ViewModel.SlackEnabled)
                    {
                        SetSlackSubStep(1);
                    }
                    else
                    {
                        _slackSubStep = 0;
                        ViewModel.GoNext();
                    }
                }
            })
            .DisposeWith(Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Enable Slack integration?").WithForeground(Color.White))
            .WithChild(_slackEnabledList);
    }

    private ILayoutNode BuildSlackBotTokenSubStep()
    {
        _slackBotTokenInput = new TextInputNode()
            .AsPassword()
            .WithPlaceholder("xoxb-...");

        // Auto-focus for cursor display
        _slackBotTokenInput.OnFocused();
        _lastFocusedInput = _slackBotTokenInput;

        _slackBotTokenInput.Submitted
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Subscribe(text =>
            {
                ViewModel.SlackBotToken = text;
                SetSlackSubStep(2);
            })
            .DisposeWith(Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Slack Bot Token:").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("Bot Token")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_slackBotTokenInput)
                .Height(3));
    }

    private ILayoutNode BuildSlackAppTokenSubStep()
    {
        _slackAppTokenInput = new TextInputNode()
            .AsPassword()
            .WithPlaceholder("xapp-...");

        // Auto-focus for cursor display
        _slackAppTokenInput.OnFocused();
        _lastFocusedInput = _slackAppTokenInput;

        _slackAppTokenInput.Submitted
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Subscribe(text =>
            {
                ViewModel.SlackAppToken = text;
                _slackSubStep = 0;
                ViewModel.GoNext();
            })
            .DisposeWith(Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Slack App Token (Socket Mode):").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("App Token")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_slackAppTokenInput)
                .Height(3));
    }

    private ILayoutNode BuildAclStep()
    {
        _ownerIdentityInput = new TextInputNode()
            .WithPlaceholder("U01234ABCDE (your Slack user ID)");

        if (!string.IsNullOrWhiteSpace(ViewModel.OwnerIdentity))
            _ownerIdentityInput.Text = ViewModel.OwnerIdentity;

        // Auto-focus for cursor display
        _ownerIdentityInput.OnFocused();
        _lastFocusedInput = _ownerIdentityInput;

        _ownerIdentityInput.Submitted
            .Subscribe(text =>
            {
                ViewModel.OwnerIdentity = string.IsNullOrWhiteSpace(text) ? null : text;
                ViewModel.GoNext();
            })
            .DisposeWith(Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Owner identity (press Enter to skip):").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("Owner ID")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_ownerIdentityInput)
                .Height(3));
    }

    private ILayoutNode BuildMcpStep()
    {
        _mcpList = Layouts.SelectionList(
                "Memorizer (recommended — persistent memory)",
                "Custom MCP server (configure later)",
                "Skip — no MCP servers")
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        // Auto-focus so the highlight bar is visible immediately
        _mcpList.OnFocused();
        _lastFocusedList = _mcpList;

        _mcpList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    ViewModel.McpSelection = selected[0];
                    ViewModel.GoNext();
                }
            })
            .DisposeWith(Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  MCP tool servers:").WithForeground(Color.White))
            .WithChild(_mcpList);
    }

    private ILayoutNode BuildExposureStep()
    {
        _exposureList = Layouts.SelectionList(
                "Local only (recommended for homelab)",
                "Tailscale (configure later)",
                "Cloudflare Tunnel (configure later)")
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        // Auto-focus so the highlight bar is visible immediately
        _exposureList.OnFocused();
        _lastFocusedList = _exposureList;

        _exposureList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    ViewModel.ExposureMode = selected[0];
                    ViewModel.GoNext();
                }
            })
            .DisposeWith(Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Network exposure:").WithForeground(Color.White))
            .WithChild(_exposureList);
    }

    private ILayoutNode BuildHealthCheckStep()
    {
        var items = ViewModel.HealthCheckResults;
        var lines = new List<ILayoutNode>();

        foreach (var item in items)
        {
            var (icon, color) = item.Passed switch
            {
                true => ("\u2713", Color.Green),
                false => ("\u2717", Color.Red),
                null => ("\u25cf", Color.Yellow)
            };
            lines.Add(new TextNode($"  {icon}  {item.Label}").WithForeground(color));
        }

        if (lines.Count == 0)
            lines.Add(new TextNode("  Press Enter to run health checks...").WithForeground(Color.BrightBlack));

        var layout = Layouts.Vertical();
        foreach (var line in lines)
            layout.WithChild(line);
        return layout;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Input handling
    // ═══════════════════════════════════════════════════════════════════

    private void HandleKeyPress(KeyPressed key)
    {
        var keyInfo = key.KeyInfo;

        // Escape: go back (or sub-step back)
        if (keyInfo.Key == ConsoleKey.Escape)
        {
            if (HandleSubStepBack())
                return;
            ViewModel.GoBack();
            return;
        }

        // Route input to active component
        RouteInputToActiveComponent(keyInfo);
    }

    private bool HandleSubStepBack()
    {
        if (ViewModel.CurrentStep.Value == WizardStep.Provider && _providerSubStep > 0)
        {
            if (_providerSubStep == 2)
                ViewModel.ClearFromProvider();
            SetProviderSubStep(_providerSubStep - 1);
            return true;
        }

        if (ViewModel.CurrentStep.Value == WizardStep.Slack && _slackSubStep > 0)
        {
            SetSlackSubStep(_slackSubStep - 1);
            return true;
        }

        return false;
    }

    private void RouteInputToActiveComponent(ConsoleKeyInfo keyInfo)
    {
        // Try active selection lists first
        var activeList = GetActiveSelectionList();
        if (activeList is not null)
        {
            // Blur any previously focused text input
            if (_lastFocusedInput is not null)
            {
                _lastFocusedInput.OnBlurred();
                _lastFocusedInput = null;
            }

            // Focus the selection list if it changed
            if (_lastFocusedList != activeList)
            {
                _lastFocusedList?.OnBlurred();
                activeList.OnFocused();
                _lastFocusedList = activeList;
            }

            activeList.HandleInput(keyInfo);
            ViewModel.RequestRedraw();
            return;
        }

        // Try active text inputs
        var activeInput = GetActiveTextInput();
        if (activeInput is not null)
        {
            // Blur any previously focused selection list
            if (_lastFocusedList is not null)
            {
                _lastFocusedList.OnBlurred();
                _lastFocusedList = null;
            }

            // Auto-focus the text input for cursor display
            if (_lastFocusedInput != activeInput)
            {
                _lastFocusedInput?.OnBlurred();
                activeInput.OnFocused();
                _lastFocusedInput = activeInput;
            }
            activeInput.HandleInput(keyInfo);
            ViewModel.RequestRedraw();
            return;
        }

        // On health check step, Enter triggers the check
        if (ViewModel.CurrentStep.Value == WizardStep.HealthCheck && keyInfo.Key == ConsoleKey.Enter)
        {
            if (ViewModel.IsComplete.Value)
                ViewModel.RequestQuit();
            else
                ViewModel.GoNext();
        }
    }

    private IFocusable? GetActiveSelectionList()
    {
        return ViewModel.CurrentStep.Value switch
        {
            WizardStep.Provider when _providerSubStep == 0 => _providerList,
            WizardStep.Provider when _providerSubStep == 1 => _authMethodList,
            WizardStep.Slack when _slackSubStep == 0 => _slackEnabledList,
            WizardStep.Mcp => _mcpList,
            WizardStep.Exposure => _exposureList,
            _ => null
        };
    }

    private TextInputNode? GetActiveTextInput()
    {
        return ViewModel.CurrentStep.Value switch
        {
            WizardStep.Provider when _providerSubStep == 2 && _endpointInput is not null => _endpointInput,
            WizardStep.Provider when _providerSubStep == 2 => _apiKeyInput,
            WizardStep.Slack when _slackSubStep == 1 => _slackBotTokenInput,
            WizardStep.Slack when _slackSubStep == 2 => _slackAppTokenInput,
            WizardStep.Acl => _ownerIdentityInput,
            _ => null
        };
    }

    private void SetProviderSubStep(int step)
    {
        _providerSubStep = step;
        _stepContentNode?.Invalidate();
        _helpTextNode?.Invalidate();
        ViewModel.RequestRedraw();
    }

    private void SetSlackSubStep(int step)
    {
        _slackSubStep = step;
        _stepContentNode?.Invalidate();
        _helpTextNode?.Invalidate();
        ViewModel.RequestRedraw();
    }

    private void InitializeComponents()
    {
        // Invalidate dynamic layouts when step changes so they re-evaluate their factories
        ViewModel.CurrentStep
            .Subscribe(step =>
            {
                if (step == WizardStep.Slack)
                    _slackSubStep = 0;

                _stepContentNode?.Invalidate();
                _helpTextNode?.Invalidate();
            })
            .DisposeWith(Subscriptions);
    }
}
