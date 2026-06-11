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
                        content.WithChild(
                            Layouts.SelectionList(items)
                                .WithMode(SelectionMode.Single)
                                .WithHighlightColors(Color.Black, Color.Cyan)
                                .WithHighlightedIndex(selectedIndex)
                                .WithFillHeight());
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

    private string FormatSessionLine(SessionCatalogEntryDto session)
    {
        var title = session.Title ?? "Untitled";
        var channel = session.Channel;
        var turns = session.TurnCount;
        var ago = ViewModel.FormatRelativeTime(session.LastActivity);
        return $"[{channel}] {title} ({turns} turns, {ago})";
    }
}
