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
    private SelectionListNode<string>? _activeList;
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
            _activeList = null;
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

        _activeList = Layouts.SelectionList(items)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);
        _activeList.OnFocused();

        _activeList.SelectionConfirmed
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
            .WithChild(_activeList);
    }

    private ILayoutNode BuildToolGrid()
    {
        var server = ViewModel.SelectedServer ?? "?";
        var audienceLabel = ViewModel.SelectedAudience.ToWireValue();

        // Audience selector matching the channel wizard pattern: [◀ personal ▶]
        var audienceSelector = $"[\u25c0 {audienceLabel,-8} \u25b6]";

        var items = ViewModel.DiscoveredTools
            .Select(tool =>
            {
                var granted = ViewModel.IsToolGranted(tool);
                var marker = granted ? "\u2713" : " ";
                return $"[{marker}] {tool}";
            })
            .ToList();

        _activeList = Layouts.SelectionList(items)
            .WithMode(SelectionMode.Single)
            .WithHighlightColors(Color.Black, Color.Cyan);
        _activeList.OnFocused();

        _activeList.SelectionConfirmed
            .Subscribe(selected =>
            {
                if (selected.Count > 0)
                {
                    // Extract tool name from "[✓] tool_name" or "[ ] tool_name"
                    var raw = selected[0];
                    var toolName = raw.Length > 4 ? raw[4..].Trim() : raw;
                    ViewModel.ToggleTool(toolName);
                }
            })
            .DisposeWith(_stepSubs);

        var layout = Layouts.Vertical()
            .WithChild(new TextNode($"  Server: {server}").WithForeground(Color.White).Bold())
            .WithChild(new TextNode($"  Audience: {audienceSelector}").WithForeground(Color.Cyan).Bold())
            .WithSpacing(1)
            .WithChild(_activeList);

        if (!string.IsNullOrEmpty(ViewModel.StatusMessage.Value))
            layout.WithChild(new TextNode(ViewModel.StatusMessage.Value).WithForeground(Color.Green));

        return layout;
    }

    private LayoutNode BuildFooter()
    {
        return new DynamicLayoutNode(() =>
        {
            var hints = ViewModel.CurrentState.Value switch
            {
                ToolPermissionsState.ServerList => "[Enter] Select  [Esc] Quit  [Ctrl+Q] Quit",
                ToolPermissionsState.ToolGrid =>
                    "[\u2190/\u2192] Audience  [Enter] Toggle  [S] Save  [Esc] Back  [Ctrl+Q] Quit" +
                    (ViewModel.HasUnsavedChanges ? "  *unsaved*" : ""),
                _ => ""
            };

            return new TextNode(hints).WithForeground(Color.BrightBlack);
        });
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
            // From server list, Esc quits the app
            if (ViewModel.CurrentState.Value == ToolPermissionsState.ServerList)
            {
                ViewModel.RequestQuit();
                return;
            }

            ViewModel.GoBack();
            return;
        }

        if (ViewModel.CurrentState.Value == ToolPermissionsState.ToolGrid)
        {
            if (keyInfo.Key == ConsoleKey.RightArrow)
            {
                ViewModel.CycleAudience();
                return;
            }

            if (keyInfo.Key == ConsoleKey.LeftArrow)
            {
                ViewModel.CycleAudienceBack();
                return;
            }

            if (keyInfo.Key == ConsoleKey.S || keyInfo.KeyChar == 's')
            {
                ViewModel.Save();
                return;
            }
        }

        // Route to active list
        if (_activeList is not null)
        {
            _activeList.HandleInput(keyInfo);
            ViewModel.RequestRedraw();
        }
    }
}
