using R3;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Termina view for the Skill Feeds wizard step.
/// Sub-step 0: Yes/No selection to connect.
/// Sub-step 1: URL text input.
/// Sub-step 2: Probe result (spinner during probe, result/error after).
/// Sub-step 3: Name input (auto-suggested from hostname).
/// Sub-step 4: Add another or continue.
/// </summary>
public sealed class SkillFeedsStepView : IWizardStepView
{
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠸", "⠴", "⠦", "⠇"];

    private SelectionListNode<string>? _connectList;
    private TextInputNode? _urlInput;
    private TextInputNode? _nameInput;
    private SelectionListNode<string>? _errorActionList;
    private SelectionListNode<string>? _addAnotherList;
    private IFocusable? _lastFocusedList;
    private TextInputBaseNode? _lastFocusedInput;
    private StepViewCallbacks? _callbacks;
    private SkillFeedsStepViewModel? _vm;
    private int _spinnerTick;

    public string StepId => "skill-feeds";

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        _callbacks = callbacks;
        _vm = (SkillFeedsStepViewModel)stepVm;

        return _vm.CurrentSubStep switch
        {
            0 => BuildConnectPrompt(callbacks),
            1 => BuildUrlInput(callbacks),
            2 => BuildProbeResult(callbacks),
            3 => BuildNameInput(callbacks),
            4 => BuildAddAnotherPrompt(callbacks),
            _ => Layouts.Empty()
        };
    }

    private ILayoutNode BuildConnectPrompt(StepViewCallbacks callbacks)
    {
        _lastFocusedInput = null;

        var yesLabel = "Yes — add a skill server URL";
        var noLabel = "No — skip";

        _connectList = Layouts.SelectionList(yesLabel, noLabel)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _connectList.OnFocused();
        _lastFocusedList = _connectList;

        _connectList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0)
                    return;

                _vm!.SetWantsToConnect(selected[0] == yesLabel);
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        var infoContent = Layouts.Vertical()
            .WithChild(new TextNode("Any server implementing the Cloudflare Agents Skills Discovery protocol can distribute skills to Netclaw. Use ours or bring your own:")
                .WithForeground(Color.BrightBlack))
            .WithChild(new TextNode("https://github.com/netclaw-dev/skill-server")
                .WithForeground(Color.Cyan));

        return Layouts.Vertical()
            .WithChild(new TextNode("  Connect to a private skill server?").WithForeground(Color.White))
            .WithSpacing(1)
            .WithChild(new PanelNode()
                .WithTitle("ℹ  What's a skill server?")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(infoContent))
            .WithSpacing(1)
            .WithChild(_connectList);
    }

    private ILayoutNode BuildUrlInput(StepViewCallbacks callbacks)
    {
        _lastFocusedList = null;

        _urlInput = new TextInputNode()
            .WithPlaceholder("https://skills.example.com");

        if (!string.IsNullOrWhiteSpace(_vm!.CurrentUrl))
            _urlInput.Text = _vm.CurrentUrl;

        _urlInput.OnFocused();
        _lastFocusedInput = _urlInput;

        _urlInput.Submitted
            .Subscribe(text =>
            {
                if (string.IsNullOrWhiteSpace(text))
                    return;

                _vm.SetUrl(text);
                _vm.BeginProbe();
                callbacks.AdvanceStep();

                _ = Task.Run(async () =>
                {
                    await _vm.ProbeAsync(CancellationToken.None);
                    callbacks.InvalidateAndRedraw();

                    if (_vm.ProbeSucceeded)
                        callbacks.AdvanceStep();
                });
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode("  Enter the skill server URL:").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("URL")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_urlInput)
                .Height(3));
    }

    private ILayoutNode BuildProbeResult(StepViewCallbacks callbacks)
    {
        _lastFocusedList = null;
        _lastFocusedInput = null;

        if (_vm!.IsProbing)
        {
            var frame = SpinnerFrames[_spinnerTick++ % SpinnerFrames.Length];
            return Layouts.Vertical()
                .WithChild(new TextNode($"  {frame} Discovering skills at {_vm.CurrentUrl} ...")
                    .WithForeground(Color.Cyan));
        }

        if (_vm.LastProbeError is not null)
        {
            var retryLabel = "Try again";
            var editLabel = "Edit URL";
            var skipLabel = "Skip this step";

            _errorActionList = Layouts.SelectionList(retryLabel, editLabel, skipLabel)
                .WithMode(SelectionMode.Single)
                .WithHighlightColors(Color.Black, Color.Cyan);

            _errorActionList.OnFocused();
            _lastFocusedList = _errorActionList;

            _errorActionList.SelectionConfirmed
                .Subscribe(selected =>
                {
                    if (selected.Count == 0) return;
                    var choice = selected[0];

                    if (choice == retryLabel)
                    {
                        _vm.BeginProbe();
                        callbacks.InvalidateAndRedraw();
                        _ = Task.Run(async () =>
                        {
                            await _vm.ProbeAsync(CancellationToken.None);
                            callbacks.InvalidateAndRedraw();
                            if (_vm.ProbeSucceeded)
                                callbacks.AdvanceStep();
                        });
                    }
                    else if (choice == editLabel)
                    {
                        _vm.TryGoBack();
                        callbacks.InvalidateAndRedraw();
                    }
                    else
                    {
                        _vm.SetWantsToConnect(false);
                        callbacks.AdvanceStep();
                    }
                })
                .DisposeWith(callbacks.Subscriptions);

            return Layouts.Vertical()
                .WithChild(new TextNode($"  ✗ Could not reach {_vm.CurrentUrl}").WithForeground(Color.Red))
                .WithChild(new TextNode($"    {_vm.LastProbeError}").WithForeground(Color.BrightBlack))
                .WithSpacing(1)
                .WithChild(_errorActionList);
        }

        // Success — probe callback handles AdvanceStep(); this is a transient render state
        return Layouts.Vertical()
            .WithChild(new TextNode($"  ✓ Connected to {_vm.CurrentUrl}").WithForeground(Color.Green))
            .WithChild(new TextNode($"    Found {_vm.LastProbeSkillCount} skills").WithForeground(Color.White));
    }

    private ILayoutNode BuildNameInput(StepViewCallbacks callbacks)
    {
        _lastFocusedList = null;

        _nameInput = new TextInputNode()
            .WithPlaceholder("feed-name");

        _nameInput.Text = _vm!.CurrentName;

        _nameInput.OnFocused();
        _lastFocusedInput = _nameInput;

        _nameInput.Submitted
            .Subscribe(text =>
            {
                if (!string.IsNullOrWhiteSpace(text))
                    _vm.SetName(text);

                _vm.SaveCurrentFeed();
                callbacks.AdvanceStep();
            })
            .DisposeWith(callbacks.Subscriptions);

        return Layouts.Vertical()
            .WithChild(new TextNode($"  ✓ Connected to {_vm.CurrentUrl}").WithForeground(Color.Green))
            .WithChild(new TextNode($"    Found {_vm.LastProbeSkillCount} skills").WithForeground(Color.White))
            .WithSpacing(1)
            .WithChild(new TextNode("  Feed name (used in config):").WithForeground(Color.White))
            .WithChild(new PanelNode()
                .WithTitle("Name")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Gray)
                .WithContent(_nameInput)
                .Height(3));
    }

    private ILayoutNode BuildAddAnotherPrompt(StepViewCallbacks callbacks)
    {
        _lastFocusedInput = null;

        var continueLabel = "Continue to next step";
        var addLabel = "Add another skill server";

        _addAnotherList = Layouts.SelectionList(continueLabel, addLabel)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);

        _addAnotherList.OnFocused();
        _lastFocusedList = _addAnotherList;

        _addAnotherList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count == 0) return;

                if (selected[0] == addLabel)
                {
                    _vm!.StartAddAnother();
                    callbacks.InvalidateAndRedraw();
                }
                else
                {
                    callbacks.AdvanceStep();
                }
            })
            .DisposeWith(callbacks.Subscriptions);

        var layout = Layouts.Vertical()
            .WithChild(new TextNode("  Configured feeds:").WithForeground(Color.White));

        foreach (var feed in _vm!.ConfiguredFeeds)
        {
            layout = layout.WithChild(
                new TextNode($"    ✓ {feed.Name} ({feed.SkillCount} skills)")
                    .WithForeground(Color.Green));
        }

        layout = layout
            .WithSpacing(1)
            .WithChild(_addAnotherList);

        return layout;
    }

    public bool HandleKeyPress(KeyPressed key)
    {
        if (_vm is null)
            return false;

        var keyInfo = key.KeyInfo;

        if (_lastFocusedInput is not null)
        {
            _lastFocusedInput.HandleInput(keyInfo);
            return true;
        }

        if (_lastFocusedList is not null)
        {
            _lastFocusedList.HandleInput(keyInfo);
            return true;
        }

        return false;
    }

    public void HandlePaste(PasteEvent paste)
    {
        _lastFocusedInput?.HandlePaste(paste);
    }

    public void ClearFocusState()
    {
        _lastFocusedList = null;
        _lastFocusedInput = null;
        _connectList = null;
        _urlInput = null;
        _nameInput = null;
        _errorActionList = null;
        _addAnotherList = null;
        _spinnerTick = 0;
    }
}
