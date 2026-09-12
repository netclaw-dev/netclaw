// -----------------------------------------------------------------------
// <copyright file="StatsPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using R3;
using Termina.Extensions;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui;

/// <summary>
/// Termina page for the stats dashboard (<c>netclaw stats --tui</c>).
/// Renders a visual snapshot of usage statistics with ASCII bar charts.
/// </summary>
public sealed class StatsPage : ReactivePage<StatsViewModel>
{
    public override ILayoutNode BuildLayout()
    {
        return Observable.CombineLatest(
                ViewModel.IsLoading,
                ViewModel.StatusMessage,
                (isLoading, status) =>
                {
                    if (isLoading)
                    {
                        return (ILayoutNode)Layouts.Vertical()
                            .WithChild(
                                new TextNode("  Loading stats from daemon...")
                                    .WithForeground(Color.Yellow)
                                    .Height(1))
                            .Fill();
                    }

                    if (ViewModel.Stats is null)
                    {
                        return (ILayoutNode)Layouts.Vertical()
                            .WithChild(
                                new TextNode($"  {status}")
                                    .WithForeground(Color.Red)
                                    .Height(1))
                            .Fill();
                    }

                    return BuildDashboard(ViewModel.Stats, status);
                })
            .AsLayout();
    }

    private static ILayoutNode BuildDashboard(DaemonStats.Response stats, string status)
    {
        var root = Layouts.Vertical();

        // Title bar
        var uptimeText = FormatUptime(stats.Process.UptimeSeconds);
        root.WithChild(
            new TextNode($"  netclaw stats — daemon up {uptimeText}")
                .WithForeground(Color.Cyan)
                .Bold()
                .Height(1));
        root.WithChild(Layouts.Empty().Height(1));

        // Top row: Tokens + Activity side by side
        var topRow = Layouts.Horizontal();

        topRow.WithChild(
            new PanelNode()
                .WithTitle("Tokens")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Blue)
                .WithContent(BuildTokensPanel(stats))
                .Fill());

        topRow.WithChild(
            new PanelNode()
                .WithTitle("Activity")
                .WithBorder(BorderStyle.Rounded)
                .WithBorderColor(Color.Green)
                .WithContent(BuildActivityPanel(stats))
                .Fill());

        root.WithChild(topRow.Height(7));

        // Daily breakdown chart (if data exists)
        if (stats.DailyBreakdown is { Count: > 0 })
        {
            root.WithChild(
                new PanelNode()
                    .WithTitle("Daily Token Usage")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Magenta)
                    .WithContent(BuildDailyChart(stats.DailyBreakdown))
                    .Fill());
        }
        else
        {
            // Middle row: Memory + channel panels side by side
            var middleRow = Layouts.Horizontal();

            middleRow.WithChild(
                new PanelNode()
                    .WithTitle("Memory")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Yellow)
                    .WithContent(BuildMemoryPanel(stats))
                    .Fill());

            foreach (var channel in stats.Channels)
            {
                middleRow.WithChild(
                    new PanelNode()
                        .WithTitle(channel.DisplayName)
                        .WithBorder(BorderStyle.Rounded)
                        .WithBorderColor(GetChannelColor(channel.ChannelType))
                        .WithContent(BuildChannelPanel(channel))
                        .Fill());
            }

            root.WithChild(middleRow.Height(6));

            // Bottom row: Webhooks (full width)
            root.WithChild(
                new PanelNode()
                    .WithTitle("Webhooks")
                    .WithBorder(BorderStyle.Rounded)
                    .WithBorderColor(Color.Red)
                    .WithContent(BuildWebhooksPanel(stats))
                    .Height(6));
        }

        // Status bar
        root.WithChild(
            new TextNode(status)
                .WithForeground(Color.BrightBlack)
                .Height(1));

        return root.Fill();
    }

    private static ILayoutNode BuildTokensPanel(DaemonStats.Response stats)
    {
        var t = stats.Tokens;
        var content = Layouts.Vertical();
        content.WithChild(new TextNode($"  Input:  {t.InputTokensTotal,12:N0}").WithForeground(Color.White).Height(1));
        content.WithChild(new TextNode($"  Output: {t.OutputTokensTotal,12:N0}").WithForeground(Color.White).Height(1));
        content.WithChild(new TextNode($"  Total:  {t.InputTokensTotal + t.OutputTokensTotal,12:N0}").WithForeground(Color.Cyan).Bold().Height(1));
        return content;
    }

    private static ILayoutNode BuildActivityPanel(DaemonStats.Response stats)
    {
        var t = stats.Tokens;
        var s = stats.Sessions;
        var content = Layouts.Vertical();
        content.WithChild(new TextNode($"  Turns: {t.TurnsCompletedTotal:N0}    Sessions: {s.TotalSessions} ({s.ActiveSessions} active)").WithForeground(Color.White).Height(1));
        content.WithChild(new TextNode($"  Memories formed: {t.MemoriesFormedTotal:N0}    recalled: {t.MemoriesRecalledTotal:N0}").WithForeground(Color.White).Height(1));
        content.WithChild(new TextNode($"  Skills loaded: {t.SkillsLoadedTotal:N0}    available: {stats.Skills.TotalAvailable}").WithForeground(Color.White).Height(1));

        if (stats.Reminders is { } r)
            content.WithChild(new TextNode($"  Reminders: {r.ScheduledCount} scheduled, {r.ActiveExecutions} active, {r.FailedCount} failed").WithForeground(Color.White).Height(1));

        return content;
    }

    private static ILayoutNode BuildMemoryPanel(DaemonStats.Response stats)
    {
        var m = stats.Memory;
        var content = Layouts.Vertical();
        if (m.Status is "unavailable")
        {
            content.WithChild(new TextNode("  unavailable").WithForeground(Color.BrightBlack).Height(1));
        }
        else
        {
            content.WithChild(new TextNode($"  Anchors: {m.AnchorCount}    Documents: {m.DocumentCount}").WithForeground(Color.White).Height(1));
            content.WithChild(new TextNode($"  Records: {m.RecordCount}    Edges: {m.EdgeCount}").WithForeground(Color.White).Height(1));
            content.WithChild(new TextNode($"  Pending checkpoints: {m.PendingCheckpoints}").WithForeground(m.PendingCheckpoints > 0 ? Color.Yellow : Color.White).Height(1));
        }

        return content;
    }

    private static ILayoutNode BuildChannelPanel(DaemonStats.ChannelActivity channel)
    {
        var content = Layouts.Vertical();
        content.WithChild(new TextNode($"  Events: recv={channel.EventsReceived} routed={channel.EventsRouted} dropped={channel.EventsDropped}").WithForeground(Color.White).Height(1));
        content.WithChild(new TextNode($"  Replies: posted={channel.RepliesPosted} rejected={channel.RepliesRejected} failed={channel.RepliesFailed}").WithForeground(channel.RepliesFailed > 0 || channel.RepliesRejected > 0 ? Color.Yellow : Color.White).Height(1));

        if (channel.Extras is { Count: > 0 })
        {
            var extraLine = string.Join("  ", channel.Extras.Select(kv => $"{kv.Key}={kv.Value}"));
            content.WithChild(new TextNode($"  {extraLine}").WithForeground(Color.White).Height(1));
        }

        return content;
    }

    private static Color GetChannelColor(string channelType)
    {
        if (!ChannelTypeExtensions.TryFromWireValue(channelType, out var ct))
            return Color.White;

        return ct switch
        {
            ChannelType.Slack => Color.Cyan,
            ChannelType.Discord => Color.Magenta,
            _ => Color.White
        };
    }

    private static ILayoutNode BuildWebhooksPanel(DaemonStats.Response stats)
    {
        var w = stats.Webhooks;
        var content = Layouts.Vertical();
        content.WithChild(new TextNode($"  Routes: total={w.TotalRoutes} enabled={w.EnabledRoutes} disabled={w.DisabledRoutes} invalid={w.InvalidRoutes}")
            .WithForeground(w.InvalidRoutes > 0 ? Color.Yellow : Color.White).Height(1));
        content.WithChild(new TextNode($"  Deliveries: accepted={w.Accepted} filtered={w.EventFiltered} duplicate={w.DuplicateDelivery}").WithForeground(Color.White).Height(1));
        var rejectedAny = w.RouteNotFound + w.VerificationFailed + w.BodyTooLarge + w.InvalidJson + w.RateLimited;
        content.WithChild(new TextNode($"  Rejected: 404={w.RouteNotFound} 401={w.VerificationFailed} 413={w.BodyTooLarge} 400={w.InvalidJson} 429={w.RateLimited}")
            .WithForeground(rejectedAny > 0 ? Color.Yellow : Color.White).Height(1));
        return content;
    }

    private static ILayoutNode BuildDailyChart(List<DaemonStats.DailyRow> rows)
    {
        var content = Layouts.Vertical();

        if (rows.Count == 0)
        {
            content.WithChild(new TextNode("  No daily data").WithForeground(Color.BrightBlack).Height(1));
            return content;
        }

        var maxTokens = rows.Max(r => r.InputTokens + r.OutputTokens);
        if (maxTokens == 0) maxTokens = 1; // avoid division by zero

        const int barMaxWidth = 40;

        // Header
        content.WithChild(
            new TextNode($"  {"Date",-12} {"In Tokens",10} {"Out Tokens",10}  Bar")
                .WithForeground(Color.BrightBlack)
                .Height(1));

        // Rows (newest first, already sorted by query)
        foreach (var row in rows)
        {
            var total = row.InputTokens + row.OutputTokens;
            var barLen = (int)((double)total / maxTokens * barMaxWidth);
            var inputBarLen = maxTokens > 0 ? (int)((double)row.InputTokens / maxTokens * barMaxWidth) : 0;
            var outputBarLen = Math.Max(0, barLen - inputBarLen);

            var bar = new string('█', inputBarLen) + new string('▓', outputBarLen);

            content.WithChild(
                new TextNode($"  {row.Date,-12} {row.InputTokens,10:N0} {row.OutputTokens,10:N0}  {bar}")
                    .WithForeground(Color.Cyan)
                    .Height(1));
        }

        // Totals
        var totalIn = rows.Sum(r => r.InputTokens);
        var totalOut = rows.Sum(r => r.OutputTokens);
        content.WithChild(
            new TextNode($"  {"totals",-12} {totalIn,10:N0} {totalOut,10:N0}")
                .WithForeground(Color.White)
                .Bold()
                .Height(1));

        // Legend
        content.WithChild(
            new TextNode("  █ input  ▓ output")
                .WithForeground(Color.BrightBlack)
                .Height(1));

        return content;
    }

    private static string FormatUptime(long uptimeSeconds)
    {
        var uptime = TimeSpan.FromSeconds(Math.Max(0, uptimeSeconds));
        if (uptime.TotalDays >= 1) return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        if (uptime.TotalHours >= 1) return $"{uptime.Hours}h {uptime.Minutes}m";
        return $"{uptime.Minutes}m {uptime.Seconds}s";
    }
}
