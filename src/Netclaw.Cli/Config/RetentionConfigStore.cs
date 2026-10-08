// -----------------------------------------------------------------------
// <copyright file="RetentionConfigStore.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Configuration;
using Netclaw.Cli.Tui.Sections;
using Netclaw.Configuration;

namespace Netclaw.Cli.Config;

/// <summary>The stored value of one <see cref="RetentionSetting"/>.</summary>
/// <param name="Days">The number of days that applies. Zero keeps the data forever.</param>
/// <param name="IsSet">False when netclaw.json does not set the key and the default applies.</param>
internal sealed record RetentionValue(int Days, bool IsSet);

/// <summary>
/// Reads and writes the retention settings in netclaw.json for the <c>netclaw config</c> editor
/// and <c>netclaw config retention</c>. A read resolves the value the way the daemon does, and a
/// write goes through the shared config editor pipeline.
/// </summary>
internal static class RetentionConfigStore
{
    /// <summary>Appended to a status line after a write.</summary>
    public const string Applied = ConfigFileHelper.DaemonAppliesChange;

    public static RetentionValue Read(NetclawPaths paths, RetentionSetting setting)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(paths.NetclawConfigPath, optional: true, reloadOnChange: false)
            .Build();
        var days = RetentionPolicy.ResolveDays(configuration, setting.ConfigKey, setting.DefaultDays, out _);
        return new RetentionValue(days, !string.IsNullOrWhiteSpace(configuration[setting.ConfigKey]));
    }

    /// <summary>
    /// Writes the changed settings in one save and returns the number of keys written. A key that
    /// is not set stays unset when the new value is the default, and a value that did not change
    /// is not written.
    /// </summary>
    public static int Save(NetclawPaths paths, IEnumerable<(RetentionSetting Setting, int Days)> changes)
    {
        var actions = new List<SectionFieldAction>();
        foreach (var (setting, days) in changes)
        {
            var current = Read(paths, setting);
            var unchanged = current.IsSet ? current.Days == days : days == setting.DefaultDays;
            if (!unchanged)
                actions.Add(new SectionFieldAction(setting.FilePath, SectionFieldActionKind.Set, days));
        }

        if (actions.Count == 0)
            return 0;

        var session = new ConfigEditorSession(paths);
        session.Apply(new SectionContribution(actions));
        session.Save();
        return actions.Count;
    }

    /// <summary>"keep 14 days", "keep 1 day", or "keep forever".</summary>
    public static string Describe(int days)
        => days <= 0 ? "keep forever" : $"keep {days} {(days == 1 ? "day" : "days")}";

    /// <summary>The value for the dashboard summary: "14d" or "forever".</summary>
    public static string Short(int days) => days <= 0 ? "forever" : $"{days}d";
}
