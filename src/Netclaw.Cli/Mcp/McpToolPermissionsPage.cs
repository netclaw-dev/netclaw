using Netclaw.Configuration;
using R3;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Mcp;

public sealed class McpToolPermissionsPage : ReactivePage<McpToolPermissionsViewModel>
{
    private SelectionListNode<string>? _serverList;
    private DynamicLayoutNode? _contentNode;
    private readonly CompositeDisposable _stepSubs = new();
    private int _toolCursor;

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
            .WithChild(BuildHeader())
            .WithChild(BuildContent())
            .WithChild(BuildFooter());
    }

    private static LayoutNode BuildHeader()
    {
        return new PanelNode()
            .WithBorder(BorderStyle.Rounded)
            .WithBorderColor(Color.Cyan)
            .WithContent(new TextNode("MCP Tool Permissions")
                .WithForeground(Color.White)
                .Bold());
    }

    private LayoutNode BuildContent()
    {
        _contentNode = new DynamicLayoutNode(() =>
        {
            _serverList = null;
            _stepSubs.Clear();

            return ViewModel.CurrentState.Value switch
            {
                ToolPermissionsState.Loading => BuildLoading(),
                ToolPermissionsState.ServerList => BuildServerList(),
                ToolPermissionsState.ToolGrid => BuildToolGrid(),
                ToolPermissionsState.Saving => BuildLoading(),
                _ => Layouts.Empty()
            };
        });

        ViewModel.StateVersion
            .Subscribe(_ => _contentNode.Invalidate())
            .DisposeWith(Subscriptions);

        return _contentNode;
    }

    private ILayoutNode BuildLoading()
    {
        var msg = string.IsNullOrEmpty(ViewModel.StatusMessage.Value)
            ? "Loading..."
            : ViewModel.StatusMessage.Value;

        return new TextNode(msg).WithForeground(Color.BrightBlack);
    }

    private ILayoutNode BuildServerList()
    {
        if (ViewModel.Servers.Count == 0)
            return new TextNode("No MCP servers connected.").WithForeground(Color.BrightBlack);

        var items = ViewModel.Servers
            .Select(s => $"{s.Name}  ({s.Status}, {s.ToolCount} tools)")
            .ToList();

        _serverList = Layouts.SelectionList(items)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);
        _serverList.OnFocused();

        _serverList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    var serverName = selected[0].Split("  (", 2)[0].Trim();
                    ViewModel.SelectServer(serverName);
                }
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("Select a server:").WithForeground(Color.White))
            .WithChild(_serverList);
    }

    /// <summary>
    /// Manual cursor rendering — matches the channel wizard pattern.
    /// Preserves cursor position across rebuilds (no SelectionListNode reset).
    /// </summary>
    private ILayoutNode BuildToolGrid()
    {
        var server = ViewModel.SelectedServer ?? "?";
        var audienceLabel = ViewModel.SelectedAudience.ToWireValue();
        var audienceSelector = $"[\u25c0 {audienceLabel,-8} \u25b6]";

        var tools = ViewModel.DiscoveredTools;
        if (_toolCursor >= tools.Count) _toolCursor = tools.Count - 1;
        if (_toolCursor < 0) _toolCursor = 0;

        var serverAllowed = ViewModel.IsServerAllowedForSelectedAudience();
        var accessMarker = serverAllowed ? "\u2713" : " ";

        var layout = Layouts.Vertical()
            .WithChild(new TextNode($"  Server: {server}").WithForeground(Color.White).Bold())
            .WithChild(new TextNode($"  Audience: {audienceSelector}").WithForeground(Color.Cyan).Bold())
            .WithSpacing(1)
            .WithChild(new TextNode($"  [{accessMarker}] Server enabled for {audienceLabel}")
                .WithForeground(serverAllowed ? Color.White : Color.Yellow))
            .WithSpacing(1);

        for (var i = 0; i < tools.Count; i++)
        {
            var tool = tools[i];
            var isFocused = i == _toolCursor;
            var granted = serverAllowed && ViewModel.IsToolGranted(tool);
            var prefix = isFocused ? " \u25b6 " : "   ";
            var marker = granted ? "\u2713" : " ";
            var line = $"{prefix}[{marker}] {tool}";

            var node = new TextNode(line);
            if (!serverAllowed)
                node = node.WithForeground(Color.BrightBlack);
            else if (isFocused)
                node = node.WithForeground(Color.Cyan).Bold();
            else if (granted)
                node = node.WithForeground(Color.White);
            else
                node = node.WithForeground(Color.BrightBlack);
            layout = layout.WithChild(node);
        }

        if (!string.IsNullOrEmpty(ViewModel.StatusMessage.Value))
        {
            layout = layout.WithSpacing(1)
                .WithChild(new TextNode($"  {ViewModel.StatusMessage.Value}").WithForeground(Color.Green));
        }

        return layout;
    }

    private LayoutNode BuildFooter()
    {
        var footerNode = new DynamicLayoutNode(() =>
        {
            var hints = ViewModel.CurrentState.Value switch
            {
                ToolPermissionsState.ServerList => "[Enter] Select  [Esc] Quit  [Ctrl+Q] Quit",
                ToolPermissionsState.ToolGrid =>
                    "[\u2190/\u2192] Audience  [\u2191/\u2193] Navigate  [Enter] Toggle  [A] All  [E] Enable/Disable  [S] Save  [Esc] Back" +
                    (ViewModel.HasUnsavedChanges ? "  *unsaved*" : ""),
                _ => ""
            };

            return new TextNode(hints).WithForeground(Color.BrightBlack);
        });

        // Footer must invalidate on state changes to show correct hints
        ViewModel.StateVersion
            .Subscribe(_ => footerNode.Invalidate())
            .DisposeWith(Subscriptions);

        return footerNode;
    }

    private void HandleKeyPress(KeyPressed key)
    {
        var keyInfo = key.KeyInfo;

        // Ctrl+Q always quits
        if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestQuit();
            return;
        }

        if (keyInfo.Key == ConsoleKey.Escape)
        {
            if (ViewModel.CurrentState.Value == ToolPermissionsState.ServerList)
            {
                ViewModel.RequestQuit();
                return;
            }

            _toolCursor = 0;
            ViewModel.GoBack();
            return;
        }

        if (ViewModel.CurrentState.Value == ToolPermissionsState.ToolGrid)
        {
            switch (keyInfo.Key)
            {
                case ConsoleKey.UpArrow:
                    if (_toolCursor > 0) _toolCursor--;
                    InvalidateAndRedraw();
                    return;

                case ConsoleKey.DownArrow:
                    if (_toolCursor < ViewModel.DiscoveredTools.Count - 1) _toolCursor++;
                    InvalidateAndRedraw();
                    return;

                case ConsoleKey.RightArrow:
                    ViewModel.CycleAudience();
                    return;

                case ConsoleKey.LeftArrow:
                    ViewModel.CycleAudienceBack();
                    return;

                case ConsoleKey.Enter:
                    if (ViewModel.IsServerAllowedForSelectedAudience()
                        && ViewModel.DiscoveredTools.Count > 0)
                        ViewModel.ToggleTool(ViewModel.DiscoveredTools[_toolCursor]);
                    return;

                case ConsoleKey.S:
                    ViewModel.Save();
                    return;

                case ConsoleKey.A:
                    if (ViewModel.IsServerAllowedForSelectedAudience())
                        ViewModel.ToggleAll();
                    return;

                case ConsoleKey.E:
                    ViewModel.ToggleServerAccess();
                    return;
            }

            if (keyInfo.KeyChar == 's')
            {
                ViewModel.Save();
                return;
            }

            if (keyInfo.KeyChar == 'a' && ViewModel.IsServerAllowedForSelectedAudience())
            {
                ViewModel.ToggleAll();
                return;
            }

            if (keyInfo.KeyChar == 'e')
            {
                ViewModel.ToggleServerAccess();
                return;
            }

            return;
        }

        // Server list — route to SelectionListNode
        if (_serverList is not null)
        {
            _serverList.HandleInput(keyInfo);
            ViewModel.RequestRedraw();
        }
    }

    private void InvalidateAndRedraw()
    {
        _contentNode?.Invalidate();
        ViewModel.RequestRedraw();
    }
}
