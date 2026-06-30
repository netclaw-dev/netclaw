// -----------------------------------------------------------------------
// <copyright file="SessionsPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Cli.Daemon;
using R3;
using Termina.Extensions;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Termina page for the session browser (<c>netclaw sessions</c>).
/// Displays a list of recent sessions from the daemon catalog.
/// </summary>
public sealed class SessionsPage : ReactivePage<SessionsViewModel>
{
    public override ILayoutNode BuildLayout()
    {
        return Observable.CombineLatest(
                ViewModel.IsLoading,
                ViewModel.SelectedIndex,
                ViewModel.StatusMessage,
                (isLoading, selectedIndex, status) =>
                {
                    var content = Layouts.Vertical();

                    if (isLoading)
                    {
                        content.WithChild(
                            new TextNode("Loading sessions...")
                                .WithForeground(Color.Yellow)
                                .Height(1));
                    }
                    else if (ViewModel.Sessions.Count == 0)
                    {
                        content.WithChild(
                            new TextNode("No sessions found. Press Enter to start a new chat.")
                                .WithForeground(Color.BrightBlack)
                                .Height(1));
                    }
                    else
                    {
                        var items = ViewModel.Sessions.Select(FormatSessionLine).ToList();
                        var list = Layouts.SelectionList(items)
                            .WithMode(SelectionMode.Single)
                            .WithHighlightColors(Color.Black, Color.Cyan)
                            .WithHighlightedIndex(selectedIndex)
                            .WithFillHeight();
                        list.OnFocused();

                        content.WithChild(
                            list);
                    }

                    return (ILayoutNode)Layouts.Vertical()
                        .WithChild(
                            new PanelNode()
                                .WithTitle("Sessions")
                                .WithBorder(BorderStyle.Rounded)
                                .WithBorderColor(Color.Gray)
                                .WithContent(content.Fill())
                                .Fill())
                        .WithChild(
                            new TextNode($" {status}")
                                .WithForeground(Color.BrightBlack)
                                .Height(1));
                })
            .AsLayout();
    }

    // The scrollable SelectionListNode is focusable (FocusPriority 10), so the page's focus
    // policy hands it keyboard focus and Termina's focus manager would route the arrows and
    // Enter straight into it — moving the list's own highlight while the ViewModel's
    // SelectedIndex (the resume target) never changes. Claim those keys at the page level,
    // which Termina dispatches BEFORE the focus manager, so the list stays a pure renderer
    // driven one-way by .WithHighlightedIndex(SelectedIndex).
    public override bool HandlePageInput(ConsoleKeyInfo keyInfo)
    {
        if (base.HandlePageInput(keyInfo))
            return true;

        return ViewModel.HandleKey(keyInfo);
    }

    private string FormatSessionLine(SessionCatalogEntryDto session)
    {
        var title = session.Title ?? "Untitled";
        var channel = session.Channel;
        var turns = session.TurnCount;
        var ago = ViewModel.FormatRelativeTime(session.LastActivity);
        return $"[{channel}] {title} ({turns} turns, {ago})";
    }
}
