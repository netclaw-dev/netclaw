// -----------------------------------------------------------------------
// <copyright file="McpToolPermissionsPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Tui.Config;
using Netclaw.Configuration;
using Netclaw.Tools;
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
    private KeyedDynamicLayoutNode<(ToolPermissionsState State, int Revision)>? _contentNode;
    private DynamicLayoutNode? _footerNode;
    private DynamicLayoutNode? _gridHeaderRowsNode;
    private DynamicLayoutNode? _toolRowsNode;
    private ScrollableContainerNode? _toolScrollNode;
    private readonly CompositeDisposable _stepSubs = [];
    private readonly TextNode _confirmSaveFooterNode = new TextNode(
        "Save changes?  [Enter/Y] Save  [N] Discard  [Esc] Continue editing")
        .WithForeground(Color.Yellow)
        .Bold()
        .NoWrap();
    private int _gridCursor;
    private bool _confirmingSave;

    private const int AudienceRow = 0;
    private const int ServerEnabledRow = 1;
    private const int ServerDefaultRow = 2;
    private const int FirstToolRow = 3;

    private int TotalRows => FirstToolRow + ViewModel.DiscoveredTools.Count;

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
            .WithContent(new TextNode("MCP Permissions")
                .WithForeground(Color.White)
                .Bold());
    }

    private LayoutNode BuildContent()
    {
        _contentNode = new KeyedDynamicLayoutNode<(ToolPermissionsState State, int Revision)>(
            GetContentKey,
            _ =>
            {
                _serverList = null;
                _toolScrollNode = null;
                _gridHeaderRowsNode = null;
                _toolRowsNode = null;
                _stepSubs.Clear();

                return ViewModel.CurrentState.Value switch
                {
                    ToolPermissionsState.Loading => BuildLoading(),
                    ToolPermissionsState.ServerList => BuildServerList(),
                    ToolPermissionsState.ToolGrid => BuildToolGrid(),
                    ToolPermissionsState.Saving => BuildLoading(),
                    _ => Layouts.Empty()
                };
            },
            KeyedDynamicCachePolicy.EvictOnKeyChange);

        // ToolGrid updates invalidate only the header and rows, so the scroll container persists and keeps its scroll position.
        ViewModel.StateVersion
            .Subscribe(_ =>
            {
                if (ViewModel.CurrentState.Value == ToolPermissionsState.ToolGrid
                    && _toolRowsNode is not null)
                {
                    _gridHeaderRowsNode?.Invalidate();
                    _toolRowsNode.Invalidate();
                }
                else
                {
                    _contentNode.Invalidate();
                }
            })
            .DisposeWith(Subscriptions);

        return _contentNode.Fill();
    }

    private (ToolPermissionsState State, int Revision) GetContentKey()
    {
        var state = ViewModel.CurrentState.Value;
        var revision = state is ToolPermissionsState.Loading or ToolPermissionsState.Saving
            ? ViewModel.StateVersion.Value
            : 0;
        return (state, revision);
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
                    var serverName = new McpServerName(selected[0].Split("  (", 2)[0].Trim());
                    ViewModel.SelectServer(serverName);
                }
            })
            .DisposeWith(_stepSubs);

        return Layouts.Vertical()
            .WithChild(new TextNode("Select a server:").WithForeground(Color.White))
            .WithChild(_serverList.WithFillHeight());
    }

    private ILayoutNode BuildToolGrid()
    {
        var server = ViewModel.SelectedServer ?? "?";
        var maxRow = TotalRows - 1;
        if (_gridCursor > maxRow) _gridCursor = maxRow;
        if (_gridCursor < 0) _gridCursor = 0;

        var layout = Layouts.Vertical()
            .WithChild(new TextNode($"  Server: {server}").WithForeground(Color.White).Bold());

        // Header and tool rows are separate dynamic nodes so cursor movement can
        // repaint both regions without recreating the scroll container.
        _gridHeaderRowsNode = new DynamicLayoutNode(BuildGridHeaderRows);
        layout = layout
            .WithChild(_gridHeaderRowsNode)
            .WithSpacing(1);

        // Tool rows live in a separate DynamicLayoutNode so cursor navigation
        // can invalidate just the rows without resetting the scroll container.
        _toolRowsNode = new DynamicLayoutNode(BuildToolRows);
        _toolScrollNode = new ScrollableContainerNode()
            .WithAutoScroll(AutoScrollPolicy.None)
            .WithContent(_toolRowsNode);
        _toolScrollNode.Fill();
        layout = layout.WithChild(_toolScrollNode.Fill());

        return layout;
    }

    private ILayoutNode BuildGridHeaderRows()
    {
        var audienceLabel = ViewModel.SelectedAudience.ToWireValue();
        var serverAllowed = ViewModel.IsServerAllowedForSelectedAudience();
        var serverDefault = ViewModel.GetServerDefault();
        var accessMarker = serverAllowed ? "\u2713" : " ";

        return Layouts.Vertical()
            .WithChild(ConfigSelectionRow.Create(
                $"Audience: [\u25c0 {audienceLabel,-8} \u25b6]",
                _gridCursor == AudienceRow,
                bold: true))
            .WithChild(ConfigSelectionRow.Create(
                $"[{accessMarker}] Server enabled for {audienceLabel}",
                _gridCursor == ServerEnabledRow,
                serverAllowed ? Color.White : Color.Yellow,
                bold: true))
            .WithChild(ConfigSelectionRow.Create(
                $"Server default: [{serverDefault}]",
                _gridCursor == ServerDefaultRow,
                ColorForMode(serverDefault),
                bold: true))
            .WithSpacing(1);
    }

    private ILayoutNode BuildToolRows()

    {
        var tools = ViewModel.DiscoveredTools;
        var serverAllowed = ViewModel.IsServerAllowedForSelectedAudience();
        var maxToolNameLen = tools.Count > 0 ? tools.Max(t => t.Length) : 0;
        var rows = Layouts.Vertical();

        for (var i = 0; i < tools.Count; i++)
        {
            var rowIndex = FirstToolRow + i;
            var isFocused = _gridCursor == rowIndex;
            var tool = tools[i];
            var toolName = new ToolName(tool);
            var granted = serverAllowed && ViewModel.IsToolGranted(toolName);
            var marker = granted ? "\u2713" : " ";
            var paddedName = tool.PadRight(maxToolNameLen);
            var (effectiveMode, inherited) = ViewModel.GetEffectiveMode(toolName);
            var modeBadge = $"[{effectiveMode}]";
            var inheritSuffix = inherited ? "(def)" : "(override)";
            var line = $"[{marker}] {paddedName}  {modeBadge,-12} {inheritSuffix}";
            var foreground = serverAllowed && granted ? Color.White : Color.BrightBlack;
            rows = rows.WithChild(ConfigSelectionRow.Create(line, isFocused, foreground, bold: isFocused));
        }

        return rows;
    }

    // Adjusts the scroll container so the cursor row stays in the visible window.
    // Called after each Up/Down keypress; uses ContentHeight/MaxScroll from the
    // previous render (valid as long as the tool list hasn't changed size).
    private void EnsureToolCursorVisible()
    {
        if (_toolScrollNode is null || _gridCursor < FirstToolRow) return;
        var toolIdx = _gridCursor - FirstToolRow;
        if (_toolScrollNode.MaxScroll == 0)
        {
            // All tools fit in the viewport. Reset any stale offset left over from
            // a prior larger scroll position (e.g. after a terminal resize or after
            // the audience changes to a smaller visible tool set).
            if (_toolScrollNode.ScrollOffset != 0)
                _toolScrollNode.ScrollTo(0);
            return;
        }
        var viewportH = _toolScrollNode.ContentHeight - _toolScrollNode.MaxScroll;
        if (viewportH <= 0) return;
        if (toolIdx < _toolScrollNode.ScrollOffset)
            _toolScrollNode.ScrollTo(toolIdx);
        else if (toolIdx >= _toolScrollNode.ScrollOffset + viewportH)
            _toolScrollNode.ScrollTo(toolIdx - viewportH + 1);
    }

    private static Color ColorForMode(ToolApprovalMode mode) => mode switch
    {
        ToolApprovalMode.Approval => Color.Yellow,
        ToolApprovalMode.Deny => Color.Red,
        _ => Color.White
    };

    private LayoutNode BuildFooter()
    {
        _footerNode = new DynamicLayoutNode(() =>
        {
            if (_confirmingSave)
            {
                return _confirmSaveFooterNode;
            }

            var hints = ViewModel.CurrentState.Value switch
            {
                ToolPermissionsState.ServerList => "[Enter] Select  [Esc] Quit  [Ctrl+Q] Quit",
                ToolPermissionsState.ToolGrid =>
                    "[↑/↓] Navigate  [←/→] Change  [Space] Toggle  [A] All  [Enter] Done  [Esc] Back",
                _ => ""
            };

            if (ViewModel.CurrentState.Value == ToolPermissionsState.ToolGrid)
            {
                var statusText = ViewModel.StatusMessage.Value;
                var hasStatus = !string.IsNullOrEmpty(statusText);

                if (ViewModel.HasSaveError)
                    return BuildToolGridFooterWithStatus(hints, $"  {statusText}", Color.Red);
                if (ViewModel.HasUnsavedChanges)
                    return BuildToolGridFooterWithStatus(hints, "  *unsaved*", Color.Yellow);
                if (hasStatus)
                    return BuildToolGridFooterWithStatus(hints, $"  {statusText}", Color.Green);
            }

            return new TextNode(hints).WithForeground(Color.BrightBlack).NoWrap();
        });

        ViewModel.StateVersion
            .Subscribe(_ => _footerNode.Invalidate())
            .DisposeWith(Subscriptions);

        return _footerNode;
    }

    public override void Dispose()
    {
        base.Dispose();
        _confirmSaveFooterNode.Dispose();
    }

    private static LayoutNode BuildToolGridFooterWithStatus(string hints, string status, Color color)
    {
        return Layouts.Horizontal()
            .WithChild(new TextNode(hints).WithForeground(Color.BrightBlack).NoWrap())
            .WithChild(new TextNode(status).WithForeground(color).WidthAuto());
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

        // Handle save confirmation dialog
        if (_confirmingSave)
        {
            HandleConfirmation(keyInfo);
            return;
        }

        if (keyInfo.Key == ConsoleKey.Escape)
        {
            if (ViewModel.CurrentState.Value == ToolPermissionsState.ToolGrid)
            {
                _gridCursor = 0;
                ViewModel.GoBack();
            }
            else
            {
                ViewModel.RequestQuit();
            }

            return;
        }

        if (ViewModel.CurrentState.Value == ToolPermissionsState.ToolGrid)
        {
            switch (keyInfo.Key)
            {
                case ConsoleKey.UpArrow:
                    if (_gridCursor > 0) _gridCursor--;
                    EnsureToolCursorVisible();
                    InvalidateCursorAndRedraw();
                    return;

                case ConsoleKey.DownArrow:
                    if (_gridCursor < TotalRows - 1) _gridCursor++;
                    EnsureToolCursorVisible();
                    InvalidateCursorAndRedraw();
                    return;

                case ConsoleKey.RightArrow:
                    HandleRightArrow();
                    return;

                case ConsoleKey.LeftArrow:
                    HandleLeftArrow();
                    return;

                case ConsoleKey.Spacebar:
                    HandleToggle();
                    return;

                case ConsoleKey.Enter:
                    HandleDone();
                    return;

                case ConsoleKey.A:
                    if (ViewModel.IsServerAllowedForSelectedAudience())
                        ViewModel.ToggleAll();
                    return;

                case ConsoleKey.E:
                    ViewModel.ToggleServerAccess();
                    return;

                case ConsoleKey.M:
                    ViewModel.CycleServerDefault();
                    return;

                case ConsoleKey.P:
                    if (ViewModel.IsServerAllowedForSelectedAudience()
                        && ViewModel.DiscoveredTools.Count > 0
                        && _gridCursor >= FirstToolRow)
                        ViewModel.CycleToolOverride(new ToolName(ViewModel.DiscoveredTools[_gridCursor - FirstToolRow]));
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

    private void HandleRightArrow() => DispatchGridAction(
        ViewModel.CycleAudience,
        ViewModel.ToggleServerAccess,
        ViewModel.CycleServerDefault,
        idx => ViewModel.CycleToolOverride(new ToolName(ViewModel.DiscoveredTools[idx])));

    private void HandleLeftArrow() => DispatchGridAction(
        ViewModel.CycleAudienceBack,
        ViewModel.ToggleServerAccess,
        ViewModel.CycleServerDefaultBack,
        idx => ViewModel.CycleToolOverrideBack(new ToolName(ViewModel.DiscoveredTools[idx])));

    private void HandleToggle() => DispatchGridAction(
        ViewModel.CycleAudience,
        ViewModel.ToggleServerAccess,
        ViewModel.CycleServerDefault,
        idx => ViewModel.ToggleTool(new ToolName(ViewModel.DiscoveredTools[idx])));

    private void DispatchGridAction(
        Action audienceAction,
        Action serverEnabledAction,
        Action serverDefaultAction,
        Action<int> toolAction)
    {
        switch (_gridCursor)
        {
            case AudienceRow:
                audienceAction();
                break;
            case ServerEnabledRow:
                serverEnabledAction();
                break;
            case ServerDefaultRow:
                serverDefaultAction();
                break;
            default:
                if (_gridCursor >= FirstToolRow
                    && ViewModel.IsServerAllowedForSelectedAudience()
                    && ViewModel.DiscoveredTools.Count > 0)
                {
                    toolAction(_gridCursor - FirstToolRow);
                }
                break;
        }
    }

    private void HandleDone()
    {
        if (ViewModel.HasUnsavedChanges)
        {
            _confirmingSave = true;
            InvalidateAndRedraw();
        }
        else
        {
            _gridCursor = 0;
            ViewModel.GoBack();
        }
    }

    private void HandleConfirmation(ConsoleKeyInfo keyInfo)
    {
        switch (keyInfo.Key)
        {
            case ConsoleKey.Enter:
            case ConsoleKey.Y:
                _confirmingSave = false;
                if (!ViewModel.Save())
                {
                    InvalidateAndRedraw();
                    return;
                }
                _gridCursor = 0;
                ViewModel.GoBack();
                break;

            case ConsoleKey.N:
                _confirmingSave = false;
                _gridCursor = 0;
                ViewModel.DiscardChanges();
                ViewModel.GoBack();
                break;

            case ConsoleKey.Escape:
                _confirmingSave = false;
                InvalidateAndRedraw();
                break;
        }
    }

    private void InvalidateAndRedraw()
    {
        _contentNode?.Invalidate();
        _footerNode?.Invalidate();
        ViewModel.RequestRedraw();
    }

    private void InvalidateCursorAndRedraw()
    {
        _gridHeaderRowsNode?.Invalidate();
        _toolRowsNode?.Invalidate();
        _footerNode?.Invalidate();
        ViewModel.RequestRedraw();
    }
}
