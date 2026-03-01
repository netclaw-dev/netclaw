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
    private const int MaxDisplayedModels = 30;
    private static readonly string[] SpinnerFrames = ["\u280b", "\u2819", "\u2838", "\u2834", "\u2826", "\u2807"];

    // Step 1: Provider selection list + auth input + model selection
    private SelectionListNode<string>? _providerList;
    private SelectionListNode<string>? _authMethodList;
    private TextInputNode? _apiKeyInput;
    private TextInputNode? _endpointInput;
    private SelectionListNode<string>? _modelList;
    private TextInputNode? _manualModelInput;
    private bool _manualModelEntry; // true when user chose "Enter model ID manually..."

    // Step 2: Chat Services (Slack)
    private TextInputNode? _slackBotTokenInput;
    private TextInputNode? _slackAppTokenInput;
    private TextInputNode? _slackChannelNamesInput;
    private SelectionListNode<string>? _slackEnabledList;

    // Step 3: ACL
    private TextInputNode? _ownerIdentityInput;

    // Step 4: Search
    private SelectionListNode<string>? _searchBackendList;
    private TextInputNode? _braveApiKeyInput;
    private TextInputNode? _searxngEndpointInput;
    private int _searchSubStep; // 0=backend selection, 1=credentials (brave key or searxng endpoint)

    // Step 5: Exposure
    private SelectionListNode<string>? _exposureList;

    // Step 6: Identity
    private TextInputNode? _agentNameInput;
    private SelectionListNode<string>? _commStyleList;
    private TextInputNode? _userNameInput;
    private TextInputNode? _timezoneInput;
    private TextInputNode? _primaryUseInput;

    // Track sub-step for provider (0=select, 1=auth, 2=credentials, 3=validate, 4=model)
    private int _providerSubStep;
    private int _chatServicesSubStep; // 0=enable?, 1=bot token, 2=app token, 3=channel names
    private int _identitySubStep; // 0=agent name, 1=comm style, 2=user name, 3=timezone, 4=primary use

    // Focus tracking for selection lists (mirrors _lastFocusedInput for text inputs)
    private IFocusable? _lastFocusedList;
    private TextInputNode? _lastFocusedInput;

    // Dynamic layout nodes — invalidation-driven (Termina 0.7.1+).
    // Factory runs once on creation, then only on Invalidate().
    private DynamicLayoutNode? _stepContentNode;
    private DynamicLayoutNode? _helpTextNode;

    // Step-specific subscriptions — cleared when step content is rebuilt
    // so old subscriptions on disposed components don't linger.
    private readonly CompositeDisposable _stepSubs = new();

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
            // Step content (no Fill — only takes the height it needs)
            .WithChild(BuildStepContent())
            // Help text (immediately below step content)
            .WithChild(BuildHelpText())
            // Spacer pushes status + key bindings to bottom
            .WithChild(Layouts.Empty().Fill())
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
                var activeCount = ViewModel.ActiveStepCount;
                var displayNum = ViewModel.GetDisplayStepNumber(step);
                var filled = new string('\u25a0', displayNum);
                var empty = new string('\u25a1', activeCount - displayNum);
                var pct = displayNum * 100 / activeCount;
                var title = step switch
                {
                    WizardStep.Provider => "LLM Provider",
                    WizardStep.ChatServices => "Chat Services",
                    WizardStep.Acl => "Access Control",
                    WizardStep.Search => "Web Search",
                    WizardStep.Exposure => "Exposure Mode",
                    WizardStep.Identity => "Identity",
                    WizardStep.HealthCheck => "Health Check",
                    _ => ""
                };
                return (ILayoutNode)new TextNode(
                        $"  Step {displayNum} of {activeCount}: {title}        [{filled}{empty}] {pct}%")
                    .WithForeground(Color.White)
                    .Bold();
            })
            .AsLayout()
            .Height(1);
    }

    private LayoutNode BuildStepContent()
    {
        _stepContentNode = new DynamicLayoutNode(() =>
        {
            var step = ViewModel.CurrentStep.Value;

            // Health check has no stateful components — safe to rebuild on every invalidation
            if (step == WizardStep.HealthCheck)
                return BuildHealthCheckStep();

            // Validation sub-step (provider step 3) is also stateless — just a spinner
            // or error text. Skip clearing focus/subs so the spinner can tick without
            // disposing interactive state from the previous sub-step. More importantly,
            // this factory must NEVER call SetProviderSubStep() — that would re-entrantly
            // invalidate _stepContentNode during its own evaluation, blanking the screen.
            if (step == WizardStep.Provider && _providerSubStep == 3)
                return BuildValidationSubStep();

            // Clear stale focus references BEFORE building new content.
            // The old components are about to be replaced/disposed by DynamicLayoutNode.
            // If we leave _lastFocused* pointing at disposed components, the next
            // RouteInputToActiveComponent call will OnBlurred() a disposed component,
            // throwing ObjectDisposedException and killing the input subscription.
            _lastFocusedList = null;
            _lastFocusedInput = null;

            // Clear subscriptions from previous step content before building new
            _stepSubs.Clear();

            return step switch
            {
                WizardStep.Provider => BuildProviderStep(),
                WizardStep.ChatServices => BuildChatServicesStep(),
                WizardStep.Acl => BuildAclStep(),
                WizardStep.Search => BuildSearchStep(),
                WizardStep.Exposure => BuildExposureStep(),
                WizardStep.Identity => BuildIdentityStep(),
                _ => Layouts.Empty()
            };
        });

        return _stepContentNode;
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
                WizardStep.Provider when _providerSubStep == 3 =>
                    "  Validating connection and discovering available models...",
                WizardStep.Provider when _providerSubStep == 4 =>
                    "  Select the model to use for conversations.",
                WizardStep.ChatServices when _chatServicesSubStep == 0 =>
                    "  Enable Slack to connect Netclaw as a Slack bot.",
                WizardStep.ChatServices when _chatServicesSubStep == 3 =>
                    "  Channel names separated by commas. Bot needs channels:read scope to resolve.",
                WizardStep.ChatServices =>
                    "  Socket Mode requires both tokens. See: https://api.slack.com/apis/socket-mode",
                WizardStep.Acl =>
                    "  Your Slack user ID (e.g., U01234ABCDE) for admin access.",
                WizardStep.Search when _searchSubStep == 0 =>
                    "  DuckDuckGo works without config but may hit bot detection. Brave Search is more reliable.",
                WizardStep.Search when _searchSubStep == 1 && ViewModel.SelectedSearchBackend == "brave" =>
                    "  Get a free API key at https://brave.com/search/api/. Stored in secrets.json.",
                WizardStep.Search when _searchSubStep == 1 && ViewModel.SelectedSearchBackend == "searxng" =>
                    "  Enter the base URL of your SearXNG instance. JSON format must be enabled in settings.yml.",
                WizardStep.Exposure =>
                    "  Local-only is recommended for homelab use.",
                WizardStep.Identity when _identitySubStep == 0 =>
                    "  Give your assistant a name, or keep the default.",
                WizardStep.Identity when _identitySubStep == 1 =>
                    "  How should your assistant communicate?",
                WizardStep.Identity when _identitySubStep == 2 =>
                    "  So your assistant knows what to call you.",
                WizardStep.Identity when _identitySubStep == 3 =>
                    "  Used for time-aware responses and scheduling.",
                WizardStep.Identity when _identitySubStep == 4 =>
                    "  What will you primarily use this assistant for?",
                WizardStep.HealthCheck =>
                    "  Validating your configuration...",
                _ => ""
            };
            return (ILayoutNode)new TextNode(text).WithForeground(Color.Gray);
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
                        $" [\u2191/\u2193] Navigate  [Enter] Next  [Esc] {backLabel}  [Ctrl+Q] Quit").WithForeground(Color.BrightBlack);
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
            3 => BuildValidationSubStep(),
            4 => BuildModelSelectionSubStep(),
            _ => Layouts.Empty()
        };
    }

    private ILayoutNode BuildProviderSelectionSubStep()
    {
        _providerList = Layouts.SelectionList(
                ProviderCapabilities.KnownProviderTypes)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

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
                        ViewModel.SelectedAuthMethod = AuthMethod.None;
                        SetProviderSubStep(2);
                    }
                    else
                    {
                        SetProviderSubStep(1);
                    }
                }
            })
            .DisposeWith(_stepSubs);

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
            .DisposeWith(_stepSubs);

        _authMethodList.Cancelled
            .Subscribe(_ =>
            {
                SetProviderSubStep(0);
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode($"  Authentication for {providerType}:").WithForeground(Color.White))
            .WithChild(_authMethodList);
    }

    private ILayoutNode BuildCredentialInputSubStep()
    {
        var providerType = ViewModel.SelectedProviderType ?? "unknown";

        if (providerType == "ollama")
        {
            var ollamaDefault = ProviderCapabilities.GetDefaultEndpoint("ollama");
            _endpointInput = new TextInputNode()
                .WithPlaceholder(ollamaDefault);
            _endpointInput.Text = ViewModel.EndpointInput ?? ollamaDefault;

            _endpointInput.OnFocused();
            _lastFocusedInput = _endpointInput;

            _endpointInput.Submitted
                .Subscribe(text =>
                {
                    ViewModel.EndpointInput = string.IsNullOrWhiteSpace(text)
                        ? ProviderCapabilities.GetDefaultEndpoint("ollama") : text;
                    // Start validation instead of advancing to next step
                    SetProviderSubStep(3);
                    ViewModel.StartProbe();
                })
                .DisposeWith(_stepSubs);

            return Layouts.Vertical()
                .WithChild(new TextNode("  Ollama endpoint:").WithForeground(Color.White))
                .WithChild(new PanelNode()
                    .WithTitle("Endpoint")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Gray)
                    .WithContent(_endpointInput)
                    .Height(3));
        }

        _apiKeyInput = new TextInputNode()
            .AsPassword()
            .WithPlaceholder($"Enter {providerType} API key...");

        if (!string.IsNullOrWhiteSpace(ViewModel.ApiKeyInput))
            _apiKeyInput.Text = ViewModel.ApiKeyInput;

        _apiKeyInput.OnFocused();
        _lastFocusedInput = _apiKeyInput;

        _apiKeyInput.Submitted
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Subscribe(text =>
            {
                ViewModel.ApiKeyInput = text;
                // Start validation instead of advancing to next step
                SetProviderSubStep(3);
                ViewModel.StartProbe();
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode($"  {providerType} API key:").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("API Key")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_apiKeyInput)
                .Height(3));
    }

    /// <summary>
    /// Render-only: shows spinner while probing, success flash, or error message.
    /// MUST NOT call SetProviderSubStep — that would re-entrantly invalidate
    /// the DynamicLayoutNode during its own factory evaluation.
    /// Auto-advance on success is handled by the ProbeResult subscription
    /// in InitializeComponents.
    /// </summary>
    private ILayoutNode BuildValidationSubStep()
    {
        var probeResult = ViewModel.ProbeResult.Value;

        if (ViewModel.IsProbing.Value || probeResult is null)
        {
            // Animated spinner + elapsed timer
            var elapsed = ViewModel.ProbeElapsedSeconds.Value;
            var frame = SpinnerFrames[elapsed % SpinnerFrames.Length];
            var timerText = elapsed > 0 ? $" ({elapsed}s)" : "";
            var provider = ViewModel.SelectedProviderType ?? "provider";

            return Layouts.Vertical()
                .WithChild(new TextNode($"  {frame} Validating connection to {provider}...{timerText}")
                    .WithForeground(Color.Yellow));
        }

        if (probeResult.Success)
        {
            // Brief success flash — the ProbeResult subscription will advance
            // to sub-step 4 (model selection) on the next cycle.
            var modelCount = probeResult.Models.Count;
            return Layouts.Vertical()
                .WithChild(new TextNode($"  \u2713 Connected! Found {modelCount} model{(modelCount == 1 ? "" : "s")}.")
                    .WithForeground(Color.Green));
        }

        // Failure — show error and prompt to retry or go back
        return Layouts.Vertical()
            .WithChild(new TextNode($"  \u2717 {probeResult.ErrorMessage}").WithForeground(Color.Red))
            .WithChild(new TextNode(""))
            .WithChild(new TextNode("  Press Enter to retry, or Esc to go back.").WithForeground(Color.BrightBlack));
    }

    private ILayoutNode BuildModelSelectionSubStep()
    {
        if (_manualModelEntry)
        {
            return BuildManualModelInput();
        }

        var models = ViewModel.DiscoveredModels;
        var items = new List<string>();

        // Limit display for large catalogs (e.g., OpenRouter)
        var displayCount = Math.Min(models.Count, MaxDisplayedModels);
        for (var i = 0; i < displayCount; i++)
            items.Add(models[i].ModelId);

        if (models.Count > MaxDisplayedModels)
            items.Add($"... and {models.Count - MaxDisplayedModels} more (enter manually)");

        items.Add("Enter model ID manually...");

        _modelList = Layouts.SelectionList(items)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _modelList.OnFocused();
        _lastFocusedList = _modelList;

        _modelList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    var choice = selected[0];
                    if (choice == "Enter model ID manually..." || choice.StartsWith("... and ", StringComparison.Ordinal))
                    {
                        _manualModelEntry = true;
                        InvalidateProviderSubStep();
                    }
                    else
                    {
                        ViewModel.SelectedModelId = choice;
                        _providerSubStep = 0;
                        ViewModel.GoNext();
                    }
                }
            })
            .DisposeWith(_stepSubs);

        var header = models.Count > 0
            ? $"  Select a model ({models.Count} available):"
            : "  No models discovered. Enter a model ID manually:";

        return Layouts.Vertical()
            .WithChild(new TextNode(header).WithForeground(Color.White))
            .WithChild(_modelList);
    }

    private ILayoutNode BuildManualModelInput()
    {
        _manualModelInput = new TextInputNode()
            .WithPlaceholder("e.g., anthropic/claude-sonnet-4-20250514");

        _manualModelInput.OnFocused();
        _lastFocusedInput = _manualModelInput;

        _manualModelInput.Submitted
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Subscribe(text =>
            {
                ViewModel.SelectedModelId = text;
                _manualModelEntry = false;
                _providerSubStep = 0;
                ViewModel.GoNext();
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Enter model ID:").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("Model ID")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_manualModelInput)
                .Height(3));
    }

    private ILayoutNode BuildChatServicesStep()
    {
        return _chatServicesSubStep switch
        {
            0 => BuildSlackEnableSubStep(),
            1 => BuildSlackBotTokenSubStep(),
            2 => BuildSlackAppTokenSubStep(),
            3 => BuildSlackChannelNamesSubStep(),
            _ => Layouts.Empty()
        };
    }

    private ILayoutNode BuildSlackEnableSubStep()
    {
        _slackEnabledList = Layouts.SelectionList("Yes \u2014 configure Slack bot", "No \u2014 skip for now")
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

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
                        SetChatServicesSubStep(1);
                    }
                    else
                    {
                        _chatServicesSubStep = 0;
                        ViewModel.GoNext();
                    }
                }
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Enable Slack integration?").WithForeground(Color.White))
            .WithChild(_slackEnabledList);
    }

    private ILayoutNode BuildSlackBotTokenSubStep()
    {
        _slackBotTokenInput = new TextInputNode()
            .AsPassword()
            .WithPlaceholder("xoxb-...");

        _slackBotTokenInput.OnFocused();
        _lastFocusedInput = _slackBotTokenInput;

        _slackBotTokenInput.Submitted
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Subscribe(text =>
            {
                ViewModel.SlackBotToken = text;
                SetChatServicesSubStep(2);
            })
            .DisposeWith(_stepSubs);

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

        _slackAppTokenInput.OnFocused();
        _lastFocusedInput = _slackAppTokenInput;

        _slackAppTokenInput.Submitted
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Subscribe(text =>
            {
                ViewModel.SlackAppToken = text;
                SetChatServicesSubStep(3);
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Slack App Token (Socket Mode):").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("App Token")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_slackAppTokenInput)
                .Height(3));
    }

    private ILayoutNode BuildSlackChannelNamesSubStep()
    {
        _slackChannelNamesInput = new TextInputNode()
            .WithPlaceholder("general, dev, random  (leave blank to skip)");

        if (!string.IsNullOrWhiteSpace(ViewModel.SlackChannelNamesInput))
            _slackChannelNamesInput.Text = ViewModel.SlackChannelNamesInput;

        _slackChannelNamesInput.OnFocused();
        _lastFocusedInput = _slackChannelNamesInput;

        _slackChannelNamesInput.Submitted
            .Subscribe(text =>
            {
                ViewModel.SlackChannelNamesInput = string.IsNullOrWhiteSpace(text) ? null : text;
                _chatServicesSubStep = 0;
                ViewModel.GoNext();
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Channel names (press Enter to skip):").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("Channel Names")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_slackChannelNamesInput)
                .Height(3));
    }

    private ILayoutNode BuildAclStep()
    {
        _ownerIdentityInput = new TextInputNode()
            .WithPlaceholder("U01234ABCDE (your Slack user ID)");

        if (!string.IsNullOrWhiteSpace(ViewModel.OwnerIdentity))
            _ownerIdentityInput.Text = ViewModel.OwnerIdentity;

        _ownerIdentityInput.OnFocused();
        _lastFocusedInput = _ownerIdentityInput;

        _ownerIdentityInput.Submitted
            .Subscribe(text =>
            {
                ViewModel.OwnerIdentity = string.IsNullOrWhiteSpace(text) ? null : text;
                ViewModel.GoNext();
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Owner identity (press Enter to skip):").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("Owner ID")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_ownerIdentityInput)
                .Height(3));
    }

    private ILayoutNode BuildSearchStep()
    {
        return _searchSubStep switch
        {
            0 => BuildSearchBackendSelectionSubStep(),
            1 => BuildSearchCredentialSubStep(),
            _ => Layouts.Empty()
        };
    }

    private ILayoutNode BuildSearchBackendSelectionSubStep()
    {
        _searchBackendList = Layouts.SelectionList(
                "DuckDuckGo (default \u2014 no config needed, may hit bot detection)",
                "Brave Search (API key required \u2014 reliable, fast)",
                "SearXNG (self-hosted \u2014 endpoint required)")
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _searchBackendList.OnFocused();
        _lastFocusedList = _searchBackendList;

        _searchBackendList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    var choice = selected[0];
                    if (choice.StartsWith("DuckDuckGo", StringComparison.Ordinal))
                    {
                        ViewModel.SelectedSearchBackend = "duckduckgo";
                        _searchSubStep = 0;
                        ViewModel.GoNext();
                    }
                    else if (choice.StartsWith("Brave", StringComparison.Ordinal))
                    {
                        ViewModel.SelectedSearchBackend = "brave";
                        SetSearchSubStep(1);
                    }
                    else if (choice.StartsWith("SearXNG", StringComparison.Ordinal))
                    {
                        ViewModel.SelectedSearchBackend = "searxng";
                        SetSearchSubStep(1);
                    }
                }
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Choose your web search provider:").WithForeground(Color.White))
            .WithChild(_searchBackendList);
    }

    private ILayoutNode BuildSearchCredentialSubStep()
    {
        if (ViewModel.SelectedSearchBackend == "brave")
        {
            _braveApiKeyInput = new TextInputNode()
                .AsPassword()
                .WithPlaceholder("Enter Brave Search API key...");

            if (!string.IsNullOrWhiteSpace(ViewModel.BraveApiKeyInput))
                _braveApiKeyInput.Text = ViewModel.BraveApiKeyInput;

            _braveApiKeyInput.OnFocused();
            _lastFocusedInput = _braveApiKeyInput;

            _braveApiKeyInput.Submitted
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Subscribe(text =>
                {
                    ViewModel.BraveApiKeyInput = text;
                    _searchSubStep = 0;
                    ViewModel.GoNext();
                })
                .DisposeWith(_stepSubs);

            return Layouts.Vertical()
                .WithChild(new TextNode("  Brave Search API key:").WithForeground(Color.White))
                .WithChild(new PanelNode()
                    .WithTitle("API Key")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Gray)
                    .WithContent(_braveApiKeyInput)
                    .Height(3));
        }

        // SearXNG endpoint
        _searxngEndpointInput = new TextInputNode()
            .WithPlaceholder("http://searxng.local:8080");

        if (!string.IsNullOrWhiteSpace(ViewModel.SearXngEndpointInput))
            _searxngEndpointInput.Text = ViewModel.SearXngEndpointInput;

        _searxngEndpointInput.OnFocused();
        _lastFocusedInput = _searxngEndpointInput;

        _searxngEndpointInput.Submitted
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Subscribe(text =>
            {
                ViewModel.SearXngEndpointInput = text;
                _searchSubStep = 0;
                ViewModel.GoNext();
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("  SearXNG endpoint URL:").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("Endpoint")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_searxngEndpointInput)
                .Height(3));
    }

    private ILayoutNode BuildExposureStep()
    {
        _exposureList = Layouts.SelectionList(
                "Local only (recommended for homelab)",
                "Tailscale (configure later)",
                "Cloudflare Tunnel (configure later)")
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

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
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Network exposure:").WithForeground(Color.White))
            .WithChild(_exposureList);
    }

    private ILayoutNode BuildIdentityStep()
    {
        return _identitySubStep switch
        {
            0 => BuildAgentNameSubStep(),
            1 => BuildCommStyleSubStep(),
            2 => BuildUserNameSubStep(),
            3 => BuildTimezoneSubStep(),
            4 => BuildPrimaryUseSubStep(),
            _ => Layouts.Empty()
        };
    }

    private ILayoutNode BuildAgentNameSubStep()
    {
        _agentNameInput = new TextInputNode()
            .WithPlaceholder("Netclaw");
        _agentNameInput.Text = ViewModel.AgentName;

        _agentNameInput.OnFocused();
        _lastFocusedInput = _agentNameInput;

        _agentNameInput.Submitted
            .Subscribe(text =>
            {
                ViewModel.AgentName = string.IsNullOrWhiteSpace(text) ? "Netclaw" : text;
                SetIdentitySubStep(1);
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Agent name:").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("Name")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_agentNameInput)
                .Height(3));
    }

    private ILayoutNode BuildCommStyleSubStep()
    {
        _commStyleList = Layouts.SelectionList(
                "Concise & casual",
                "Concise & formal",
                "Detailed & casual",
                "Detailed & formal")
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _commStyleList.OnFocused();
        _lastFocusedList = _commStyleList;

        _commStyleList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    ViewModel.CommunicationStyle = selected[0];
                    SetIdentitySubStep(2);
                }
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Communication style:").WithForeground(Color.White))
            .WithChild(_commStyleList);
    }

    private ILayoutNode BuildUserNameSubStep()
    {
        _userNameInput = new TextInputNode()
            .WithPlaceholder("Your name");

        if (!string.IsNullOrWhiteSpace(ViewModel.UserName))
            _userNameInput.Text = ViewModel.UserName;

        _userNameInput.OnFocused();
        _lastFocusedInput = _userNameInput;

        _userNameInput.Submitted
            .Subscribe(text =>
            {
                ViewModel.UserName = string.IsNullOrWhiteSpace(text) ? null : text;
                SetIdentitySubStep(3);
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Your name:").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("Name")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_userNameInput)
                .Height(3));
    }

    private ILayoutNode BuildTimezoneSubStep()
    {
        _timezoneInput = new TextInputNode()
            .WithPlaceholder(TimeZoneInfo.Local.Id);
        _timezoneInput.Text = ViewModel.UserTimezone;

        _timezoneInput.OnFocused();
        _lastFocusedInput = _timezoneInput;

        _timezoneInput.Submitted
            .Subscribe(text =>
            {
                ViewModel.UserTimezone = string.IsNullOrWhiteSpace(text)
                    ? TimeZoneInfo.Local.Id : text;
                SetIdentitySubStep(4);
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Your timezone:").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("Timezone")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_timezoneInput)
                .Height(3));
    }

    private ILayoutNode BuildPrimaryUseSubStep()
    {
        _primaryUseInput = new TextInputNode()
            .WithPlaceholder("e.g., homelab management, dev environment, home automation");

        if (!string.IsNullOrWhiteSpace(ViewModel.PrimaryUse))
            _primaryUseInput.Text = ViewModel.PrimaryUse;

        _primaryUseInput.OnFocused();
        _lastFocusedInput = _primaryUseInput;

        _primaryUseInput.Submitted
            .Subscribe(text =>
            {
                ViewModel.PrimaryUse = string.IsNullOrWhiteSpace(text) ? null : text;
                _identitySubStep = 0;
                ViewModel.GoNext();
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("  What will you primarily use this for?").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("Primary Use")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_primaryUseInput)
                .Height(3));
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
            if (_providerSubStep == 4)
            {
                // Going back from model selection to validation — re-probe or skip to credentials
                _manualModelEntry = false;
                SetProviderSubStep(2);
                return true;
            }
            if (_providerSubStep == 3)
            {
                // Going back from validation to credentials — cancel in-flight probe
                ViewModel.CancelProbe();
                SetProviderSubStep(2);
                return true;
            }
            SetProviderSubStep(_providerSubStep - 1);
            return true;
        }

        if (ViewModel.CurrentStep.Value == WizardStep.Search && _searchSubStep > 0)
        {
            SetSearchSubStep(_searchSubStep - 1);
            return true;
        }

        if (ViewModel.CurrentStep.Value == WizardStep.ChatServices && _chatServicesSubStep > 0)
        {
            SetChatServicesSubStep(_chatServicesSubStep - 1);
            return true;
        }

        if (ViewModel.CurrentStep.Value == WizardStep.Identity && _identitySubStep > 0)
        {
            SetIdentitySubStep(_identitySubStep - 1);
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
            if (_lastFocusedInput is not null)
            {
                _lastFocusedInput.OnBlurred();
                _lastFocusedInput = null;
            }

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
            if (_lastFocusedList is not null)
            {
                _lastFocusedList.OnBlurred();
                _lastFocusedList = null;
            }

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

        // On validation sub-step with failed result, Enter triggers retry
        if (ViewModel.CurrentStep.Value == WizardStep.Provider
            && _providerSubStep == 3
            && keyInfo.Key == ConsoleKey.Enter
            && ViewModel.ProbeResult.Value is { Success: false })
        {
            ViewModel.StartProbe();
            _stepContentNode?.Invalidate();
            _helpTextNode?.Invalidate();
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
            WizardStep.Provider when _providerSubStep == 4 && !_manualModelEntry => _modelList,
            WizardStep.ChatServices when _chatServicesSubStep == 0 => _slackEnabledList,
            WizardStep.Search when _searchSubStep == 0 => _searchBackendList,
            WizardStep.Exposure => _exposureList,
            WizardStep.Identity when _identitySubStep == 1 => _commStyleList,
            _ => null
        };
    }

    private TextInputNode? GetActiveTextInput()
    {
        return ViewModel.CurrentStep.Value switch
        {
            WizardStep.Provider when _providerSubStep == 2 && _endpointInput is not null => _endpointInput,
            WizardStep.Provider when _providerSubStep == 2 => _apiKeyInput,
            WizardStep.Provider when _providerSubStep == 4 && _manualModelEntry => _manualModelInput,
            WizardStep.ChatServices when _chatServicesSubStep == 1 => _slackBotTokenInput,
            WizardStep.ChatServices when _chatServicesSubStep == 2 => _slackAppTokenInput,
            WizardStep.ChatServices when _chatServicesSubStep == 3 => _slackChannelNamesInput,
            WizardStep.Search when _searchSubStep == 1 && _braveApiKeyInput is not null => _braveApiKeyInput,
            WizardStep.Search when _searchSubStep == 1 => _searxngEndpointInput,
            WizardStep.Acl => _ownerIdentityInput,
            WizardStep.Identity when _identitySubStep == 0 => _agentNameInput,
            WizardStep.Identity when _identitySubStep == 2 => _userNameInput,
            WizardStep.Identity when _identitySubStep == 3 => _timezoneInput,
            WizardStep.Identity when _identitySubStep == 4 => _primaryUseInput,
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

    private void InvalidateProviderSubStep()
    {
        _stepContentNode?.Invalidate();
        _helpTextNode?.Invalidate();
        ViewModel.RequestRedraw();
    }

    private void SetChatServicesSubStep(int step)
    {
        _chatServicesSubStep = step;
        _stepContentNode?.Invalidate();
        _helpTextNode?.Invalidate();
        ViewModel.RequestRedraw();
    }

    private void SetSearchSubStep(int step)
    {
        _searchSubStep = step;
        _stepContentNode?.Invalidate();
        _helpTextNode?.Invalidate();
        ViewModel.RequestRedraw();
    }

    private void SetIdentitySubStep(int step)
    {
        _identitySubStep = step;
        _stepContentNode?.Invalidate();
        _helpTextNode?.Invalidate();
        ViewModel.RequestRedraw();
    }

    private void InitializeComponents()
    {
        // Invalidate dynamic layouts when step changes so they re-evaluate their factories.
        // Also reset sub-step counters when entering a step.
        ViewModel.CurrentStep
            .Subscribe(step =>
            {
                if (step == WizardStep.Provider)
                    _providerSubStep = 0;
                if (step == WizardStep.ChatServices)
                    _chatServicesSubStep = 0;
                if (step == WizardStep.Search)
                    _searchSubStep = 0;
                if (step == WizardStep.Identity)
                    _identitySubStep = 0;

                _stepContentNode?.Invalidate();
                _helpTextNode?.Invalidate();
            })
            .DisposeWith(Subscriptions);

        // Probe result changed — invalidate to show success/failure, then auto-advance
        // on success after a brief flash. This runs OUTSIDE the DynamicLayoutNode factory,
        // so SetProviderSubStep is safe here (no re-entrant invalidation).
        ViewModel.ProbeResult
            .Subscribe(result =>
            {
                if (ViewModel.CurrentStep.Value != WizardStep.Provider || _providerSubStep != 3)
                    return;

                // Always invalidate to render the new state (success flash or error)
                _stepContentNode?.Invalidate();
                _helpTextNode?.Invalidate();

                // Auto-advance to model selection on success
                if (result is { Success: true })
                    SetProviderSubStep(4);
            })
            .DisposeWith(Subscriptions);

        // Animate spinner on validation sub-step every second
        ViewModel.ProbeElapsedSeconds
            .Subscribe(_ =>
            {
                if (ViewModel.CurrentStep.Value == WizardStep.Provider && _providerSubStep == 3)
                    _stepContentNode?.Invalidate();
            })
            .DisposeWith(Subscriptions);

        // Health check results change between Invalidate calls (via RequestRedraw).
        // With invalidation-driven DynamicLayoutNode, we need explicit Invalidate
        // when the ViewModel signals that health check results have updated.
        ViewModel.HealthCheckResultVersion
            .Subscribe(_ =>
            {
                if (ViewModel.CurrentStep.Value == WizardStep.HealthCheck)
                    _stepContentNode?.Invalidate();
            })
            .DisposeWith(Subscriptions);
    }

    public override void Dispose()
    {
        _stepSubs.Dispose();
        base.Dispose();
    }
}
