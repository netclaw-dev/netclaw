using Netclaw.Actors.Channels;
using Netclaw.Cli.Tui;
using Netclaw.Configuration;
using R3;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Wizard.Steps;

/// <summary>
/// Termina view for the Channels wizard step.
/// Custom keyboard navigation: ↑/↓ cursor, ←/→ audience cycling, a/d add/delete, Enter to continue.
/// </summary>
public sealed class ChannelsStepView : IWizardStepView
{
    private static readonly TrustAudience[] AudienceValues = [TrustAudience.Personal, TrustAudience.Team, TrustAudience.Public];

    private int _cursorIndex;
    private bool _addMode;
    private TextInputNode? _addInput;
    private TextInputBaseNode? _lastFocusedInput;
    private StepViewCallbacks? _callbacks;
    private ChannelsStepViewModel? _vm;

    public string StepId => "channels";

    /// <summary>Whether the view is in add-channel mode. Exposed for headless testing.</summary>
    internal bool IsAddMode => _addMode;

    public ILayoutNode BuildContent(IWizardStepViewModel stepVm, StepViewCallbacks callbacks)
    {
        _callbacks = callbacks;
        _vm = (ChannelsStepViewModel)stepVm;
        return BuildChannelList(callbacks);
    }

    private ILayoutNode BuildChannelList(StepViewCallbacks callbacks)
    {
        if (_addMode && _addInput is not null)
        {
            return Layouts.Vertical()
                .WithChild(new TextNode("  Add channel:").WithForeground(Color.White))
                .WithChild(new PanelNode()
                    .WithTitle("Channel Name")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Gray)
                    .WithContent(_addInput)
                    .Height(3))
                .WithSpacing(1)
                .WithChild(new TextNode("  Enter to add, Esc to cancel.")
                    .WithForeground(Color.BrightBlack));
        }

        var entries = _vm?.AllEntries ?? [];

        if (entries.Count == 0)
        {
            return Layouts.Vertical()
                .WithChild(new TextNode("  No channels configured.").WithForeground(Color.Yellow))
                .WithChild(new TextNode("  Press [a] to add a channel, or Enter to continue.")
                    .WithForeground(Color.BrightBlack));
        }

        if (_cursorIndex >= entries.Count) _cursorIndex = entries.Count - 1;
        if (_cursorIndex < 0) _cursorIndex = 0;

        var layout = Layouts.Vertical()
            .WithChild(new TextNode("  Slack channels:").WithForeground(Color.White))
            .WithSpacing(1);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var isFocused = i == _cursorIndex;
            var prefix = isFocused ? " \u25b6 " : "   ";
            var name = entry.DisplayName.PadRight(20);
            var audience = $"[\u25c0 {entry.Audience.ToWireValue(),-8} \u25b6]";
            var line = $"{prefix}{name} {audience}";

            var node = new TextNode(line);
            node = isFocused
                ? node.WithForeground(Color.Cyan).Bold()
                : node.WithForeground(Color.White);
            layout = layout.WithChild(node);
        }

        layout = layout.WithSpacing(1)
            .WithChild(new TextNode("  [a] Add channel    [d] Remove channel")
                .WithForeground(Color.BrightBlack));

        return layout;
    }

    public bool HandleKeyPress(KeyPressed key)
    {
        var keyInfo = key.KeyInfo;
        var entries = _vm?.AllEntries ?? [];

        // Add-channel mode
        if (_addMode)
        {
            if (keyInfo.Key == ConsoleKey.Escape)
            {
                _addMode = false;
                _addInput = null;
                _lastFocusedInput = null;
                _callbacks?.InvalidateAndRedraw();
                return true;
            }

            if (_addInput is not null)
            {
                if (keyInfo.Key == ConsoleKey.Enter)
                {
                    var text = _addInput.Text?.Trim().TrimStart('#');
                    if (!string.IsNullOrWhiteSpace(text) && _vm is not null)
                    {
                        var posture = _vm.SelectedPosture;
                        var audience = posture == DeploymentPosture.Public
                            ? TrustAudience.Public
                            : TrustAudience.Team;

                        if (!entries.Any(e =>
                            e.DisplayName.Equals($"#{text}", StringComparison.OrdinalIgnoreCase)))
                        {
                            // Add to Slack by default — when Discord is added,
                            // the add UI will need a source selector
                            _vm.AddEntry(ChannelType.Slack, new ChannelEntry($"#{text}", text, audience));
                        }
                    }

                    _addMode = false;
                    _addInput = null;
                    _lastFocusedInput = null;
                    _callbacks?.InvalidateAndRedraw();
                    return true;
                }

                _addInput.HandleInput(keyInfo);
                _callbacks?.RequestRedraw();
            }
            return true;
        }

        // Normal mode
        switch (keyInfo.Key)
        {
            case ConsoleKey.UpArrow:
                if (_cursorIndex > 0) _cursorIndex--;
                break;

            case ConsoleKey.DownArrow:
                if (_cursorIndex < entries.Count - 1) _cursorIndex++;
                break;

            case ConsoleKey.RightArrow:
                if (entries.Count > 0)
                {
                    var entry = entries[_cursorIndex];
                    var idx = Array.IndexOf(AudienceValues, entry.Audience);
                    entry.Audience = AudienceValues[(idx + 1) % AudienceValues.Length];
                }
                break;

            case ConsoleKey.LeftArrow:
                if (entries.Count > 0)
                {
                    var entry = entries[_cursorIndex];
                    var idx = Array.IndexOf(AudienceValues, entry.Audience);
                    entry.Audience = AudienceValues[(idx - 1 + AudienceValues.Length) % AudienceValues.Length];
                }
                break;

            case ConsoleKey.A:
                _addMode = true;
                _addInput = new TextInputNode().WithPlaceholder("channel-name");
                _addInput.OnFocused();
                _lastFocusedInput = _addInput;
                break;

            case ConsoleKey.D:
                if (entries.Count > 0 && !entries[_cursorIndex].IsDmRow && _vm is not null)
                {
                    _vm.RemoveEntry(entries[_cursorIndex]);
                    // Re-fetch entries after removal for cursor clamping
                    var remaining = _vm.AllEntries;
                    if (_cursorIndex >= remaining.Count && remaining.Count > 0)
                        _cursorIndex = remaining.Count - 1;
                }
                break;

            case ConsoleKey.Enter:
                _callbacks?.AdvanceStep();
                return true;

            default:
                return false;
        }

        _callbacks?.InvalidateAndRedraw();
        return true;
    }

    public void HandlePaste(PasteEvent paste)
    {
        _lastFocusedInput?.HandlePaste(paste);
    }

    public void ClearFocusState()
    {
        _lastFocusedInput = null;
        _addInput = null;
        _addMode = false;
        _cursorIndex = 0;
    }

}
