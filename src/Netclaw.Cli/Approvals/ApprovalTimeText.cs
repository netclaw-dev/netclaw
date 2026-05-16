// -----------------------------------------------------------------------
// <copyright file="ApprovalTimeText.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Cli.Approvals;

/// <summary>
/// Renders an <see cref="Netclaw.Configuration.ApprovalEntry.CreatedAt"/>
/// value as the relative "added ..." text shown by <c>netclaw approvals
/// list</c> and the interactive approvals TUI. A <c>null</c> timestamp — a
/// grant written before approval timestamps were tracked — renders the fixed
/// <c>added —</c> placeholder rather than a fabricated or blank value.
/// </summary>
internal static class ApprovalTimeText
{
    public static string Added(DateTimeOffset? createdAt, DateTimeOffset now)
        => createdAt is { } ts ? $"added {Relative(ts, now)}" : "added —";

    private static string Relative(DateTimeOffset then, DateTimeOffset now)
    {
        var elapsed = now - then;

        // Clock skew (a timestamp ahead of "now") collapses to "just now"
        // rather than rendering a negative age.
        if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
        if (elapsed < TimeSpan.FromHours(1)) return Plural((int)elapsed.TotalMinutes, "minute");
        if (elapsed < TimeSpan.FromDays(1)) return Plural((int)elapsed.TotalHours, "hour");
        if (elapsed < TimeSpan.FromDays(30)) return Plural((int)elapsed.TotalDays, "day");
        if (elapsed < TimeSpan.FromDays(365)) return Plural((int)(elapsed.TotalDays / 30), "month");
        return Plural((int)(elapsed.TotalDays / 365), "year");
    }

    private static string Plural(int count, string unit)
        => count <= 1 ? $"1 {unit} ago" : $"{count} {unit}s ago";
}
